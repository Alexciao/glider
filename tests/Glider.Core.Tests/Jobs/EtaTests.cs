using System.Globalization;
using Glider.Core.Contracts;
using Glider.Core.Jobs;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Jobs;

/// <summary>
/// Time left on the owner's machine: Fortnite (install tags) onto a slow SATA SSD. The drive sustains ~8 MiB/s, Legendary keeps
/// downloading into its shared-memory cache until that is full and then stops, so the network arrives in bursts (their log: 21.6,
/// 0.0, 1.4, 14.6, 2.0 MiB/s half a minute apart) and the writer drains a full cache after the last byte arrives. Each run here
/// is Legendary's own output (the summary lines and the five-line progress block, once a second) through the real parser.
/// </summary>
public class EtaTests
{
    private const double MiB = 1024 * 1024;

    // The owner's Fortnite repair, verbatim from their log.
    private static readonly string[] FortniteSummary =
    [
        "[DLM] INFO: Found 277 files to skip based on install tag.",
        "[DLM] INFO: Remaining files after filtering: 734",
        "[cli] INFO: Install size: 13026.59 MiB",
        "[cli] INFO: Download size: 12169.49 MiB (Compression savings: 6.6%)",
        "[cli] INFO: Reusable size: 0.00 MiB (chunks) / 140151.13 MiB (unchanged / skipped)",
    ];

    [Fact]
    public void ATaggedRunsWriteTotal_IsWhatTheWriterWrites_NotInstallMinusSkipped()
    {
        var sizes = SizesOf(FortniteSummary);
        Assert.True(sizes.Filtered);
        Assert.True(sizes.InstallBytes - sizes.UnchangedBytes < 0); // 13,027 − 140,151 MiB: what the old subtraction gave
        Assert.Equal(12169.49 / (1 - 0.066), sizes.UncompressedDownloadBytes!.Value / MiB, tolerance: 0.01);
        Assert.Equal(13029.43, JobProgressTracker.WriteTotal(sizes)!.Value / MiB, tolerance: 0.1); // ≈ the 13,026.59 MiB of files it adds

        // An untagged update: install − unchanged is exact.
        Assert.Equal(1600 * (long)MiB - 560 * (long)MiB, JobProgressTracker.WriteTotal(new LegendaryRunSizes(650 * (long)MiB, 1600 * (long)MiB, 560 * (long)MiB, 0, 700 * (long)MiB)));
    }

    [Fact]
    public void ADiskBoundTaggedDownload_CountsDownToTheWritersEnd_WithoutJumps()
    {
        // 2,000 MiB to download (6.6% compression, 2,141 MiB to write), a 1 GiB cache, a 25 MiB/s line, an 8 MiB/s drive.
        var run = new SimulatedRun(downloadMiB: 2000, savings: 0.066, cacheMiB: 1024, netMiBps: 25, diskMiBps: 8);
        var time = new ManualTimeProvider();
        var emitted = new List<JobProgress>();
        using var t = new JobProgressTracker("j", "Fortnite", JobSegment.Download(0, 90), new ProgressBaseline(), time, TimeSpan.Zero, emitted.Add, _ => { });
        t.OnPhase(JobPhase.Downloading);
        t.OnSizes(SizesOf([
            "[DLM] INFO: Remaining files after filtering: 734",
            "[cli] INFO: Install size: 2141.33 MiB",
            "[cli] INFO: Download size: 2000.00 MiB (Compression savings: 6.6%)",
            "[cli] INFO: Reusable size: 0.00 MiB (chunks) / 140151.13 MiB (unchanged / skipped)",
        ]));

        var parser = new ProgressParser();
        double? previous = null;
        var sawTail = false;
        while (!run.Done)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            run.Step();
            foreach (var line in run.Block())
            {
                Assert.True(parser.TryParse(line, out _), line);
            }

            t.Report(parser.Snapshot);
            var p = emitted[^1];
            var truth = run.SecondsLeft;
            if (run.Elapsed >= 40 && truth > 20)
            {
                Assert.NotNull(p.EtaSeconds);
                var eta = p.EtaSeconds!.Value;
                // Within 15% of when the writer really finishes, and a countdown: never more than a few seconds up.
                Assert.True(Math.Abs(eta - truth) <= Math.Max(0.15 * truth, 8), $"t={run.Elapsed}s: ETA {eta:0} s, really {truth:0} s ({p.Phase})");
                Assert.True(previous is null || eta <= previous + 3, $"t={run.Elapsed}s: ETA jumped {previous:0} -> {eta:0}");
            }

            if (p.Phase == JobPhase.Installing && truth > 60)
            {
                sawTail = true; // the cache still to drain after the last byte arrived: not "less than a minute"
                Assert.True(p.EtaSeconds > 0.8 * truth, $"t={run.Elapsed}s: tail ETA {p.EtaSeconds:0} s, really {truth:0} s");
            }

            previous = p.EtaSeconds;
        }

        Assert.True(sawTail);
    }

    [Fact]
    public void WithNoByteTotalForTheWriter_ItsPercentPaceStillCountsTheTail()
    {
        // No summary at all (a run whose summary lines were lost): only Legendary's percent, which counts chunks written.
        var run = new SimulatedRun(downloadMiB: 1000, savings: 0, cacheMiB: 512, netMiBps: 40, diskMiBps: 8);
        var time = new ManualTimeProvider();
        var emitted = new List<JobProgress>();
        using var t = new JobProgressTracker("j", "Game", JobSegment.Download(0, 90), new ProgressBaseline(), time, TimeSpan.Zero, emitted.Add, _ => { });
        t.OnPhase(JobPhase.Downloading);
        t.OnSizes(new LegendaryRunSizes(1000 * (long)MiB, null, null, null));
        var parser = new ProgressParser();
        while (!run.Done)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            run.Step();
            foreach (var line in run.Block())
            {
                Assert.True(parser.TryParse(line, out _), line);
            }

            t.Report(parser.Snapshot);
            if (run.Elapsed >= 30 && run.SecondsLeft > 20)
            {
                var eta = emitted[^1].EtaSeconds!.Value;
                Assert.True(Math.Abs(eta - run.SecondsLeft) <= Math.Max(0.15 * run.SecondsLeft, 8), $"t={run.Elapsed}s: ETA {eta:0} s, really {run.SecondsLeft:0} s");
            }
        }
    }

    [Fact]
    public void ABurstyNetwork_GivesASteadyEstimate_FromTheBytesThatArrived()
    {
        // 30 MiB/s for 2 s, then nothing for 6 s (7.5 MiB/s on average): remaining ÷ a 3 s speed average swung 3x.
        var time = new ManualTimeProvider();
        var s = new ProgressSmoother(time);
        long remaining = 3000 * (long)MiB;
        var etas = new List<double>();
        for (var sec = 0; sec < 240; sec++)
        {
            var raw = sec % 8 < 2 ? 30 * MiB : 0;
            remaining -= (long)raw;
            s.Update(raw, 7.5 * MiB, 0, remaining, null);
            if (sec >= 70)
            {
                var truth = remaining / (7.5 * MiB);
                etas.Add(s.EtaSeconds!.Value);
                Assert.True(Math.Abs(s.EtaSeconds!.Value - truth) <= 0.12 * truth, $"t={sec}: {s.EtaSeconds:0} vs {truth:0}");
            }

            time.Advance(TimeSpan.FromSeconds(1));
        }

        for (var i = 1; i < etas.Count; i++)
        {
            Assert.True(etas[i] <= etas[i - 1] + 3, $"went up at {i}: {etas[i - 1]:0} -> {etas[i]:0}");
        }
    }

    [Fact]
    public void AWholeWindowWithoutProgress_HoldsTheEstimate_InsteadOfCountingDownToZero()
    {
        var time = new ManualTimeProvider();
        var s = new ProgressSmoother(time);
        long remaining = 600 * (long)MiB;
        for (var sec = 0; sec < 30; sec++)
        {
            remaining -= (long)(10 * MiB);
            s.Update(10 * MiB, 10 * MiB, 0, remaining, null);
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var before = s.EtaSeconds!.Value;
        Assert.InRange(before, 25, 35);
        for (var sec = 0; sec < 90; sec++)
        {
            s.Update(0, 0, 0, remaining, null); // a stalled CDN: nothing arrives
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.True(s.EtaSeconds >= before, $"{s.EtaSeconds} < {before}");
    }

    private static LegendaryRunSizes SizesOf(IEnumerable<string> lines)
    {
        var facts = new LegendaryOutputFacts();
        foreach (var l in lines)
        {
            facts.Observe(l, stdout: false);
        }

        return new LegendaryRunSizes(facts.DownloadSizeBytes, facts.InstallSizeBytes, facts.UnchangedBytes, facts.ReusableBytes, facts.UncompressedDownloadBytes, facts.FilteredFiles);
    }

    /// <summary>
    /// One second at a time, what Legendary's download manager does: workers download (compressed) while the shared-memory cache
    /// has room, the writer puts decompressed chunks on the drive at the drive's pace, and the progress block reports the counters
    /// (percent = chunks written, about 1 MiB each; ETA = chunks left ÷ the run's average chunk rate). The drive's pace wobbles
    /// ±40% second to second around its average, as a real SATA SSD's does.
    /// </summary>
    private sealed class SimulatedRun(double downloadMiB, double savings, double cacheMiB, double netMiBps, double diskMiBps)
    {
        private readonly Random _rnd = new(11);
        private readonly double _writeMiB = downloadMiB / (1 - savings);
        private double _downloaded;
        private double _written;
        private double _netRate;
        private double _diskRate;

        public int Elapsed { get; private set; }

        public bool Done => _written >= _writeMiB - 1e-6;

        /// <summary>When the writer really finishes, from now.</summary>
        public double SecondsLeft => (_writeMiB - _written) / diskMiBps;

        public void Step()
        {
            Elapsed++;
            var cached = _downloaded / (1 - savings) - _written;
            var get = cached < cacheMiB ? Math.Min(netMiBps, downloadMiB - _downloaded) : 0;
            _downloaded += get;
            var put = Math.Min(diskMiBps * (0.6 + 0.8 * _rnd.NextDouble()), _downloaded / (1 - savings) - _written);
            _written += put;
            _netRate = get;
            _diskRate = put;
        }

        public IEnumerable<string> Block()
        {
            var chunks = (long)Math.Ceiling(_writeMiB);
            var done = (long)Math.Floor(_written);
            var pct = done * 100.0 / chunks;
            var eta = done > 0 ? (long)((chunks - done) / (done / (double)Elapsed)) : 0;
            var cache = _downloaded / (1 - savings) - _written;
            static string Clock(long s) => $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}";
            string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
            yield return $"[DLManager] INFO: = Progress: {F(pct)}% ({done}/{chunks}), Running for {Clock(Elapsed)}, ETA: {Clock(eta)}";
            yield return $"[DLManager] INFO:  - Downloaded: {F(_downloaded)} MiB, Written: {F(_written)} MiB";
            yield return $"[DLManager] INFO:  - Cache usage: {F(cache)} MiB, active tasks: {(cache < cacheMiB ? 16 : 0)}";
            yield return $"[DLManager] INFO:  + Download\t- {F(_netRate)} MiB/s (raw) / {F(_netRate / (1 - savings))} MiB/s (decompressed)";
            yield return $"[DLManager] INFO:  + Disk\t- {F(_diskRate)} MiB/s (write) / 0.00 MiB/s (read)";
        }
    }
}
