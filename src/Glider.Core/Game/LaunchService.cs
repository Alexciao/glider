using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Game;

/// <summary>
/// The launch lane (docs/v0.2-design.md §3): Preparing → (InstallingPrerequisites) → Launching → Running → Closing → Idle,
/// per game. `launch &lt;app&gt; --json --skip-version-check [--offline]` never waits behind a download (it never takes
/// installed.json.lock and jumps the auth gate); it is refused only when that game or its base game has an unfinished
/// writer-lane job. Glider spawns the exe from the JSON so it owns the process, then <see cref="SessionTracker"/> follows
/// the process tree. Playtime accumulates per game (flushed every 30 s and at exit) into games.json.
/// </summary>
internal sealed class LaunchService
{
    /// <summary>
    /// How long a prepared `launch --json` result may wait for the click. It carries a single-use Epic exchange code that
    /// Epic issues with a 300 s lifetime (`expiresInSeconds`); the game spends a further 10-60 s booting before it redeems
    /// it, so we stay well inside that window.
    /// </summary>
    internal static readonly TimeSpan PreparedTtl = TimeSpan.FromSeconds(90);

    /// <summary>A prepared command younger than this is kept on a repeated hover instead of being fetched again.</summary>
    private static readonly TimeSpan PreparedRefreshAfter = TimeSpan.FromSeconds(60);

    /// <summary>How often a running session is flushed to disk, so a crash or power cut loses at most this much.</summary>
    internal static readonly TimeSpan PlaytimeFlushInterval = TimeSpan.FromSeconds(30);

    private readonly ServiceContext _c;
    private readonly EacService _eac;
    private readonly Action<string> _sessionEnded;
    private readonly Lock _prepGate = new();
    private readonly Lock _gate = new();
    private readonly HashSet<string> _launching = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (PlaytimeTracker Tracker, CancellationTokenSource Loop)> _playtime = new(StringComparer.Ordinal);
    private PreparedLaunch? _prepared;
    private Task _preparing = Task.CompletedTask;
    private bool _hidByUs;
    private Task _watchLoop = Task.CompletedTask;
    private Task _playtimeFlush = Task.CompletedTask;

    public LaunchService(ServiceContext context, EacService eac, Action<string> sessionEnded)
    {
        _c = context;
        _eac = eac;
        _sessionEnded = sessionEnded;
        Sessions = new SessionTracker(context.Options.ProcessMonitor, context.Time, context.Options.LaunchTimeout, context.Options.SessionEndGrace);
        Sessions.Changed += OnSessionChanged;
    }

    internal SessionTracker Sessions { get; }

    /// <summary>Cloud saves: synced before the game starts and after it closes (set by GliderService).</summary>
    internal Saves.SaveService? Saves { get; set; }

    /// <summary>Third-party store games (set by GliderService). Null = the v0.2 hand-off is unavailable.</summary>
    internal ThirdPartyService? ThirdParty { get; set; }

    /// <summary>Runs a game's pending prerequisites before its first start (set by GliderService).</summary>
    internal PrerequisiteService? Prerequisites { get; set; }

    /// <summary>Completes when the current prepare fetch ends (tests).</summary>
    internal Task Preparing
    {
        get
        {
            lock (_prepGate)
            {
                return _preparing;
            }
        }
    }

    /// <summary>Completes when the current watch loop ends (tests).</summary>
    internal Task WatchLoop => _watchLoop;

    /// <summary>Completes when the latest playtime write ends (tests).</summary>
    internal Task PlaytimeFlushed => _playtimeFlush;

    /// <summary>What a prepared command must still match when the user clicks Play.</summary>
    private sealed record LaunchKey(string AppName, string InstallPath, string Version, string LaunchArgs, string? AccountId, bool Offline);

    private sealed record PreparedLaunch(LaunchKey Key, LaunchCommand Command, DateTimeOffset At);

    // ------------------------------------------------------------------ prepare (hover)

    /// <summary>
    /// The UI signals intent (Play hovered/focused): resolve `launch --json` now, in the background, so the click spawns
    /// the game without waiting ~1.5-3 s for Legendary. Best effort and idempotent; never throws, never blocks.
    /// </summary>
    public void Prepare(string app)
    {
        if (Key(app, null, requireSignedIn: true) is not { } key || CanLaunch(app, key.InstallPath) is not null)
        {
            return;
        }

        lock (_prepGate)
        {
            if (!_preparing.IsCompleted || (_prepared is { } p && p.Key == key && _c.Now - p.At < PreparedRefreshAfter))
            {
                return;
            }

            _prepared = null;
            var ct = _c.Shutdown;
            _preparing = Task.Run(async () =>
            {
                var started = _c.Time.GetTimestamp();
                try
                {
                    var cmd = await _c.Legendary.GetLaunchCommandAsync(app, skipVersionCheck: true, key.LaunchArgs, key.Offline, ct);
                    lock (_prepGate)
                    {
                        _prepared = new PreparedLaunch(key, cmd, _c.Now);
                    }

                    _c.Log.Info("launch", $"{app}: launch command prepared in {_c.Time.GetElapsedTime(started).TotalMilliseconds:0} ms");
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    // Launch fetches again and surfaces the real error; the message never carries the exchange code.
                    _c.Log.Info("launch", $"{app}: launch prepare failed: {(e is GliderException g ? g.Error.Code.ToString() : e.GetType().Name)}");
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>Drops a prepared command (sign-out, settings or install changes are also caught by the key check).</summary>
    public void InvalidatePrepared()
    {
        lock (_prepGate)
        {
            _prepared = null;
        }
    }

    private LaunchKey? Key(string app, bool? offline, bool requireSignedIn)
    {
        var summary = _c.Library.Summary(app);
        var auth = _c.Hub.State.Auth;
        if (summary?.InstallPath is not { } path || summary.InstalledVersion is not { } version)
        {
            return null;
        }

        var off = offline ?? DecideOffline(summary, null);
        if (requireSignedIn && !off && auth.Status != AuthStatus.SignedIn)
        {
            return null;
        }

        return new LaunchKey(app, path, version, _c.Games.Get(app).Settings.LaunchArgs ?? "", auth.Account?.AccountId, off);
    }

    /// <summary>Offline when asked, or (Auto) when Epic is unreachable and the game may run offline.</summary>
    private bool DecideOffline(GameSummary summary, LaunchMode? requested)
    {
        var mode = requested ?? _c.Games.Get(summary.AppName).Settings.LaunchMode;
        return mode switch
        {
            LaunchMode.Offline => true,
            LaunchMode.Online => false,
            _ => _c.Hub.State.Connectivity == Connectivity.Offline && summary.CanRunOffline,
        };
    }

    /// <summary>Null when the game can launch now; otherwise why not (never because another game downloads).</summary>
    private GliderError? CanLaunch(string app, string installPath)
    {
        var summary = _c.Library.Summary(app);
        var title = summary?.Title;
        if (summary is null || summary.InstallStatus is not (InstallStatus.Installed or InstallStatus.Broken) || summary.InstallPath is null)
        {
            return Errors.NotInstalled(title, app);
        }

        if (_c.Hub.State.RunStateOf(app) != RunState.Idle || Sessions.IsActive(app))
        {
            return Errors.GameRunning(title, app);
        }

        // Only this game's (or its base game's) writer jobs block it: half-patched files must not run.
        var blocking = _c.Hub.ActiveJobs(app) is [var own, ..] ? own
            : summary.BaseAppName is { } b && _c.Hub.ActiveJobs(b) is [var baseJob, ..] ? baseJob
            : null;
        if (blocking is not null)
        {
            return Errors.GameBusy(InstallService.JobNoun(blocking.Kind), title, app);
        }

        if (_c.Egl.IsBusyWith(installPath))
        {
            return Errors.EglBusy();
        }

        return null;
    }

    /// <summary>The prepared command if it is still valid for this exact launch (single use), else a fresh `launch --json`.</summary>
    private async Task<(LaunchCommand Command, bool Prepared)> TakeLaunchCommandAsync(LaunchKey key, CancellationToken ct)
    {
        Task inflight;
        lock (_prepGate)
        {
            inflight = _preparing;
        }

        await inflight.WaitAsync(ct); // a hover just started it: joining beats a second spawn

        PreparedLaunch? p;
        lock (_prepGate)
        {
            p = _prepared;
            _prepared = null; // exchange codes are single use
        }

        if (p is not null && p.Key == key && _c.Now - p.At < PreparedTtl)
        {
            return (p.Command, true);
        }

        return (await _c.Legendary.GetLaunchCommandAsync(key.AppName, skipVersionCheck: true, key.LaunchArgs, key.Offline, ct), false);
    }

    // ------------------------------------------------------------------ launch

    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken ct)
    {
        var app = request.AppName;
        var summary = _c.Library.Summary(app) ?? throw new GliderException(Errors.NotFound(app));
        if (summary.Kind == GameKind.Dlc)
        {
            throw new GliderException(Errors.InvalidRequest($"{summary.Title} is DLC; launch its base game instead."));
        }

        if (ThirdPartyService.IsManaged(summary))
        {
            // Another store installs and starts it: hand over to that launcher (Untracked) instead of spawning anything.
            return ThirdParty is { } thirdParty
                ? await thirdParty.LaunchManagedAsync(summary, request, ct)
                : throw new GliderException(Errors.ThirdPartyRequired(summary.ThirdPartyName ?? "its own launcher", summary.Title, app));
        }

        if (DetectExisting(app))
        {
            throw new GliderException(Errors.GameRunning(summary.Title, app));
        }

        if (CanLaunch(app, summary.InstallPath ?? "") is { } refusal)
        {
            throw new GliderException(refusal);
        }

        var installPath = summary.InstallPath!;
        var executable = _c.Library.Installed(app)?.Executable;
        if (!GameProfiles.LooksLikeInstall(app, installPath, executable))
        {
            var probe = GameProfiles.For(app).LivenessExeRelative ?? executable ?? "the game files";
            throw new GliderException(Errors.Corrupted($"{probe} is missing") with { AppName = app });
        }

        var offline = DecideOffline(summary, request.Mode);
        if (!offline)
        {
            await _c.RequireSignedInAsync(ct);
        }

        if (ThirdParty is { } linked && await linked.ActivationGateAsync(summary, offline, ct) is { } needsActivation)
        {
            return needsActivation; // Ubisoft must know the Epic account owns the game first
        }

        lock (_gate)
        {
            if (!_launching.Add(app))
            {
                throw new GliderException(Errors.GameRunning(summary.Title, app));
            }
        }

        var clicked = _c.Time.GetTimestamp();
        try
        {
            SetRun(app, summary.Title, RunState.Preparing, null, offline);
            if (!request.SkipSaveSync && Saves is { } saves && saves.IsEnabled(app))
            {
                // Download a newer cloud save / upload what changed; a conflict stops here (nothing is launched, nothing is overwritten).
                SetRun(app, summary.Title, RunState.SyncingSaves, null, offline);
                var sync = await saves.PreLaunchAsync(app, offline, ct);
                if (sync.Conflict is { } conflict)
                {
                    SetRun(app, summary.Title, RunState.Idle, null, offline);
                    return new LaunchResult(LaunchOutcome.SaveConflict, conflict, offline, sync.Message);
                }

                SetRun(app, summary.Title, RunState.Preparing, null, offline);
            }

            if (Prerequisites is { } prerequisites)
            {
                await prerequisites.EnsureAsync(app, () => SetRun(app, summary.Title, RunState.InstallingPrerequisites, null, offline), ct);
            }

            var ranEac = await _eac.EnsureInstalledAsync(app, installPath, () => SetRun(app, summary.Title, RunState.InstallingPrerequisites, null, offline), ct);

            SetRun(app, summary.Title, RunState.Launching, null, offline);
            var key = Key(app, offline, requireSignedIn: false)!;
            var (cmd, prepared) = await TakeLaunchCommandAsync(key, ct);
            Sessions.Begin(app, GameProfiles.For(app));
            RunningProcess process;
            try
            {
                process = _c.Options.ProcessStarter.Start(cmd);
            }
            catch (Exception e) when (e is not GliderException)
            {
                Sessions.Abort(app);
                throw new GliderException(Errors.LaunchFailed(e.Message, summary.Title, app), e);
            }

            Sessions.Spawned(app, process);
            _c.Log.Info("launch", $"{app}: started {Path.GetFileName(cmd.Executable)} (pid {process.Id}) {_c.Time.GetElapsedTime(clicked).TotalMilliseconds:0} ms after Play (prepared={prepared}, offline={offline}, eacInstalledNow={ranEac})");
            StartWatching();
            return new LaunchResult(LaunchOutcome.Started, null, offline, ThirdParty?.LauncherNote(summary));
        }
        catch (Exception e)
        {
            Sessions.Abort(app);
            SetRun(app, summary.Title, RunState.Idle, null, offline);
            if (e is GliderException or OperationCanceledException)
            {
                throw;
            }

            throw new GliderException(Errors.LaunchFailed(e.Message, summary.Title, app), e);
        }
        finally
        {
            lock (_gate)
            {
                _launching.Remove(app);
            }
        }
    }

    /// <summary>Closes the game: every tracked process of the session plus the profile's named processes. The tracker sees the exit.</summary>
    public Task CloseGameAsync(string app) => Task.Run(() =>
    {
        var monitor = _c.Options.ProcessMonitor;
        var names = GameProfiles.For(app).AllTrackedNames.ToList();
        var snapshot = monitor.Snapshot();
        foreach (var pid in Sessions.TrackedPids(app))
        {
            if (snapshot.FirstOrDefault(p => p.Id == pid) is { } entry)
            {
                monitor.Close(new RunningProcess(entry.Id, entry.Name, entry.StartTime));
            }
        }

        foreach (var name in names)
        {
            foreach (var p in monitor.Find(name))
            {
                monitor.Close(p);
            }
        }
    });

    /// <summary>Startup / pre-launch: adopt a game that is already running (games whose profile names their process).</summary>
    public bool DetectExisting(string app)
    {
        if (Sessions.IsActive(app) || !Sessions.DetectExisting(app, GameProfiles.For(app)))
        {
            return false;
        }

        StartWatching();
        return true;
    }

    /// <summary>Startup: every installed game with a profile liveness process.</summary>
    public void DetectAllExisting()
    {
        foreach (var g in _c.Library.Games.Values)
        {
            if (g.InstallPath is not null && GameProfiles.For(g.AppName).LivenessProcessName is not null)
            {
                DetectExisting(g.AppName);
            }
        }
    }

    private void StartWatching()
    {
        lock (_gate)
        {
            if (!_watchLoop.IsCompleted)
            {
                return;
            }

            _watchLoop = Task.Run(async () =>
            {
                try
                {
                    await Sessions.RunAsync(_c.Options.ProcessPollInterval, _c.Shutdown);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    _c.Log.Error("launch", "process watch failed", e);
                }
            });
        }
    }

    private void OnSessionChanged(string app, RunState state, DateTimeOffset? since, bool failed)
    {
        var title = _c.TitleOf(app);
        var previous = _c.Hub.State.RunStateOf(app);
        SetRun(app, title, state, since, _c.Hub.State.Sessions.TryGetValue(app, out var s) && s.Offline);
        TrackPlaytime(app, state, since);
        if (state == RunState.Running && previous is RunState.Launching && _c.Settings.MinimizeToTrayWhilePlaying)
        {
            _hidByUs = true;
            _c.Host.HideToTray();
        }

        if (state != RunState.Idle)
        {
            _c.Jobs.Reevaluate(); // WhilePlaying: hold the writer lane while a game runs
            return;
        }

        if (_hidByUs && !_c.Hub.State.AnySessionActive)
        {
            _hidByUs = false;
            _c.Host.RestoreWindow();
        }

        if (failed)
        {
            var e = Errors.LaunchFailed("The game process never appeared", title, app);
            _c.Log.Warn("launch", $"{app}: {e.Detail}");
            _c.Notify(NoticeLevel.Error, e.Title, e.Message, e.Fix, appName: app);
        }
        else if (previous is RunState.Running or RunState.Closing)
        {
            _c.Log.Info("launch", $"{app}: game closed");
            Saves?.AfterExit(app); // upload what the session changed (retries; never deletes anything)
        }

        _c.Jobs.Reevaluate();
        _sessionEnded(app);
    }

    private void TrackPlaytime(string app, RunState state, DateTimeOffset? since)
    {
        lock (_gate)
        {
            if (state == RunState.Running && !_playtime.ContainsKey(app))
            {
                var tracker = new PlaytimeTracker();
                tracker.Start(since ?? _c.Now);
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_c.Shutdown);
                _playtime[app] = (tracker, cts);
                _ = QueueFlush(app, tracker, final: false);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (true)
                        {
                            await Task.Delay(PlaytimeFlushInterval, _c.Time, cts.Token);
                            await QueueFlush(app, tracker, final: false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                });
            }
            else if (state == RunState.Idle && _playtime.Remove(app, out var entry))
            {
                entry.Loop.Cancel();
                _ = QueueFlush(app, entry.Tracker, final: true);
            }
        }
    }

    /// <summary>Playtime writes run one after another so a periodic flush can never overlap the final one.</summary>
    private Task QueueFlush(string app, PlaytimeTracker tracker, bool final)
    {
        lock (_gate)
        {
            return _playtimeFlush = _playtimeFlush.ContinueWith(_ => FlushPlaytimeAsync(app, tracker, final), TaskScheduler.Default).Unwrap();
        }
    }

    private async Task FlushPlaytimeAsync(string app, PlaytimeTracker tracker, bool final)
    {
        try
        {
            if (!final && !tracker.Active)
            {
                return;
            }

            var now = _c.Now;
            var seconds = tracker.Take(now, final);
            await _c.Games.UpdateAsync(app, e => PlaytimeTracker.Apply(e, seconds, now));
            _c.Library.Invalidate();
        }
        catch (Exception e)
        {
            _c.Log.Warn("launch", $"could not save playtime of {app}", e);
        }
    }

    private void SetRun(string app, string title, RunState state, DateTimeOffset? since, bool offline) =>
        _c.Hub.Mutate(s =>
        {
            if (state == RunState.Idle)
            {
                return s.Sessions.ContainsKey(app) ? s with { Sessions = s.Sessions.Remove(app) } : s;
            }

            var old = s.Sessions.GetValueOrDefault(app);
            var runningSince = state == RunState.Running ? since ?? old?.Since ?? _c.Now : (DateTimeOffset?)null;
            var next = new RunningGame(app, title, state, runningSince, offline, false);
            return old == next ? s : s with { Sessions = s.Sessions.SetItem(app, next) };
        });
}
