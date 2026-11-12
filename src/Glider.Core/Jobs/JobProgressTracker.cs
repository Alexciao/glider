using Glider.Core.Contracts;
using Glider.Core.Legendary;

namespace Glider.Core.Jobs;

/// <summary>
/// Turns raw per-run Legendary progress into the job's <see cref="JobProgress"/>. Legendary restarts its counters on
/// every run (a resume reports 0% of the *remaining* work), so the overall figure is
/// <c>base + (100 − base) × run%</c>, where <c>base</c> is the share of the first run's download that the current
/// run no longer needs. The bar never moves backwards within a phase; speeds/ETA are smoothed; output is throttled.
/// </summary>
internal sealed class JobProgressTracker : ILegendaryProgressSink, IDisposable
{
    private readonly string _jobId;
    private readonly TimeProvider _time;
    private readonly ProgressSmoother _smoother;
    private readonly ProgressThrottle _throttle;
    private readonly Action<JobPhase> _onPhase;
    private readonly Lock _lock = new();
    private readonly long _runStarted;
    private ProgressBaseline _baseline;
    private JobPhase _phase = JobPhase.Preparing;
    private long? _runRemainingDownload;
    private long? _runRemainingWrite;
    private double _runBase;
    private JobProgress? _last;

    public JobProgressTracker(string jobId, ProgressBaseline baseline, TimeProvider time, TimeSpan interval, Action<JobProgress> publish, Action<JobPhase> onPhase)
    {
        _jobId = jobId;
        _time = time;
        _baseline = baseline.Clone();
        _smoother = new ProgressSmoother(time);
        _throttle = new ProgressThrottle(time, interval, publish);
        _onPhase = onPhase;
        _runStarted = time.GetTimestamp();
        if (_baseline.TotalDownloadBytes is { } total && _baseline.Percent > 0)
        {
            // Until this run prints its own sizes, assume nothing new was lost.
            _runBase = Math.Min(100, _baseline.Percent);
            _runRemainingDownload = (long)(total * (1 - _runBase / 100));
        }
    }

    public JobPhase Phase
    {
        get
        {
            lock (_lock)
            {
                return _phase;
            }
        }
    }

    /// <summary>Copy of the bookkeeping including this run's elapsed time (for persistence).</summary>
    public ProgressBaseline Snapshot()
    {
        lock (_lock)
        {
            var b = _baseline.Clone();
            b.ElapsedSeconds += _time.GetElapsedTime(_runStarted).TotalSeconds;
            return b;
        }
    }

    /// <summary>A new step (e.g. the post-install verify) measures a fresh bar.</summary>
    public void BeginStep()
    {
        lock (_lock)
        {
            var elapsed = _baseline.ElapsedSeconds;
            _baseline = new ProgressBaseline { ElapsedSeconds = elapsed };
            _runRemainingDownload = _runRemainingWrite = null;
            _runBase = 0;
            _smoother.Reset();
        }
    }

    public void SetPhase(JobPhase phase) => OnPhase(phase);

    public void OnPhase(JobPhase phase)
    {
        JobProgress? update = null;
        lock (_lock)
        {
            if (phase == _phase)
            {
                return;
            }

            _phase = phase;
            if (_last is not null)
            {
                update = _last with { Phase = phase, DownloadBytesPerSecond = phase == JobPhase.Finalizing ? 0 : _last.DownloadBytesPerSecond };
                _last = update;
            }
        }

        _onPhase(phase);
        if (update is not null)
        {
            _throttle.Post(update);
        }
    }

    public void OnSizes(LegendaryRunSizes sizes)
    {
        lock (_lock)
        {
            var write = sizes.InstallBytes is { } install ? install - (sizes.UnchangedBytes ?? 0) : (long?)null;
            if (_baseline.TotalDownloadBytes is not { } total || total <= 0)
            {
                _baseline.TotalDownloadBytes = sizes.DownloadBytes;
                _baseline.TotalWriteBytes = write;
                _runRemainingDownload = sizes.DownloadBytes;
                _runRemainingWrite = write;
                _runBase = 0;
                return;
            }

            _runRemainingDownload = sizes.DownloadBytes;
            _runRemainingWrite = write;
            _runBase = sizes.DownloadBytes is { } remaining ? Math.Clamp((total - remaining) * 100.0 / total, 0, 100) : _runBase;
        }
    }

    public void Report(LegendaryProgress p)
    {
        JobProgress progress;
        lock (_lock)
        {
            var phase = _phase;
            var measuring = phase is JobPhase.Verifying ? JobPhase.Verifying : phase is JobPhase.Moving ? JobPhase.Moving : JobPhase.Downloading;
            if (_baseline.Phase is { } previous && previous != measuring)
            {
                // e.g. legendary repair: verify 0→100%, then download 0→100%. Separate bars.
                _baseline.Percent = 0;
                _smoother.Reset();
            }

            _baseline.Phase = measuring;
            double percent;
            long downloaded = 0, totalDownload = 0, written = 0, totalWrite = 0;
            double rawDownload = 0, rawDisk;
            long? remaining = null;
            switch (measuring)
            {
                case JobPhase.Verifying:
                    percent = p.Percent;
                    rawDisk = p.DiskReadBytesPerSecond;
                    break;

                case JobPhase.Moving:
                    percent = p.Percent;
                    written = p.WrittenBytes;
                    totalWrite = p.Percent > 0 ? (long)(p.WrittenBytes * 100 / p.Percent) : 0;
                    rawDisk = p.DiskWriteBytesPerSecond;
                    break;

                default:
                    totalDownload = _baseline.TotalDownloadBytes ?? 0;
                    totalWrite = _baseline.TotalWriteBytes ?? 0;
                    var doneBefore = totalDownload > 0 && _runRemainingDownload is { } rd ? Math.Max(0, totalDownload - rd) : 0;
                    var writtenBefore = totalWrite > 0 && _runRemainingWrite is { } rw ? Math.Max(0, totalWrite - rw) : 0;
                    downloaded = totalDownload > 0 ? Math.Min(totalDownload, doneBefore + p.DownloadedBytes) : p.DownloadedBytes;
                    written = totalWrite > 0 ? Math.Min(totalWrite, writtenBefore + p.WrittenBytes) : p.WrittenBytes;
                    percent = _runBase + (100 - _runBase) * p.Percent / 100;
                    remaining = totalDownload > 0 ? Math.Max(0, totalDownload - downloaded) : null;
                    rawDownload = p.DownloadBytesPerSecond;
                    rawDisk = p.DiskWriteBytesPerSecond;
                    break;
            }

            percent = Math.Clamp(Math.Max(_baseline.Percent, percent), 0, 100);
            _baseline.Percent = percent;
            _smoother.Update(rawDownload, rawDisk, percent, remaining, p.EtaSeconds);
            var elapsed = _baseline.ElapsedSeconds + _time.GetElapsedTime(_runStarted).TotalSeconds;
            progress = new JobProgress(_jobId, phase, Math.Round(percent, 2), downloaded, totalDownload, written, totalWrite,
                _smoother.DownloadBytesPerSecond, _smoother.DiskBytesPerSecond, rawDownload,
                _smoother.EtaSeconds is { } eta ? Math.Round(eta) : null, (long)elapsed);
            _last = progress;
        }

        _throttle.Post(progress);
    }

    public void Flush() => _throttle.Flush();

    public void Dispose() => _throttle.Dispose();
}
