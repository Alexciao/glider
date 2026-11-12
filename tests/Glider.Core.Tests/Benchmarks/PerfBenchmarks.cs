using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Glider.Core.Contracts;
using Glider.Core.Legendary;
using Glider.Core.Tests.EndToEnd;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Benchmarks;

/// <summary>
/// Stopwatch micro-benchmarks behind docs/perf.md. They print their numbers (test output) and assert only loose floors,
/// so they stay green on a loaded CI machine while still catching an order-of-magnitude regression.
/// </summary>
public sealed class PerfBenchmarks
{
    private static void Report(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    /// <summary>
    /// Legendary's stderr during a download (5-line DLManager block per second) plus verify's stdout lines, through the
    /// exact path the runner uses: LinePump (decode + split) -> ProgressParser -> LegendaryOutputFacts for non-progress lines.
    /// </summary>
    [Fact]
    public void ProgressLineParsingThroughput()
    {
        var download = DownloadOutput(blocks: 40_000);
        var verify = VerifyOutput(files: 100_000);

        var (dlLines, dlMs, dlAlloc, dlPublished) = Pump(download, LegendaryEncoding.Stderr, unescape: true);
        var (vLines, vMs, vAlloc, vPublished) = Pump(verify, LegendaryEncoding.Stdout, unescape: false);

        Report($"download: {dlLines:N0} lines ({download.Length / 1048576.0:0.0} MiB) in {dlMs:0.0} ms = {dlLines / dlMs * 1000:N0} lines/s, " +
               $"{dlAlloc / (double)dlLines:0.00} B allocated/line, {dlPublished:N0} snapshots");
        Report($"verify:   {vLines:N0} lines ({verify.Length / 1048576.0:0.0} MiB) in {vMs:0.0} ms = {vLines / vMs * 1000:N0} lines/s, " +
               $"{vAlloc / (double)vLines:0.00} B allocated/line, {vPublished:N0} snapshots");

        Assert.Equal(40_000, dlPublished);
        Assert.Equal(100_000, vPublished);
        // Legendary prints 5 lines/s while downloading; even 100k lines/s would be 20,000x headroom.
        Assert.True(dlLines / dlMs * 1000 > 100_000, "download parsing regressed");
        Assert.True(dlAlloc / (double)dlLines < 4, "progress lines should not allocate per line");
    }

    private static (int Lines, double Ms, long Allocated, int Published) Pump(byte[] data, Encoding encoding, bool unescape)
    {
        // Warm-up pass (JIT), then the measured pass.
        Run(data.AsSpan(0, Math.Min(data.Length, 64 * 1024)).ToArray(), encoding, unescape);
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var (lines, published) = Run(data, encoding, unescape);
        sw.Stop();
        return (lines, sw.Elapsed.TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - before, published);
    }

    private static (int Lines, int Published) Run(byte[] data, Encoding encoding, bool unescape)
    {
        var parser = new ProgressParser();
        var facts = new LegendaryOutputFacts();
        int lines = 0, published = 0;
        var pump = new LinePump(encoding, unescape, line =>
        {
            lines++;
            if (parser.TryParse(line, out var p))
            {
                published += p ? 1 : 0;
                return;
            }

            facts.Observe(line, stdout: !unescape);
        });

        // Feed in 16 KiB reads, like the PipeReader in production.
        // Unconsumed bytes stay in place (no copies), like the PipeReader's buffer.
        var consumed = 0;
        var available = 0;
        while (available < data.Length)
        {
            available = Math.Min(available + 16 * 1024, data.Length);
            var seq = new ReadOnlySequence<byte>(data, consumed, available - consumed);
            var length = seq.Length;
            pump.Consume(ref seq, final: available == data.Length);
            consumed += (int)(length - seq.Length);
        }

        if (parser.TryFlush())
        {
            published++;
        }

        return (lines, published);
    }

    private static byte[] DownloadOutput(int blocks)
    {
        var sb = new StringBuilder();
        static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
        for (var i = 1; i <= blocks; i++)
        {
            var pct = i * 100.0 / blocks;
            sb.Append(CultureInfo.InvariantCulture, $"[DLManager] INFO: = Progress: {F(pct)}% ({i}/{blocks}), Running for 00:{i / 60 % 60:D2}:{i % 60:D2}, ETA: 00:09:51\r\n");
            sb.Append(CultureInfo.InvariantCulture, $"[DLManager] INFO:  - Downloaded: {F(i * 1.5)} MiB, Written: {F(i * 2.4)} MiB\r\n");
            sb.Append("[DLManager] INFO:  - Cache usage: 96.00 MiB, active tasks: 16\r\n");
            sb.Append("[DLManager] INFO:  + Download\t- 55.20 MiB/s (raw) / 120.40 MiB/s (decompressed)\r\n");
            sb.Append("[DLManager] INFO:  + Disk\t- 118.00 MiB/s (write) / 0.00 MiB/s (read)\r\n");
        }

        return Fixture.Cp1252.GetBytes(sb.ToString());
    }

    private static byte[] VerifyOutput(int files)
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= files; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Verification progress: {i}/{files} ({(i * 100.0 / files).ToString("F1", CultureInfo.InvariantCulture)}%) [512.3 MiB/s]\t\r");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Play -> game process start through the real runtime. Legendary's `launch --json` is modelled by a FakeLegendary
    /// script that sleeps for the measured PyInstaller start of the real exe (~1.4 s, docs/perf.md) — a lower bound,
    /// since the real command also makes 1-3 Epic API calls.
    /// </summary>
    [Fact]
    public async Task PlayToProcessStart()
    {
        const int legendaryStartMs = 1400;
        await using var h = await EndToEndHarness.StartAsync(EglTestTreeVersions.V1);
        var (game, _) = h.EglInstall(EglTestTreeVersions.V1);
        await h.Rpc.Adopt(new AdoptRequest(null, game, VerifyMode.Auto));
        var dir = game.Replace(Path.DirectorySeparatorChar, '/');
        h.Legendary.Script("launch", $$"""
            sleep {{legendaryStartMs}}
            out {"game_parameters": [], "game_executable": "FortniteGame/Binaries/Win64/FortniteLauncher.exe", "game_directory": "{{dir}}", "egl_parameters": ["-AUTH_PASSWORD=code"], "launch_command": [], "working_directory": "", "user_parameters": [], "environment": {}, "pre_launch_command": "", "pre_launch_wait": false}
            """);

        var cold = new List<double>();
        var prepared = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            cold.Add(await TimeLaunch(h, prepare: false));
            prepared.Add(await TimeLaunch(h, prepare: true));
        }

        Report($"Play -> process start, launch --json modelled at {legendaryStartMs} ms: cold {Median(cold):0} ms (runs {string.Join(", ", cold.Select(x => x.ToString("0", CultureInfo.InvariantCulture)))}), " +
               $"prepared {Median(prepared):0.0} ms (runs {string.Join(", ", prepared.Select(x => x.ToString("0.0", CultureInfo.InvariantCulture)))})");
        Assert.True(Median(cold) >= legendaryStartMs);
        Assert.True(Median(prepared) < 250, $"prepared launch took {Median(prepared)} ms");
    }

    private static async Task<double> TimeLaunch(EndToEndHarness h, bool prepare)
    {
        if (prepare)
        {
            await h.Rpc.PrepareLaunch();
            await h.Runtime.Service.LaunchServiceForTests.Preparing;
        }

        var before = h.GameProcesses.Started.Count;
        var sw = Stopwatch.StartNew();
        await h.Rpc.Launch();
        var ms = sw.Elapsed.TotalMilliseconds;
        Assert.Equal(before + 1, h.GameProcesses.Started.Count);

        // Pretend the game exited at once (the watcher's own timeouts are tens of seconds).
        h.Runtime.Service.LaunchServiceForTests.Watcher.Reset();
        h.Runtime.Service.Hub.Mutate(s => s with { RunState = RunState.Idle, RunningSince = null });
        return ms;
    }

    private static double Median(List<double> xs)
    {
        var s = xs.Order().ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}

internal static class EglTestTreeVersions
{
    public const string V1 = Egl.EglTestTree.V3310;
}
