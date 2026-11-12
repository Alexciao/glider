using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Game;

/// <summary>
/// Preparing → (InstallingPrerequisites) → Launching → Running → Closing → Idle.
/// Glider spawns the game from `launch --json` so it owns the process; the watcher follows the
/// launcher-stub → Shipping chain.
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

    private readonly ServiceContext _c;
    private readonly Lock _prepGate = new();
    private PreparedLaunch? _prepared;
    private Task _preparing = Task.CompletedTask;
    private readonly EacService _eac;
    private readonly UpdateChecker _updates;
    private readonly ProcessWatcher _watcher;
    private int _launching;
    private bool _hidByUs;
    private Task _watchLoop = Task.CompletedTask;

    public LaunchService(ServiceContext context, EacService eac, UpdateChecker updates)
    {
        _c = context;
        _eac = eac;
        _updates = updates;
        _watcher = new ProcessWatcher(context.Options.ProcessMonitor, context.Time, context.Options.LaunchTimeout);
        _watcher.Changed += OnWatcherChanged;
    }

    internal ProcessWatcher Watcher => _watcher;

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

    /// <summary>What a prepared command must still match when the user clicks Play.</summary>
    private sealed record LaunchKey(string InstallPath, string Version, string LaunchArgs, string? AccountId);

    private sealed record PreparedLaunch(LaunchKey Key, bool SkippedVersionCheck, LaunchCommand Command, DateTimeOffset At);

    /// <summary>
    /// The UI signals intent (Play hovered/focused): resolve `launch --json` now, in the background, so the click spawns
    /// the game without waiting ~1.5-3 s for Legendary. Best effort and idempotent; never throws, never blocks.
    /// </summary>
    public void Prepare()
    {
        if (Key(requireSignedIn: true) is not { } key || !CanLaunchNow(key.InstallPath))
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
            var skip = _updates.CheckedRecentlyWithoutUpdate();
            var ct = _c.Shutdown;
            _preparing = Task.Run(async () =>
            {
                var started = _c.Time.GetTimestamp();
                try
                {
                    var cmd = await _c.Legendary.GetLaunchCommandAsync(ServiceContext.App, skip, key.LaunchArgs, ct);
                    lock (_prepGate)
                    {
                        _prepared = new PreparedLaunch(key, skip, cmd, _c.Now);
                    }

                    _c.Log.Info("launch", $"launch command prepared in {_c.Time.GetElapsedTime(started).TotalMilliseconds:0} ms (skipVersionCheck={skip})");
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    // Launch fetches again and surfaces the real error; the message never carries the exchange code.
                    _c.Log.Info("launch", $"launch prepare failed: {(e is GliderException g ? g.Error.Code.ToString() : e.GetType().Name)}");
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

    private LaunchKey? Key(bool requireSignedIn)
    {
        var rec = _c.Records.Current;
        var auth = _c.Hub.State.Auth;
        if (rec is null || (requireSignedIn && auth.Status != AuthStatus.SignedIn))
        {
            return null;
        }

        return new LaunchKey(rec.InstallPath, rec.Version, _c.Settings.LaunchArgs ?? "", auth.Account?.AccountId);
    }

    private bool CanLaunchNow(string installPath) =>
        _c.Hub.State.RunState == RunState.Idle && !_watcher.IsActive && _c.Hub.FirstActiveJob() is null &&
        !_c.Hub.BuildGame().UpdateAvailable && !_c.Egl.IsBusyWith(installPath) && FortniteLayout.LooksLikeInstall(installPath);

    /// <summary>The prepared command if it is still valid for this exact launch (single use), else a fresh `launch --json`.</summary>
    private async Task<(LaunchCommand Command, bool Prepared)> TakeLaunchCommandAsync(bool skip, CancellationToken ct)
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

        var key = Key(requireSignedIn: false);
        if (p is not null && p.Key == key && _c.Now - p.At < PreparedTtl && (skip || !p.SkippedVersionCheck) && !_c.Hub.BuildGame().UpdateAvailable)
        {
            return (p.Command, true);
        }

        return (await _c.Legendary.GetLaunchCommandAsync(ServiceContext.App, skip, _c.Settings.LaunchArgs ?? "", ct), false);
    }

    /// <summary>Completes when the current watch loop ends (tests).</summary>
    internal Task WatchLoop => _watchLoop;

    public async Task<LaunchResult> LaunchAsync(CancellationToken ct)
    {
        var rec = _c.Records.Current ?? throw new GliderException(Errors.NotInstalled());
        if (_c.Hub.State.RunState != RunState.Idle)
        {
            throw new GliderException(Errors.GameRunning());
        }

        if (DetectExisting())
        {
            throw new GliderException(Errors.GameRunning());
        }

        if (_c.Hub.FirstActiveJob() is { } job)
        {
            throw new GliderException(Errors.Busy(InstallService.JobNoun(job.Kind)));
        }

        if (_c.Egl.IsBusyWith(rec.InstallPath))
        {
            throw new GliderException(Errors.EglBusy());
        }

        if (!FortniteLayout.LooksLikeInstall(rec.InstallPath))
        {
            throw new GliderException(Errors.Corrupted($"{FortniteLayout.ShippingExeRelative} is missing"));
        }

        await _c.RequireSignedInAsync(ct);
        if (Interlocked.Exchange(ref _launching, 1) == 1)
        {
            throw new GliderException(Errors.GameRunning());
        }

        var clicked = _c.Time.GetTimestamp();
        try
        {
            SetRun(RunState.Preparing, null);
            var ranEac = await _eac.EnsureInstalledAsync(rec.InstallPath, () => SetRun(RunState.InstallingPrerequisites, null), ct);

            SetRun(RunState.Launching, null);
            var skip = _updates.CheckedRecentlyWithoutUpdate();
            var (cmd, prepared) = await TakeLaunchCommandAsync(skip, ct);
            _watcher.BeginLaunch();
            try
            {
                _c.Options.ProcessStarter.Start(cmd);
            }
            catch (Exception e) when (e is not GliderException)
            {
                _watcher.Reset();
                throw new GliderException(Errors.LaunchFailed(e.Message), e);
            }

            _c.Log.Info("launch", $"started {Path.GetFileName(cmd.Executable)} {_c.Time.GetElapsedTime(clicked).TotalMilliseconds:0} ms after Play (prepared={prepared}, skipVersionCheck={skip}, eacInstalledNow={ranEac})");
            StartWatching();
            return new LaunchResult(true, ranEac);
        }
        catch (Exception e)
        {
            SetRun(RunState.Idle, null);
            if (e is GliderException or OperationCanceledException)
            {
                throw;
            }

            throw new GliderException(Errors.LaunchFailed(e.Message), e);
        }
        finally
        {
            Volatile.Write(ref _launching, 0);
        }
    }

    /// <summary>Startup / pre-launch: adopt a game that is already running.</summary>
    public bool DetectExisting()
    {
        if (_watcher.IsActive || !_watcher.DetectExisting())
        {
            return false;
        }

        StartWatching();
        return true;
    }

    private void StartWatching()
    {
        if (_watchLoop.IsCompleted)
        {
            _watchLoop = Task.Run(async () =>
            {
                try
                {
                    await _watcher.RunAsync(_c.Options.ProcessPollInterval, _c.Shutdown);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    _c.Log.Error("launch", "process watch failed", e);
                    SetRun(RunState.Idle, null);
                }
            });
        }
    }

    private void OnWatcherChanged(RunState state, DateTimeOffset? since, bool failed)
    {
        var previous = _c.Hub.State.RunState;
        SetRun(state, since);
        TrackPlaytime(state, since);
        if (state == RunState.Running && previous is RunState.Launching && _c.Settings.MinimizeToTrayWhilePlaying)
        {
            _hidByUs = true;
            _c.Host.HideToTray();
        }

        if (state != RunState.Idle)
        {
            return;
        }

        if (_hidByUs)
        {
            _hidByUs = false;
            _c.Host.RestoreWindow();
        }

        if (failed)
        {
            var e = Errors.LaunchFailed("The game process never appeared");
            _c.Log.Warn("launch", e.Detail!);
            _c.Notify(NoticeLevel.Error, e.Title, e.Message, e.Fix);
        }
        else if (previous is RunState.Running or RunState.Closing)
        {
            _c.Log.Info("launch", "game closed");
            _updates.TryAutoUpdate();
        }
    }

    /// <summary>How often a running session is flushed to disk, so a crash or power cut loses at most this much.</summary>
    internal static readonly TimeSpan PlaytimeFlushInterval = TimeSpan.FromSeconds(30);

    private readonly PlaytimeTracker _playtime = new();
    private readonly Lock _playtimeGate = new();
    private CancellationTokenSource? _playtimeLoop;
    private Task _playtimeFlush = Task.CompletedTask;

    /// <summary>Completes when the latest playtime write ends (tests).</summary>
    internal Task PlaytimeFlushed => _playtimeFlush;

    private void TrackPlaytime(RunState state, DateTimeOffset? since)
    {
        if (state == RunState.Running && !_playtime.Active)
        {
            _playtime.Start(since ?? _c.Now);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_c.Shutdown);
            _playtimeLoop = cts;
            _ = QueueFlush(final: false);
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(PlaytimeFlushInterval, _c.Time, cts.Token);
                        await QueueFlush(final: false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });
        }
        else if (state == RunState.Idle && _playtime.Active)
        {
            _playtimeLoop?.Cancel();
            _playtimeLoop = null;
            _ = QueueFlush(final: true);
        }
    }

    /// <summary>Playtime writes run one after another so a periodic flush can never overlap the final one.</summary>
    private Task QueueFlush(bool final)
    {
        lock (_playtimeGate)
        {
            return _playtimeFlush = _playtimeFlush.ContinueWith(_ => FlushPlaytimeAsync(final), TaskScheduler.Default).Unwrap();
        }
    }

    private async Task FlushPlaytimeAsync(bool final)
    {
        try
        {
            if (!final && !_playtime.Active)
            {
                return;
            }

            var now = _c.Now;
            var seconds = _playtime.Take(now, final);
            if (_c.Records.Current is { } rec)
            {
                await _c.SaveRecordAsync(PlaytimeTracker.Apply(rec, seconds, now));
            }
        }
        catch (Exception e)
        {
            _c.Log.Warn("launch", "could not save playtime", e);
        }
    }

    private void SetRun(RunState state, DateTimeOffset? since) =>
        _c.Hub.Mutate(s => s with { RunState = state, RunningSince = state == RunState.Running ? since ?? s.RunningSince ?? _c.Now : null });
}
