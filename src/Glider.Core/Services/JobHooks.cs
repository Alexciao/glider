using Glider.Core.Contracts;

namespace Glider.Core.Services;

/// <summary>The service layer's side of every job: gating before it runs and bookkeeping after.</summary>
internal sealed class JobHooks : IJobHooks
{
    private readonly ServiceContext _c;

    public JobHooks(ServiceContext context) => _c = context;

    public async Task BeforeRunAsync(JobInfo job, Action<string?> setBlockedReason, CancellationToken ct)
    {
        if (ServiceContext.IsWriteJob(job.Kind) && _c.IsGameRunning())
        {
            throw new GliderException(Errors.GameRunning());
        }

        if (ServiceContext.IsNetworkJob(job.Kind))
        {
            await _c.RequireSignedInAsync(ct);
        }

        var path = TargetFolder(job);
        if (path is not null && _c.Egl.IsBusyWith(path))
        {
            _c.Log.Info("jobs", $"{job.Kind} {job.Id} waiting: EGL is busy with {path}");
            setBlockedReason(Errors.EglBusy().Message);
            await _c.Egl.WaitUntilNotBusyAsync(path, reason => setBlockedReason(reason ?? Errors.EglBusy().Message), ct);
            setBlockedReason(null);
            _c.Log.Info("jobs", $"{job.Kind} {job.Id} continuing: EGL finished");
        }
    }

    /// <summary>The folder a job touches: the install, or the planned install folder for a fresh install.</summary>
    private string? TargetFolder(JobInfo job)
    {
        if (_c.Records.Current is { } rec)
        {
            return rec.InstallPath;
        }

        return job.Kind == JobKind.Install && !string.IsNullOrEmpty(job.TargetPath)
            ? Path.Combine(job.TargetPath, _c.LastGameInfo?.FolderName ?? FortniteLayout.AppName)
            : job.Kind == JobKind.Import ? job.TargetPath : null;
    }

    public async Task OnSucceededAsync(JobInfo job, CancellationToken ct)
    {
        var before = _c.Records.Current;
        switch (job.Kind)
        {
            case JobKind.Uninstall:
                // Never touches EGL: GliderOnly forgets, DeleteFiles was done by Legendary.
                await _c.SaveRecordAsync(null);
                _c.Notify(NoticeLevel.Success, "Fortnite removed from Glider", null, toast: false);
                return;

            case JobKind.Install:
            case JobKind.Update:
            case JobKind.Repair:
            {
                var rec = await _c.SyncRecordAsync(ct, keepNeedsVerification: false);
                _c.Sizes.Request(filesChanged: true);
                if (rec is not null)
                {
                    await WriteBackIfSharedAsync(rec, ct);
                }

                var (title, msg) = job.Kind switch
                {
                    JobKind.Install => ("Fortnite is ready to play", "The download finished."),
                    JobKind.Update => ("Fortnite is up to date", rec is null ? null : $"Updated to {rec.Version}."),
                    _ => ("Repair complete", "All game files are OK."),
                };
                _c.Notify(NoticeLevel.Success, title, msg);
                return;
            }

            case JobKind.Verify:
                await _c.SyncRecordAsync(ct, r => r with { NeedsVerification = false });
                _c.Sizes.Request(filesChanged: true);
                var (vTitle, vMsg) = VerifyNotice(job.Verify);
                _c.Notify(NoticeLevel.Success, vTitle, vMsg, toast: job.Verify is { FilesBad: > 0 });
                return;

            case JobKind.Move:
            {
                var rec = await _c.SyncRecordAsync(ct);
                _c.Sizes.Request(filesChanged: true);
                if (rec is not null && before?.EglInstallationGuid is { } guid)
                {
                    await AfterSharedMoveAsync(rec, guid, ct);
                }

                _c.Notify(NoticeLevel.Success, "Fortnite moved", rec?.InstallPath);
                return;
            }

            case JobKind.Import:
                await _c.SyncRecordAsync(ct);
                _c.Sizes.Request(filesChanged: false);
                return;
        }
    }

    private static string N(int n) => n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    internal static (string Title, string Message) VerifyNotice(VerifyReport? r) => r switch
    {
        null => ("Verification complete", "All game files are OK."),
        { FilesBad: 0 } => ("Verification complete", $"Checked {N(r.FilesChecked)} files. All game files are OK."),
        _ => ("Repair complete", $"Checked {N(r.FilesChecked)} files: {N(r.FilesBad)} damaged or missing, {N(r.FilesFixed)} fixed. All game files are OK now."),
    };

    private async Task WriteBackIfSharedAsync(GameRecord rec, CancellationToken ct)
    {
        if (!_c.Settings.EglSync)
        {
            return;
        }

        var guid = rec.EglInstallationGuid ?? _c.Egl.FindByLocation(rec.InstallPath)?.InstallationGuid;
        if (guid is null)
        {
            return;
        }

        if (rec.EglInstallationGuid is null)
        {
            rec = rec with { EglInstallationGuid = guid };
            await _c.SaveRecordAsync(rec);
        }

        try
        {
            var installed = await _c.Legendary.GetInstalledAsync(ServiceContext.App, ct);
            await _c.Egl.WriteBackAsync(_c.BuildWriteBack(rec, installed, _c.LastGameInfo), ct);
            _c.Log.Info("egl", $"wrote {rec.Version} back to EGL ({guid})");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _c.Log.Warn("egl", "EGL write-back failed", e);
            _c.Notify(NoticeLevel.Warning, "Couldn't update Epic Games Launcher",
                "Fortnite is fine in Glider, but the Epic Games Launcher may want to verify it next time.", FixAction.OpenLogs, toast: false);
        }
    }

    private async Task AfterSharedMoveAsync(GameRecord rec, string guid, CancellationToken ct)
    {
        if (_c.Settings.EglSync)
        {
            try
            {
                await _c.Egl.UpdateLocationAsync(guid, rec.InstallPath, ct);
                return;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _c.Log.Warn("egl", "EGL location update failed", e);
            }
        }

        // EGL still points at the old folder: the link is gone, so stop treating the install as shared.
        await _c.SaveRecordAsync(rec with { EglInstallationGuid = null });
        _c.Notify(NoticeLevel.Warning, "Epic Games Launcher will lose track of Fortnite",
            "It still expects the old folder. Turn on \"Keep Epic Games Launcher in sync\" before moving to avoid this.", toast: false);
    }

    public async Task OnFailedAsync(JobInfo job, CancellationToken ct)
    {
        var e = job.Error ?? Errors.LegendaryFailed();
        if (e.Code == ErrorCode.Cancelled)
        {
            return;
        }

        if (job.Kind == JobKind.Verify && e.Code == ErrorCode.Corrupted && _c.Records.Current is { } rec)
        {
            await _c.SaveRecordAsync(rec with { NeedsVerification = true });
        }

        var message = job.Kind == JobKind.Verify && job.Verify is { FilesBad: > 0 } r && e.Code == ErrorCode.Corrupted
            ? $"Checked {N(r.FilesChecked)} files: {N(r.FilesBad)} damaged or missing, {N(r.FilesFixed)} fixed, {N(r.FilesBad - r.FilesFixed)} still bad. Try again, or check your disk."
            : e.Message;
        _c.Notify(NoticeLevel.Error, e.Title, message, e.Fix);
    }
}
