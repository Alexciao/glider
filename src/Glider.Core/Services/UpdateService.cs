using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;
using Glider.Core.Jobs;

namespace Glider.Core.Services;

/// <summary>
/// Updates (docs/v0.2-design.md §2). Detection is the library's (assets.json build vs installed.json version, refreshed by
/// `list-installed --check-updates` on the metadata lane every <see cref="Settings.UpdateCheckIntervalMinutes"/>). Policy per game
/// (<see cref="GameSettings.AutoUpdate"/>, Default = <see cref="Settings.AutoUpdate"/>):
/// Always queues the update (never while the game runs or while Epic is unreachable; with AutoUpdateOnlyWhenIdle only when the user
/// is idle, retried every <see cref="GliderServiceOptions.AutoUpdateRetryInterval"/>, when a session ends and when Epic is back); Ask raises one notice per version with an
/// Update action plus a toast; Never only shows the badge (<see cref="LibraryStatus.UpdatesAvailable"/> counts it).
/// </summary>
internal sealed class UpdateService : IDisposable
{
    private const string Category = "update";

    public const int MinIntervalMinutes = 5;
    public const int MaxIntervalMinutes = 24 * 60;

    private readonly ServiceContext _c;
    private readonly Dictionary<string, string> _notified = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deferred = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly Lock _enqueueGate = new();
    private readonly ITimer _retryTimer;
    private readonly string _notifiedPath;
    private bool _notifiedLoaded;
    private bool _retryArmed;
    private bool _kicked;
    private bool _checking;
    private bool _disposed;
    private DateTimeOffset _lastCheck;
    private CancellationTokenSource _intervalChanged = new();
    private int _lastInterval;

    public UpdateService(ServiceContext context)
    {
        _c = context;
        _notifiedPath = Path.Combine(context.Paths.Cache, "update-notices.json");
        _retryTimer = context.Time.CreateTimer(_ => RetryDeferred(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _lastInterval = ClampMinutes(context.Settings.UpdateCheckIntervalMinutes);
        context.SettingsStore.Changed += OnSettingsChanged;
    }

    private static int ClampMinutes(int minutes) => Math.Clamp(minutes, MinIntervalMinutes, MaxIntervalMinutes);

    /// <summary>Apps whose auto-update is waiting for idle time, a closed game or a session (tests).</summary>
    internal IReadOnlyCollection<string> Deferred
    {
        get
        {
            lock (_gate)
            {
                return [.. _deferred];
            }
        }
    }

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

        LoadNotified();
        var changed = false;
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
                    changed = true;
                }
            }

            if (notify && policy == AutoUpdatePolicy.Always)
            {
                _c.Notify(NoticeLevel.Info, $"{g.Title} update available", "Glider will update it when you're not playing.", appName: g.AppName);
            }
            else if (notify && policy == AutoUpdatePolicy.Ask)
            {
                // One notice (and one toast, when Glider isn't in front) per version, with the one-click action.
                _c.Notify(NoticeLevel.Info, $"{g.Title} update available", "Update now to keep playing.", FixAction.Update, appName: g.AppName);
            }

            if (policy == AutoUpdatePolicy.Always)
            {
                TryAutoUpdate(g.AppName);
            }
        }

        if (changed)
        {
            SaveNotified();
        }
    }

    /// <summary>
    /// Enqueue an update if the policy allows. While the game runs, the user is active (AutoUpdateOnlyWhenIdle), Epic isn't signed in
    /// or Epic is unreachable, the app is deferred and retried by a timer (and at once when a session ends).
    /// </summary>
    public bool TryAutoUpdate(string app)
    {
        // The periodic check, the library's update notification, the retry timer, a finished session and "Epic is back" all land here,
        // on different threads: look and enqueue as one step so an app is not queued twice.
        lock (_enqueueGate)
        {
            return TryAutoUpdateCore(app);
        }
    }

    private bool TryAutoUpdateCore(string app)
    {
        var g = _c.Library.Summary(app);
        if (g is not { UpdateAvailable: true } || PolicyFor(app) != AutoUpdatePolicy.Always || _c.ActiveJob(app) is not null)
        {
            Settle(app);
            return false;
        }

        if (!_c.Hub.State.Warm ||
            _c.Hub.State.Auth.Status != AuthStatus.SignedIn ||
            _c.Hub.State.Connectivity == Connectivity.Offline ||
            _c.IsGameRunning(app) ||
            (_c.Settings.AutoUpdateOnlyWhenIdle && _c.Options.IdleSignal is { } idle && idle.IdleTime < _c.Options.IdleThreshold))
        {
            Defer(app);
            return false;
        }

        Settle(app);
        var job = _c.Jobs.Enqueue(new JobRequest(app, JobKind.Update, null, null, g.InstalledVersion, g.LatestVersion, g.Title,
            g.BaseAppName is { } b ? new JobOptions(BaseAppName: b) : null));
        _c.Log.Info(Category, $"auto-update queued for {app}: {g.InstalledVersion} -> {g.LatestVersion} ({job.Id})");
        return true;
    }

    // ------------------------------------------------------------------ deferred auto-updates

    private void Defer(string app)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _deferred.Add(app);
            if (_retryArmed)
            {
                return;
            }

            _retryArmed = true;
        }

        _retryTimer.Change(_c.Options.AutoUpdateRetryInterval, Timeout.InfiniteTimeSpan);
    }

    private void Settle(string app)
    {
        lock (_gate)
        {
            _deferred.Remove(app);
        }
    }

    /// <summary>The user may have gone idle or closed the game: try everything that was waiting. Re-arms itself while any is left.</summary>
    internal void RetryDeferred()
    {
        string[] apps;
        lock (_gate)
        {
            _retryArmed = false;
            apps = [.. _deferred];
        }

        foreach (var app in apps)
        {
            TryAutoUpdate(app); // settles itself, or defers again (re-arming the timer)
        }
    }

    // ------------------------------------------------------------------ Epic is reachable again

    /// <summary>
    /// Offline became Online (<see cref="ConnectivityMonitor"/>): run the periodic check now instead of waiting out the interval
    /// (the loop skips its turns while offline), and try the auto-updates that were waiting for a connection. Never blocks.
    /// </summary>
    internal void OnCameOnline()
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_checking)
            {
                return; // a check is running right now (it is what noticed): it will Evaluate when it ends
            }

            _kicked = true;
            old = _intervalChanged;
            _intervalChanged = new CancellationTokenSource();
        }

        old.Cancel();
        old.Dispose();
        RetryDeferred();
    }

    // ------------------------------------------------------------------ "told you once" ledger

    private void LoadNotified()
    {
        lock (_gate)
        {
            if (_notifiedLoaded)
            {
                return;
            }

            _notifiedLoaded = true;
        }

        try
        {
            if (File.Exists(_notifiedPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(_notifiedPath));
                lock (_gate)
                {
                    foreach (var p in doc.RootElement.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } v)
                        {
                            _notified.TryAdd(p.Name, v);
                        }
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            _c.Log.Warn(Category, "cache/update-notices.json is unreadable; update notices may repeat once", e);
        }
    }

    /// <summary>A restart must not repeat the notice for a version the user already heard about.</summary>
    private void SaveNotified()
    {
        byte[] bytes;
        lock (_gate)
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                foreach (var (app, version) in _notified.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    w.WriteString(app, version);
                }

                w.WriteEndObject();
            }

            bytes = ms.ToArray();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await AtomicFile.WriteAllBytesAsync(_notifiedPath, bytes).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _c.Log.Warn(Category, "couldn't write cache/update-notices.json", e);
            }
        });
    }

    // ------------------------------------------------------------------ library.updateAll

    /// <summary>library.updateAll: every installed game with an update, except those set to Never and those whose last update failed (they have their own retry).</summary>
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

            // A failed update stays in the list with its own "Try again"; "Update all" doesn't queue it a second time behind its back.
            if (_c.Jobs.Snapshot().Any(j => j.AppName == g.AppName && j is { Kind: JobKind.Update, Status: JobStatus.Failed }))
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
            Settle(g.AppName);
        }

        return new UpdateAllResult(jobs);
    }

    // ------------------------------------------------------------------ periodic checks

    /// <summary>
    /// Background work until shutdown: every <see cref="Settings.UpdateCheckIntervalMinutes"/> (re-read when it changes, 5 min while
    /// offline) an EGL rescan and an update check (`list-installed --check-updates`, skipped when nothing is installed); a full library
    /// sync (`list -T --json`) instead when the last one is older than <see cref="GliderServiceOptions.LibrarySyncInterval"/>.
    /// </summary>
    public async Task RunPeriodicAsync(Func<CancellationToken, Task> rescanEgl, CancellationToken ct)
    {
        _lastCheck = _c.Now; // pre-warm has just checked
        while (!ct.IsCancellationRequested)
        {
            await WaitUntilDueAsync(ct);
            _lastCheck = _c.Now; // set before the attempt: a failing check waits a full interval, never a hot loop
            try
            {
                if (_c.Hub.State.Connectivity == Connectivity.Offline)
                {
                    // Nothing to ask Epic. The ConnectivityMonitor probes, and kicks this loop when Epic is back; the short wait
                    // only re-reads the state.
                    _lastCheck = _c.Now - TimeSpan.FromMinutes(_lastInterval) + TimeSpan.FromMinutes(MinIntervalMinutes);
                    continue;
                }

                lock (_gate)
                {
                    _checking = true;
                }

                await rescanEgl(ct);
                var full = _c.Hub.State.LastSyncedAt is not { } last || _c.Now - last >= _c.Options.LibrarySyncInterval;
                if (full || _c.Library.Games.Values.Any(g => g.InstallStatus is InstallStatus.Installed or InstallStatus.Broken))
                {
                    await _c.Library.SyncAsync(userInitiated: false, full, ct);
                }

                Evaluate();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _c.Log.Warn(Category, "periodic check failed", e);
            }
            finally
            {
                lock (_gate)
                {
                    _checking = false;
                }
            }
        }
    }

    private async Task WaitUntilDueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var remaining = _lastCheck + TimeSpan.FromMinutes(ClampMinutes(_c.Settings.UpdateCheckIntervalMinutes)) - _c.Now;
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            CancellationTokenSource changed;
            lock (_gate)
            {
                if (_kicked)
                {
                    _kicked = false;
                    return; // Epic is reachable again: check now
                }

                changed = _intervalChanged;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, changed.Token);
            try
            {
                await Task.Delay(remaining, _c.Time, linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The interval setting changed: work out the remaining time again.
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    private void OnSettingsChanged(Settings s)
    {
        CancellationTokenSource old;
        lock (_gate)
        {
            var minutes = ClampMinutes(s.UpdateCheckIntervalMinutes);
            if (_disposed || minutes == _lastInterval)
            {
                return;
            }

            _lastInterval = minutes;
            old = _intervalChanged;
            _intervalChanged = new CancellationTokenSource();
        }

        old.Cancel();
        old.Dispose();
    }

    public void Dispose()
    {
        CancellationTokenSource changed;
        lock (_gate)
        {
            _disposed = true;
            changed = _intervalChanged;
        }

        _c.SettingsStore.Changed -= OnSettingsChanged;
        _retryTimer.Dispose();
        changed.Cancel();
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

    /// <summary>What <see cref="HoldReason"/> says while Epic can't be reached; the UI shows it as is.</summary>
    internal const string OfflineHold = "Waiting for an internet connection";

    /// <summary>
    /// Why pausable jobs don't run: no connection (a download would only fail, and "Couldn't finish" would pile up), else a game
    /// is running with "While a game is running" = Pause.
    /// </summary>
    public string? HoldReason =>
        _c.Hub.State.Connectivity == Connectivity.Offline
            ? OfflineHold
            : _c.Settings.WhilePlaying == WhilePlayingPolicy.Pause && Playing is { } p ? $"Paused while {p.Title} is running" : null;

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
        return new QueueState([.. unfinished, .. finished], jobs.Paused, mode, reason, HoldReason);
    }
}
