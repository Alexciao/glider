using System.Net.NetworkInformation;
using Glider.Core.Contracts;

namespace Glider.Core.Services;

/// <summary>A cheap "can this PC reach Epic right now?" check. Any answer from Epic's servers counts as reachable, whatever the status code.</summary>
public interface IConnectivityProbe
{
    /// <summary>True when Epic answered. Never throws for network failures (they are the answer: false); honours the token.</summary>
    Task<bool> IsEpicReachableAsync(CancellationToken ct);
}

/// <summary>The host's "the network changed" signal (Wi-Fi came up, a cable was plugged in). Optional; absent = timed probes only.</summary>
public interface INetworkChangeSignal
{
    /// <summary>Calls <paramref name="onChanged"/> (from any thread) on every network change until the returned handle is disposed.</summary>
    IDisposable Subscribe(Action onChanged);
}

/// <summary>HEAD request to Epic's launcher service: any HTTP response means the network path to Epic works. Sends nothing about the user.</summary>
public sealed class HttpConnectivityProbe : IConnectivityProbe
{
    private static readonly Uri Endpoint = new("https://launcher-public-service-prod06.ol.epicgames.com/");

    private static readonly Lazy<HttpClient> Shared = new(static () =>
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(6),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Glider-connectivity/1");
        return http;
    });

    public async Task<bool> IsEpicReachableAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, Endpoint);
            using var response = await Shared.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}

/// <summary>.NET's own network-change events (Windows: NotifyAddrChange / NetworkListManager); nothing host-specific.</summary>
public sealed class SystemNetworkChangeSignal : INetworkChangeSignal
{
    public IDisposable Subscribe(Action onChanged)
    {
        NetworkAvailabilityChangedEventHandler availability = (_, _) => onChanged();
        NetworkAddressChangedEventHandler address = (_, _) => onChanged();
        try
        {
            NetworkChange.NetworkAvailabilityChanged += availability;
            NetworkChange.NetworkAddressChanged += address;
        }
        catch (Exception e) when (e is NetworkInformationException or PlatformNotSupportedException)
        {
            return Subscription.None; // timed probes still work
        }

        return new Subscription(() =>
        {
            NetworkChange.NetworkAvailabilityChanged -= availability;
            NetworkChange.NetworkAddressChanged -= address;
        });
    }

    private sealed class Subscription(Action undo) : IDisposable
    {
        public static readonly Subscription None = new(static () => { });

        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                undo();
            }
        }
    }
}

/// <summary>
/// Makes "offline" a state that is re-checked (review H2). Core flips to Offline on a network error (pre-warm, a library sync, a
/// token refresh); nothing used to flip it back unless the user refreshed by hand, so update checks, auto-updates and the
/// post-exit cloud-save uploads stayed off for good. While the app is Offline this probes Epic with a growing delay
/// (<see cref="GliderServiceOptions.ConnectivityProbeInitialDelay"/> doubling up to <see cref="GliderServiceOptions.ConnectivityProbeMaxDelay"/>),
/// at once when the host reports a network change or <see cref="Recheck"/> is called, and publishes Online when Epic answers. It also
/// watches the hub, so an Online set by anything else (a successful library sync, sign-in, a cloud-save listing) counts as a flip back.
/// While Online it does nothing: no timer, no probe.
/// </summary>
internal sealed class ConnectivityMonitor : IDisposable
{
    private const string Category = "connectivity";

    private readonly ServiceContext _c;
    private readonly IConnectivityProbe? _probe;
    private readonly INetworkChangeSignal? _signal;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;
    private readonly TimeSpan _probeTimeout;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop;
    private TaskCompletionSource _wake = NewWake();
    private bool _reset;
    private Connectivity _seen;
    private DateTimeOffset? _nextProbe;
    private DateTimeOffset? _offlineSince;
    private IDisposable? _subscription;
    private Task _loop = Task.CompletedTask;
    private int _started;
    private int _probes;
    private bool _disposed;

    public ConnectivityMonitor(ServiceContext context, IConnectivityProbe? probe, INetworkChangeSignal? signal)
    {
        _c = context;
        _probe = probe;
        _signal = signal;
        _initialDelay = context.Options.ConnectivityProbeInitialDelay;
        _maxDelay = context.Options.ConnectivityProbeMaxDelay > _initialDelay ? context.Options.ConnectivityProbeMaxDelay : _initialDelay;
        _probeTimeout = context.Options.ConnectivityProbeTimeout;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(context.Shutdown);
        _seen = context.Hub.State.Connectivity;
    }

    /// <summary>Raised (synchronously, on whichever thread flipped the state) when Offline becomes Online. Handlers must not block.</summary>
    public event Action? CameOnline;

    /// <summary>Raised (synchronously, on the flipping thread) after every change between Offline and a reachable state. Handlers must not block.</summary>
    public event Action? Changed;

    /// <summary>When the latest offline spell began (kept after Epic is back, so a came-online handler can tell which failures belong to it); null before the first one.</summary>
    public DateTimeOffset? OfflineSince
    {
        get
        {
            lock (_gate)
            {
                return _offlineSince;
            }
        }
    }

    /// <summary>Probes started so far (diagnostics/tests).</summary>
    public int Probes => Volatile.Read(ref _probes);

    /// <summary>The delay before probe number <paramref name="attempt"/> (0-based) of an offline spell: 15 s, 30 s, 60 s … capped.</summary>
    internal static TimeSpan Backoff(int attempt, TimeSpan initial, TimeSpan max)
    {
        var ticks = initial.Ticks;
        for (var i = 0; i < attempt && ticks < max.Ticks; i++)
        {
            ticks = ticks > long.MaxValue / 2 ? max.Ticks : ticks * 2;
        }

        return TimeSpan.FromTicks(Math.Min(ticks, max.Ticks));
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _seen = _c.Hub.State.Connectivity;
        if (_seen == Connectivity.Offline)
        {
            lock (_gate)
            {
                _offlineSince ??= _c.Now;
            }
        }

        _c.Hub.StateChanged += OnHubChanged;
        try
        {
            _subscription = _signal?.Subscribe(Recheck);
        }
        catch (Exception e)
        {
            _c.Log.Warn(Category, "network-change signal unavailable; probing on a timer only", e);
        }

        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>When the next timed probe is due; null while online, probing, or before the loop has worked it out (tests wait for it before moving the clock).</summary>
    internal DateTimeOffset? NextProbe
    {
        get
        {
            lock (_gate)
            {
                return _nextProbe;
            }
        }
    }

    private void SetNextProbe(DateTimeOffset? due)
    {
        lock (_gate)
        {
            _nextProbe = due;
        }
    }

    /// <summary>Completes when the probing loop ends (tests).</summary>
    internal Task Loop => _loop;

    /// <summary>The network may have changed: probe now (if offline) and start the delays over.</summary>
    public void Recheck() => Wake(reset: true);

    /// <summary>A real Epic call just worked (cloud-save listing, an update check, a download): Online, without waiting for a probe.</summary>
    public void ReportOnline() => _c.Hub.Mutate(s => s.Connectivity == Connectivity.Offline ? s with { Connectivity = Connectivity.Online } : s);

    /// <summary>A real Epic call failed with a network error.</summary>
    public void ReportOffline() => _c.Hub.Mutate(s => s.Connectivity == Connectivity.Offline ? s : s with { Connectivity = Connectivity.Offline });

    /// <summary>Download bytes are flowing from Epic's CDN: that is as good as a probe.</summary>
    public void NoteProgress(JobProgress p)
    {
        if (p is { Phase: JobPhase.Downloading, Network.BytesPerSecond: > 0 } && IsOffline)
        {
            ReportOnline();
        }
    }

    private static TaskCompletionSource NewWake() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Wake(bool reset)
    {
        lock (_gate)
        {
            _reset |= reset;
            _wake.TrySetResult();
        }
    }

    private bool TakeReset()
    {
        lock (_gate)
        {
            var r = _reset;
            _reset = false;
            return r;
        }
    }

    private void OnHubChanged(HubState state)
    {
        bool cameOnline;
        lock (_gate)
        {
            cameOnline = _seen == Connectivity.Offline && state.Connectivity == Connectivity.Online;
            if (_seen == state.Connectivity || _disposed)
            {
                return;
            }

            _seen = state.Connectivity;
            if (state.Connectivity == Connectivity.Offline)
            {
                _offlineSince = _c.Now;
            }
        }

        Wake(reset: false);
        if (cameOnline)
        {
            _c.Log.Info(Category, "Epic is reachable again");
        }

        try
        {
            Changed?.Invoke();
            if (cameOnline)
            {
                CameOnline?.Invoke();
            }
        }
        catch (Exception e)
        {
            _c.Log.Warn(Category, "a connectivity handler failed", e);
        }
    }

    private bool IsOffline => _c.Hub.State.Connectivity == Connectivity.Offline;

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        DateTimeOffset? due = null;
        DateTimeOffset? onlineSince = null; // null while an offline spell is in progress
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!IsOffline || _probe is null)
                {
                    due = null;
                    SetNextProbe(null);
                    onlineSince ??= _c.Now;
                    TakeReset();
                    await WaitAsync(Timeout.InfiniteTimeSpan, null, ct).ConfigureAwait(false); // parked until the state changes
                    continue;
                }

                if (onlineSince is { } since)
                {
                    // A new offline spell. A network that flaps (probe answers, the real call then fails again) keeps its delays; only
                    // an outage after a calm stretch starts over at the short one.
                    if (_c.Now - since >= _maxDelay)
                    {
                        attempt = 0;
                    }

                    onlineSince = null;
                }

                if (TakeReset())
                {
                    attempt = -1; // this probe is the immediate one; the delays start over after it (15 s if it fails)
                    due = _c.Now; // a network change or Recheck: probe now
                }

                due ??= _c.Now + Backoff(attempt, _initialDelay, _maxDelay);
                SetNextProbe(due);
                var remaining = due.Value - _c.Now;
                if (remaining > TimeSpan.Zero)
                {
                    await WaitAsync(remaining, due, ct).ConfigureAwait(false); // a wake just re-evaluates; the due time stays
                    continue;
                }

                due = null;
                SetNextProbe(null);
                // Either way the next spell (or the next probe) waits longer: a probe that succeeds but is followed by a failing real call
                // is a flapping network, and must not be probed every 15 s.
                attempt++;
                if (await ProbeAsync(_probe, ct).ConfigureAwait(false))
                {
                    ReportOnline();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _c.Log.Error(Category, "connectivity monitor stopped", e);
        }
    }

    private async Task<bool> ProbeAsync(IConnectivityProbe probe, CancellationToken ct)
    {
        Interlocked.Increment(ref _probes);
        using var timeout = new CancellationTokenSource(_probeTimeout, _c.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var ok = await probe.IsEpicReachableAsync(linked.Token).ConfigureAwait(false);
            _c.Log.Debug(Category, ok ? "probe: Epic answered" : "probe: Epic unreachable");
            return ok;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // the probe timed out
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _c.Log.Debug(Category, $"probe failed: {e.GetType().Name}");
            return false;
        }
    }

    /// <summary>Returns when woken or when <paramref name="delay"/> passed. A wake that arrived while the loop was busy is not lost.</summary>
    private async Task WaitAsync(TimeSpan delay, DateTimeOffset? until, CancellationToken ct)
    {
        Task woken;
        lock (_gate)
        {
            if (_wake.Task.IsCompleted)
            {
                _wake = NewWake();
                return;
            }

            woken = _wake.Task;
        }

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var timer = delay == Timeout.InfiniteTimeSpan ? Task.Delay(Timeout.InfiniteTimeSpan, cancel.Token) : Task.Delay(delay, _c.Time, cancel.Token);
        try
        {
            if (until is { } u && _c.Now >= u)
            {
                return; // the clock moved on while the timer was being set up
            }

            await Task.WhenAny(woken, timer).ConfigureAwait(false);
        }
        finally
        {
            await cancel.CancelAsync().ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_wake.Task.IsCompleted)
            {
                _wake = NewWake();
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _c.Hub.StateChanged -= OnHubChanged;
        _subscription?.Dispose();
        _stop.Cancel();
        _stop.Dispose();
    }
}
