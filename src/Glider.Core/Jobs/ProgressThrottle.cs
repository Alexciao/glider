using Glider.Core.Contracts;

namespace Glider.Core.Jobs;

/// <summary>
/// Leading + trailing edge throttle: the first update goes out at once, later ones at most every <c>interval</c>,
/// and the newest value is always delivered eventually (nothing is left stale when updates stop).
/// </summary>
internal sealed class ProgressThrottle : IDisposable
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly Action<JobProgress> _emit;
    private readonly Lock _lock = new();
    private readonly ITimer _timer;
    private JobProgress? _pending;
    private long _lastEmit;
    private bool _emitted;
    private bool _timerArmed;

    public ProgressThrottle(TimeProvider time, TimeSpan interval, Action<JobProgress> emit)
    {
        _time = time;
        _interval = interval;
        _emit = emit;
        _timer = time.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Post(JobProgress progress)
    {
        JobProgress? now = null;
        lock (_lock)
        {
            var elapsed = _time.GetElapsedTime(_lastEmit);
            if (!_emitted || elapsed >= _interval)
            {
                _emitted = true;
                _lastEmit = _time.GetTimestamp();
                _pending = null;
                now = progress;
            }
            else
            {
                _pending = progress;
                if (!_timerArmed)
                {
                    _timerArmed = true;
                    _timer.Change(_interval - elapsed, Timeout.InfiniteTimeSpan);
                }
            }
        }

        if (now is not null)
        {
            _emit(now);
        }
    }

    /// <summary>Delivers the pending value (if any) immediately.</summary>
    public void Flush()
    {
        JobProgress? pending;
        lock (_lock)
        {
            _timerArmed = false;
            pending = _pending;
            _pending = null;
            if (pending is not null)
            {
                _lastEmit = _time.GetTimestamp();
            }
        }

        if (pending is not null)
        {
            _emit(pending);
        }
    }

    public void Dispose() => _timer.Dispose();
}
