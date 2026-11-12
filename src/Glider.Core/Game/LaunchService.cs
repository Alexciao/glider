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
    private readonly ServiceContext _c;
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

        try
        {
            SetRun(RunState.Preparing, null);
            var ranEac = await _eac.EnsureInstalledAsync(rec.InstallPath, () => SetRun(RunState.InstallingPrerequisites, null), ct);

            SetRun(RunState.Launching, null);
            var skip = _updates.CheckedRecentlyWithoutUpdate();
            var cmd = await _c.Legendary.GetLaunchCommandAsync(ServiceContext.App, skip, _c.Settings.LaunchArgs ?? "", ct);
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

            _c.Log.Info("launch", $"started {Path.GetFileName(cmd.Executable)} (skipVersionCheck={skip}, eacInstalledNow={ranEac})");
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

    private void SetRun(RunState state, DateTimeOffset? since) =>
        _c.Hub.Mutate(s => s with { RunState = state, RunningSince = state == RunState.Running ? since ?? s.RunningSince ?? _c.Now : null });
}
