using Glider.Core.Contracts;

namespace Glider.Core.Jobs;

/// <summary>
/// The live knobs of one Legendary run: the effective download mode and the speed limit. The job queue creates it through
/// <see cref="IResourcePolicy"/>, keeps it while the run lives and calls <see cref="Update"/> when the mode or limit changes
/// (settings, a game starting, idle full speed); the runner re-applies per-process priorities and the limiter on <see cref="Changed"/>.
/// No restart is needed: every knob is a priority or a throttle that Windows lets us change while the processes run.
/// </summary>
public sealed class RunControl
{
    private readonly Lock _gate = new();
    private DownloadMode _mode;
    private long? _speedLimit;

    public RunControl(DownloadMode mode, long? speedLimitBytesPerSecond = null)
    {
        _mode = mode;
        _speedLimit = speedLimitBytesPerSecond is > 0 ? speedLimitBytesPerSecond : null;
    }

    public DownloadMode Mode
    {
        get
        {
            lock (_gate)
            {
                return _mode;
            }
        }
    }

    /// <summary>Bytes per second of raw network download, or null for unlimited.</summary>
    public long? SpeedLimitBytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _speedLimit;
            }
        }
    }

    /// <summary>Raised after a change, outside the lock.</summary>
    public event Action? Changed;

    /// <summary>Raw network rate (bytes/s) from the latest progress block; the speed limiter closes its loop on it.</summary>
    public double RawBytesPerSecond { get; private set; }

    /// <summary>Raised for every <see cref="ReportRawRate"/> (about once a second while downloading).</summary>
    public event Action<double>? RateReported;

    public void ReportRawRate(double bytesPerSecond)
    {
        RawBytesPerSecond = bytesPerSecond;
        RateReported?.Invoke(bytesPerSecond);
    }

    /// <summary>Returns true when something changed.</summary>
    public bool Update(DownloadMode mode, long? speedLimitBytesPerSecond)
    {
        var limit = speedLimitBytesPerSecond is > 0 ? speedLimitBytesPerSecond : null;
        lock (_gate)
        {
            if (_mode == mode && _speedLimit == limit)
            {
                return false;
            }

            _mode = mode;
            _speedLimit = limit;
        }

        Changed?.Invoke();
        return true;
    }
}
