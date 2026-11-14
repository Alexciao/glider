using System.Diagnostics;
using System.Runtime.Versioning;
using Glider.Core.Contracts;
using Glider.Core.Jobs;
using Glider.Core.Legendary;
using Glider.Core.Services;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Legendary;

/// <summary>Per-mode process settings, who is the writer, and the live control (docs/download-perf.md §5.6).</summary>
public class DownloadModeTests
{
    [Fact]
    public void ModesMapToProcessSettings()
    {
        Assert.Equal(ProcessSettings.Normal, ModeSettings.For(DownloadMode.Full, isWriter: true, wholeTree: false));
        var writer = ModeSettings.For(DownloadMode.Balanced, isWriter: true, wholeTree: false);
        Assert.Equal(new ProcessSettings(CpuPriority.BelowNormal, IoPriority.Low, MemoryPriority.Low, false), writer);
        // The network side keeps normal CPU and I/O (so it matches Full); only its memory priority is low.
        Assert.Equal(new ProcessSettings(CpuPriority.Normal, IoPriority.Normal, MemoryPriority.Low, false), ModeSettings.For(DownloadMode.Balanced, false, false));
        Assert.Equal(writer, ModeSettings.For(DownloadMode.Balanced, false, wholeTree: true)); // verify has no separate writer
        var background = new ProcessSettings(CpuPriority.Idle, IoPriority.VeryLow, MemoryPriority.VeryLow, true);
        Assert.Equal(background, ModeSettings.For(DownloadMode.Background, false, false));
        Assert.Equal(background, ModeSettings.For(DownloadMode.Background, true, false));
    }

    [Fact]
    public void ThePolicyGivesLiveControlToInstallsAndCapsBackgroundWorkers()
    {
        var policy = new ModeResourcePolicy(() => 5_000_000);
        var balanced = policy.For(DownloadMode.Balanced, JobKind.Update);
        Assert.False(balanced.LowPriority);
        Assert.Equal(DownloadMode.Balanced, balanced.Control!.Mode);
        Assert.Equal(5_000_000, balanced.Control.SpeedLimitBytesPerSecond);
        Assert.Null(balanced.WorkerCap);
        Assert.Equal(ModeResourcePolicy.BackgroundWorkers, policy.For(DownloadMode.Background, JobKind.Install).WorkerCap);
        Assert.True(policy.For(DownloadMode.Balanced, JobKind.Verify).WholeTree);
        Assert.Same(ResourceProfile.Default, policy.For(DownloadMode.Full, JobKind.Move));
        Assert.NotSame(policy.For(DownloadMode.Full, JobKind.Install).Control, policy.For(DownloadMode.Full, JobKind.Install).Control);
    }

    [Fact]
    public void RunControlRaisesChangedOnlyForRealChanges()
    {
        var control = new RunControl(DownloadMode.Balanced);
        var fired = 0;
        control.Changed += () => fired++;
        Assert.False(control.Update(DownloadMode.Balanced, null));
        Assert.False(control.Update(DownloadMode.Balanced, 0)); // 0 = unlimited
        Assert.True(control.Update(DownloadMode.Background, null));
        Assert.True(control.Update(DownloadMode.Background, 1_000_000));
        Assert.Equal(2, fired);
        Assert.Equal(1_000_000, control.SpeedLimitBytesPerSecond);
    }

    private static ProcessEntry E(int id, int parent) => new(id, parent, "legendary", null);

    [Fact]
    public void TheWriterIsTheDownloadManagersNewestChildAndEverythingBelowIt()
    {
        // CLI(1) -> DLManager(10) -> workers 11,12,13 and writer 14 (with a PyInstaller child 15 below it)
        ProcessEntry[] snapshot = [E(1, 0), E(10, 1), E(11, 10), E(12, 10), E(13, 10), E(14, 10), E(15, 14), E(99, 1)];
        int[] job = [1, 10, 11, 12, 13, 14, 15];
        long? Created(int id) => id * 100L;

        var writer = ProcessRoles.Writer(job, 10, snapshot, Created);
        Assert.Equal([14, 15], writer.Order());
        Assert.Equal([11, 12, 13], ProcessRoles.Workers(job, 10, writer, snapshot).Order());
        // Processes outside the job are never touched, and an unknown manager means no writer.
        Assert.Empty(ProcessRoles.Writer(job, 77, snapshot, Created));
        Assert.Empty(ProcessRoles.Writer([1, 11], 10, snapshot, Created));
        Assert.Empty(ProcessRoles.Writer(job, null, snapshot, Created));
    }

    [Fact]
    public void CreationTimeDecidesNotThePidOrder()
    {
        ProcessEntry[] snapshot = [E(10, 1), E(500, 10), E(20, 10)]; // pid 20 was created after pid 500 (pids get reused)
        var writer = ProcessRoles.Writer([10, 500, 20], 10, snapshot, id => id == 20 ? 2000L : 1000L);
        Assert.Equal([20], writer.Order());
    }

    [Fact]
    public void ParsesTheDownloadManagerPidAndTheWriterLine()
    {
        var facts = new LegendaryOutputFacts();
        facts.Observe("[DLManager] INFO: Download Manager running with process-id: 4242", stdout: false);
        Assert.Equal(4242, facts.DownloadManagerPid);
        Assert.False(facts.WriterStarting);
        facts.Observe("[DLManager] INFO: Starting file writing worker...", stdout: false);
        Assert.True(facts.WriterStarting);
    }

    // ------------------------------------------------------------------ real processes (Windows)

    private static Process StartSleeper()
    {
        var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep -Seconds 60") { UseShellExecute = false, CreateNoWindow = true };
        return Process.Start(psi)!;
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void LowersAndRestoresPriorityClassIoAndMemoryPriorityOnARealProcess()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows process priorities");
        using var p = StartSleeper();
        try
        {
            var idle = new ProcessSettings(CpuPriority.Idle, IoPriority.VeryLow, MemoryPriority.VeryLow, EcoQos: true);
            Assert.True(ProcessTuning.TryApply(p.Id, idle));
            var q = ProcessTuning.Query(p.Id)!.Value;
            Assert.Equal(CpuPriority.Idle, q.Cpu);
            Assert.Equal(IoPriority.VeryLow, q.Io);
            Assert.Equal(MemoryPriority.VeryLow, q.Memory);

            var balanced = ModeSettings.For(DownloadMode.Balanced, isWriter: true, wholeTree: false);
            Assert.True(ProcessTuning.TryApply(p.Id, balanced));
            var b = ProcessTuning.Query(p.Id)!.Value;
            Assert.Equal((CpuPriority.BelowNormal, IoPriority.Low, MemoryPriority.Low), (b.Cpu, b.Io, b.Memory));

            // Live mode change back to Full raises everything again (no restart needed, no privilege).
            Assert.True(ProcessTuning.TryApply(p.Id, ProcessSettings.Normal));
            var n = ProcessTuning.Query(p.Id)!.Value;
            Assert.Equal((CpuPriority.Normal, IoPriority.Normal, MemoryPriority.Normal), (n.Cpu, n.Io, n.Memory));
        }
        finally
        {
            p.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void SuspendAndResumeWork()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows process control");
        using var p = StartSleeper();
        try
        {
            Assert.True(ProcessTuning.Suspend(p.Id));
            Assert.True(ProcessTuning.Resume(p.Id));
        }
        finally
        {
            p.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task TheRunnerTunesOnlyTheWriterInBalancedAndTheWholeTreeInBackground()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Job Objects and priorities are Windows-only");
        using var env = new FakeLegendaryEnv();
        // The fake acts as the DLManager: it announces itself, starts two "workers", prints the writer line and then starts the writer.
        env.Script("install", """
            err [DLManager] INFO: Download Manager running with process-id: {pid}
            child w1
            child w2
            err [DLManager] INFO: Starting file writing worker...
            child writer
            sleep 400
            hang
            """);
        using var runner = new LegendaryProcessRunner(env.Paths, env.Log);
        var control = new RunControl(DownloadMode.Balanced);
        var profile = new ResourceProfile(LowPriority: false) { Control = control };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var task = runner.RunAsync(new LegendaryCommand(LegendaryCommandKind.Install, ["install", "Fortnite"]) { Timeout = Timeout.InfiniteTimeSpan, Resources = profile, DownloadWorkers = 2 }, null, cts.Token);

        int Pid(string tag) => int.Parse(File.ReadAllText(Path.Combine(env.FakeDir, $"child.{tag}.pid")));
        await WaitUntil(() => File.Exists(Path.Combine(env.FakeDir, "child.writer.pid")) && new FileInfo(Path.Combine(env.FakeDir, "child.writer.pid")).Length > 0);
        int writer = Pid("writer"), worker = Pid("w1");

        await WaitUntil(() => ProcessTuning.Query(writer)?.Io == IoPriority.Low);
        var w = ProcessTuning.Query(writer)!.Value;
        Assert.Equal((CpuPriority.BelowNormal, IoPriority.Low, MemoryPriority.Low), (w.Cpu, w.Io, w.Memory));
        await WaitUntil(() => ProcessTuning.Query(worker)?.Memory == MemoryPriority.Low);
        var n = ProcessTuning.Query(worker)!.Value;
        Assert.Equal((CpuPriority.Normal, IoPriority.Normal), (n.Cpu, n.Io)); // the network side is not slowed

        control.Update(DownloadMode.Background, null);
        await WaitUntil(() => ProcessTuning.Query(worker)?.Cpu == CpuPriority.Idle);
        Assert.Equal(IoPriority.VeryLow, ProcessTuning.Query(worker)!.Value.Io);
        Assert.True(ProcessTuning.Query(worker)!.Value.EcoQos || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000));

        control.Update(DownloadMode.Full, null);
        await WaitUntil(() => ProcessTuning.Query(writer)?.Io == IoPriority.Normal);
        Assert.Equal(CpuPriority.Normal, ProcessTuning.Query(writer)!.Value.Cpu);

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 20_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < timeoutMs, "timed out waiting for the process tree");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}

/// <summary>Disk-bound and slow-CDN detection (docs/download-perf.md §5.2, §5.7) and the sentences shown for them.</summary>
public class DownloadHealthTests
{
    [Fact]
    public void ACacheThatStaysFullIsDiskBoundButOneThatJustFilledIsNot()
    {
        var d = new DiskBoundDetector();
        for (var t = 0; t < 6; t++)
        {
            d.Add(t * 1000, 1020, 1024);
        }

        Assert.False(d.IsDiskBound); // too few seconds to tell
        for (var t = 6; t < 16; t++)
        {
            d.Add(t * 1000, 1020, 1024);
        }

        Assert.True(d.IsDiskBound);
        for (var t = 16; t < 32; t++)
        {
            d.Add(t * 1000, 100, 1024); // the cache drained: network-bound again
        }

        Assert.False(d.IsDiskBound);
        d.Add(40_000, 1020, 0);
        Assert.False(d.IsDiskBound); // unknown total records nothing
    }

    private static CdnWatch Run(CdnWatch w, int seconds, double bytesPerSecond, long? remaining = 50L << 30, bool disk = false)
    {
        for (var t = 0; t <= seconds; t++)
        {
            w.Add(t * 1000L, bytesPerSecond, remaining, disk);
        }

        return w;
    }

    [Fact]
    public void ThreeSlowMinutesOnAProbedHostSwitchesOnce()
    {
        var w = new CdnWatch(probeMegabytesPerSecond: 40); // threshold 16 MB/s
        Run(w, 100, 8_000_000);
        Assert.True(w.IsSlow);
        Assert.False(w.ShouldSwitch); // slow, but not for three minutes yet
        Run(w, 400, 8_000_000);
        Assert.True(w.ShouldSwitch);
    }

    [Fact]
    public void FastDiskBoundNearlyDoneOrRecoveringJobsNeverSwitch()
    {
        Assert.False(Run(new CdnWatch(40), 500, 30_000_000).ShouldSwitch);                  // above 40 % of the probe
        Assert.False(Run(new CdnWatch(40), 500, 1_000_000, disk: true).ShouldSwitch);       // a full cache is the drive, not the CDN
        Assert.False(Run(new CdnWatch(40), 500, 1_000_000, remaining: 2L << 30).ShouldSwitch); // < 3 GB left: not worth a restart
        var recovering = Run(new CdnWatch(40), 150, 1_000_000);
        for (var t = 151; t < 200; t++)
        {
            recovering.Add(t * 1000L, 40_000_000, 50L << 30, false);
        }

        Assert.False(recovering.IsSlow);
        Assert.True(Run(new CdnWatch(40), 500, 1_000_000).ShouldSwitch); // control: slow for 8 minutes does switch
    }

    [Fact]
    public void WithoutAProbeTheFloorIsFiveMegabytesPerSecond()
    {
        Assert.True(Run(new CdnWatch(null), 400, 4_000_000).ShouldSwitch);
        Assert.False(Run(new CdnWatch(null), 400, 6_000_000).ShouldSwitch);
    }

    private static LimiterInput Input(JobPhase phase = JobPhase.Downloading, DownloadMode mode = DownloadMode.Balanced) => new(
        phase, mode, null, DiskBound: false, CdnSlow: false, SpeedLimitBytesPerSecond: null, RawBytesPerSecond: 30e6, WriteBytesPerSecond: 30e6,
        PeakNetworkBytesPerSecond: 40e6, ProbeMegabytesPerSecond: null, Drive: "D:", CdnHost: "fastly.example", SecondsInPhase: 60);

    [Fact]
    public void LimiterNamesTheRealCauseInPlainLanguage()
    {
        var disk = LimiterDecision.Decide(Input() with { DiskBound = true, WriteBytesPerSecond = 8e6 });
        Assert.Equal(Limiter.Disk, disk.Limiter);
        Assert.StartsWith("Limited by drive D: (≈8.0 MB/s). Your connection can do ≈40 MB/s.", disk.Reason, StringComparison.Ordinal);

        Assert.Equal(Limiter.Cdn, LimiterDecision.Decide(Input() with { CdnSlow = true }).Limiter);
        Assert.Contains("fastly.example", LimiterDecision.Decide(Input() with { CdnSlow = true }).Reason, StringComparison.Ordinal);
        Assert.Equal(Limiter.Network, LimiterDecision.Decide(Input()).Limiter);
        Assert.Equal(Limiter.Unknown, LimiterDecision.Decide(Input() with { SecondsInPhase = 3 }).Limiter);

        var capped = LimiterDecision.Decide(Input() with { SpeedLimitBytesPerSecond = 10_000_000, RawBytesPerSecond = 9.5e6, DiskBound = true });
        Assert.Equal(Limiter.SpeedLimit, capped.Limiter); // a limit the user set explains it before the drive does
        Assert.Equal(Limiter.Unknown, LimiterDecision.Decide(Input() with { SpeedLimitBytesPerSecond = 10_000_000, RawBytesPerSecond = 3e6, SecondsInPhase = 2 }).Limiter);

        var background = LimiterDecision.Decide(Input(mode: DownloadMode.Background) with { ModeReason = "Background mode while Fortnite is running" });
        Assert.Equal((Limiter.Mode, "Background mode while Fortnite is running"), (background.Limiter, background.Reason));

        Assert.Equal(Limiter.Disk, LimiterDecision.Decide(Input(JobPhase.Installing)).Limiter);
        Assert.Equal(Limiter.Disk, LimiterDecision.Decide(Input(JobPhase.Verifying)).Limiter);
        Assert.Equal(Limiter.Unknown, LimiterDecision.Decide(Input(JobPhase.Preparing)).Limiter);
    }
}
