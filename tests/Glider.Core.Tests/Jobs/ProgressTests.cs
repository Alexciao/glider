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

    private static JobProgress P(double pct) => new("j", JobPhase.Downloading, pct, 0, 0, 0, 0, 0, 0, 0, null, 0);
}

public class JobProgressTrackerTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public void CombinesRunsSoResumeNeverJumpsBackwards()
    {
        var time = new ManualTimeProvider();
        var emitted = new List<JobProgress>();
        var baseline = new ProgressBaseline();
        using (var run1 = new JobProgressTracker("j", baseline, time, TimeSpan.Zero, emitted.Add, _ => { }))
        {
            run1.OnPhase(JobPhase.Downloading);
            run1.OnSizes(new LegendaryRunSizes(1000 * MiB, 1600 * MiB, 0, 0));
            run1.Report(Raw(40, 400 * MiB));
            Assert.Equal(40, emitted[^1].Percent);
            Assert.Equal(400 * MiB, emitted[^1].DownloadedBytes);
            Assert.Equal(1000 * MiB, emitted[^1].TotalDownloadBytes);
            time.Advance(TimeSpan.FromSeconds(30));
            baseline = run1.Snapshot();
        }

        // paused, resumed: Legendary restarts at 0% of the remaining 650 MiB (some in-flight files were lost)
        using var run2 = new JobProgressTracker("j", baseline, time, TimeSpan.Zero, emitted.Add, _ => { });
        run2.OnPhase(JobPhase.Downloading);
        run2.OnSizes(new LegendaryRunSizes(650 * MiB, 1600 * MiB, 560 * MiB, 0));
        run2.Report(Raw(0, 0));
        Assert.Equal(40, emitted[^1].Percent);                 // held, not reset to 35% or 0%
        Assert.Equal(350 * MiB, emitted[^1].DownloadedBytes);
        run2.Report(Raw(50, 325 * MiB));
        Assert.Equal(67.5, emitted[^1].Percent);               // 35 + 65 * 0.5
        Assert.Equal(675 * MiB, emitted[^1].DownloadedBytes);
        Assert.Equal(1000 * MiB, emitted[^1].TotalDownloadBytes);
        Assert.Equal(30, emitted[^1].ElapsedSeconds);
    }

    [Fact]
    public void VerifyAndDownloadAreSeparateBars()
    {
        var time = new ManualTimeProvider();
        var emitted = new List<JobProgress>();
        using var t = new JobProgressTracker("j", new ProgressBaseline(), time, TimeSpan.Zero, emitted.Add, _ => { });
        t.OnPhase(JobPhase.Verifying);
        t.Report(new LegendaryProgress(100, 10, 10, 0, null, 0, 0, 0, 0, 0, 500 * MiB));
        Assert.Equal(100, emitted[^1].Percent);
        Assert.Equal(500 * MiB, emitted[^1].DiskBytesPerSecond);
        t.OnPhase(JobPhase.Downloading); // legendary repair: verify first, then download
        t.Report(Raw(10, 10 * MiB));
        Assert.Equal(10, emitted[^1].Percent);
        Assert.Equal(JobPhase.Downloading, emitted[^1].Phase);
    }

    private static LegendaryProgress Raw(double pct, long downloaded) =>
        new(pct, (long)pct, 100, 1, 60, downloaded, downloaded * 2, 50 * MiB, 80 * MiB, 80 * MiB, 0);
}
