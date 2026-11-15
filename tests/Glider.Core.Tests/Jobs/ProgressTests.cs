using Glider.Core.Contracts;
using Glider.Core.Jobs;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Jobs;

public class ProgressSmootherTests
{
    private const double MiB = 1024 * 1024;

    [Fact]
    public void SpeedsConvergeAndEtaCountsDownWithoutJitter()
    {
        var time = new ManualTimeProvider();
        var s = new ProgressSmoother(time);
        var rnd = new Random(7);
        long total = 6200L * 1024 * 1024, done = 0;
        double? previousEta = null;
        for (var t = 0; t < 60; t++)
        {
            var raw = 100 * MiB * (0.8 + 0.4 * rnd.NextDouble()); // ±20% jitter around 100 MiB/s
            done += (long)raw;
            s.Update(raw, raw * 1.6, done * 100.0 / total, total - done, null);
            if (t > 10)
            {
                Assert.InRange(s.DownloadBytesPerSecond / MiB, 85, 115);
                Assert.NotNull(s.EtaSeconds);
                // a countdown: never goes up, and drops by roughly a second per second
                Assert.True(s.EtaSeconds <= previousEta + 0.5, $"ETA went up at t={t}: {previousEta} -> {s.EtaSeconds}");
                Assert.InRange(previousEta!.Value - s.EtaSeconds!.Value, 0, 3);
            }

            previousEta = s.EtaSeconds;
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.InRange(s.EtaSeconds!.Value, 0, 5);
    }

    [Fact]
    public void FallsBackToPercentRateThenLegendaryEta()
    {
        var time = new ManualTimeProvider();
        var s = new ProgressSmoother(time);
        s.Update(0, 500 * MiB, 0, null, 120);
        Assert.Equal(120, s.EtaSeconds); // nothing measured yet: Legendary's own estimate
        for (var i = 1; i <= 10; i++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            s.Update(0, 500 * MiB, i * 2.0, null, null); // verify: 2%/s, no byte totals
        }

        Assert.InRange(s.EtaSeconds!.Value, 35, 45); // (100 - 20) / 2
    }
}

public class WriteEtaTests
{
    private const double MiB = 1024 * 1024;

    [Fact]
    public void TheWriteTailCountsDownAtTheDrivesPace_AndIsNullWithoutTotals()
    {
        var time = new ManualTimeProvider();
        var s = new ProgressSmoother(time);
        long owed = 1000L * (long)MiB; // 1000 MiB still to write at 10 MiB/s: 100 s
        s.Update(0, 10 * MiB, 50, 0, null, owed);
        Assert.InRange(s.WriteEtaSeconds!.Value, 99, 101);
        double? previous = s.WriteEtaSeconds;
        for (var t = 0; t < 40; t++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            owed -= (long)(10 * MiB);
            s.Update(0, 10 * MiB, 50, 0, null, owed);
            Assert.True(s.WriteEtaSeconds <= previous + 0.5, $"went up at {t}");
            previous = s.WriteEtaSeconds;
        }

        Assert.InRange(s.WriteEtaSeconds!.Value, 58, 62);
        time.Advance(TimeSpan.FromSeconds(1));
        s.Update(0, 10 * MiB, 50, 0, null, null);
        Assert.Null(s.WriteEtaSeconds); // the writer's total is unknown: nothing to say
        time.Advance(TimeSpan.FromSeconds(1));
        s.Update(0, 10 * MiB, 50, 0, null, 0);
        Assert.Equal(0, s.WriteEtaSeconds);
    }
}

public class ProgressThrottleTests
{
    [Fact]
    public void EmitsAtMostTenPerSecondAndAlwaysTheLatest()
    {
        var time = new ManualTimeProvider();
        var emitted = new List<JobProgress>();
        using var throttle = new ProgressThrottle(time, TimeSpan.FromMilliseconds(100), emitted.Add);
        for (var i = 0; i < 1000; i++)
        {
            throttle.Post(P(i));
            time.Advance(TimeSpan.FromMilliseconds(1));
        }

        time.Advance(TimeSpan.FromMilliseconds(200)); // trailing edge fires
        Assert.InRange(emitted.Count, 9, 12);
        Assert.Equal(0, emitted[0].Percent);             // leading edge is immediate
        Assert.Equal(999, emitted[^1].Percent);          // the last value is never lost
    }

    private static JobProgress P(double pct) => new("j", "Fortnite", JobPhase.Downloading, pct, pct, 0, null, null, null, null, null, null, DownloadMode.Balanced, Limiter.Unknown);
}

public class JobProgressTrackerTests
{
    private const long MiB = 1024 * 1024;

    private static JobProgressTracker Tracker(ProgressBaseline baseline, ManualTimeProvider time, List<JobProgress> emitted, JobSegment? segment = null) =>
        new("j", "Fortnite", segment ?? JobSegment.Download(0, 100), baseline, time, TimeSpan.Zero, emitted.Add, _ => { });

    [Fact]
    public void CombinesRunsSoResumeNeverJumpsBackwards()
    {
        var time = new ManualTimeProvider();
        var emitted = new List<JobProgress>();
        var baseline = new ProgressBaseline();
        using (var run1 = Tracker(baseline, time, emitted))
        {
            run1.OnPhase(JobPhase.Downloading);
            run1.OnSizes(new LegendaryRunSizes(1000 * MiB, 1600 * MiB, 0, 0));
            run1.Report(Raw(40, 400 * MiB));
            Assert.Equal(40, emitted[^1].Percent);
            Assert.Equal(400 * MiB, emitted[^1].Network!.DownloadedBytes);
            Assert.Equal(1000 * MiB, emitted[^1].Network!.TotalBytes);
            time.Advance(TimeSpan.FromSeconds(30));
            baseline = run1.Snapshot();
        }

        // paused, resumed: Legendary restarts at 0% of the remaining 650 MiB (some in-flight files were lost)
        using var run2 = Tracker(baseline, time, emitted);
        run2.OnPhase(JobPhase.Downloading);
        run2.OnSizes(new LegendaryRunSizes(650 * MiB, 1600 * MiB, 560 * MiB, 0));
        run2.Report(Raw(0, 0));
        Assert.Equal(40, emitted[^1].Percent);                 // held, not reset to 35% or 0%
        Assert.Equal(350 * MiB, emitted[^1].Network!.DownloadedBytes);
        run2.Report(Raw(50, 325 * MiB));
        Assert.Equal(67.5, emitted[^1].Percent);               // 35 + 65 * 0.5
        Assert.Equal(675 * MiB, emitted[^1].Network!.DownloadedBytes);
        Assert.Equal(1000 * MiB, emitted[^1].Network!.TotalBytes);
        Assert.Equal(30, emitted[^1].ElapsedSeconds);
    }

    [Fact]
    public void VerifyAndDownloadAreSeparatePhaseBars_OnOneMonotonicJobBar()
    {
        var time = new ManualTimeProvider();
        var emitted = new List<JobProgress>();
        using var t = Tracker(new ProgressBaseline(), time, emitted, new JobSegment(0, 35, 35, 99, Repairing: true)); // `legendary repair`
        t.OnPhase(JobPhase.Verifying);
        t.Report(new LegendaryProgress(100, 10, 10, 0, null, 0, 0, 0, 0, 0, 500 * MiB));
        var verify = emitted[^1];
        Assert.Equal(100, verify.PhasePercent);
        Assert.Equal(35, verify.Percent);
        Assert.Null(verify.Network);                            // verifying only reads
        Assert.Equal(500 * MiB, verify.Verify!.ReadBytesPerSecond);
        Assert.Equal(10, verify.FilesDone);

        t.OnPhase(JobPhase.Downloading); // the download inside a repair step is "Repairing"
        t.Report(Raw(10, 10 * MiB));
        var repair = emitted[^1];
        Assert.Equal(10, repair.PhasePercent);
        Assert.Equal(JobPhase.Repairing, repair.Phase);
        Assert.True(repair.Percent > verify.Percent);
        Assert.NotNull(repair.Network);
        Assert.NotNull(repair.Disk);
        Assert.Null(repair.Verify);
    }

    [Fact]
    public void TheWriterTailAfterTheNetworkIsInstalling_WithDiskFiguresOnly()
    {
        var time = new ManualTimeProvider();
        var emitted = new List<JobProgress>();
        var phases = new List<JobPhase>();
        using var t = new JobProgressTracker("j", "Fortnite", JobSegment.Download(0, 90), new ProgressBaseline(), time, TimeSpan.Zero, emitted.Add, phases.Add);
        t.OnPhase(JobPhase.Downloading);
        t.OnSizes(new LegendaryRunSizes(100 * MiB, 200 * MiB, 0, 0));
        t.Report(Raw(50, 50 * MiB));
        time.Advance(TimeSpan.FromSeconds(1));
        t.Report(Raw(80, 100 * MiB)); // everything downloaded, 20% still to write
        var tail = emitted[^1];
        Assert.Equal(JobPhase.Installing, tail.Phase);
        Assert.Contains(JobPhase.Installing, phases);
        Assert.Null(tail.Network);
        Assert.NotNull(tail.Disk);
        Assert.Equal(Limiter.Disk, tail.Limiter);
        Assert.Equal(72, tail.Percent); // 80% of the 0-90 download slice
        Assert.True(tail.Disk!.PeakBytesPerSecond > 0);
    }

    // Whole-job time left: the download and the writes owed run side by side, so the later of the two is when you can play.

    private static LegendaryProgress RawAt(double pct, long downloaded, double netRate, double writeRate, long written) =>
        new(pct, (long)pct, 100, 1, 60, downloaded, written, netRate, writeRate, writeRate, 0);

    [Fact]
    public void WhileDownloading_TheEtaIsTheSlowerOfTheDownloadAndTheWritesOwed()
    {
        var time = new ManualTimeProvider();
        var emitted = new List<JobProgress>();
        using var t = Tracker(new ProgressBaseline(), time, emitted);
        t.OnPhase(JobPhase.Downloading);
        // 400 MiB to download at 40 MiB/s (10 s), 1200 MiB to write at 8 MiB/s (150 s): the drive decides.
        t.OnSizes(new LegendaryRunSizes(400 * MiB, 1200 * MiB, 0, 0));
        for (var i = 0; i < 4; i++)
        {
            t.Report(RawAt(i, 0, 40 * MiB, 8 * MiB, 0));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        var eta = emitted[^1].EtaSeconds!.Value;
        Assert.InRange(eta, 130, 160);
        Assert.True(eta > (400.0 - 0) / 40 + 100, "the write tail is counted, not just the download");
    }

    [Fact]
    public void WhileInstalling_TheEtaKeepsAValue_FromTheWritesOwed()
    {
        var time = new ManualTimeProvider();
        var emitted = new List<JobProgress>();
        using var t = Tracker(new ProgressBaseline(), time, emitted, JobSegment.Download(0, 90));
        t.OnPhase(JobPhase.Downloading);
        t.OnSizes(new LegendaryRunSizes(100 * MiB, 1000 * MiB, 0, 0));
        t.Report(RawAt(50, 50 * MiB, 50 * MiB, 20 * MiB, 100 * MiB));
        time.Advance(TimeSpan.FromSeconds(1));
        t.Report(RawAt(80, 100 * MiB, 0, 20 * MiB, 300 * MiB)); // all downloaded: 700 MiB still to write at 20 MiB/s
        var tail = emitted[^1];
        Assert.Equal(JobPhase.Installing, tail.Phase);
        Assert.NotNull(tail.EtaSeconds);
        Assert.InRange(tail.EtaSeconds!.Value, 25, 45);

        time.Advance(TimeSpan.FromSeconds(10));
        t.Report(RawAt(90, 100 * MiB, 0, 20 * MiB, 500 * MiB));
        Assert.True(emitted[^1].EtaSeconds < tail.EtaSeconds, "and it counts down");
    }

    [Fact]
    public void WithoutTheWritersTotal_TheEtaIsTheDownloadsAsBefore_AndNullWhileInstalling()
    {
        var time = new ManualTimeProvider();
        var emitted = new List<JobProgress>();
        using var t = Tracker(new ProgressBaseline(), time, emitted);
        t.OnPhase(JobPhase.Downloading);
        t.OnSizes(new LegendaryRunSizes(1000 * MiB, null, null, null));
        for (var i = 0; i < 3; i++)
        {
            t.Report(RawAt(10 + i, 100 * MiB, 50 * MiB, 50 * MiB, 0));
            time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.InRange(emitted[^1].EtaSeconds!.Value, 10, 25); // 900 MiB at 50 MiB/s, nothing owed to the writer
    }

    [Fact]
    public void TheConnectionsCeiling_IsTheLargerOfThePeakAndTheProbe_AndNullWhileUnknown()
    {
        Assert.Null(LimiterDecision.Ceiling(0, null));
        Assert.Null(LimiterDecision.Ceiling(0, 0));
        Assert.Equal(40_000_000, LimiterDecision.Ceiling(8_000_000, 40));   // the probe saw more than the job has reached
        Assert.Equal(50_000_000, LimiterDecision.Ceiling(50_000_000, 40));  // the job has already beaten the probe
        Assert.Equal(8_000_000, LimiterDecision.Ceiling(8_000_000, null));

        var time = new ManualTimeProvider();
        var emitted = new List<JobProgress>();
        using var t = Tracker(new ProgressBaseline(), time, emitted);
        t.OnRunInfo(new RunInfo("cdn.example", 8, 1024, 40));
        t.OnPhase(JobPhase.Downloading);
        t.OnSizes(new LegendaryRunSizes(1000 * MiB, 1000 * MiB, 0, 0));
        t.Report(RawAt(5, 50 * MiB, 8 * MiB, 8 * MiB, 50 * MiB));
        Assert.Equal(40_000_000, emitted[^1].Network!.CeilingBytesPerSecond);
    }

    private static LegendaryProgress Raw(double pct, long downloaded) =>
        new(pct, (long)pct, 100, 1, 60, downloaded, downloaded * 2, 50 * MiB, 80 * MiB, 80 * MiB, 0);
}

public class JobSamplesTests
{
    [Fact]
    public void KeepsAboutOneSamplePerSecond_AndNeverMoreThanTheCap()
    {
        var s = new JobSamples();
        for (var i = 0; i < 5000; i++)
        {
            s.Add(new JobSample(i * 0.5, i, i));
        }

        var all = s.Snapshot();
        Assert.InRange(all.Count, 300, JobSamples.Max);
        Assert.Equal(all.Select(x => x.T).Order(), all.Select(x => x.T));
    }
}
