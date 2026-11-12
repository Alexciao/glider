using Glider.Core.Contracts;

namespace Glider.Core.Services;

/// <summary>`info Fortnite --json` → latest version, sizes, art; update badge/notice/toast; auto-update policy.</summary>
internal sealed class UpdateChecker
{
    private static readonly TimeSpan InfoFreshFor = TimeSpan.FromMinutes(10);

    private readonly ServiceContext _c;
    private DateTimeOffset? _infoFetchedAt;
    private string? _notifiedVersion;
    private string? _artFor;

    public UpdateChecker(ServiceContext context) => _c = context;

    /// <summary>Recent game info (cached ≤10 min), or null when it can't be fetched (signed out/offline).</summary>
    public async Task<LegendaryGameInfo?> GetInfoAsync(CancellationToken ct)
    {
        if (_c.LastGameInfo is { } cached && _infoFetchedAt is { } at && _c.Now - at < InfoFreshFor)
        {
            return cached;
        }

        if (_c.Hub.State.Auth.Status != AuthStatus.SignedIn)
        {
            return _c.LastGameInfo;
        }

        try
        {
            return await FetchAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _c.Log.Warn("update", "game info unavailable", e);
            return _c.LastGameInfo;
        }
    }

    private async Task<LegendaryGameInfo> FetchAsync(CancellationToken ct)
    {
        var info = await _c.Legendary.GetGameInfoAsync(ServiceContext.App, ct);
        _c.LastGameInfo = info;
        _infoFetchedAt = _c.Now;
        // The manifest's download_size is the whole game. For an installed game with an update pending the real download is the
        // delta, which only Legendary's analysis knows (measured below); until then it is unknown (null), never the full size.
        var installedVersion = _c.Records.Current?.Version;
        var updating = installedVersion is not null && info.Version.Length > 0 && info.Version != installedVersion;
        _c.Hub.Mutate(s => s with
        {
            LatestVersion = string.IsNullOrEmpty(info.Version) ? s.LatestVersion : info.Version,
            DownloadSizeBytes = updating ? DeltaFor(installedVersion!, info.Version) : info.DownloadSizeBytes > 0 ? info.DownloadSizeBytes : s.DownloadSizeBytes,
            InstallSizeBytes = info.InstallSizeBytes > 0 ? info.InstallSizeBytes : s.InstallSizeBytes,
            LastUpdateCheck = _c.Now,
        });
        if (updating)
        {
            MeasureUpdateInBackground(installedVersion!, info.Version);
        }

        await RefreshArtAsync(info, ct);
        return info;
    }

    private (string Installed, string Latest)? _deltaKey;
    private long? _delta;

    /// <summary>Completes when the background update-size measurement (if any) finished (tests).</summary>
    internal Task UpdateSizeTask { get; private set; } = Task.CompletedTask;

    private long? DeltaFor(string installed, string latest) => _deltaKey == (installed, latest) ? _delta : null;

    /// <summary>
    /// Legendary's dry-run analysis of the pending update, once per (installed, latest) pair. Off the check's path: it needs
    /// Legendary's exclusive lock, so it must never hold CheckingForUpdates (or block) while a job runs.
    /// </summary>
    private void MeasureUpdateInBackground(string installed, string latest)
    {
        if (_deltaKey == (installed, latest))
        {
            return;
        }

        _deltaKey = (installed, latest);
        _delta = null;
        UpdateSizeTask = Task.Run(async () =>
        {
            try
            {
                if (_c.Hub.ActiveJobs().Count > 0)
                {
                    _deltaKey = null; // try again at the next check
                    return;
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(_c.Shutdown);
                cts.CancelAfter(TimeSpan.FromMinutes(10));
                var sizes = await _c.Legendary.GetUpdateSizesAsync(ServiceContext.App, cts.Token);
                if (sizes is null || _deltaKey != (installed, latest))
                {
                    return;
                }

                _delta = sizes.DownloadBytes;
                _c.Hub.Mutate(s => _c.Records.Current?.Version == installed ? s with { DownloadSizeBytes = sizes.DownloadBytes } : s);
            }
            catch (Exception e) when (e is not OperationCanceledException || !_c.Shutdown.IsCancellationRequested)
            {
                _c.Log.Warn("update", "couldn't measure the update's download size", e);
            }
        });
    }

    private async Task RefreshArtAsync(LegendaryGameInfo info, CancellationToken ct)
    {
        var key = string.Join('|', info.KeyImages.Select(k => k.Url));
        if (key.Length == 0 || key == _artFor)
        {
            return;
        }

        try
        {
            var art = await _c.Art.RefreshAsync(info.KeyImages, ct);
            if (art is not null)
            {
                _artFor = key;
                _c.Hub.Mutate(s => s with { Art = art });
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _c.Log.Warn("art", "key art refresh failed", e);
        }
    }

    /// <summary>Checks Epic for a newer build. User-initiated checks surface errors; background ones log them.</summary>
    public async Task<UpdateCheckResult> CheckAsync(bool userInitiated, CancellationToken ct)
    {
        if (_c.Hub.State.Auth.Status != AuthStatus.SignedIn)
        {
            if (userInitiated)
            {
                await _c.RequireSignedInAsync(ct);
            }

            return Result();
        }

        _c.Hub.Mutate(s => s with { CheckingForUpdates = true });
        try
        {
            await ReconcileInstalledVersionAsync(ct);
            await FetchAsync(ct);
        }
        catch (Exception e) when (!userInitiated && e is not OperationCanceledException)
        {
            _c.Log.Warn("update", "background update check failed", e);
            return Result();
        }
        catch (Exception e) when (e is not GliderException and not OperationCanceledException)
        {
            throw new GliderException(Errors.Network(e.Message), e);
        }
        finally
        {
            _c.Hub.Mutate(s => s with { CheckingForUpdates = false });
        }

        // Before pre-warm finishes, persisted jobs aren't restored yet; pre-warm evaluates once at the end.
        if (_c.Hub.State.Warm)
        {
            Evaluate();
        }

        return Result();
    }

    /// <summary>
    /// The installed side of the comparison: Legendary's installed.json is the source of truth, our record is a copy that only
    /// jobs and the startup drift check refresh. Re-read it so a build installed outside a Glider job (EGL, a manual
    /// `legendary update`) neither shows a phantom update nor hides a real one. Skipped while a job is writing the install.
    /// </summary>
    private async Task ReconcileInstalledVersionAsync(CancellationToken ct)
    {
        if (_c.Records.Current is not { } rec || _c.Hub.ActiveJobs().Count > 0)
        {
            return;
        }

        try
        {
            var installed = await _c.Legendary.GetInstalledAsync(ServiceContext.App, ct);
            if (installed is { Version.Length: > 0 } && !string.Equals(installed.Version, rec.Version, StringComparison.Ordinal))
            {
                _c.Log.Info("update", $"installed version changed outside Glider: {rec.Version} -> {installed.Version}");
                await _c.SyncRecordAsync(ct);
                _c.Sizes.Request(filesChanged: false);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _c.Log.Warn("update", "couldn't re-read the installed version", e);
        }
    }

    private UpdateCheckResult Result()
    {
        var g = _c.Hub.BuildGame();
        return new UpdateCheckResult(g.UpdateAvailable, g.Installed?.Version, g.LatestVersion);
    }

    /// <summary>Badge is implicit (snapshot.UpdateAvailable). Notifies once per new version; then applies the auto-update policy.</summary>
    public void Evaluate()
    {
        var game = _c.Hub.BuildGame();
        if (!game.UpdateAvailable || game.LatestVersion is null)
        {
            return;
        }

        if (_notifiedVersion != game.LatestVersion)
        {
            _notifiedVersion = game.LatestVersion;
            _c.Notify(NoticeLevel.Info, "Fortnite update available",
                _c.Settings.AutoUpdate ? "Glider will update it when you're not playing." : "Update now to keep playing.");
        }

        TryAutoUpdate();
    }

    /// <summary>Enqueue an update if the policy allows. Never while the game runs (the job would be refused anyway).</summary>
    public bool TryAutoUpdate()
    {
        var s = _c.Settings;
        if (!s.AutoUpdate || !_c.Hub.BuildGame().UpdateAvailable || _c.Hub.ActiveJobs().Count > 0 || _c.IsGameRunning())
        {
            return false;
        }

        if (s.AutoUpdateOnlyWhenIdle && _c.Options.IdleSignal is { } idle && idle.IdleTime < _c.Options.IdleThreshold)
        {
            return false;
        }

        var rec = _c.Records.Current!;
        var job = _c.Jobs.Enqueue(new JobRequest(JobKind.Update, null, null, rec.Version, _c.Hub.State.LatestVersion));
        _c.Log.Info("update", $"auto-update queued {rec.Version} -> {_c.Hub.State.LatestVersion} ({job.Id})");
        return true;
    }

    /// <summary>Periodic checks every Settings.UpdateCheckIntervalMinutes until shutdown.</summary>
    public async Task RunPeriodicAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var minutes = Math.Clamp(_c.Settings.UpdateCheckIntervalMinutes, 5, 24 * 60);
            await Task.Delay(TimeSpan.FromMinutes(minutes), _c.Time, ct);
            try
            {
                var egl = await _c.Egl.ScanAsync(ct);
                _c.Hub.Mutate(st => st with { Egl = egl });
                await CheckAsync(userInitiated: false, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _c.Log.Warn("update", "periodic check failed", e);
            }
        }
    }

    public bool CheckedRecentlyWithoutUpdate()
    {
        var s = _c.Hub.State;
        return s.LastUpdateCheck is { } t && _c.Now - t < _c.Options.RecentUpdateCheck && !_c.Hub.BuildGame().UpdateAvailable;
    }
}
