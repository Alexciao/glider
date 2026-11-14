using System.Runtime.Versioning;
using System.Text;

namespace Glider.Core.Legendary;

/// <summary>
/// Speed limit (Settings.SpeedLimitBytesPerSecond) by worker throttling. Measured on this machine: a Job Object net-rate limit
/// (<c>JobObjectNetRateControlInformation</c>) is accepted unelevated but only shapes <em>outgoing</em> traffic, so a download in
/// a job capped at 5 MB/s still ran at 31 MB/s. Legendary 0.21.1 has no limit of its own, so the limiter closes the loop on the
/// raw download rate Legendary prints every second and throttles the download workers: stock exe = duty-cycle suspension of the
/// worker processes (<see cref="DutyCycleActuator"/>), Glider fork = the control file's <c>max_active</c> (<see cref="ControlFileActuator"/>).
/// </summary>
internal static class SpeedGovernor
{
    public const double MinFraction = 0.03;

    /// <summary>
    /// Multiplicative feedback for the duty cycle: the fraction of time the workers run. If the measured rate is above the limit the
    /// fraction shrinks by limit/measured, if below it grows, damped by half so a noisy 1 s sample doesn't make it oscillate.
    /// </summary>
    public static double NextFraction(double current, long? limitBytesPerSecond, double measuredBytesPerSecond)
    {
        if (limitBytesPerSecond is not > 0)
        {
            return 1;
        }

        if (measuredBytesPerSecond < 1)
        {
            return Math.Min(1, Math.Max(current, MinFraction) * 1.5); // nothing measured (stalled or just resumed): open up gently
        }

        var target = current * limitBytesPerSecond.Value / measuredBytesPerSecond;
        var next = current + 0.5 * (target - current);
        return Math.Clamp(next, MinFraction, 1);
    }

    /// <summary>AIMD on the number of in-flight chunks (fork): −25 % above the limit, +1 below 90 % of it.</summary>
    public static int NextMaxActive(int current, int ceiling, long? limitBytesPerSecond, double measuredBytesPerSecond)
    {
        if (limitBytesPerSecond is not > 0)
        {
            return ceiling;
        }

        if (measuredBytesPerSecond > limitBytesPerSecond.Value)
        {
            return Math.Max(1, (int)(current * 0.75));
        }

        return measuredBytesPerSecond < 0.9 * limitBytesPerSecond.Value ? Math.Min(ceiling, current + 1) : current;
    }
}

/// <summary>Something that can hold the download back. One per run; <see cref="Retarget"/> runs about once a second.</summary>
internal interface IRateActuator : IDisposable
{
    /// <summary><paramref name="limit"/> null = unlimited (release everything).</summary>
    void Retarget(long? limit, double measuredBytesPerSecond, IReadOnlyCollection<int> workerPids);
}

/// <summary>
/// Stock exe: suspends the download-worker processes for (1 − fraction) of every 250 ms period (NtSuspendProcess; workers are
/// network-bound and idle-wait on sockets, so a 100–200 ms pause just lets the receive buffers fill). Everything is resumed on
/// dispose, and the job object's kill-on-close reaps suspended processes if Glider itself dies.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DutyCycleActuator : IRateActuator
{
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private double _fraction = 1;
    private HashSet<int> _workers = [];

    public double Fraction
    {
        get
        {
            lock (_gate)
            {
                return _fraction;
            }
        }
    }

    public void Retarget(long? limit, double measuredBytesPerSecond, IReadOnlyCollection<int> workerPids)
    {
        lock (_gate)
        {
            _workers = [.. workerPids];
            _fraction = SpeedGovernor.NextFraction(_fraction, limit, measuredBytesPerSecond);
            if (limit is > 0 && _loop is null)
            {
                _loop = Task.Run(() => LoopAsync(_stop.Token));
            }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var suspended = new HashSet<int>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                double fraction;
                HashSet<int> workers;
                lock (_gate)
                {
                    fraction = _fraction;
                    workers = _workers;
                }

                if (fraction >= 0.99 || workers.Count == 0)
                {
                    Release(suspended);
                    await Task.Delay(Period, ct).ConfigureAwait(false);
                    continue;
                }

                await Task.Delay(Period * fraction, ct).ConfigureAwait(false);
                foreach (var pid in workers)
                {
                    if (ProcessTuning.Suspend(pid))
                    {
                        suspended.Add(pid);
                    }
                }

                await Task.Delay(Period * (1 - fraction), ct).ConfigureAwait(false);
                Release(suspended);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Release(suspended);
        }
    }

    private static void Release(HashSet<int> suspended)
    {
        foreach (var pid in suspended)
        {
            ProcessTuning.Resume(pid);
        }

        suspended.Clear();
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _stop.Dispose();
    }
}

/// <summary>Fork: <c>{"max_active": n}</c> in the control file Legendary polls, written atomically (temp + rename).</summary>
internal sealed class ControlFileActuator : IRateActuator
{
    private readonly string _path;
    private readonly int _ceiling;
    private int _current;
    private int _written = -1;

    public ControlFileActuator(string path, int workers)
    {
        _path = path;
        _ceiling = Math.Max(1, workers * 2); // Legendary's own in-flight cap
        _current = _ceiling;
    }

    public int MaxActive => _current;

    public void Retarget(long? limit, double measuredBytesPerSecond, IReadOnlyCollection<int> workerPids)
    {
        _current = SpeedGovernor.NextMaxActive(_current, _ceiling, limit, measuredBytesPerSecond);
        if (_current != _written)
        {
            Write(_current);
        }
    }

    /// <summary>Sets an absolute in-flight cap (Background).</summary>
    public void Pin(int maxActive)
    {
        _current = Math.Clamp(maxActive, 1, _ceiling);
        Write(_current);
    }

    private void Write(int maxActive)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, $"{{\"max_active\": {maxActive}}}", new UTF8Encoding(false));
            File.Move(tmp, _path, overwrite: true);
            _written = maxActive;
        }
        catch (IOException)
        {
            // Legendary has it open for a moment; the next second tries again.
        }
    }

    public void Dispose()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }
}
