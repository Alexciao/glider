using Glider.Core.Contracts;
using Glider.Core.Legendary;

namespace Glider.Core.Jobs;

/// <summary>
/// Turns raw per-run Legendary progress into the job's <see cref="JobProgress"/> (docs/v0.2-design.md §5). Legendary restarts
/// its counters on every run (a resume reports 0% of the *remaining* work), so a step's percent is
/// <c>base + (100 − base) × run%</c>, where <c>base</c> is the share of the first run's download the current run no longer
/// needs. Each step maps onto its <see cref="JobSegment"/> of the whole-job bar, which never moves backwards. Groups are
/// filled honestly by phase: verifying = local reads only (Verify), downloading/repairing = Network + Disk, installing and
/// moving = Disk. Speeds/ETA are smoothed, averages and peaks kept, ≈1 Hz graph samples recorded, output throttled.
/// </summary>
/// <summary>What the tracker asks the rest of the app for when it explains the limiter.</summary>
internal sealed class LimiterContext
{
    public Func<long?> SpeedLimit { get; init; } = () => null;

    /// <summary>Why the effective mode differs from the setting ("Background mode while Fortnite is running").</summary>
    public Func<string?> ModeReason { get; init; } = () => null;

    /// <summary>Install drive, e.g. "D:".</summary>
    public Func<string?> Drive { get; init; } = () => null;
}

internal sealed class JobProgressTracker : ILegendaryProgressSink, IDisposable
{
    private readonly string _jobId;
    private readonly string _appName;
    private readonly TimeProvider _time;
    private readonly ProgressSmoother _smoother;
    private readonly ProgressThrottle _throttle;
    private readonly Action<JobPhase> _onPhase;
    private readonly Func<DownloadMode> _mode;
    private readonly JobSamples _samples;
    private readonly Func<(int Bad, int Fixed)> _verifyCounts;
    private readonly long? _installSize;
    private readonly Lock _lock = new();
    private readonly long _runStarted;
    private long _stepStarted;
    private ProgressBaseline _baseline;
    private JobSegment _segment;
    private JobPhase _phase = JobPhase.Preparing;
    private long? _runRemainingDownload;
    private long? _runRemainingWrite;
    private double _runBase;
    private JobProgress? _last;
    private Action? _onFirstProgress;
    private readonly LimiterContext _limiterContext;
    private RunInfo _runInfo;
    private bool _diskBound;
    private bool _cdnSlow;
    private double _lastWriteRate;
    private double _lastRawRate;
    private double _diskBoundWriteRate;

    public JobProgressTracker(
        string jobId,
        string appName,
        JobSegment segment,
        ProgressBaseline baseline,
        TimeProvider time,
        TimeSpan interval,
        Action<JobProgress> publish,
        Action<JobPhase> onPhase,
        Func<DownloadMode>? mode = null,
        JobSamples? samples = null,
        Func<(int Bad, int Fixed)>? verifyCounts = null,
        long? installSizeBytes = null,
        LimiterContext? limiterContext = null)
    {
        _limiterContext = limiterContext ?? new LimiterContext();
        _jobId = jobId;
        _appName = appName;
        _segment = segment;
        _time = time;
        _baseline = baseline.Clone();
        _smoother = new ProgressSmoother(time);
        _throttle = new ProgressThrottle(time, interval, publish);
        _onPhase = onPhase;
        _mode = mode ?? (() => DownloadMode.Balanced);
        _samples = samples ?? new JobSamples();
        _verifyCounts = verifyCounts ?? (() => (0, 0));
        _installSize = installSizeBytes;
        _runStarted = _stepStarted = time.GetTimestamp();
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

    public double JobPercent
    {
        get
        {
            lock (_lock)
            {
                return _baseline.JobPercent;
            }
        }
    }

    /// <summary>Called once at the first progress line of the current Legendary run (the writer left its pre-download phase).</summary>
    public void OnFirstProgress(Action callback)
    {
        lock (_lock)
        {
            _onFirstProgress = callback;
        }
    }

    /// <summary>Copy of the bookkeeping including this run's elapsed time (for persistence).</summary>
    public ProgressBaseline Snapshot()
    {
        lock (_lock)
        {
            var b = _baseline.Clone();
            b.ElapsedSeconds += _time.GetElapsedTime(_runStarted).TotalSeconds;
            b.StepElapsedSeconds += _time.GetElapsedTime(_stepStarted).TotalSeconds;
            return b;
        }
    }

    /// <summary>A new step (e.g. the post-install verify) measures a fresh phase bar on its own slice of the job bar.</summary>
    public void BeginStep(JobSegment segment)
    {
        lock (_lock)
        {
            _baseline = new ProgressBaseline
            {
                ElapsedSeconds = _baseline.ElapsedSeconds,
                JobPercent = Math.Max(_baseline.JobPercent, segment.VerifyFrom == segment.VerifyTo ? segment.DownloadFrom : segment.VerifyFrom),
                PeakNetworkBytesPerSecond = _baseline.PeakNetworkBytesPerSecond,
                PeakDiskBytesPerSecond = _baseline.PeakDiskBytesPerSecond,
            };
            _segment = segment;
            _stepStarted = _time.GetTimestamp();
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
            phase = Display(phase);
            if (phase == _phase)
            {
                return;
            }

            _phase = phase;
            if (_last is not null)
            {
                var full = phase == JobPhase.Verifying
                    ? _last with
                    {
                        Phase = phase,
                        PhasePercent = 0,
                        EtaSeconds = null,
                        Verify = new VerifyStats(0, 0, 0, _installSize ?? 0, 0, _verifyCounts().Bad, _verifyCounts().Fixed),
                        FilesDone = 0,
                        FilesTotal = 0,
                    }
                    : _last with { Phase = phase };
                _last = full;
                update = Groups(full, phase);
            }
        }

        _onPhase(phase);
        if (update is not null)
        {
            _throttle.Post(update);
        }
    }

    /// <summary>Legendary's phase names, adjusted for the step: a download inside a repair step is "Repairing".</summary>
    private JobPhase Display(JobPhase raw) => raw == JobPhase.Downloading && _segment.Repairing ? JobPhase.Repairing : raw;

    public void OnRunInfo(RunInfo info)
    {
        lock (_lock)
        {
            _runInfo = info;
            _diskBound = false;
            _cdnSlow = false;
        }
    }

    public void OnHealth(bool diskBound, bool cdnSlow)
    {
        lock (_lock)
        {
            _diskBound = diskBound;
            _cdnSlow = cdnSlow;
        }
    }

    /// <summary>Smoothed write speed while the drive was the limit (0 if it never was): what a drive really sustains.</summary>
    public double DiskBoundWriteBytesPerSecond
    {
        get
        {
            lock (_lock)
            {
                return _diskBoundWriteRate;
            }
        }
    }

    public void OnSizes(LegendaryRunSizes sizes)
    {
        lock (_lock)
        {
            var write = sizes.InstallBytes is { } install ? install - (sizes.UnchangedBytes ?? 0) : (long?)null;
            if (write < 0)
            {
                write = null;
            }

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
        JobPhase? phaseChange = null;
        Action? first;
        lock (_lock)
        {
            first = _onFirstProgress;
            _onFirstProgress = null;
            var measuring = _phase switch
            {
                JobPhase.Verifying => JobPhase.Verifying,
                JobPhase.Moving => JobPhase.Moving,
                _ => JobPhase.Downloading,
            };
            if (_baseline.Phase is { } previous && previous != measuring)
            {
                // e.g. legendary repair: verify 0→100%, then download 0→100%. Separate phase bars; the job bar keeps growing.
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
                    // Network done, writer still busy: the disk-only tail is "Installing" (never "downloading at 0 B/s").
                    if (_phase is JobPhase.Downloading or JobPhase.Repairing && totalDownload > 0 && downloaded >= totalDownload && percent < 100)
                    {
                        phaseChange = JobPhase.Installing;
                    }

                    break;
            }

            percent = Math.Clamp(Math.Max(_baseline.Percent, percent), 0, 100);
            _baseline.Percent = percent;
            var (from, to) = _segment.For(measuring);
            _baseline.JobPercent = Math.Clamp(Math.Max(_baseline.JobPercent, from + (to - from) * percent / 100), 0, 100);
            _smoother.Update(rawDownload, rawDisk, percent, remaining, p.EtaSeconds);
            var elapsed = _baseline.ElapsedSeconds + _time.GetElapsedTime(_runStarted).TotalSeconds;
            var stepElapsed = Math.Max(1e-3, _baseline.StepElapsedSeconds + _time.GetElapsedTime(_stepStarted).TotalSeconds);
            var phase = phaseChange ?? _phase;
            var net = _smoother.DownloadBytesPerSecond;
            var disk = _smoother.DiskBytesPerSecond;
            var counts = _verifyCounts();
            var verifyTotalBytes = _installSize ?? 0;

            if (phase is JobPhase.Downloading or JobPhase.Repairing)
            {
                _baseline.PeakNetworkBytesPerSecond = Math.Max(_baseline.PeakNetworkBytesPerSecond, net);
            }

            if (phase is JobPhase.Downloading or JobPhase.Repairing or JobPhase.Installing or JobPhase.Moving)
            {
                _baseline.PeakDiskBytesPerSecond = Math.Max(_baseline.PeakDiskBytesPerSecond, disk);
            }

            var full = new JobProgress(
                _jobId,
                _appName,
                phase,
                Math.Round(_baseline.JobPercent, 2),
                Math.Round(percent, 2),
                Math.Round(elapsed),
                _smoother.EtaSeconds is { } eta ? Math.Round(eta) : null,
                new NetworkStats(downloaded, totalDownload, net, downloaded / stepElapsed, _baseline.PeakNetworkBytesPerSecond, p.Cdn ?? _runInfo.Cdn, _runInfo.Connections),
                new DiskStats(written, totalWrite, disk, written / stepElapsed, _baseline.PeakDiskBytesPerSecond, _installSize),
                new VerifyStats((int)Math.Min(p.ProcessedChunks, int.MaxValue), (int)Math.Min(p.TotalChunks, int.MaxValue),
                    (long)(verifyTotalBytes * percent / 100), verifyTotalBytes, disk, counts.Bad, counts.Fixed),
                measuring == JobPhase.Verifying ? (int)Math.Min(p.ProcessedChunks, int.MaxValue) : null,
                measuring == JobPhase.Verifying ? (int)Math.Min(p.TotalChunks, int.MaxValue) : null,
                _mode(),
                Limiter.Unknown);
            _lastRawRate = rawDownload;
            _lastWriteRate = rawDisk;
            if (_diskBound && rawDisk > 0)
            {
                _diskBoundWriteRate = _diskBoundWriteRate == 0 ? rawDisk : 0.9 * _diskBoundWriteRate + 0.1 * rawDisk;
            }
            progress = Groups(full, phase);
            _last = full;
            _samples.Add(new JobSample(Math.Round(elapsed, 1),
                progress.Network?.BytesPerSecond ?? 0,
                progress.Disk?.BytesPerSecond ?? progress.Verify?.ReadBytesPerSecond ?? 0));
        }

        first?.Invoke();
        if (phaseChange is { } change)
        {
            OnPhase(change);
        }

        _throttle.Post(progress);
    }

    /// <summary>Only the groups that apply to the phase are sent (verifying never shows network figures).</summary>
    private JobProgress Groups(JobProgress p, JobPhase phase)
    {
        var shaped = phase switch
        {
            JobPhase.Verifying => p with { Network = null, Disk = null },
            JobPhase.Downloading or JobPhase.Repairing => p with { Verify = null, FilesDone = null, FilesTotal = null },
            JobPhase.Installing or JobPhase.Moving => p with
            {
                Network = null,
                Verify = null,
                FilesDone = null,
                FilesTotal = null,
                EtaSeconds = phase == JobPhase.Installing ? null : p.EtaSeconds,
            },
            _ => p with { Network = null, Disk = null, Verify = null, FilesDone = null, FilesTotal = null, EtaSeconds = null },
        };
        var (limiter, reason) = LimiterDecision.Decide(new LimiterInput(
            phase,
            p.Mode,
            _limiterContext.ModeReason(),
            _diskBound,
            _cdnSlow,
            _limiterContext.SpeedLimit(),
            _lastRawRate,
            _lastWriteRate,
            _baseline.PeakNetworkBytesPerSecond,
            _runInfo.ProbeMegabytesPerSecond,
            _limiterContext.Drive(),
            p.Network?.Cdn ?? _runInfo.Cdn,
            _time.GetElapsedTime(_stepStarted).TotalSeconds));
        return shaped with { Limiter = limiter, LimiterReason = reason };
    }

    public void Flush() => _throttle.Flush();

    public void Dispose() => _throttle.Dispose();
}
