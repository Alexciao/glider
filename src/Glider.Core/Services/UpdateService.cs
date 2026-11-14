using Glider.Core.Contracts;
using Glider.Core.Jobs;

namespace Glider.Core.Services;

/// <summary>
/// Auto-update policy per game (GameSettings.AutoUpdate, Default = Settings.AutoUpdate): Always queues the update when the
/// game isn't running (and, with AutoUpdateOnlyWhenIdle, the user is idle); Ask notifies once per version; Never only shows
/// the badge. Update detection itself is the library's (assets.json vs installed.json, L§10).
/// </summary>
internal sealed class UpdateService
{
    private readonly ServiceContext _c;
    private readonly Dictionary<string, string> _notified = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public UpdateService(ServiceContext context) => _c = context;

    public AutoUpdatePolicy PolicyFor(string app)
    {
        var p = _c.Games.Get(app).Settings.AutoUpdate;
        return p == AutoUpdatePolicy.Default ? _c.Settings.AutoUpdate : p;
    }

    /// <summary>Notify once per new version and apply each game's policy. Only once pre-warm restored the queue.</summary>
    public void Evaluate()
    {
        if (!_c.Hub.State.Warm)
        {
            return;
        }

        foreach (var g in _c.Library.Games.Values.Where(g => g.UpdateAvailable && g.LatestVersion is not null).ToList())
        {
            var policy = PolicyFor(g.AppName);
            bool notify;
            lock (_gate)
            {
                notify = !_notified.TryGetValue(g.AppName, out var v) || v != g.LatestVersion;
                if (notify)
                {
                    _notified[g.AppName] = g.LatestVersion!;
                }
            }

            if (notify && policy != AutoUpdatePolicy.Never)
            {
                _c.Notify(NoticeLevel.Info, $"{g.Title} update available",
                    policy == AutoUpdatePolicy.Always ? "Glider will update it when you're not playing." : "Update now to keep playing.",
                    appName: g.AppName);
            }

            if (policy == AutoUpdatePolicy.Always)
            {
                TryAutoUpdate(g.AppName);
            }
        }
    }

    /// <summary>Enqueue an update if the policy allows. Never while that game runs (the job would wait anyway).</summary>
    public bool TryAutoUpdate(string app)
    {
        var g = _c.Library.Summary(app);
        if (g is not { UpdateAvailable: true } || PolicyFor(app) != AutoUpdatePolicy.Always || _c.ActiveJob(app) is not null || _c.IsGameRunning(app))
        {
            return false;
        }

        if (_c.Settings.AutoUpdateOnlyWhenIdle && _c.Options.IdleSignal is { } idle && idle.IdleTime < _c.Options.IdleThreshold)
        {
            return false;
        }

        if (_c.Hub.State.Auth.Status != AuthStatus.SignedIn)
        {
            return false;
        }

        var job = _c.Jobs.Enqueue(new JobRequest(app, JobKind.Update, null, null, g.InstalledVersion, g.LatestVersion, g.Title,
            g.BaseAppName is { } b ? new JobOptions(BaseAppName: b) : null));
        _c.Log.Info("update", $"auto-update queued for {app}: {g.InstalledVersion} -> {g.LatestVersion} ({job.Id})");
        return true;
    }

    /// <summary>library.updateAll: every installed game with an update, except those set to Never.</summary>
    public async Task<UpdateAllResult> UpdateAllAsync(CancellationToken ct)
    {
        await _c.RequireSignedInAsync(ct);
        var jobs = new List<JobInfo>();
        foreach (var g in _c.Library.Games.Values.Where(g => g.UpdateAvailable && g.InstallStatus is InstallStatus.Installed).OrderBy(g => g.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            if (PolicyFor(g.AppName) == AutoUpdatePolicy.Never)
            {
                continue;
            }

            if (_c.ActiveJob(g.AppName) is { } existing)
            {
                if (existing.Kind == JobKind.Update)
                {
                    jobs.Add(existing);
                }

                continue;
            }

            jobs.Add(_c.Jobs.Enqueue(new JobRequest(g.AppName, JobKind.Update, null, null, g.InstalledVersion, g.LatestVersion, g.Title,
                g.BaseAppName is { } b ? new JobOptions(BaseAppName: b) : null)));
        }

        return new UpdateAllResult(jobs);
    }

    /// <summary>
    /// Periodic background work until shutdown: every Settings.UpdateCheckIntervalMinutes an EGL rescan and an update check;
    /// a full library sync when the last one is older than <see cref="GliderServiceOptions.LibrarySyncInterval"/>.
    /// </summary>
    public async Task RunPeriodicAsync(Func<CancellationToken, Task> rescanEgl, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var minutes = Math.Clamp(_c.Settings.UpdateCheckIntervalMinutes, 5, 24 * 60);
            await Task.Delay(TimeSpan.FromMinutes(minutes), _c.Time, ct);
            try
            {
                await rescanEgl(ct);
                var full = _c.Hub.State.LastSyncedAt is not { } last || _c.Now - last >= _c.Options.LibrarySyncInterval;
                await _c.Library.SyncAsync(userInitiated: false, full, ct);
                Evaluate();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _c.Log.Warn("update", "periodic check failed", e);
            }
        }
    }
}

/// <summary>
/// The writer lane's view of the rest of the app: Settings.WhilePlaying (Pause = hold the queue while any game runs;
/// Throttle = effective mode Background; Continue = nothing) and the idle-full-speed rule. Only the policy lives here; the
/// per-mode process priorities are the <see cref="IResourcePolicy"/> seam.
/// </summary>
internal sealed class QueuePolicy : IQueuePolicy
{
    private readonly ServiceContext _c;

    public QueuePolicy(ServiceContext context) => _c = context;

    private RunningGame? Playing => _c.Hub.State.Sessions.Values
        .Where(s => s.State is RunState.Launching or RunState.Running or RunState.Closing or RunState.Preparing or RunState.InstallingPrerequisites)
        .OrderBy(s => s.Since ?? DateTimeOffset.MaxValue)
        .FirstOrDefault();

    public string? HoldReason =>
        _c.Settings.WhilePlaying == WhilePlayingPolicy.Pause && Playing is { } p ? $"Paused while {p.Title} is running" : null;

    public DownloadMode EffectiveMode => Effective().Mode;

    public string? ModeReason => Effective().Reason;

    private (DownloadMode Mode, string? Reason) Effective()
    {
        var s = _c.Settings;
        if (Playing is { } p)
        {
            return s.WhilePlaying switch
            {
                WhilePlayingPolicy.Pause => (s.DownloadMode, $"Paused while {p.Title} is running"),
                WhilePlayingPolicy.Throttle when s.DownloadMode != DownloadMode.Background => (DownloadMode.Background, $"Background mode while {p.Title} is running"),
                _ => (s.DownloadMode, null),
            };
        }

        if (s.IdleFullSpeed && s.DownloadMode != DownloadMode.Full && _c.Options.IdleSignal is { } idle && idle.IdleTime >= _c.Options.IdleFullSpeedAfter)
        {
            return (DownloadMode.Full, "Full speed while the PC is idle");
        }

        return (s.DownloadMode, null);
    }

    public QueueState State(IJobQueue jobs)
    {
        var all = jobs.Snapshot();
        var unfinished = all.Where(StateHub.IsActive).ToList();
        var finished = all.Where(j => !StateHub.IsActive(j)).Take(20);
        var (mode, reason) = Effective();
        return new QueueState([.. unfinished, .. finished], jobs.Paused, mode, reason);
    }
}
