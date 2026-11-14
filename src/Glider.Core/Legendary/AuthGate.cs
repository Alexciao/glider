namespace Glider.Core.Legendary;

/// <summary>
/// The auth gate (L§1.6, L§1.8): at most one Legendary process inside <c>login()</c> at a time, so two processes never race on
/// user.json's refresh-token rotation (Legendary writes it unlocked and non-atomically). A spawn that logs in enters the
/// gate first; the lease is released at the first post-login log line, at exit, or after <see cref="MaxHold"/>. Priority
/// entrants (launch) are served before normal ones, so Play waits for at most the one login already in progress.
/// </summary>
internal sealed class AuthGate
{
    public static readonly TimeSpan DefaultMaxHold = TimeSpan.FromSeconds(45);

    private readonly Lock _gate = new();
    private readonly LinkedList<Waiter> _priority = new();
    private readonly LinkedList<Waiter> _normal = new();
    private readonly TimeProvider _time;
    private Lease? _holder;

    public AuthGate(TimeProvider? time = null, TimeSpan? maxHold = null)
    {
        _time = time ?? TimeProvider.System;
        MaxHold = maxHold ?? DefaultMaxHold;
    }

    public TimeSpan MaxHold { get; }

    /// <summary>Someone is inside login() right now (diagnostics/tests).</summary>
    public bool Held
    {
        get
        {
            lock (_gate)
            {
                return _holder is not null;
            }
        }
    }

    /// <summary>Waiting entrants (diagnostics/tests).</summary>
    public int Waiting
    {
        get
        {
            lock (_gate)
            {
                return _priority.Count + _normal.Count;
            }
        }
    }

    public async Task<Lease> EnterAsync(bool priority, string label, CancellationToken ct)
    {
        Waiter waiter;
        LinkedListNode<Waiter> node;
        lock (_gate)
        {
            if (_holder is null)
            {
                return _holder = NewLease(label);
            }

            waiter = new Waiter(label);
            node = (priority ? _priority : _normal).AddLast(waiter);
        }

        try
        {
            return await waiter.Granted.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                node.List?.Remove(node);
            }

            if (waiter.Granted.Task.IsCompletedSuccessfully)
            {
                waiter.Granted.Task.Result.Release(); // granted while we gave up: pass it on
            }

            throw;
        }
    }

    private Lease NewLease(string label)
    {
        var lease = new Lease(this, label);
        lease.Timer = _time.CreateTimer(static s => ((Lease)s!).Release(), lease, MaxHold, Timeout.InfiniteTimeSpan);
        return lease;
    }

    private void Release(Lease lease)
    {
        Waiter? next = null;
        Lease? nextLease = null;
        lock (_gate)
        {
            if (_holder != lease)
            {
                return;
            }

            _holder = null;
            var list = _priority.Count > 0 ? _priority : _normal.Count > 0 ? _normal : null;
            if (list is not null)
            {
                next = list.First!.Value;
                list.RemoveFirst();
                _holder = nextLease = NewLease(next.Label);
            }
        }

        lease.Timer?.Dispose();
        if (next is not null && !next.Granted.TrySetResult(nextLease!))
        {
            nextLease!.Release();
        }
    }

    private sealed record Waiter(string Label)
    {
        public TaskCompletionSource<Lease> Granted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class Lease(AuthGate owner, string label) : IDisposable
    {
        private int _released;

        public string Label { get; } = label;

        internal ITimer? Timer { get; set; }

        public bool Released => Volatile.Read(ref _released) == 1;

        public void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(this);
            }
        }

        public void Dispose() => Release();
    }

    /// <summary>
    /// Legendary's login lines (core.py:211-278, cli.py auth): the gate stays closed while these print and opens at the first
    /// other line after one of them.
    /// </summary>
    /// <summary>Output that only appears once a command is past its login (download manager, verify, JSON results).</summary>
    public static bool IsPastLogin(ReadOnlySpan<char> line) =>
        line.Contains("[DLManager]", StringComparison.Ordinal) ||
        line.StartsWith("Verification progress", StringComparison.Ordinal) ||
        line.StartsWith("{", StringComparison.Ordinal);

    public static bool IsLoginLine(ReadOnlySpan<char> line) =>
        line.Contains("login session", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Logging in", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("refresh token", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("login data", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("Checking for updates", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("update check", StringComparison.OrdinalIgnoreCase);
}
