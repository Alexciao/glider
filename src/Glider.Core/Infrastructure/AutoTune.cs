namespace Glider.Core.Infrastructure;

/// <summary>Download knobs picked from the machine when the user leaves Settings.DownloadWorkers / SharedMemoryMb on auto.</summary>
public sealed record AutoTuneResult(int Workers, int SharedMemoryMb);

/// <summary>
/// Chooses Legendary's <c>--max-workers</c> and <c>--max-shared-memory</c>.
/// <para>
/// Workers: Legendary's own default is <c>min(2 × CPUs, 16)</c> (manager.py:52). Each worker is a separate
/// process that fetches one ~1 MiB chunk at a time, so throughput is bounded by per-request latency, not CPU:
/// ~10–20 MiB/s per worker against Epic's CDN, i.e. 8–16 workers saturate 1–2.5 Gbit/s. CPU cost (zlib + SHA-1)
/// scales with bytes, not with worker count, so we keep Legendary's curve but raise the floor to 6 for 2–3 core
/// machines (they would otherwise get 4 and under-fill a fast pipe), cap at 16 (beyond that the single
/// FileWorker writer is the bottleneck), and cap lower on small-RAM machines (each worker costs ~40–60 MB RSS).
/// Legendary runs at below-normal priority (see LegendaryProcessRunner), so this never starves the foreground.
/// </para>
/// <para>
/// Shared memory: allocated up front as one block (commit charge). Legendary's real default is 2048 MiB
/// (core.py:1645; the help text's "1 GiB" is wrong). More cache only helps when the disk is slower than the
/// network or when an update reuses many old chunks (analysis raises MemoryError if the cache is smaller than
/// its computed minimum). So: 1 GiB on &lt;6 GiB RAM, 2 GiB (Legendary's default) up to 12 GiB, 3 GiB up to
/// 24 GiB, 4 GiB above. A MemoryError is retried once with Legendary's suggested size, up to
/// <see cref="MaxSharedMemoryMb"/>.
/// </para>
/// </summary>
public static class AutoTune
{
    private const long GiB = 1024L * 1024 * 1024;

    public const int MinWorkers = 1;
    public const int MaxWorkers = 32;
    public const int MinSharedMemoryMb = 256;
    public const int MaxSharedMemoryCeilingMb = 16384;

    public static AutoTuneResult Compute(int cpuCount, long totalMemoryBytes)
    {
        cpuCount = Math.Max(1, cpuCount);
        var workers = Math.Clamp(cpuCount * 2, 6, 16);
        if (totalMemoryBytes > 0 && totalMemoryBytes < 4 * GiB)
        {
            workers = Math.Min(workers, 4);
        }
        else if (totalMemoryBytes > 0 && totalMemoryBytes < 8 * GiB)
        {
            workers = Math.Min(workers, 8);
        }

        var shm = totalMemoryBytes switch
        {
            <= 0 => 2048,
            < 6 * GiB => 1024,
            < 12 * GiB => 2048,
            < 24 * GiB => 3072,
            _ => 4096,
        };

        return new AutoTuneResult(workers, shm);
    }

    public static AutoTuneResult ForCurrentMachine() => Compute(Environment.ProcessorCount, TotalMemoryBytes());

    /// <summary>Physical memory visible to this process (container/job limits respected).</summary>
    public static long TotalMemoryBytes() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>Upper bound for the automatic MemoryError retry: half the RAM, at most 12 GiB.</summary>
    public static int MaxSharedMemoryMb(long totalMemoryBytes) =>
        totalMemoryBytes <= 0 ? 4096 : (int)Math.Clamp(totalMemoryBytes / 2 / (1024 * 1024), 1024, 12288);
}
