using System.Diagnostics;
using Glider.Core.Contracts;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Legendary;

public class LegendaryProcessRunnerTests
{
    private static LegendaryCommand Cmd(string command, params string[] extra) =>
        new(LegendaryCommandKind.Status, [command, .. extra]) { CaptureStdout = true, Timeout = TimeSpan.FromSeconds(20) };

    [Fact]
    public async Task DecodesBothStreamsAndIsolatesTheConfigDir()
    {
        using var env = new FakeLegendaryEnv();
        env.UseFixture("encoding-test-default.stderr.bin");
        env.Script("status", """
            errbin encoding-test-default.stderr.bin
            out config={config}
            out Ünïcødé 日本 😀
            """);
        using var runner = new LegendaryProcessRunner(env.Paths, env.Log);
        var run = await runner.RunAsync(Cmd("status"), null, TestContext.Current.CancellationToken);

        Assert.Equal(0, run.ExitCode);
        Assert.Equal($"config={env.Config}\nÜnïcødé 日本 😀\n", run.Stdout);
        Assert.True(run.Facts.ImportPathMissing);
        // The captured bytes are cp1252; Legendary would use this machine's ANSI code page, and so does the runner.
        Assert.SkipUnless(LegendaryEncoding.Stderr.CodePage == 1252, "this machine's ANSI code page is not 1252");
        Assert.Contains("[cli] ERROR: Specified path \"D:\\ünïcödé日本\" does not exist!", run.Tail);
    }

    [Fact]
    public async Task StdinIsClosedSoPromptsFailInsteadOfHanging()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("auth", "readstdin");
        using var runner = new LegendaryProcessRunner(env.Paths, env.Log);
        var run = await runner.RunAsync(Cmd("auth"), null, TestContext.Current.CancellationToken);
        Assert.Equal(1, run.ExitCode);
        Assert.True(run.Facts.PromptHitEof);
        Assert.Equal(ErrorCode.LegendaryFailed, ErrorClassifier.Classify(LegendaryCommandKind.AuthCode, run)?.Code);
    }

    [Fact]
    public async Task CancellationKillsTheWholeProcessTree()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("install", """
            child
            err [DLManager] INFO: Download Manager running with process-id: 1
            hang
            """);
        using var runner = new LegendaryProcessRunner(env.Paths, env.Log);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pidFile = Path.Combine(env.FakeDir, "child.pid");
        var task = runner.RunAsync(new LegendaryCommand(LegendaryCommandKind.Install, ["install", "Fortnite"]) { Timeout = Timeout.InfiniteTimeSpan }, null, cts.Token);
        await WaitUntil(() => File.Exists(pidFile) && new FileInfo(pidFile).Length > 0);
        var childPid = int.Parse(await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken));
        Assert.True(IsAlive(childPid));

        var sw = Stopwatch.StartNew();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10));
        await WaitUntil(() => !IsAlive(childPid));
        Assert.Equal(0, runner.ActiveProcesses);
    }

    [Fact]
    public async Task OrphansDieWithTheJobWhenLegendaryExits()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Job Objects are Windows-only; elsewhere only an explicit tree kill reaps children.");
        using var env = new FakeLegendaryEnv();
        env.Script("status", "child");
        using var runner = new LegendaryProcessRunner(env.Paths, env.Log);
        await runner.RunAsync(Cmd("status"), null, TestContext.Current.CancellationToken);
        var childPid = int.Parse(await File.ReadAllTextAsync(Path.Combine(env.FakeDir, "child.pid"), TestContext.Current.CancellationToken));
        await WaitUntil(() => !IsAlive(childPid)); // KILL_ON_JOB_CLOSE reaped the grandchild Legendary left behind
    }

    [Fact]
    public async Task StallWatchdogKillsARunWhoseProgressStopsMoving()
    {
        using var env = new FakeLegendaryEnv();
        // progresses for 4 files, then the network "drops": retry spam + identical progress blocks forever
        env.Script("install", "download chunks=100 files=10 mib=50 interval=20 stallat=40 spam");
        using var runner = new LegendaryProcessRunner(env.Paths, env.Log);
        var progress = new List<LegendaryProgress>();
        var run = await runner.RunAsync(new LegendaryCommand(LegendaryCommandKind.Install, ["install", "Fortnite"])
        {
            StallTimeout = TimeSpan.FromSeconds(1),
            Timeout = Timeout.InfiniteTimeSpan,
        }, new Progress(progress), TestContext.Current.CancellationToken);

        Assert.True(run.Stalled);
        Assert.True(run.Facts.ChunkRetries > 5);
        Assert.Equal(ErrorCode.Network, ErrorClassifier.Classify(LegendaryCommandKind.Install, run)?.Code);
        Assert.Equal(4, File.ReadAllLines(env.ResumeFile).Length); // completed files survive for the resume
        Assert.Equal(40, progress.Max(p => p.Percent));
    }

    [Fact]
    public async Task TimeoutKillsAHungCommand()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("status", "hang");
        using var runner = new LegendaryProcessRunner(env.Paths, env.Log);
        var run = await runner.RunAsync(Cmd("status") with { Timeout = TimeSpan.FromMilliseconds(600) }, null, TestContext.Current.CancellationToken);
        Assert.True(run.TimedOut);
        Assert.Equal(ErrorCode.Timeout, ErrorClassifier.Classify(LegendaryCommandKind.Status, run)?.Code);
    }

    [Fact]
    public async Task ExclusiveCommandsNeverOverlap()
    {
        using var env = new FakeLegendaryEnv();
        foreach (var name in new[] { "verify", "uninstall" })
        {
            env.Script(name, $"""
                writefile fake/{name}.start x
                sleep 300
                writefile fake/{name}.end x
                """);
        }

        using var runner = new LegendaryProcessRunner(env.Paths, env.Log);
        var ct = TestContext.Current.CancellationToken;
        LegendaryCommand Exclusive(string c) => new(LegendaryCommandKind.Verify, [c]) { Exclusive = true };
        await Task.WhenAll(runner.RunAsync(Exclusive("verify"), null, ct), runner.RunAsync(Exclusive("uninstall"), null, ct));

        DateTime T(string f) => File.GetLastWriteTimeUtc(Path.Combine(env.FakeDir, f));
        var (first, second) = T("verify.start") < T("uninstall.start") ? ("verify", "uninstall") : ("uninstall", "verify");
        Assert.True(T($"{second}.start") >= T($"{first}.end"), "the second mutating command started before the first ended");
    }

    [Fact]
    public async Task WritesConfigIniBeforeSpawningAndKeepsLegendaryState()
    {
        using var env = new FakeLegendaryEnv();
        var ini = Path.Combine(env.Config, "config.ini");
        await File.WriteAllTextAsync(ini, "[Legendary]\negl_sync = true\n\n[Fortnite]\ninstall_tags = ,Startup\n", TestContext.Current.CancellationToken);
        env.Script("status", "outfile {config}/config.ini");
        using var runner = new LegendaryProcessRunner(env.Paths, env.Log);
        var run = await runner.RunAsync(Cmd("status"), null, TestContext.Current.CancellationToken);

        Assert.Contains("egl_sync = false", run.Stdout);           // seen by the child at spawn
        Assert.Contains("disable_update_notice = true", run.Stdout);
        Assert.Contains("disable_update_check = false", run.Stdout); // no version.json yet
        Assert.Contains("install_tags = ,Startup", run.Stdout);
        Assert.DoesNotContain("egl_sync = true", run.Stdout);
    }

    [Fact]
    public async Task ReportsProgressAndPhases()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("install", """
            err [cli] INFO: Preparing download for "Fortnite" (Fortnite)...
            download chunks=500 files=5 mib=100 interval=10
            err [cli] INFO: Finished installation process in 0.10 seconds.
            """);
        using var runner = new LegendaryProcessRunner(env.Paths, env.Log);
        var sink = new RecordingSink();
        var run = await runner.RunAsync(new LegendaryCommand(LegendaryCommandKind.Install, ["install", "Fortnite"]), sink, TestContext.Current.CancellationToken);

        Assert.Null(ErrorClassifier.Classify(LegendaryCommandKind.Install, run));
        Assert.Equal(5, sink.Progress.Count);
        Assert.Equal(100, sink.Progress[^1].Percent);
        Assert.Equal([JobPhase.Downloading, JobPhase.Finalizing], sink.Phases);
        Assert.Equal((long)(100 * 1024 * 1024), sink.Sizes?.DownloadBytes);
        Assert.DoesNotContain(run.Tail, l => l.Contains("= Progress", StringComparison.Ordinal)); // progress lines aren't kept
    }

    [Fact]
    public async Task ASlowConsumerNeverStallsLegendary()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("install", "download chunks=200 files=20 mib=20 interval=5");
        using var runner = new LegendaryProcessRunner(env.Paths, env.Log);
        var slow = new SlowProgress();
        var sw = Stopwatch.StartNew();
        var run = await runner.RunAsync(new LegendaryCommand(LegendaryCommandKind.Install, ["install"]), slow, TestContext.Current.CancellationToken);

        Assert.Equal(20, slow.Count);                                   // every event delivered, in order, before return
        Assert.True(run.Duration < TimeSpan.FromMilliseconds(20 * 100)); // the process didn't wait for the consumer
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(20 * 100));
    }

    [Fact]
    public async Task AbortWhenStopsADoomedRunEarly()
    {
        using var env = new FakeLegendaryEnv();
        env.UseFixture("synthetic-install-sdl-failed.stderr.txt");
        env.Script("install", """
            errfile synthetic-install-sdl-failed.stderr.txt
            hang
            """);
        using var runner = new LegendaryProcessRunner(env.Paths, env.Log);
        var run = await runner.RunAsync(new LegendaryCommand(LegendaryCommandKind.Install, ["install"]) { AbortWhen = f => f.SdlDownloadFailed, Timeout = TimeSpan.FromSeconds(30) },
            null, TestContext.Current.CancellationToken);
        Assert.True(run.AbortedEarly);
        Assert.True(run.Duration < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task MissingExecutableIsAHumanError()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path, dir.Combine("nope", "legendary.exe"));
        using var runner = new LegendaryProcessRunner(paths, new TestLog());
        var e = await Assert.ThrowsAsync<GliderException>(() => runner.RunAsync(Cmd("status"), null, TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCode.LegendaryMissing, e.Error.Code);
    }

    [Fact]
    public async Task RedactsSecretArgumentsAndSensitiveStdout()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("auth", """err [cli] INFO: Successfully logged in as "Tester" """);
        env.Script("launch", """out {"egl_parameters": ["-AUTH_PASSWORD=supersecret"]}""");
        using var runner = new LegendaryProcessRunner(env.Paths, env.Log);
        var ct = TestContext.Current.CancellationToken;
        await runner.RunAsync(new LegendaryCommand(LegendaryCommandKind.AuthCode, ["auth", "--code", "hunter2"]) { RedactedArguments = [2] }, null, ct);
        var launch = await runner.RunAsync(new LegendaryCommand(LegendaryCommandKind.Launch, ["launch"]) { CaptureStdout = true, SensitiveStdout = true }, null, ct);

        Assert.Contains("supersecret", launch.Stdout); // the caller still gets it...
        Assert.DoesNotContain(launch.Tail, l => l.Contains("supersecret", StringComparison.Ordinal));
        Assert.False(env.Log.Contains("supersecret")); // ...but it never reaches the log
        Assert.False(env.Log.Contains("hunter2"));
        Assert.True(env.Log.Contains("auth --code ***"));
    }

    internal static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.ElapsedMilliseconds < timeoutMs, "condition not met in time");
            await Task.Delay(20);
        }
    }

    internal static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class Progress(List<LegendaryProgress> list) : IProgress<LegendaryProgress>
    {
        public void Report(LegendaryProgress value)
        {
            lock (list)
            {
                list.Add(value);
            }
        }
    }

    private sealed class SlowProgress : IProgress<LegendaryProgress>
    {
        private double _last = -1;

        public int Count { get; private set; }

        public void Report(LegendaryProgress value)
        {
            Assert.True(value.Percent > _last);
            _last = value.Percent;
            Count++;
            Thread.Sleep(100); // a consumer doing synchronous work
        }
    }

    internal sealed class RecordingSink : ILegendaryProgressSink
    {
        public List<LegendaryProgress> Progress { get; } = [];

        public List<JobPhase> Phases { get; } = [];

        public LegendaryRunSizes? Sizes { get; private set; }

        public void Report(LegendaryProgress value)
        {
            lock (Progress)
            {
                Progress.Add(value);
            }
        }

        public void OnPhase(JobPhase phase)
        {
            lock (Phases)
            {
                Phases.Add(phase);
            }
        }

        public void OnSizes(LegendaryRunSizes sizes) => Sizes = sizes;
    }
}
