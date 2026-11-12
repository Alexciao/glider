using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Game;

/// <summary>
/// Tracks the game's lifetime by polling for <c>FortniteClient-Win64-Shipping</c> (cheap name lookup every
/// ~1.5 s). The exe Legendary resolves is usually a stub (FortniteLauncher / the EAC bootstrap) that spawns
/// Shipping and exits, so "Launching" lasts while any stub is alive or until Shipping appears, bounded by a
/// timeout. "Closed" = Shipping missing on two consecutive polls (first miss → Closing).
/// The state machine is <see cref="Tick"/>; the polling loop just calls it.
/// </summary>
internal sealed class ProcessWatcher
{
    /// <summary>Stubs that may precede the Shipping process.</summary>
    public static readonly string[] LauncherNames =
    [
        "FortniteLauncher",
        "FortniteClient-Win64-Shipping_EAC_EOS",
        "FortniteClient-Win64-Shipping_EAC",
        "FortniteClient-Win64-Shipping_BE",
    ];

    private static readonly TimeSpan StubExitGrace = TimeSpan.FromSeconds(10);

    private enum Phase { Idle, Launching, Running, Closing }

    private readonly IProcessMonitor _monitor;
    private readonly TimeProvider _time;
    private readonly TimeSpan _launchTimeout;
    private readonly Lock _gate = new();
    private Phase _phase;
    private DateTimeOffset _launchStarted;
    private DateTimeOffset? _stubGoneSince;

    public ProcessWatcher(IProcessMonitor monitor, TimeProvider time, TimeSpan launchTimeout)
    {
        _monitor = monitor;
        _time = time;
        _launchTimeout = launchTimeout;
    }

    /// <summary>Raised on every transition: (state, runningSince, launchFailed).</summary>
    public event Action<RunState, DateTimeOffset?, bool>? Changed;

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _phase != Phase.Idle;
            }
        }
    }

    public void BeginLaunch()
    {
        lock (_gate)
        {
            _phase = Phase.Launching;
            _launchStarted = _time.GetUtcNow();
            _stubGoneSince = null;
        }
    }

    /// <summary>Abort a launch that never started a process.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _phase = Phase.Idle;
            _stubGoneSince = null;
        }
    }

    /// <summary>Startup: is the game already running (e.g. started by EGL or before Glider)? Starts tracking it.</summary>
    public bool DetectExisting()
    {
        var shipping = _monitor.Find(FortniteLayout.ShippingProcessName);
        if (shipping.Count == 0)
        {
            return false;
        }

        lock (_gate)
        {
            _phase = Phase.Running;
        }

        Changed?.Invoke(RunState.Running, shipping[0].StartTime ?? _time.GetUtcNow(), false);
        return true;
    }

    /// <summary>One poll. Returns false once the watcher is idle (stop polling).</summary>
    public bool Tick()
    {
        var shipping = _monitor.Find(FortniteLayout.ShippingProcessName);
        var now = _time.GetUtcNow();
        (RunState State, DateTimeOffset? Since, bool Failed)? transition = null;
        bool active;
        lock (_gate)
        {
            switch (_phase)
            {
                case Phase.Launching:
                    if (shipping.Count > 0)
                    {
                        _phase = Phase.Running;
                        transition = (RunState.Running, shipping[0].StartTime ?? now, false);
                        break;
                    }

                    var stubAlive = LauncherNames.Any(n => _monitor.Find(n).Count > 0);
                    _stubGoneSince = stubAlive ? null : _stubGoneSince ?? now;
                    if (now - _launchStarted > _launchTimeout || (_stubGoneSince is { } gone && now - gone >= StubExitGrace))
                    {
                        _phase = Phase.Idle;
                        transition = (RunState.Idle, null, true);
                    }

                    break;

                case Phase.Running:
                    if (shipping.Count == 0)
                    {
                        _phase = Phase.Closing;
                        transition = (RunState.Closing, null, false);
                    }

                    break;

                case Phase.Closing:
                    if (shipping.Count == 0)
                    {
                        _phase = Phase.Idle;
                        transition = (RunState.Idle, null, false);
                    }
                    else
                    {
                        _phase = Phase.Running;
                        transition = (RunState.Running, shipping[0].StartTime ?? now, false);
                    }

                    break;
            }

            active = _phase != Phase.Idle;
        }

        if (transition is { } t)
        {
            Changed?.Invoke(t.State, t.Since, t.Failed);
        }

        return active;
    }

    /// <summary>Poll until idle. Each iteration is a name lookup — no handles are held between polls.</summary>
    public async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        do
        {
            await Task.Delay(interval, _time, ct);
        }
        while (Tick());
    }
}
