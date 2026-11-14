using Glider.Core.Services;

namespace Glider.Core.Tests.Services;

/// <summary>Stands in for "can this PC reach Epic": unreachable until a test says otherwise; counts its probes.</summary>
public sealed class FakeConnectivityProbe : IConnectivityProbe
{
    private int _calls;

    public bool Reachable { get; set; }

    /// <summary>Probes started so far.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>When set, a probe waits for it (a slow network).</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<bool> IsEpicReachableAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        return Reachable;
    }
}

/// <summary>The host's "network changed" event, raised by hand.</summary>
public sealed class FakeNetworkSignal : INetworkChangeSignal
{
    private Action? _handler;

    public bool Subscribed => _handler is not null;

    public IDisposable Subscribe(Action onChanged)
    {
        _handler += onChanged;
        return new Handle(this, onChanged);
    }

    public void Raise() => _handler?.Invoke();

    private sealed class Handle(FakeNetworkSignal owner, Action handler) : IDisposable
    {
        public void Dispose() => owner._handler -= handler;
    }
}
