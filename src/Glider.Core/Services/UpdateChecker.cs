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
        _c.Hub.Mutate(s => s with
        {
            LatestVersion = string.IsNullOrEmpty(info.Version) ? s.LatestVersion : info.Version,
            DownloadSizeBytes = info.DownloadSizeBytes > 0 ? info.DownloadSizeBytes : s.DownloadSizeBytes,
            InstallSizeBytes = info.InstallSizeBytes > 0 ? info.InstallSizeBytes : s.InstallSizeBytes,
            LastUpdateCheck = _c.Now,
        });
        await RefreshArtAsync(info, ct);
        return info;
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
