using Glider.Core.Jobs;

namespace Glider.Core.Services;

/// <summary>
/// The metadata lane (L§1.8): one online library command at a time (`list`, `list-installed --check-updates`, `info`, update
/// dry runs), never while the writer lane's job is in its pre-download phase (it refreshes the same files then).
/// Two priorities, like <c>AuthGate</c>: <see cref="RunInteractiveAsync"/> (a user is waiting: install plan, game info) is
/// served before every queued <see cref="RunAsync"/> (background sync, update-size checks). A running command is never
/// preempted; a queued background job holds nothing, so it yields to any interactive call until its turn comes.
/// </summary>
public sealed class MetadataLane : IDisposable
{
    private sealed class Waiter(TaskCompletionSource granted)
    {
        public TaskCompletionSource Granted { get; } = granted;
        public LinkedList<Waiter>? Queue { get; set; }
        public LinkedListNode<Waiter>? Node { get; set; }
    }

    private readonly Lock _gate = new();
    private readonly LinkedList<Waiter> _interactive = new();
    private readonly LinkedList<Waiter> _background = new();
    private readonly CancellationTokenSource _disposed = new();
    private readonly WriterActivity _writer;
    private bool _running;
    private bool _waitingForWriter;
    private bool _isDisposed;

    public MetadataLane(WriterActivity writer) => _writer = writer;

    /// <summary>A command is running (waiting callers don't count).</summary>
    public bool Busy
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>Background work: library syncs, update-size dry runs, cloud-save listings. Served after any interactive call.</summary>
    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct) => RunCoreAsync(work, interactive: false, ct);

    /// <summary>A user is waiting for the answer (install plan, game info): jumps ahead of queued background work, never preempts a running command.</summary>
    public Task<T> RunInteractiveAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct) => RunCoreAsync(work, interactive: true, ct);

    private async Task<T> RunCoreAsync<T>(Func<CancellationToken, Task<T>> work, bool interactive, CancellationToken ct)
    {
        await AcquireAsync(interactive, ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            return await work(ct).ConfigureAwait(false);
        }
        finally
        {
            Release();
        }
    }

    private Task AcquireAsync(bool interactive, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var waiter = new Waiter(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            waiter.Queue = interactive ? _interactive : _background;
            waiter.Node = waiter.Queue.AddLast(waiter);
        }

        if (ct.CanBeCanceled)
        {
            var registration = ct.Register(() => Cancel(waiter, ct));
            waiter.Granted.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        Pump();
        return waiter.Granted.Task;
    }

    private void Cancel(Waiter waiter, CancellationToken ct)
    {
        lock (_gate)
        {
            if (waiter.Node is { } node)
            {
                waiter.Queue!.Remove(node);
                waiter.Node = null;
            }
        }

        waiter.Granted.TrySetCanceled(ct); // a lane already granted stays granted; RunCoreAsync releases it
    }

    private void Release()
    {
        lock (_gate)
        {
            _running = false;
        }

        Pump();
    }

    /// <summary>Hands the lane to the next waiter (interactive first) unless a command runs or the writer is in its pre-download phase.</summary>
    private void Pump()
    {
        Waiter next;
        lock (_gate)
        {
            if (_running || _isDisposed || _waitingForWriter)
            {
                return;
            }

            var queue = _interactive.First is not null ? _interactive : _background.First is not null ? _background : null;
            if (queue is null)
            {
                return;
            }

            if (_writer.InPreDownload)
            {
                // Nobody holds the lane while we wait, so whoever is first in line when the phase ends (an interactive call arriving
                // meanwhile included) is the one that runs.
                _waitingForWriter = true;
                _writer.WhenNotPreDownloadAsync(_disposed.Token).ContinueWith(
                    _ =>
                    {
                        lock (_gate)
                        {
                            _waitingForWriter = false;
                        }

                        Pump();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }

            next = queue.First!.Value;
            queue.RemoveFirst();
            next.Node = null;
            _running = true;
        }

        next.Granted.TrySetResult();
    }

    public void Dispose()
    {
        Waiter[] waiting;
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            waiting = [.. _interactive, .. _background];
            _interactive.Clear();
            _background.Clear();
        }

        _disposed.Cancel();
        foreach (var w in waiting)
        {
            w.Granted.TrySetException(new ObjectDisposedException(nameof(MetadataLane)));
        }

        _disposed.Dispose();
    }
}
