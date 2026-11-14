namespace Glider.Core.Legendary;

/// <summary>
/// Is the install drive the bottleneck? Legendary's shared-memory cache fills when chunks arrive faster than the writer can put
/// them on disk, so a cache that is ≥ 85 % full for ≥ 10 of the last 15 s means "disk-bound" (docs/download-perf.md §5.7). A full
/// cache never means a slow CDN, so <see cref="CdnWatch"/> doesn't blame the network then.
/// </summary>
internal sealed class DiskBoundDetector
{
    private const double FullShare = 0.85;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    private readonly Queue<(long Ms, bool Full)> _samples = new();
    private readonly object _gate = new();

    /// <summary>Feed one progress block (≈1 Hz). <paramref name="totalMiB"/> ≤ 0 (unknown) records nothing.</summary>
    public void Add(long nowMs, double usedMiB, double totalMiB)
    {
        if (totalMiB <= 0)
        {
            return;
        }

        lock (_gate)
        {
            _samples.Enqueue((nowMs, usedMiB >= FullShare * totalMiB));
            while (_samples.Count > 0 && nowMs - _samples.Peek().Ms > Window.TotalMilliseconds)
            {
                _samples.Dequeue();
            }
        }
    }

    /// <summary>≥ 2/3 of the samples of the last 15 s are full, over at least 10 s (a cache that merely started filling doesn't count).</summary>
    public bool IsDiskBound
    {
        get
        {
            lock (_gate)
            {
                if (_samples.Count < 8)
                {
                    return false;
                }

                var span = _samples.Last().Ms - _samples.Peek().Ms;
                var full = _samples.Count(s => s.Full);
                return span >= 9_000 && full * 3 >= _samples.Count * 2;
            }
        }
    }
}

/// <summary>
/// Notices a job that is slow on its CDN (docs/download-perf.md §5.2): not disk-bound, the 60 s average raw rate below 40 % of what
/// the probe measured for this host (or below 5 MB/s with no probe), and more than 3 GB still to download — for three minutes in
/// a row. <see cref="ShouldSwitch"/> then turns true once; the caller restarts on the next CDN (or, with the fork, switches in place).
/// </summary>
internal sealed class CdnWatch
{
    internal const double NoProbeFloorBytesPerSecond = 5_000_000;
    private static readonly TimeSpan AverageWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SlowFor = TimeSpan.FromMinutes(3);
    private const long MinRemainingBytes = 3L * 1024 * 1024 * 1024;

    private readonly double _thresholdBytesPerSecond;
    private readonly Queue<(long Ms, double Rate)> _rates = new();
    private readonly object _gate = new();
    private long? _slowSince;
    private bool _fired;

    /// <param name="probeMegabytesPerSecond">The probe's MB/s (10^6 bytes) for the chosen host; null = no probe.</param>
    public CdnWatch(double? probeMegabytesPerSecond)
    {
        _thresholdBytesPerSecond = probeMegabytesPerSecond is > 0 ? 0.4 * probeMegabytesPerSecond.Value * 1_000_000 : NoProbeFloorBytesPerSecond;
    }

    /// <summary>The host is slow right now (even before the three minutes are up); for the "limited by the download server" message.</summary>
    public bool IsSlow
    {
        get
        {
            lock (_gate)
            {
                return _slowSince is not null;
            }
        }
    }

    public bool ShouldSwitch
    {
        get
        {
            lock (_gate)
            {
                return _fired;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _rates.Clear();
            _slowSince = null;
            _fired = false;
        }
    }

    /// <param name="rawBytesPerSecond">The "raw" download rate of the block.</param>
    /// <param name="remainingBytes">Bytes left to download, if known.</param>
    public void Add(long nowMs, double rawBytesPerSecond, long? remainingBytes, bool diskBound)
    {
        lock (_gate)
        {
            _rates.Enqueue((nowMs, rawBytesPerSecond));
            while (_rates.Count > 0 && nowMs - _rates.Peek().Ms > AverageWindow.TotalMilliseconds)
            {
                _rates.Dequeue();
            }

            var spanned = _rates.Count > 1 && nowMs - _rates.Peek().Ms >= AverageWindow.TotalMilliseconds * 0.75;
            var average = _rates.Average(r => r.Rate);
            var slow = spanned && !diskBound && average < _thresholdBytesPerSecond && remainingBytes is null or > MinRemainingBytes;
            if (!slow)
            {
                _slowSince = null;
                return;
            }

            _slowSince ??= nowMs;
            if (!_fired && nowMs - _slowSince >= SlowFor.TotalMilliseconds)
            {
                _fired = true;
            }
        }
    }
}

/// <summary>
/// Watches one download run: feeds <see cref="DiskBoundDetector"/> and <see cref="CdnWatch"/> from the progress blocks, tells the
/// progress sink when the verdicts change, and says when the job should move to the next CDN. One per Legendary run.
/// </summary>
internal sealed class DownloadMonitor
{
    private readonly TimeProvider _time;
    private readonly ILegendaryProgressSink? _sink;
    private readonly double _cacheTotalMiB;
    private readonly object _gate = new();
    private readonly long _t0;
    private LegendaryProgress _latest;
    private bool _unconsumed;
    private bool _reportedDisk;
    private bool _reportedCdn;

    public DownloadMonitor(TimeProvider time, ILegendaryProgressSink? sink, int cacheMiB, double? probeMegabytesPerSecond)
    {
        _time = time;
        _sink = sink;
        _cacheTotalMiB = cacheMiB;
        _t0 = time.GetTimestamp();
        Disk = new DiskBoundDetector();
        Cdn = new CdnWatch(probeMegabytesPerSecond);
    }

    public DiskBoundDetector Disk { get; }

    public CdnWatch Cdn { get; }

    /// <summary>Called for every published block.</summary>
    public void OnProgress(LegendaryProgress p)
    {
        lock (_gate)
        {
            _latest = p;
            _unconsumed = true;
            Disk.Add(NowMs(), p.CacheUsedMiB, p.CacheTotalMiB > 0 ? p.CacheTotalMiB : _cacheTotalMiB);
        }
    }

    /// <summary>The abort predicate of the run: true once the CDN has been slow long enough to move on (see <see cref="CdnWatch"/>).</summary>
    public bool WantsSwitch(LegendaryOutputFacts facts)
    {
        bool diskBound, cdnSlow, shouldSwitch;
        lock (_gate)
        {
            if (_unconsumed)
            {
                _unconsumed = false;
                long? remaining = facts.DownloadSizeBytes is { } total ? Math.Max(0, total - _latest.DownloadedBytes) : null;
                Cdn.Add(NowMs(), _latest.DownloadBytesPerSecond, remaining, Disk.IsDiskBound);
            }

            diskBound = Disk.IsDiskBound;
            cdnSlow = Cdn.IsSlow;
            shouldSwitch = Cdn.ShouldSwitch;
            if (diskBound == _reportedDisk && cdnSlow == _reportedCdn)
            {
                return shouldSwitch;
            }

            _reportedDisk = diskBound;
            _reportedCdn = cdnSlow;
        }

        _sink?.OnHealth(diskBound, cdnSlow);
        return shouldSwitch;
    }

    private long NowMs() => (long)_time.GetElapsedTime(_t0).TotalMilliseconds;
}
