using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;
using Glider.Core.Jobs;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Jobs;

internal sealed class TestHooks : IJobHooks
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public Func<JobInfo, Action<string?>, CancellationToken, Task>? Before { get; set; }

    public Task BeforeRunAsync(JobInfo job, Action<string?> setBlockedReason, CancellationToken ct)
    {
        Calls.Enqueue($"before:{job.Kind}");
        return Before?.Invoke(job, setBlockedReason, ct) ?? Task.CompletedTask;
    }

    public Task OnSucceededAsync(JobInfo job, CancellationToken ct)
    {
        Calls.Enqueue($"ok:{job.Kind}");
        return Task.CompletedTask;
    }

    public Task OnFailedAsync(JobInfo job, CancellationToken ct)
    {
        Calls.Enqueue($"failed:{job.Kind}:{job.Error?.Code}");
        return Task.CompletedTask;
    }
}

internal sealed class TestSettings(Settings settings) : ISettingsStore
{
    public Settings Current { get; private set; } = settings;

    public event Action<Settings>? Changed;

    public Task<Settings> UpdateAsync(Settings s)
    {
        Current = s;
        Changed?.Invoke(s);
        return Task.FromResult(s);
    }
}

/// <summary>In-memory ILegendary for queue tests that are about the queue, not about Legendary.</summary>
internal sealed class StubLegendary(bool verifySucceeds) : ILegendary
{
    private static readonly Task<LegendaryOutcome> Ok = Task.FromResult(new LegendaryOutcome(true, null, null, null));

    public Task<LegendaryOutcome> VerifyAsync(string appName, IProgress<LegendaryProgress> progress, CancellationToken ct) =>
        verifySucceeds ? Ok : Task.FromResult(new LegendaryOutcome(false, Errors.Corrupted("stub"), null, null));

    public Task<string> GetVersionAsync(CancellationToken ct) => Task.FromResult("0.21.1");

    public Task<LegendaryAccount?> GetAccountOfflineAsync(CancellationToken ct) => Task.FromResult<LegendaryAccount?>(null);

    public Task<LegendaryAccount?> RefreshSessionAsync(CancellationToken ct) => Task.FromResult<LegendaryAccount?>(null);

    public Task<LegendaryAccount> SignInWithCodeAsync(string authorizationCode, CancellationToken ct) => throw new NotSupportedException();

    public Task SignOutAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<LegendaryGameInfo> GetGameInfoAsync(string appName, CancellationToken ct) => throw new NotSupportedException();

    public Task<LegendaryInstalledGame?> GetInstalledAsync(string appName, CancellationToken ct) => Task.FromResult<LegendaryInstalledGame?>(null);

    public Task<LegendaryOutcome> InstallAsync(InstallArgs args, IProgress<LegendaryProgress> progress, CancellationToken ct) => Ok;

    public Task<LegendaryOutcome> ImportAsync(string appName, string gamePath, CancellationToken ct) => Ok;

    public Task<LegendaryOutcome> MoveAsync(string appName, string newBasePath, bool skipMove, IProgress<LegendaryProgress>? progress, CancellationToken ct) => Ok;

    public Task<LegendaryOutcome> UninstallAsync(string appName, bool keepFiles, CancellationToken ct) => Ok;

    public Task<LaunchCommand> GetLaunchCommandAsync(string appName, bool skipVersionCheck, string extraArgs, CancellationToken ct) => throw new NotSupportedException();

    public string? GetInstalledManifestPath(string appName, string version) => null;
}

/// <summary>A queue over the real LegendaryCli driving FakeLegendary.</summary>
internal sealed class QueueHarness : IAsyncDisposable
{
    public const string Build = "++Fortnite+Release-33.00-CL-38324456-Windows";

    private readonly List<JobQueue> _queues = [];

    private readonly ILegendary? _legendary;

    public QueueHarness(TimeSpan? stall = null, ILegendary? legendary = null)
    {
        _legendary = legendary;
        Cli = new LegendaryCli(Env.Paths, Env.Log, new LegendaryOptions { StallTimeout = stall ?? TimeSpan.FromSeconds(30) });
        Games = Path.Combine(Env.Root, "Games");
        GameFolder = Path.Combine(Games, "Fortnite");
    }

    public FakeLegendaryEnv Env { get; } = new();

    public LegendaryCli Cli { get; }

    public TestHooks Hooks { get; } = new();

    public TestSettings Settings { get; } = new(global::Glider.Core.Contracts.Settings.Default);

    public ConcurrentQueue<JobProgress> Progress { get; } = new();

    public string Games { get; }

    public string GameFolder { get; }

    public JobQueue NewQueue(JobQueueOptions? options = null)
    {
        var q = new JobQueue(Env.Paths, _legendary ?? Cli, Settings, Hooks, Env.Log, options: options ?? new JobQueueOptions
        {
            AutoTune = new AutoTuneResult(7, 1536),
            ProgressInterval = TimeSpan.FromMilliseconds(1),
        });
        q.ProgressChanged += Progress.Enqueue;
        _queues.Add(q);
        return q;
    }

    public async Task<JobQueue> StartAsync(JobQueueOptions? options = null)
    {
        var q = NewQueue(options);
        await q.RestoreAsync(TestContext.Current.CancellationToken);
        return q;
    }

    public static async Task<JobInfo> WaitFor(JobQueue q, string id, Func<JobInfo, bool> condition, int timeoutMs = 15_000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var job = q.Snapshot().Single(j => j.Id == id);
            if (condition(job))
            {
                return job;
            }

            Assert.True(sw.ElapsedMilliseconds < timeoutMs, $"timed out; job is {job.Status} {job.Error?.Code} {job.Error?.Detail}");
            await Task.Delay(10);
        }
    }

    public static Task<JobInfo> WaitForStatus(JobQueue q, string id, JobStatus status) => WaitFor(q, id, j => j.Status == status);

    /// <summary>A fresh install: creates the game folder, downloads, registers, finishes.</summary>
    public void InstallScript(int files = 8, int interval = 10, string extra = "") => Env.Script("install", $"""
        writefile {"{basepath}"}/Fortnite/FortniteGame/marker.txt x
        download chunks={files * 50} files={files} mib={files * 10} interval={interval} {extra}
        installed version={Build} path={"{basepath}"}/Fortnite
        err [cli] INFO: Finished installation process in 0.20 seconds.
        """);

    public JobRequest Install() => new(JobKind.Install, Games, null, null, Build);

    public async ValueTask DisposeAsync()
    {
        foreach (var q in _queues)
        {
            await q.DisposeAsync();
        }

        Cli.Dispose();
        Env.Dispose();
    }
}

public class JobQueueRunTests
{
    [Fact]
    public async Task InstallRunsDownloadThenVerifyAndCompletes()
    {
        await using var h = new QueueHarness();
        h.InstallScript();
        h.Env.Script("verify", "verify files=10 interval=1");
        var q = await h.StartAsync();

        var job = q.Enqueue(h.Install());
        Assert.Equal(job.Id, q.Enqueue(h.Install()).Id); // idempotent while active
        var done = await QueueHarness.WaitForStatus(q, job.Id, JobStatus.Completed);

        Assert.NotNull(done.FinishedAt);
        Assert.Null(done.Error);
        Assert.Equal(100, done.Progress?.Percent);
        Assert.Equal(["before:Install", "ok:Install"], h.Hooks.Calls);
        var calls = h.Env.Invocations();
        Assert.Equal(["install", "verify"], calls.Select(c => c.Command));
        Assert.Contains("--max-workers", calls[0].Args);
        Assert.Equal("7", calls[0].Args[Array.IndexOf(calls[0].Args, "--max-workers") + 1]);
        Assert.Equal("1536", calls[0].Args[Array.IndexOf(calls[0].Args, "--max-shared-memory") + 1]);

        var phases = h.Progress.Select(p => p.Phase).Distinct().ToList();
        Assert.Contains(JobPhase.Downloading, phases);
        Assert.Contains(JobPhase.Verifying, phases);
        var download = h.Progress.Where(p => p.Phase == JobPhase.Downloading).Select(p => p.Percent).ToList();
        Assert.Equal(download.Order(), download); // monotonic bar

        await q.DisposeAsync(); // flushes the final save
        var onDisk = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(JobQueue.PathFor(h.Env.Paths), TestContext.Current.CancellationToken),
            EngineJsonContext.Default.PersistedJobFile)!;
        Assert.Equal(JobStatus.Completed, onDisk.Jobs.Single().Info.Status);
    }

}

public class JobQueueSettingsTests
{
    [Fact]
    public async Task SettingsOverrideAutoTune()
    {
        await using var h = new QueueHarness();
        h.InstallScript(files: 2);
        h.Env.Script("verify", "verify files=2 interval=1");
        await h.Settings.UpdateAsync(Settings.Default with { DownloadWorkers = 3, SharedMemoryMb = 4096, PreferredCdn = "fastly" });
        var q = await h.StartAsync();
        var job = q.Enqueue(h.Install());
        await QueueHarness.WaitForStatus(q, job.Id, JobStatus.Completed);

        var args = h.Env.Invocations()[0].Args;
        Assert.Equal("3", args[Array.IndexOf(args, "--max-workers") + 1]);
        Assert.Equal("4096", args[Array.IndexOf(args, "--max-shared-memory") + 1]);
        Assert.Equal("fastly", args[Array.IndexOf(args, "--preferred-cdn") + 1]);
    }

}

public class JobQueueAutoRepairTests
{
    [Fact]
    public async Task VerifyFindingsAreRepairedOnceAutomatically()
    {
        await using var h = new QueueHarness();
        h.InstallScript(files: 2);
        h.Env.Script("verify", "verify files=6 interval=1 corrupt=1");
        h.Env.Script("repair", """
            err [cli] INFO: Using existing repair file: x
            download chunks=10 files=1 mib=1 interval=5
            err [cli] INFO: Finished installation process in 0.05 seconds.
            """);
        var q = await h.StartAsync();
        var job = q.Enqueue(h.Install());
        await QueueHarness.WaitForStatus(q, job.Id, JobStatus.Completed);
        Assert.Equal(["install", "verify", "repair"], h.Env.Invocations().Select(c => c.Command));
    }

}

public class JobQueueWriteFailureTests
{
    [Fact]
    public async Task UpdateWithWriteFailuresVerifiesAndRepairs()
    {
        await using var h = new QueueHarness();
        File.Copy(Fixture.PathOf("synthetic-installed.json"), Path.Combine(h.Env.Config, "installed.json"));
        h.Env.Script("update", """
            download chunks=40 files=4 mib=4 interval=5 writefail=1
            err [cli] INFO: Finished installation process in 0.05 seconds.
            """);
        h.Env.Script("verify", "verify files=4 interval=1 corrupt=1");
        h.Env.Script("repair", "err [cli] INFO: Finished installation process in 0.05 seconds.");
        var q = await h.StartAsync();
        var job = q.Enqueue(new JobRequest(JobKind.Update, null, null, "old", QueueHarness.Build));
        await QueueHarness.WaitForStatus(q, job.Id, JobStatus.Completed);
        Assert.Equal(["update", "verify", "repair"], h.Env.Invocations().Select(c => c.Command));
    }

}

public class JobQueueFailureTests
{
    [Fact]
    public async Task FailuresAreMappedAndRetryable()
    {
        await using var h = new QueueHarness();
        h.Env.UseFixture("synthetic-install-nospace.stdout.txt");
        h.Env.UseFixture("synthetic-install-nospace.stderr.txt");
        h.Env.Script("install", """
            outfile synthetic-install-nospace.stdout.txt
            errfile synthetic-install-nospace.stderr.txt
            exit 1
            """, invocation: 1);
        h.InstallScript(files: 2); // the retry succeeds
        h.Env.Script("verify", "verify files=2 interval=1");
        var q = await h.StartAsync();
        var job = q.Enqueue(h.Install());

        var failed = await QueueHarness.WaitForStatus(q, job.Id, JobStatus.Failed);
        Assert.Equal(ErrorCode.InsufficientSpace, failed.Error?.Code);
        Assert.Equal(FixAction.FreeSpace, failed.Error?.Fix);
        Assert.Contains("failed:Install:InsufficientSpace", h.Hooks.Calls);

        await q.ResumeAsync(job.Id); // "Retry"
        var done = await QueueHarness.WaitForStatus(q, job.Id, JobStatus.Completed);
        Assert.Null(done.Error);
    }

}

public class JobQueueStallTests
{
    [Fact]
    public async Task StallFailsWithNetworkAndResumeContinues()
    {
        await using var h = new QueueHarness(stall: TimeSpan.FromSeconds(2)); // > the fake's startup time on a loaded machine
        h.Env.Script("install", """
            download chunks=100 files=10 mib=20 interval=10 stallat=50 spam
            """, invocation: 1);
        h.InstallScript(files: 10);
        h.Env.Script("verify", "verify files=2 interval=1");
        var q = await h.StartAsync();
        var job = q.Enqueue(h.Install());

        var failed = await QueueHarness.WaitForStatus(q, job.Id, JobStatus.Failed);
        Assert.Equal(ErrorCode.Network, failed.Error?.Code);
        Assert.Equal(5, File.ReadAllLines(h.Env.ResumeFile).Length);

        await q.ResumeAsync(job.Id);
        await QueueHarness.WaitForStatus(q, job.Id, JobStatus.Completed);
        Assert.True(h.Env.Log.Contains("Skipping 5 files based on resume data."));
    }

}

public class JobQueueHookTests
{
    [Fact]
    public async Task WaitsWhileHooksBlock()
    {
        await using var h = new QueueHarness();
        h.Env.Script("verify", "verify files=3 interval=1");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Hooks.Before = async (_, setBlocked, ct) =>
        {
            setBlocked("Epic Games Launcher is updating Fortnite.");
            await release.Task.WaitAsync(ct);
            setBlocked(null);
        };
        var q = await h.StartAsync();
        var job = q.Enqueue(new JobRequest(JobKind.Verify, null, null, null, null));

        var waiting = await QueueHarness.WaitForStatus(q, job.Id, JobStatus.Waiting);
        Assert.Equal("Epic Games Launcher is updating Fortnite.", waiting.BlockedReason);
        Assert.Empty(h.Env.Invocations());
        Assert.True(q.IsBusy);

        release.SetResult();
        var done = await QueueHarness.WaitForStatus(q, job.Id, JobStatus.Completed);
        Assert.Null(done.BlockedReason);
    }

    [Fact]
    public async Task OtherKindsUseTheirCommands()
    {
        await using var h = new QueueHarness();
        File.Copy(Fixture.PathOf("synthetic-installed.json"), Path.Combine(h.Env.Config, "installed.json"));
        h.Env.UseFixture("synthetic-uninstall-ok.stderr.txt");
        h.Env.UseFixture("synthetic-import-egl.stderr.txt");
        h.Env.Script("uninstall", """
            uninstalled
            errfile synthetic-uninstall-ok.stderr.txt
            """);
        h.Env.Script("import", """
            installed version=1 path={arg:1}
            errfile synthetic-import-egl.stderr.txt
            """);
        var q = await h.StartAsync();

        var uninstall = q.Enqueue(new JobRequest(JobKind.Uninstall, null, UninstallMode.DeleteFiles, null, null));
        await QueueHarness.WaitForStatus(q, uninstall.Id, JobStatus.Completed);
        Directory.CreateDirectory(h.GameFolder);
        var import = q.Enqueue(new JobRequest(JobKind.Import, h.GameFolder, null, null, null));
        await QueueHarness.WaitForStatus(q, import.Id, JobStatus.Completed);

        var calls = h.Env.Invocations();
        Assert.Equal(["-y", "uninstall", "Fortnite", "--skip-uninstaller"], calls[0].Args);
        Assert.Equal(["-y", "import", "Fortnite", h.GameFolder, "--skip-dlcs"], calls[1].Args);
        Assert.Throws<GliderException>(() => q.Enqueue(new JobRequest(JobKind.Move, "relative", null, null, null)));
        Assert.Throws<GliderException>(() => q.Enqueue(new JobRequest(JobKind.Uninstall, null, null, null, null)));
    }
}

public class JobQueuePauseResumeTests
{
    [Fact]
    public async Task PauseKillsLegendaryAndResumeContinuesFromTheResumeFile()
    {
        await using var h = new QueueHarness();
        h.InstallScript(files: 20, interval: 40);
        h.Env.Script("verify", "verify files=2 interval=1");
        var q = await h.StartAsync();
        var job = q.Enqueue(h.Install());

        await QueueHarness.WaitFor(q, job.Id, j => j.Progress is { Percent: >= 25, Phase: JobPhase.Downloading });
        var paused = await q.PauseAsync(job.Id);
        Assert.Equal(JobStatus.Paused, paused.Status);
        Assert.False(q.IsBusy);
        var done = File.ReadAllLines(h.Env.ResumeFile).Length;
        Assert.InRange(done, 4, 19);
        var pausedAt = paused.Progress!.Percent;

        await q.ResumeAsync(job.Id);
        await QueueHarness.WaitForStatus(q, job.Id, JobStatus.Completed);

        Assert.True(h.Env.Log.Contains($"Skipping {done} files based on resume data."));
        Assert.Equal(["before:Install", "before:Install", "ok:Install"], h.Hooks.Calls); // hooks gate the resume too
        var afterResume = h.Progress.Where(p => p.Phase == JobPhase.Downloading).SkipWhile(p => p.Percent < pausedAt).Select(p => p.Percent).ToList();
        Assert.Equal(afterResume.Order(), afterResume); // the bar never jumps back after the resume
    }

}

public class JobQueuePauseRaceTests
{
    [Fact]
    public async Task PauseRightAfterEnqueueAlwaysWins()
    {
        await using var h = new QueueHarness();
        h.Env.Script("verify", "verify files=50 interval=20");
        var q = await h.StartAsync();
        for (var i = 0; i < 10; i++)
        {
            var job = q.Enqueue(new JobRequest(JobKind.Verify, null, null, null, null));
            if (i % 2 == 1)
            {
                await Task.Delay(i * 3, TestContext.Current.CancellationToken); // land on both sides of the claim
            }

            var paused = await q.PauseAsync(job.Id);
            Assert.Equal(JobStatus.Paused, paused.Status);
            await Task.Delay(30, TestContext.Current.CancellationToken);
            Assert.Equal(JobStatus.Paused, q.Snapshot().Single(j => j.Id == job.Id).Status);
            await q.CancelAsync(job.Id);
            q.Dismiss(job.Id);
        }

        Assert.False(q.IsBusy);
    }

}

public class JobQueueBlockingTests
{
    [Fact]
    public async Task PausedJobBlocksTheQueueUntilCancelled()
    {
        await using var h = new QueueHarness();
        h.InstallScript(files: 20, interval: 40);
        h.Env.Script("verify", "verify files=2 interval=1");
        var q = await h.StartAsync();
        var install = q.Enqueue(h.Install());
        await QueueHarness.WaitFor(q, install.Id, j => j.Progress is { Percent: > 0 });
        await q.PauseAsync(install.Id);

        var verify = q.Enqueue(new JobRequest(JobKind.Verify, null, null, null, null));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(JobStatus.Queued, q.Snapshot().Single(j => j.Id == verify.Id).Status);

        await q.CancelAsync(install.Id);
        await QueueHarness.WaitForStatus(q, verify.Id, JobStatus.Completed);
        await Assert.ThrowsAsync<GliderException>(() => q.PauseAsync(verify.Id));
    }

}

public class JobQueueCancelInstallTests
{
    [Fact]
    public async Task CancellingAFreshInstallRemovesItsResumeFileAndFolder()
    {
        await using var h = new QueueHarness();
        h.InstallScript(files: 20, interval: 40);
        var q = await h.StartAsync();
        var job = q.Enqueue(h.Install());
        await QueueHarness.WaitFor(q, job.Id, j => j.Progress is { Percent: >= 10 });
        Assert.True(Directory.Exists(h.GameFolder));

        var cancelled = await q.CancelAsync(job.Id);
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        await q.WhenCleanupDoneAsync();
        Assert.False(File.Exists(h.Env.ResumeFile));
        Assert.False(Directory.Exists(h.GameFolder));
        Assert.True(Directory.Exists(h.Games)); // the user's base folder is untouched
        Assert.DoesNotContain(h.Hooks.Calls, c => c.StartsWith("ok", StringComparison.Ordinal) || c.StartsWith("failed", StringComparison.Ordinal));
        q.Dismiss(job.Id);
        Assert.Empty(q.Snapshot());
    }

    [Fact]
    public async Task CancellingAnInstallIntoAnExistingFolderKeepsTheFolder()
    {
        await using var h = new QueueHarness();
        Directory.CreateDirectory(h.GameFolder);
        await File.WriteAllTextAsync(Path.Combine(h.GameFolder, "user-data.txt"), "mine", TestContext.Current.CancellationToken);
        h.InstallScript(files: 20, interval: 40);
        var q = await h.StartAsync();
        var job = q.Enqueue(h.Install());
        await QueueHarness.WaitFor(q, job.Id, j => j.Progress is { Percent: >= 10 });
        await q.CancelAsync(job.Id);
        await q.WhenCleanupDoneAsync();
        Assert.True(File.Exists(Path.Combine(h.GameFolder, "user-data.txt")));
        Assert.False(File.Exists(h.Env.ResumeFile));
    }

}

public class JobQueueCancelUpdateTests
{
    [Fact]
    public async Task CancellingAnUpdateKeepsTheResumeFile()
    {
        await using var h = new QueueHarness();
        File.Copy(Fixture.PathOf("synthetic-installed.json"), Path.Combine(h.Env.Config, "installed.json"));
        h.Env.Script("update", "download chunks=200 files=20 mib=20 interval=40");
        var q = await h.StartAsync();
        var job = q.Enqueue(new JobRequest(JobKind.Update, null, null, "old", "new"));
        await QueueHarness.WaitFor(q, job.Id, j => j.Progress is { Percent: >= 10 });
        // the fake appends to the resume file per completed file; under load that can lag the progress events
        for (var i = 0; i < 200 && !File.Exists(h.Env.ResumeFile); i++)
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(File.Exists(h.Env.ResumeFile), "fake never wrote a resume file");
        await q.CancelAsync(job.Id);
        await q.WhenCleanupDoneAsync();
        // an interrupted update is a mix of old and new files; only the resume file tells Legendary which are new
        Assert.True(File.Exists(h.Env.ResumeFile));
    }
}

public class JobQueueRecoveryTests
{
    [Fact]
    public async Task ARunningJobResumesAfterRestart()
    {
        await using var h = new QueueHarness();
        h.InstallScript(files: 20, interval: 40);
        h.Env.Script("verify", "verify files=2 interval=1");
        var first = await h.StartAsync();
        var job = first.Enqueue(h.Install());
        await QueueHarness.WaitFor(first, job.Id, j => j.Progress is { Percent: >= 20 });

        await first.DisposeAsync(); // app closes (or crashes) mid-download
        var onDisk = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(JobQueue.PathFor(h.Env.Paths), TestContext.Current.CancellationToken),
            EngineJsonContext.Default.PersistedJobFile)!;
        Assert.Equal(JobStatus.Running, onDisk.Jobs.Single().Info.Status);
        var completedFiles = File.ReadAllLines(h.Env.ResumeFile).Length;

        var second = await h.StartAsync(); // next launch
        var done = await QueueHarness.WaitForStatus(second, job.Id, JobStatus.Completed);
        Assert.True(h.Env.Log.Contains($"Skipping {completedFiles} files based on resume data."));
        Assert.True(done.Progress!.ElapsedSeconds >= 0);
        Assert.Equal(3, h.Hooks.Calls.Count); // before, before (after restart), ok
    }

}

public class JobQueueRestoreTests
{
    [Fact]
    public async Task RestoreKeepsPausedJobsPausedAndRequeuesInterruptedOnes()
    {
        await using var h = new QueueHarness();
        var now = DateTimeOffset.UtcNow;
        JobInfo Info(string id, JobKind kind, JobStatus status, int minutes) =>
            new(id, kind, status, null, null, now.AddMinutes(minutes), null, null, null, null, status == JobStatus.Waiting ? "EGL busy" : null);
        var file = new PersistedJobFile
        {
            Jobs =
            [
                new PersistedJob { Info = Info("done", JobKind.Verify, JobStatus.Completed, -10), Request = new JobRequest(JobKind.Verify, null, null, null, null) },
                new PersistedJob { Info = Info("paused", JobKind.Repair, JobStatus.Paused, -5), Request = new JobRequest(JobKind.Repair, null, null, null, null) },
                new PersistedJob { Info = Info("waiting", JobKind.Verify, JobStatus.Waiting, -1), Request = new JobRequest(JobKind.Verify, null, null, null, null), Step = JobStep.Main },
            ],
        };
        Directory.CreateDirectory(h.Env.Paths.State);
        await File.WriteAllBytesAsync(JobQueue.PathFor(h.Env.Paths), JsonSerializer.SerializeToUtf8Bytes(file, EngineJsonContext.Default.PersistedJobFile), TestContext.Current.CancellationToken);

        var q = await h.StartAsync();
        var jobs = q.Snapshot();
        Assert.Equal(["done", "paused", "waiting"], jobs.Select(j => j.Id));
        Assert.Equal(JobStatus.Paused, jobs[1].Status);
        Assert.Equal(JobStatus.Queued, jobs[2].Status); // interrupted while waiting: back in the queue, behind the paused one
        Assert.Null(jobs[2].BlockedReason);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Empty(h.Env.Invocations());
    }

}

public class JobQueuePersistenceTests
{
    [Fact]
    public async Task JobsJsonIsAlwaysAValidDocument()
    {
        await using var h = new QueueHarness(legendary: new StubLegendary(verifySucceeds: false));
        var q = await h.StartAsync();
        var path = JobQueue.PathFor(h.Env.Paths);
        using var stop = new CancellationTokenSource();
        var reads = 0;
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (await AtomicFile.TryReadAllBytesAsync(path, CancellationToken.None) is { Length: > 0 } bytes)
                {
                    Assert.NotNull(JsonSerializer.Deserialize(bytes, EngineJsonContext.Default.PersistedJobFile));
                    reads++;
                }

                await Task.Delay(1, CancellationToken.None);
            }
        }, CancellationToken.None);

        // many state changes: verifies fail instantly, get dismissed, repeat
        for (var i = 0; i < 40; i++)
        {
            var job = q.Enqueue(new JobRequest(JobKind.Verify, null, null, null, null));
            await QueueHarness.WaitForStatus(q, job.Id, JobStatus.Failed);
            q.Dismiss(job.Id);
        }

        await stop.CancelAsync();
        await reader;
        await q.DisposeAsync(); // waits for in-flight saves
        Assert.True(reads > 0);
        Assert.Empty(Directory.GetFiles(h.Env.Paths.State, "*.tmp"));
        Assert.DoesNotContain(Directory.GetFiles(h.Env.Paths.State), f => Path.GetFileName(f).StartsWith('.'));
    }

    [Fact]
    public async Task CorruptJobsJsonFallsBackToTheBackup()
    {
        await using var h = new QueueHarness(legendary: new StubLegendary(verifySucceeds: true));
        var first = await h.StartAsync();
        var job = first.Enqueue(new JobRequest(JobKind.Verify, null, null, null, null));
        await QueueHarness.WaitForStatus(first, job.Id, JobStatus.Completed);
        await first.DisposeAsync();
        await File.WriteAllTextAsync(JobQueue.PathFor(h.Env.Paths), "{ torn", TestContext.Current.CancellationToken);

        var second = await h.StartAsync();
        Assert.Contains(second.Snapshot(), j => j.Id == job.Id);
    }
}
