using Glider.Core.Contracts;
using Glider.Core.Game;
using Glider.Core.Jobs;

namespace Glider.Core.Services;

/// <summary>The service layer's side of every writer-lane job: per-game gating before it runs and bookkeeping after.</summary>
internal sealed class JobHooks : IJobHooks
{
    private readonly ServiceContext _c;
    private readonly AdoptionFinisher _adoption;

    public JobHooks(ServiceContext context)
    {
        _c = context;
        _adoption = new AdoptionFinisher(context);
    }

    /// <summary>Lets hooks replace the default outcome (set by GliderService; null in unit tests of the hooks alone).</summary>
    public Action<string, JobOutcome>? SetOutcome { get; set; }

    /// <summary>Queues follow-up jobs (import → verify); set by GliderService.</summary>
    public Func<GameSummary, JobInfo>? EnqueueVerify { get; set; }

    /// <summary>The overlay service's side of an overlay job's end (state refresh, notice, outcome); set by GliderService.</summary>
    public Func<JobInfo, bool, CancellationToken, Task>? OverlayFinished { get; set; }

    public async Task BeforeRunAsync(JobInfo job, Action<string?> setBlockedReason, CancellationToken ct)
    {
        var app = job.AppName;
        if (job.Kind == JobKind.Overlay)
        {
            // The overlay's DLL is loaded into every running game: it can't be replaced until they all close.
            if (_c.Hub.State.AnySessionActive)
            {
                setBlockedReason("Waiting for games to close");
                while (_c.Hub.State.AnySessionActive)
                {
                    await Task.Delay(_c.Options.ProcessPollInterval, _c.Time, ct);
                }

                setBlockedReason(null);
            }

            return;
        }

        await _c.WhenSavesIdle(app, ct); // a cloud-save sync of this game finishes first (S lane is exclusive with W jobs on the game)
        if (ServiceContext.IsWriteJob(job.Kind) && _c.IsGameRunning(app))
        {
            // Files of a running game can't be replaced: wait for it to close instead of failing the job.
            var reason = $"Waiting for {_c.TitleOf(app)} to close";
            setBlockedReason(reason);
            while (_c.IsGameRunning(app))
            {
                await Task.Delay(_c.Options.ProcessPollInterval, _c.Time, ct);
            }

            setBlockedReason(null);
        }

        if (ServiceContext.IsNetworkJob(job.Kind))
        {
            await _c.RequireSignedInAsync(ct);
        }

        var path = TargetFolder(job);
        if (path is not null && _c.Egl.IsBusyWith(path))
        {
            _c.Log.Info("jobs", $"{job.Kind} {app} {job.Id} waiting: EGL is busy with {path}");
            setBlockedReason(Errors.EglBusy().Message);
            await _c.Egl.WaitUntilNotBusyAsync(path, reason => setBlockedReason(reason ?? Errors.EglBusy().Message), ct);
            setBlockedReason(null);
            _c.Log.Info("jobs", $"{job.Kind} {app} {job.Id} continuing: EGL finished");
        }
    }

    /// <summary>The folder a job touches: the install, the planned folder of a fresh install, or the folder being imported.</summary>
    private string? TargetFolder(JobInfo job)
    {
        if (job.Kind == JobKind.Import)
        {
            return job.TargetPath;
        }

        if (_c.Library.Summary(job.AppName)?.InstallPath is { } installed)
        {
            return installed;
        }

        if (_c.Games.Get(job.AppName).Install is { } rec)
        {
            return rec.InstallPath;
        }

        return job.Kind == JobKind.Install && !string.IsNullOrEmpty(job.TargetPath)
            ? Path.Combine(job.TargetPath, _c.Library.FolderName(job.AppName))
            : null;
    }

    public async Task OnSucceededAsync(JobInfo job, CancellationToken ct)
    {
        var app = job.AppName;
        var title = job.Title;
        var before = _c.Games.Get(app).Install;
        switch (job.Kind)
        {
            case JobKind.Uninstall:
                // Never touches EGL: GliderOnly forgets, DeleteFiles was done by Legendary (which also removed installed DLC).
                await _c.SaveRecordAsync(app, null);
                await ForgetUninstalledDlcAsync(app, ct);
                _c.Notify(NoticeLevel.Success, $"{title} removed from Glider", null, toast: false, appName: app, jobId: job.Id);
                break;

            case JobKind.Install:
            case JobKind.Update:
            case JobKind.Repair:
            {
                var rec = await _c.SyncRecordAsync(app, ct, keepNeedsVerification: false);
                _c.Sizes.Request(BaseOf(app), filesChanged: true);
                if (rec is not null)
                {
                    await WriteBackIfSharedAsync(rec, ct);
                }

                var (noticeTitle, msg) = job.Kind switch
                {
                    JobKind.Install => ($"{title} is ready to play", "The download finished."),
                    JobKind.Update => ($"{title} is up to date", rec is null ? null : $"Updated to {rec.Version}."),
                    _ => ("Repair complete", $"All files of {title} are OK."),
                };
                _c.Notify(NoticeLevel.Success, noticeTitle, msg, appName: app, jobId: job.Id);
                break;
            }

            case JobKind.Verify:
                await _c.SyncRecordAsync(app, ct, r => r with { NeedsVerification = false });
                _c.Sizes.Request(BaseOf(app), filesChanged: true);
                var (vTitle, vMsg) = VerifyNotice(job.Verify, title);
                _c.Notify(NoticeLevel.Success, vTitle, vMsg, toast: job.Verify is { FilesBad: > 0 }, appName: app, jobId: job.Id);
                if (job.Verify is { FilesBad: > 0 } v)
                {
                    SetOutcome?.Invoke(job.Id, new JobOutcome(OutcomeKind.Success, $"{N(v.FilesFixed)} of {N(v.FilesBad)} files repaired"));
                }

                break;

            case JobKind.Move:
            {
                var rec = await _c.SyncRecordAsync(app, ct);
                _c.Sizes.Request(app, filesChanged: true);
                if (rec is not null && before?.EglInstallationGuid is { } guid)
                {
                    await AfterSharedMoveAsync(rec, guid, ct);
                }

                _c.Notify(NoticeLevel.Success, $"{title} moved", rec?.InstallPath, appName: app, jobId: job.Id);
                break;
            }

            case JobKind.Import:
                await AfterImportAsync(job, ct);
                break;

            case JobKind.Overlay:
                if (OverlayFinished is { } overlayDone)
                {
                    await overlayDone(job, true, ct);
                }

                return; // not a game: nothing to reload
        }

        await _c.Library.ReloadAsync();
    }

    /// <summary>
    /// Import done (zero download): carry the install tags over, record the install (linked to EGL when EGL has an .item there),
    /// decide whether a verify is in doubt (<see cref="AdoptionFinisher"/>) and report the per-game outcome.
    /// </summary>
    private async Task AfterImportAsync(JobInfo job, CancellationToken ct)
    {
        var app = job.AppName;
        var mode = _c.Jobs.Request(job.Id)?.Options?.Verify ?? VerifyMode.Auto;
        var result = await _adoption.FinishAsync(job, mode, ct);
        if (result is null)
        {
            SetOutcome?.Invoke(job.Id, new JobOutcome(OutcomeKind.Failed, "Legendary didn't register the install."));
            return;
        }

        var rec = result.Record;
        await _c.SaveRecordAsync(app, rec);
        _c.Sizes.Request(app, filesChanged: false);
        _c.Log.Info("game", $"imported {app} at {rec.InstallPath} v{rec.Version} (egl={rec.AdoptedFromEgl}, verify={result.Verify}, mode={mode}{(result.VerifyReason is null ? "" : ", " + result.VerifyReason)})");

        if (result.Verify)
        {
            await _c.Library.ReloadAsync();
            if (_c.Library.Summary(app) is { } summary && EnqueueVerify is { } enqueue)
            {
                enqueue(summary);
            }

            SetOutcome?.Invoke(job.Id, result.Outcome);
            _c.Notify(NoticeLevel.Info, $"{job.Title} imported", result.VerifyReason is { } why ? $"{why} Glider is checking its files in the background." : "Glider is checking its files in the background.",
                toast: false, appName: app, jobId: job.Id);
        }
        else
        {
            SetOutcome?.Invoke(job.Id, result.Outcome);
            _c.Notify(NoticeLevel.Success, $"{job.Title} is ready to play", "Imported without downloading anything.", toast: false, appName: app, jobId: job.Id);
        }
    }

    private string BaseOf(string app) => _c.Library.Summary(app)?.BaseAppName ?? app;

    /// <summary>Legendary uninstalls a base game's DLC with it: drop their records too.</summary>
    private async Task ForgetUninstalledDlcAsync(string baseApp, CancellationToken ct)
    {
        foreach (var dlc in _c.Library.Games.Values.Where(g => g.BaseAppName == baseApp))
        {
            if (_c.Games.Get(dlc.AppName).Install is not null && await _c.Legendary.GetInstalledAsync(dlc.AppName, ct) is null)
            {
                await _c.SaveRecordAsync(dlc.AppName, null);
            }
        }
    }

    private static string N(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    internal static (string Title, string Message) VerifyNotice(VerifyReport? r, string? title = null)
    {
        var all = title is null ? "All game files are OK." : $"All files of {title} are OK.";
        return r switch
        {
            null => ("Verification complete", all),
            { FilesBad: 0 } => ("Verification complete", $"Checked {N(r.FilesChecked)} files. {all}"),
            _ => ("Repair complete", $"Checked {N(r.FilesChecked)} files: {N(r.FilesBad)} damaged or missing, {N(r.FilesFixed)} fixed. All game files are OK now."),
        };
    }

    private async Task WriteBackIfSharedAsync(InstallRecord rec, CancellationToken ct)
    {
        if (!_c.Settings.EglSync || _c.Library.Summary(rec.AppName)?.Kind == GameKind.Dlc)
        {
            return;
        }

        var guid = rec.EglInstallationGuid ?? _c.Egl.FindByLocation(rec.InstallPath, rec.AppName)?.InstallationGuid;
        if (guid is null)
        {
            return;
        }

        if (rec.EglInstallationGuid is null)
        {
            rec = rec with { EglInstallationGuid = guid };
            await _c.SaveRecordAsync(rec.AppName, rec);
        }

        try
        {
            // EGL may be touching the folder right now: wait (up to a couple of minutes) instead of failing the write-back.
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(ct);
            patience.CancelAfter(_c.Options.EglWriteBackPatience);
            try
            {
                await _c.Egl.WaitUntilNotBusyAsync(rec.InstallPath, _ => { }, patience.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _c.Log.Info("egl", $"EGL stayed busy with {rec.InstallPath}; trying the write-back anyway");
            }

            var installed = await _c.Legendary.GetInstalledAsync(rec.AppName, ct);
            await _c.Egl.WriteBackAsync(_c.BuildWriteBack(rec, installed), ct);
            _c.Log.Info("egl", $"wrote {rec.AppName} {rec.Version} back to EGL ({guid})");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var title = _c.TitleOf(rec.AppName);
            _c.Log.Warn("egl", $"EGL write-back of {rec.AppName} failed", e);
            _c.Notify(NoticeLevel.Warning, "Couldn't update Epic Games Launcher",
                $"{title} is fine in Glider, but the Epic Games Launcher may want to verify it next time.", FixAction.OpenLogs, toast: false, appName: rec.AppName);
        }
    }

    private async Task AfterSharedMoveAsync(InstallRecord rec, string guid, CancellationToken ct)
    {
        if (_c.Settings.EglSync)
        {
            try
            {
                await _c.Egl.UpdateLocationAsync(rec.AppName, guid, rec.InstallPath, ct);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _c.Log.Warn("egl", $"EGL location update of {rec.AppName} failed", e);
            }
        }

        // EGL still points at the old folder: the link is gone, so stop treating the install as shared.
        await _c.SaveRecordAsync(rec.AppName, rec with { EglInstallationGuid = null });
        _c.Notify(NoticeLevel.Warning, $"Epic Games Launcher will lose track of {_c.TitleOf(rec.AppName)}",
            "It still expects the old folder. Turn on \"Keep Epic Games Launcher in sync\" before moving to avoid this.", toast: false, appName: rec.AppName);
    }

    public async Task OnFailedAsync(JobInfo job, CancellationToken ct)
    {
        var e = job.Error ?? Errors.LegendaryFailed();
        if (job.Kind == JobKind.Overlay)
        {
            if (OverlayFinished is { } overlayDone)
            {
                await overlayDone(job, false, ct);
            }

            return;
        }

        if (e.Code == ErrorCode.Cancelled)
        {
            return;
        }

        if (job.Kind == JobKind.Verify && e.Code == ErrorCode.Corrupted && _c.Games.Get(job.AppName).Install is { } rec)
        {
            await _c.SaveRecordAsync(job.AppName, rec with { NeedsVerification = true });
        }

        var message = job.Kind == JobKind.Verify && job.Verify is { FilesBad: > 0 } r && e.Code == ErrorCode.Corrupted
            ? $"Checked {N(r.FilesChecked)} files: {N(r.FilesBad)} damaged or missing, {N(r.FilesFixed)} fixed, {N(r.FilesBad - r.FilesFixed)} still bad. Try again, or check your disk."
            : e.Message;
        _c.Notify(NoticeLevel.Error, e.Title, message, e.Fix, appName: job.AppName, jobId: job.Id);
        await _c.Library.ReloadAsync();
    }
}
