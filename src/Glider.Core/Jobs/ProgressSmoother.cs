namespace Glider.Core.Jobs;

/// <summary>
/// Speeds for display and time-left estimates. Displayed speeds are time-based EMAs (τ = 3 s; alpha = 1 − e^(−dt/τ) from the
/// real interval, so 1 Hz download blocks and verify's line-per-file rate smooth alike). Time left is not derived from those:
/// a disk-bound Legendary download arrives in bursts (the shared-memory cache fills, the network stops for tens of seconds,
/// then refills at full speed; on the owner's drive the raw rate reads 21.6, 0.0, 1.4, 14.6, 2.0 MiB/s half a minute apart),
/// and remaining ÷ a 3 s average swung between minutes and hours. Each estimate instead divides what is left by the progress
/// actually made over the last <see cref="Window"/> (bytes or percent delivered ÷ time), which averages bursts and stalls
/// correctly, and is then a countdown corrected towards that value (predicted = previous − dt, eased with τ = 10 s), so it
/// ticks down steadily. Until the window spans <see cref="MinSpan"/> the EMA speed (or Legendary's own estimate) stands in.
/// </summary>
internal sealed class ProgressSmoother
{
    /// <summary>How far back the progress rate looks: several cache fill/drain cycles (tens of seconds each on a slow drive), still short enough to follow a mode change within a minute.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>The window's rate is used once it spans this long.</summary>
    public static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(4);

    private readonly TimeProvider _time;
    private readonly double _speedTau;
    private readonly double _etaTau;
    private readonly ProgressWindow _downloaded = new();
    private readonly ProgressWindow _written = new();
    private readonly ProgressWindow _percent = new();
    private long _origin;
    private long _last;
    private bool _started;
    private double _download;
    private double _disk;
    private double? _eta;
    private double? _writeEta;
    private double? _completionEta;
    private bool _etaIsFallback;
    private double _lastPercent;
    private double? _percentRate;

    public ProgressSmoother(TimeProvider time, double speedTauSeconds = 3, double etaTauSeconds = 10)
    {
        _time = time;
        _speedTau = speedTauSeconds;
        _etaTau = etaTauSeconds;
    }

    public double DownloadBytesPerSecond => _download;

    public double DiskBytesPerSecond => _disk;

    /// <summary>The step's own time left: bytes left to download when known, else its percent.</summary>
    public double? EtaSeconds => _eta;

    /// <summary>Seconds until the writer has put everything on the drive, when the bytes it still owes are known.</summary>
    public double? WriteEtaSeconds => _writeEta;

    /// <summary>
    /// Seconds until the percent reaches 100 at its recent pace. For a download that is the writer's pace: Legendary's percent
    /// counts chunks <em>written</em> (manager.py:601, 751), so this is the whole run's time left even when no byte total for the
    /// writer is known. Null until the percent has moved.
    /// </summary>
    public double? CompletionEtaSeconds => _completionEta;

    public void Reset()
    {
        _started = false;
        _download = _disk = 0;
        _eta = null;
        _writeEta = null;
        _completionEta = null;
        _etaIsFallback = false;
        _percentRate = null;
        _lastPercent = 0;
        _downloaded.Clear();
        _written.Clear();
        _percent.Clear();
    }

    /// <param name="remainingBytes">Bytes left to download, when known (bytes-based ETA); otherwise percent-rate based.</param>
    /// <param name="fallbackEta">Legendary's own long-run-average ETA, used until we have a rate of our own.</param>
    /// <param name="remainingWriteBytes">Bytes the writer still owes the drive, when known (the install tail's countdown).</param>
    public void Update(double rawDownload, double rawDisk, double percent, long? remainingBytes, double? fallbackEta, long? remainingWriteBytes = null)
    {
        var now = _time.GetTimestamp();
        if (!_started)
        {
            _started = true;
            _origin = _last = now;
            _download = rawDownload;
            _disk = rawDisk;
            _lastPercent = percent;
            Observe(0, percent, remainingBytes, remainingWriteBytes);
            (_eta, _etaIsFallback) = Estimate(percent, remainingBytes, fallbackEta);
            _writeEta = EstimateWrite(remainingWriteBytes);
            _completionEta = EstimateCompletion(percent);
            return;
        }

        var dt = _time.GetElapsedTime(_last, now).TotalSeconds;
        if (dt <= 0)
        {
            return;
        }

        _last = now;
        var a = 1 - Math.Exp(-dt / _speedTau);
        _download += a * (rawDownload - _download);
        _disk += a * (rawDisk - _disk);

        var rate = (percent - _lastPercent) / dt;
        _lastPercent = percent;
        if (rate >= 0)
        {
            _percentRate = _percentRate is { } r ? r + a * (rate - r) : rate;
        }

        Observe(_time.GetElapsedTime(_origin, now).TotalSeconds, percent, remainingBytes, remainingWriteBytes);
        _writeEta = remainingWriteBytes is null ? null : Countdown(_writeEta, EstimateWrite(remainingWriteBytes), dt);
        _completionEta = Countdown(_completionEta, EstimateCompletion(percent), dt);

        var (measured, fallback) = Estimate(percent, remainingBytes, fallbackEta);
        if (measured is null)
        {
            return; // nothing moved for a whole window: hold the last estimate rather than count down to a false zero
        }

        if (_eta is null || (_etaIsFallback && !fallback))
        {
            // First value, or our own measurement replaces Legendary's long-run average: snap, don't ease.
            _eta = measured;
            _etaIsFallback = fallback;
            return;
        }

        _etaIsFallback = fallback;
        _eta = Countdown(_eta, measured, dt);
    }

    private void Observe(double t, double percent, long? remainingBytes, long? remainingWriteBytes)
    {
        // Remaining counts down; the windows track progress made, so they see it negated. A count that goes back up (a new
        // run's sizes) restarts its window.
        if (remainingBytes is { } rem)
        {
            _downloaded.Add(t, -rem);
        }

        if (remainingWriteBytes is { } w)
        {
            _written.Add(t, -w);
        }

        _percent.Add(t, percent);
    }

    /// <summary>A countdown eased towards <paramref name="measured"/>; holds when there is nothing measured.</summary>
    private double? Countdown(double? previous, double? measured, double dt)
    {
        if (measured is not { } m)
        {
            return previous;
        }

        if (previous is not { } p || m == 0)
        {
            return m; // the first value, or done: nothing to ease
        }

        var predicted = Math.Max(0, p - dt);
        return Math.Max(0, predicted + (1 - Math.Exp(-dt / _etaTau)) * (m - predicted));
    }

    private double? EstimateWrite(long? remainingWriteBytes)
    {
        if (remainingWriteBytes is not { } rem)
        {
            return null;
        }

        if (rem <= 0)
        {
            return 0;
        }

        var rate = _written.Rate ?? (_disk > 1024 ? _disk : (double?)null);
        return rate is > 1024 ? rem / rate.Value : null;
    }

    private double? EstimateCompletion(double percent)
    {
        if (percent >= 100)
        {
            return 0;
        }

        return _percent.Rate is > 1e-6 and var r ? (100 - percent) / r : null;
    }

    private (double? Value, bool Fallback) Estimate(double percent, long? remainingBytes, double? fallbackEta)
    {
        if (percent >= 100)
        {
            return (0, false);
        }

        if (remainingBytes is { } rem)
        {
            if (_downloaded.Rate is { } windowed)
            {
                // Progress over the window. Zero means a whole window without a byte: no estimate (the caller holds).
                return windowed > 1024 ? (Math.Max(0, rem) / windowed, false) : (null, false);
            }

            if (_download > 1024)
            {
                return (Math.Max(0, rem) / _download, false);
            }
        }

        if (_percent.Rate is { } pr)
        {
            return pr > 1e-6 ? ((100 - percent) / pr, false) : (null, false);
        }

        if (_percentRate is > 1e-6 and var r)
        {
            return ((100 - percent) / r, false);
        }

        return (fallbackEta, true);
    }

    /// <summary>
    /// Progress (a count that only grows) sampled over the last <see cref="Window"/>; its rate is (newest − oldest) ÷ the time
    /// between them, null until they are <see cref="MinSpan"/> apart. Samples closer than half a second replace each other, so
    /// verify's line per file keeps the list short.
    /// </summary>
    private sealed class ProgressWindow
    {
        private readonly List<(double T, double V)> _samples = [];

        public double? Rate
        {
            get
            {
                if (_samples.Count < 2)
                {
                    return null;
                }

                var (t0, v0) = _samples[0];
                var (t1, v1) = _samples[^1];
                return t1 - t0 >= MinSpan.TotalSeconds ? Math.Max(0, v1 - v0) / (t1 - t0) : null;
            }
        }

        public void Clear() => _samples.Clear();

        public void Add(double t, double v)
        {
            if (_samples.Count > 0 && v < _samples[^1].V - 1e-9)
            {
                _samples.Clear();
            }

            if (_samples.Count >= 2 && t - _samples[^2].T < 0.5)
            {
                _samples[^1] = (t, v);
            }
            else
            {
                _samples.Add((t, v));
            }

            // Keep the newest sample at or before the window's start, so the rate always spans the whole window once it can.
            while (_samples.Count > 2 && _samples[1].T <= t - Window.TotalSeconds)
            {
                _samples.RemoveAt(0);
            }
        }
    }
}
