using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Jobs;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Benchmarks;

/// <summary>
/// Opt-in end-to-end download benchmark (docs/download-perf.md section 7). It drives Legendary's real DLManager pipeline
/// (<c>tools/bench/legendary_run.py</c>, a stand-in for <c>legendary install</c> that needs no login) through Glider's own process
/// runner, so what is measured is the real job object, tuner, parser and download modes. Skipped unless GLIDER_BENCH_PY is set:
/// <list type="bullet">
/// <item>GLIDER_BENCH_PY: python.exe of a venv with requests, pycryptodomex, filelock</item>
/// <item>GLIDER_BENCH_SRC: Legendary 0.21.1 checkout; GLIDER_BENCH_MANIFEST: a cached manifest; GLIDER_BENCH_BASE: CDN base URL (or http://127.0.0.1:port)</item>
/// <item>GLIDER_BENCH_DIR: scratch folder to install into (deleted per run); GLIDER_BENCH_OUT: JSON-lines results</item>
/// <item>GLIDER_BENCH_PREFIXES: ';' separated manifest path prefixes; GLIDER_BENCH_MODES: e.g. "v0.1,full,balanced,background,full" (runs in this order)</item>
/// <item>GLIDER_BENCH_WORKERS (default 16), GLIDER_BENCH_SHM_MB (default 1024), GLIDER_BENCH_LABEL</item>
/// </list>
/// </summary>
public class DownloadBenchmarkTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RunsTheConfiguredModesAgainstTheRealPipeline()
    {
        var py = Env("GLIDER_BENCH_PY");
        Assert.SkipWhen(py is null || !OperatingSystem.IsWindows(), "set GLIDER_BENCH_PY (and friends) to run the download benchmark");
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "Glider.slnx")))
        {
            repo = repo.Parent;
        }

        var script = Path.Combine(repo!.FullName, "tools", "bench", "legendary_run.py");
        var scratch = Env("GLIDER_BENCH_DIR")!;
        var workers = int.Parse(Env("GLIDER_BENCH_WORKERS") ?? "16", CultureInfo.InvariantCulture);
        var shm = int.Parse(Env("GLIDER_BENCH_SHM_MB") ?? "1024", CultureInfo.InvariantCulture);
        var prefixes = (Env("GLIDER_BENCH_PREFIXES") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        var modes = (Env("GLIDER_BENCH_MODES") ?? "full,balanced").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var ct = TestContext.Current.CancellationToken;

        foreach (var mode in modes)
        {
            using var temp = new TempDir();
            var paths = new AppPaths(temp.Path, py!);
            paths.EnsureCreated();
            using var runner = new LegendaryProcessRunner(paths, new TestLog());
            var install = Path.Combine(scratch, "install-" + Guid.NewGuid().ToString("N")[..8]);
            List<string> args = ["-I", script, "--src", Env("GLIDER_BENCH_SRC")!, "--manifest", Env("GLIDER_BENCH_MANIFEST")!, "--base", Env("GLIDER_BENCH_BASE")!,
                "--dir", install, "--workers", workers.ToString(CultureInfo.InvariantCulture), "--shm-mb", shm.ToString(CultureInfo.InvariantCulture)];
            foreach (var p in prefixes)
            {
                args.Add("--prefix");
                args.Add(p);
            }

            // v0.1 = the job object pinned at BELOW_NORMAL for the whole tree, no per-process tuning, as shipped in 0.1.x.
            ResourceProfile profile = mode == "v0.1"
                ? ResourceProfile.Default
                : new ModeResourcePolicy().For(Enum.Parse<DownloadMode>(mode, ignoreCase: true), JobKind.Install);
            var collector = new Collector();
            var started = Stopwatch.GetTimestamp();
            var priorities = new Dictionary<string, int>();
            using var sampler = new CancellationTokenSource();
            var sampling = Task.Run(async () =>
            {
                while (!collector.HasProgress)
                {
                    await Task.Delay(500, sampler.Token).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromSeconds(4), sampler.Token).ConfigureAwait(false); // workers and writer exist and have been tuned
                foreach (var proc in Process.GetProcessesByName("python"))
                {
                    using (proc)
                    {
                        try
                        {
                            if (proc.StartTime < DateTime.Now.AddSeconds(-1) && proc.StartTime > DateTime.Now.AddSeconds(-120) && ProcessTuning.Query(proc.Id) is { } q)
                            {
                                var key = $"{q.Cpu}/io{(int)q.Io}/mem{(int)q.Memory}";
                                priorities[key] = priorities.GetValueOrDefault(key) + 1;
                            }
                        }
                        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                        {
                        }
                    }
                }
            }, CancellationToken.None);

            var run = await runner.RunAsync(new LegendaryCommand(LegendaryCommandKind.Install, args)
            {
                Timeout = Timeout.InfiniteTimeSpan,
                LowPriority = profile.LowPriority,
                Resources = profile,
                DownloadWorkers = profile.WorkerCap is { } cap ? Math.Min(cap, workers) : workers,
            }, collector, ct);
            await sampler.CancelAsync();
            try
            {
                await sampling;
            }
            catch (OperationCanceledException)
            {
            }

            var wall = Stopwatch.GetElapsedTime(started).TotalSeconds;
            var row = collector.Summarize(Env("GLIDER_BENCH_LABEL") ?? "", mode, workers, shm, wall, run.ExitCode, priorities);
            File.AppendAllText(Env("GLIDER_BENCH_OUT") ?? Path.Combine(scratch, "bench.jsonl"), JsonSerializer.Serialize(row) + "\n");
            if (Directory.Exists(install))
            {
                Directory.Delete(install, recursive: true);
            }

            Assert.Equal(0, run.ExitCode);
        }
    }

    private sealed class Collector : IProgress<LegendaryProgress>
    {
        private readonly List<(double T, LegendaryProgress P)> _samples = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public bool HasProgress
        {
            get
            {
                lock (_samples)
                {
                    return _samples.Count > 0;
                }
            }
        }

        public void Report(LegendaryProgress value)
        {
            lock (_samples)
            {
                _samples.Add((_clock.Elapsed.TotalSeconds, value));
            }
        }

        public object Summarize(string label, string mode, int workers, int shm, double wall, int exit, Dictionary<string, int> priorities)
        {
            List<(double T, LegendaryProgress P)> s;
            lock (_samples)
            {
                s = [.. _samples];
            }

            if (s.Count == 0)
            {
                return new { label, mode, error = "no progress", exit };
            }

            // Steady state: skip the ramp-up (first 3 blocks) and the drain tail (last 2).
            var steady = s.Count > 8 ? s.Skip(3).Take(s.Count - 5).ToList() : s;
            var raw = steady.Select(x => x.P.DownloadBytesPerSecond / 1e6).ToList();
            var netSeconds = s[^1].T - s[0].T;
            var bytes = s[^1].P.DownloadedBytes;
            return new
            {
                label,
                mode,
                workers,
                shm_mb = shm,
                exit,
                wall_s = Math.Round(wall, 1),
                first_progress_s = Math.Round(s[0].T, 1),
                net_phase_mbps = Math.Round(bytes / Math.Max(netSeconds, 0.001) / 1e6, 1),
                steady_raw_mbps = Math.Round(raw.Average(), 1),
                peak_raw_mbps = Math.Round(s.Max(x => x.P.DownloadBytesPerSecond) / 1e6, 1),
                avg_write_mbps = Math.Round(steady.Average(x => x.P.DiskWriteBytesPerSecond) / 1e6, 1),
                cache_max_mib = Math.Round(s.Max(x => x.P.CacheUsedMiB)),
                downloaded_mib = Math.Round(bytes / 1048576.0),
                blocks = s.Count,
                process_priorities = priorities,
            };
        }
    }
}
