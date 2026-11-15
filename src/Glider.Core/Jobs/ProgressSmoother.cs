namespace Glider.Core.Jobs;

/// <summary>
/// Time-based EMAs for speeds and ETA. Legendary reports once per second with ±15% jitter; a fixed-alpha EMA would
/// smooth differently at 1 Hz and at verify's line-per-file rate, so alpha = 1 - e^(-dt/τ) is derived from the real
/// interval. The ETA is a countdown corrected towards the measured value (predicted = previous − dt), so it ticks
/// down steadily instead of jumping with every speed wobble.
/// </summary>
internal sealed class ProgressSmoother
{
    private readonly TimeProvider _time;
    private readonly double _speedTau;
    private readonly double _etaTau;
    private long _last;
    private bool _started;
    private double _download;
    private double _disk;
    private double? _eta;
    private double? _writeEta;
    private bool _etaIsFallback;
    private double _lastPercent;
    private double? _percentRate;

    public ProgressSmoother(TimeProvider time, double speedTauSeconds = 3, double etaTauSeconds = 6)
    {
        _time = time;
        _speedTau = speedTauSeconds;
        _etaTau = etaTauSeconds;
    }

    public double DownloadBytesPerSecond => _download;

    public double DiskBytesPerSecond => _disk;

    public double? EtaSeconds => _eta;

    /// <summary>Seconds until the writer has put everything on the drive (its own countdown), when the bytes still owed are known.</summary>
    public double? WriteEtaSeconds => _writeEta;

    public void Reset()
    {
        _started = false;
        _download = _disk = 0;
        _eta = null;
        _writeEta = null;
        _etaIsFallback = false;
        _percentRate = null;
        _lastPercent = 0;
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
            _last = now;
            _download = rawDownload;
            _disk = rawDisk;
            _lastPercent = percent;
            (_eta, _etaIsFallback) = Estimate(percent, remainingBytes, fallbackEta);
            _writeEta = EstimateWrite(remainingWriteBytes);
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

        var measuredWrite = EstimateWrite(remainingWriteBytes);
        if (measuredWrite is null)
        {
            _writeEta = null;
        }
        else
        {
            var predictedWrite = _writeEta is { } w ? Math.Max(0, w - dt) : measuredWrite.Value;
            _writeEta = Math.Max(0, predictedWrite + (1 - Math.Exp(-dt / _etaTau)) * (measuredWrite.Value - predictedWrite));
        }

        var (measured, fallback) = Estimate(percent, remainingBytes, fallbackEta);
        if (measured is null)
        {
            _eta = _eta is { } e ? Math.Max(0, e - dt) : null;
            return;
        }

        if (_eta is not { } previous || (_etaIsFallback && !fallback))
        {
            // First value, or our own measurement replaces Legendary's long-run average: snap, don't ease.
            _eta = measured;
            _etaIsFallback = fallback;
            return;
        }

        _etaIsFallback = fallback;

        var predicted = Math.Max(0, previous - dt);
        var b = 1 - Math.Exp(-dt / _etaTau);
        _eta = Math.Max(0, predicted + b * (measured.Value - predicted));
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

        return _disk > 1024 ? rem / _disk : null;
    }

    private (double? Value, bool Fallback) Estimate(double percent, long? remainingBytes, double? fallbackEta)
    {
        if (percent >= 100)
        {
            return (0, false);
        }

        if (remainingBytes is { } rem && _download > 1024)
        {
            return (Math.Max(0, rem) / _download, false);
        }

        if (_percentRate is > 1e-6 and var r)
        {
            return ((100 - percent) / r, false);
        }

        return (fallbackEta, true);
    }
}
