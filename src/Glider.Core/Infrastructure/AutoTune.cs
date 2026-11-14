namespace Glider.Core.Infrastructure;

/// <summary>Download knobs picked from the machine when the user leaves Settings.DownloadWorkers / SharedMemoryMb on auto.</summary>
public sealed record AutoTuneResult(int Workers, int SharedMemoryMb);

/// <summary>
/// Chooses Legendary's <c>--max-workers</c> and <c>--max-shared-memory</c> (docs/download-perf.md §5.3, §5.5).
/// <para>
/// Workers: 16 on every machine (12 below 4 GiB RAM). Workers are network-bound (93–96 % of their time blocked on a
/// socket, ~30 MB each), so the CPU count is the wrong input: 8 workers reach the ceiling on a good link, 16 are needed
/// on a high-latency one, and more than 16 is slightly worse everywhere (extra flows only queue). The old
/// <c>clamp(2 × CPUs, 6, 16)</c> gave 6–8 workers on small machines, 40–70 % below what 16 reach.
/// </para>
/// <para>
/// Shared memory: 1 GiB everywhere. The cache never filled in a network-bound run (peak 40–237 MiB); when the disk is
/// slower than the network any size fills up and a bigger one only makes the end-of-job drain longer and costs commit
/// charge while the user may be gaming. A MemoryError (analyses that reuse many old chunks) is retried with
/// Legendary's suggested size, up to <see cref="MaxSharedMemoryMb"/>.
/// </para>
/// </summary>
public static class AutoTune
{
    private const long GiB = 1024L * 1024 * 1024;

    public const int MinWorkers = 1;
    public const int MaxWorkers = 32;
    public const int MinSharedMemoryMb = 256;
    public const int MaxSharedMemoryCeilingMb = 16384;

    public const int DefaultWorkers = 16;
    public const int LowMemoryWorkers = 12;
    public const int DefaultSharedMemoryMb = 1024;

    /// <summary>Same on every machine; the parameters stay so callers don't change (RAM only lowers the worker count when tiny).</summary>
    public static AutoTuneResult Compute(int cpuCount, long totalMemoryBytes)
    {
        _ = cpuCount;
        var workers = totalMemoryBytes > 0 && totalMemoryBytes < 4 * GiB ? LowMemoryWorkers : DefaultWorkers;
        return new AutoTuneResult(workers, DefaultSharedMemoryMb);
    }

    public static AutoTuneResult ForCurrentMachine() => Compute(Environment.ProcessorCount, TotalMemoryBytes());

    /// <summary>Physical memory visible to this process (container/job limits respected).</summary>
    public static long TotalMemoryBytes() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>Upper bound for the automatic MemoryError retry: half the RAM, at most 12 GiB.</summary>
    public static int MaxSharedMemoryMb(long totalMemoryBytes) =>
        totalMemoryBytes <= 0 ? 4096 : (int)Math.Clamp(totalMemoryBytes / 2 / (1024 * 1024), 1024, 12288);
}
