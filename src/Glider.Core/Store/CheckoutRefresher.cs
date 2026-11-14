namespace Glider.Core.Store;

/// <summary>Timing of <see cref="CheckoutRefresher"/> (docs/research/store-webview.md §4.2).</summary>
public sealed class CheckoutRefreshOptions
{
    /// <summary>While a checkout was seen: refresh this often…</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>…for at most this long after the last checkout activity.</summary>
    public TimeSpan PollWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Two refreshes are never closer than this (single flight).</summary>
    public TimeSpan MinSpacing { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Turns "the user may have bought or claimed something in the Store" into library refreshes, without ever looking at the page:
/// a checkout page was seen → refresh when the Store tab is left, when the window comes back to the foreground, and on a bounded
/// poll (a claim can lag the asset list by seconds). A refresh that finds a new game ends the episode. Single-flight, spaced.
/// </summary>
internal sealed class CheckoutRefresher : IDisposable
{
    private readonly TimeProvider _time;
    private readonly Func<CancellationToken, Task<int>> _refresh;
    private readonly Func<bool> _userPresent;
    private readonly CheckoutRefreshOptions _o;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    private bool _pending;
    private DateTimeOffset _lastActivity;
    private DateTimeOffset _lastRefreshStart = DateTimeOffset.MinValue;
    private Task<int>? _running;
    private Task _poll = Task.CompletedTask;

    /// <param name="refresh">Runs one library refresh and returns how many games are new. Must not throw for ordinary failures.</param>
    /// <param name="userPresent">Polling pauses while false (window hidden and not recently active).</param>
    public CheckoutRefresher(TimeProvider time, Func<CancellationToken, Task<int>> refresh, Func<bool>? userPresent = null, CheckoutRefreshOptions? options = null)
    {
        _time = time;
        _refresh = refresh;
        _userPresent = userPresent ?? (() => true);
        _o = options ?? new CheckoutRefreshOptions();
    }

    /// <summary>A checkout happened since the last refresh that found something (or since the poll window ended).</summary>
    public bool Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>A checkout page was seen: remember it and (re)start the bounded poll.</summary>
    public void CheckoutSeen()
    {
        lock (_gate)
        {
            _pending = true;
            _lastActivity = _time.GetUtcNow();
            if (_poll.IsCompleted && !_cts.IsCancellationRequested)
            {
                _poll = Task.Run(() => PollAsync(_cts.Token));
            }
        }
    }

    /// <summary>The Store tab was left or the Store was closed: one refresh if a checkout was seen.</summary>
    public Task StoreLeftAsync() => RefreshIfPendingAsync();

    /// <summary>The window came back to the foreground (the user may have claimed in a browser/another device): refresh if pending.</summary>
    public Task WindowActivatedAsync() => RefreshIfPendingAsync();

    private async Task RefreshIfPendingAsync()
    {
        if (!Pending)
        {
            return;
        }

        int added;
        try
        {
            added = await RefreshOnceAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (added > 0 || _time.GetUtcNow() - _lastActivity > _o.PollWindow)
            {
                _pending = false;
            }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (!_pending || _time.GetUtcNow() - _lastActivity > _o.PollWindow)
                    {
                        _pending = _pending && _time.GetUtcNow() - _lastActivity <= _o.PollWindow;
                        return;
                    }
                }

                await Task.Delay(_o.PollInterval, _time, ct).ConfigureAwait(false);
                if (!_userPresent())
                {
                    continue;
                }

                var added = await RefreshOnceAsync(ct).ConfigureAwait(false);
                if (added > 0)
                {
                    lock (_gate)
                    {
                        _pending = false;
                    }

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Single flight: a caller arriving during a refresh shares it; callers arriving too soon after one wait out the spacing.</summary>
    private Task<int> RefreshOnceAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false } running)
            {
                return running;
            }

            _running = Task.Run(async () =>
            {
                var wait = _lastRefreshStart + _o.MinSpacing - _time.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, _time, ct).ConfigureAwait(false);
                }

                lock (_gate)
                {
                    _lastRefreshStart = _time.GetUtcNow();
                }

                return await _refresh(ct).ConfigureAwait(false);
            }, CancellationToken.None);
            return _running;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
