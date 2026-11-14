using System.Collections.Concurrent;
using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;
using Glider.Core.Jobs;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Jobs;

/// <summary>
/// ILegendary for writer-lane tests: install/update/repair/verify of an app block on its gate (cancellable, like killing
/// Legendary) after reporting one progress line; every call is recorded as "verb:app".
/// </summary>
internal sealed class GatedLegendary : ILegendary
{
    private static readonly Task<LegendaryOutcome> Ok = Task.FromResult(new LegendaryOutcome(true, null, null, null));

    public ConcurrentQueue<string> Calls { get; } = new();

    public ConcurrentQueue<InstallArgs> InstallArgs { get; } = new();

    public ConcurrentDictionary<string, TaskCompletionSource> Gates { get; } = new(StringComparer.Ordinal);

    /// <summary>Apps whose next N install runs answer "installed data lock busy".</summary>
    public ConcurrentDictionary<string, int> LockBusy { get; } = new(StringComparer.Ordinal);

    /// <summary>Set once the gated run reported its first progress (the writer left its pre-download phase).</summary>
    public ConcurrentDictionary<string, TaskCompletionSource> Started { get; } = new(StringComparer.Ordinal);

    public TaskCompletionSource Gate(string app) => Gates.GetOrAdd(app, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public TaskCompletionSource StartedFor(string app) => Started.GetOrAdd(app, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    private async Task<LegendaryOutcome> Run(string verb, string app, IProgress<LegendaryProgress>? progress, CancellationToken ct)
    {
        Calls.Enqueue($"{verb}:{app}");
        if (LockBusy.TryGetValue(app, out var n) && n > 0)
        {
            LockBusy[app] = n - 1;
            return new LegendaryOutcome(false, Errors.LegendaryLocked("Failed to acquire installed data lock"), null, null);
        }

        if (Gates.TryGetValue(app, out var gate))
        {
            StartedFor(app).TrySetResult();
            await gate.Task.WaitAsync(ct);
        }

        progress?.Report(new LegendaryProgress(50, 1, 2, 1, 1, 10, 10, 1, 1, 1, 0));
        StartedFor(app).TrySetResult();
        return new LegendaryOutcome(true, null, null, null);
    }

    public Task<LegendaryOutcome> InstallAsync(InstallArgs args, IProgress<LegendaryProgress> progress, CancellationToken ct)
    {
        InstallArgs.Enqueue(args);
        return Run(args.Mode.ToString().ToLowerInvariant(), args.AppName, progress, ct);
    }

    public Task<LegendaryOutcome> VerifyAsync(string appName, IProgress<LegendaryProgress> progress, CancellationToken ct) => Run("verify", appName, progress, ct);

    public Task<LegendaryOutcome> ImportAsync(string appName, string gamePath, CancellationToken ct) => Run("import", appName, null, ct);

    public Task<LegendaryOutcome> MoveAsync(string appName, string newBasePath, bool skipMove, IProgress<LegendaryProgress>? progress, CancellationToken ct) => Run("move", appName, progress, ct);

    public Task<LegendaryOutcome> UninstallAsync(string appName, bool keepFiles, CancellationToken ct)
    {
        Calls.Enqueue($"uninstall:{appName}");
        return Ok;
    }

    public Task<string> GetVersionAsync(CancellationToken ct) => Task.FromResult("0.21.1");
    public Task<LegendaryAccount?> GetAccountOfflineAsync(CancellationToken ct) => Task.FromResult<LegendaryAccount?>(null);
    public Task<LegendaryAccount?> RefreshSessionAsync(CancellationToken ct) => Task.FromResult<LegendaryAccount?>(null);
    public Task<LegendaryAccount> SignInWithExchangeTokenAsync(string exchangeToken, CancellationToken ct) => throw new NotSupportedException();
    public Task SignOutAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<LegendaryGameInfo> GetGameInfoAsync(string appName, CancellationToken ct) => throw new NotSupportedException();
    public Task<LegendaryInstalledGame?> GetInstalledAsync(string appName, CancellationToken ct) => Task.FromResult<LegendaryInstalledGame?>(null);
    public Task<LaunchCommand> GetLaunchCommandAsync(string appName, bool skipVersionCheck, string extraArgs, bool offline, CancellationToken ct) => throw new NotSupportedException();
    public string? GetInstalledManifestPath(string appName, string version) => null;
}

internal sealed class TestPolicy : IQueuePolicy
{
    public string? HoldReason { get; set; }

    public DownloadMode EffectiveMode { get; set; } = DownloadMode.Balanced;

    public string? ModeReason => null;
}

/// <summary>The writer lane (docs/v0.2-design.md §3): per-game rules, ordering, persistence, busy retry and holds.</summary>
public class LaneTests
{
    private static JobRequest Verify(string app) => new(app, JobKind.Verify, null, null, null, null, app);

    private static JobRequest Repair(string app) => new(app, JobKind.Repair, null, null, null, null, app);

    private static (QueueHarness H, GatedLegendary L) Harness(TestPolicy? policy = null, IResourcePolicy? resources = null)
    {
        var l = new GatedLegendary();
        return (new QueueHarness(legendary: l), l);
    }

    private static JobQueueOptions Options(TestPolicy? policy = null, IResourcePolicy? resources = null, WriterActivity? activity = null) => new()
    {
        AutoTune = new AutoTuneResult(7, 1536),
        ProgressInterval = TimeSpan.FromMilliseconds(1),
        LockRetryDelay = TimeSpan.FromMilliseconds(20),
        Policy = policy,
        Resources = resources ?? DefaultResourcePolicy.Instance,
        Activity = activity ?? new WriterActivity(),
    };

    private static async Task Eventually(Func<bool> condition, string what)
    {
        for (var i = 0; i < 1000 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), what);
    }

    [Fact]
    public async Task JobsAreIdempotentPerAppAndKind_NotPerKind()
    {
        var (h, l) = Harness();
        await using var _ = h;
        l.Gate("A");
        var q = await h.StartAsync(Options());

        var a1 = q.Enqueue(Verify("A"));
        var a2 = q.Enqueue(Verify("A"));
        var b = q.Enqueue(Verify("B"));
        var aRepair = q.Enqueue(Repair("A"));
        Assert.Equal(a1.Id, a2.Id);
        Assert.NotEqual(a1.Id, b.Id); // v0.1 handed game B the job of game A here (JobQueue.cs:111)
        Assert.NotEqual(a1.Id, aRepair.Id);
        Assert.Equal("B", b.AppName);
        l.Gate("A").SetResult();
        await QueueHarness.WaitForStatus(q, b.Id, JobStatus.Completed);
    }

    [Fact]
    public async Task APausedJobBlocksOnlyItsOwnGame()
    {
        var (h, l) = Harness();
        await using var _ = h;
        l.Gate("A");
        var q = await h.StartAsync(Options());

        var installA = q.Enqueue(new JobRequest("A", JobKind.Install, h.Games, null, null, "1", "A"));
        await l.StartedFor("A").Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await q.PauseAsync(installA.Id);

        var verifyA = q.Enqueue(Verify("A"));
        var verifyB = q.Enqueue(Verify("B"));
        await QueueHarness.WaitForStatus(q, verifyB.Id, JobStatus.Completed); // another game runs past the paused one
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(JobStatus.Queued, q.Snapshot().Single(j => j.Id == verifyA.Id).Status); // its own game waits

        l.Gates.TryRemove("A", out var __);
        await q.ResumeAsync(installA.Id);
        await QueueHarness.WaitForStatus(q, verifyA.Id, JobStatus.Completed);
        Assert.Equal(["install:A", "verify:B", "install:A", "verify:A", "verify:A"], l.Calls); // install's own post-install verify, then the Verify job
    }

    [Fact]
    public async Task ReorderAndMoveToTop_ArePersistedAndDecideTheRunOrder()
    {
        var (h, l) = Harness();
        await using var _ = h;
        l.Gate("A");
        var q = await h.StartAsync(Options());
        var a = q.Enqueue(Repair("A"));
        await l.StartedFor("A").Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var b = q.Enqueue(Verify("B"));
        var c = q.Enqueue(Verify("C"));
        var d = q.Enqueue(Verify("D"));

        await q.MoveToTopAsync(d.Id); // behind the running job: nothing is pre-empted
        Assert.Equal([a.Id, d.Id, b.Id, c.Id], q.Snapshot().Select(j => j.Id));
        await q.ReorderAsync(c.Id, 1);
        Assert.Equal([a.Id, c.Id, d.Id, b.Id], q.Snapshot().Select(j => j.Id));
        var priority = q.Enqueue(new JobRequest("E", JobKind.Verify, null, null, null, null, "E", Priority: true));
        Assert.Equal([a.Id, priority.Id, c.Id, d.Id, b.Id], q.Snapshot().Select(j => j.Id));

        await q.DisposeAsync(); // app closes; A was running
        l.Gates.Clear();
        var q2 = await h.StartAsync(Options());
        Assert.Equal([a.Id, priority.Id, c.Id, d.Id, b.Id], q2.Snapshot().Select(j => j.Id));
        await QueueHarness.WaitForStatus(q2, b.Id, JobStatus.Completed);
        Assert.Equal(["repair:A", "repair:A", "verify:E", "verify:C", "verify:D", "verify:B"], l.Calls);
    }

    [Fact]
    public async Task ACrashWithTwoGamesQueued_ResumesBothAtTheirSteps()
    {
        var (h, l) = Harness();
        await using var _ = h;
        var now = DateTimeOffset.UtcNow;
        JobInfo Info(string id, string app, JobKind kind) => new(id, app, app, kind, JobStatus.Running, JobPlan.Phases(kind), JobPhase.Verifying, null, null,
            now, now, null, null, null, null, null, null, null);
        var file = new PersistedJobFile
        {
            Jobs =
            [
                new PersistedJob
                {
                    Info = Info("a", "A", JobKind.Install),
                    Request = new JobRequest("A", JobKind.Install, h.Games, null, null, "2", "A"),
                    Step = JobStep.Verify, // the download finished before the crash; the post-install verify was running
                    Baseline = new ProgressBaseline { TotalDownloadBytes = 1000, Percent = 40, JobPercent = 91, ElapsedSeconds = 120 },
                },
                new PersistedJob
                {
                    Info = Info("b", "B", JobKind.Update) with { Status = JobStatus.Queued },
                    Request = new JobRequest("B", JobKind.Update, null, null, "1", "2", "B"),
                },
            ],
        };
        Directory.CreateDirectory(h.Env.Paths.State);
        await File.WriteAllBytesAsync(JobQueue.PathFor(h.Env.Paths), JsonSerializer.SerializeToUtf8Bytes(file, EngineJsonContext.Default.PersistedJobFile), TestContext.Current.CancellationToken);

        var q = await h.StartAsync(Options());
        var doneA = await QueueHarness.WaitForStatus(q, "a", JobStatus.Completed);
        await QueueHarness.WaitForStatus(q, "b", JobStatus.Completed);

        Assert.Equal(["verify:A", "update:B"], l.Calls); // A resumed at its verify, not a re-download; then B
        Assert.True(doneA.Progress!.ElapsedSeconds >= 120);
        Assert.True(doneA.Progress.Percent >= 91);
    }

    [Fact]
    public async Task LegendarysInstallLockBeingBusy_IsRetried_NeverAFailure()
    {
        var (h, l) = Harness();
        await using var _ = h;
        l.LockBusy["A"] = 2;
        var q = await h.StartAsync(Options());
        var statuses = new ConcurrentQueue<JobStatus>();
        q.Changed += () =>
        {
            if (q.Snapshot().FirstOrDefault(j => j.AppName == "A") is { } j)
            {
                statuses.Enqueue(j.Status);
            }
        };

        var job = q.Enqueue(Repair("A"));
        var done = await QueueHarness.WaitForStatus(q, job.Id, JobStatus.Completed);

        Assert.Null(done.Error);
        Assert.Equal(["repair:A", "repair:A", "repair:A"], l.Calls);
        Assert.Contains(JobStatus.Waiting, statuses);
        Assert.DoesNotContain(JobStatus.Failed, statuses);
        Assert.True(h.Env.Log.Contains("install lock is busy"));
    }

    [Fact]
    public async Task RealCli_TheLockMessageWithExitZero_IsRetried()
    {
        await using var h = new QueueHarness();
        File.Copy(Fixture.PathOf("synthetic-installed.json"), Path.Combine(h.Env.Config, "installed.json"));
        h.Env.UseFixture("synthetic-install-lock.stderr.txt");
        h.Env.Script("repair", "errfile synthetic-install-lock.stderr.txt", invocation: 1);
        h.Env.Script("repair", "err [cli] INFO: Finished installation process in 0.05 seconds.");
        var q = await h.StartAsync(Options());

        var job = q.Enqueue(Repair("Fortnite"));
        var done = await QueueHarness.WaitForStatus(q, job.Id, JobStatus.Completed);
        Assert.Null(done.Error);
        Assert.Equal(2, h.Env.Invocations().Count(i => i.Command == "repair"));
    }

    [Fact]
    public async Task WhilePlayingPause_HoldsTheRunningJob_LetsNonPausableWorkThrough_AndResumes()
    {
        var policy = new TestPolicy();
        var (h, l) = Harness();
        await using var _ = h;
        l.Gate("A");
        var q = await h.StartAsync(Options(policy));
        var update = q.Enqueue(new JobRequest("A", JobKind.Update, null, null, "1", "2", "A"));
        await l.StartedFor("A").Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        policy.HoldReason = "Paused while Fortnite is running";
        q.Reevaluate();
        var held = await QueueHarness.WaitForStatus(q, update.Id, JobStatus.Waiting);
        Assert.Equal("Paused while Fortnite is running", held.BlockedReason);
        Assert.False(q.IsBusy); // Legendary was stopped (resumable)

        var uninstall = q.Enqueue(new JobRequest("B", JobKind.Uninstall, null, UninstallMode.GliderOnly, null, null, "B"));
        await QueueHarness.WaitForStatus(q, uninstall.Id, JobStatus.Completed); // can't pause, so it isn't held
        var verify = q.Enqueue(Verify("C"));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(JobStatus.Queued, q.Snapshot().Single(j => j.Id == verify.Id).Status);

        l.Gates.Clear();
        policy.HoldReason = null;
        q.Reevaluate();
        await QueueHarness.WaitForStatus(q, update.Id, JobStatus.Completed);
        await QueueHarness.WaitForStatus(q, verify.Id, JobStatus.Completed);
        Assert.Equal(["update:A", "uninstall:B", "update:A", "verify:C"], l.Calls);
    }

    [Fact]
    public async Task PauseAll_ResumeAll_AndClearFinished()
    {
        var (h, l) = Harness();
        await using var _ = h;
        l.Gate("A");
        var q = await h.StartAsync(Options());
        var a = q.Enqueue(Repair("A"));
        await l.StartedFor("A").Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var b = q.Enqueue(Verify("B"));

        await q.PauseAllAsync();
        Assert.True(q.Paused);
        Assert.All(q.Snapshot(), j => Assert.Equal(JobStatus.Paused, j.Status));
        var c = q.Enqueue(Verify("C"));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(JobStatus.Queued, q.Snapshot().Single(j => j.Id == c.Id).Status); // nothing starts while paused

        l.Gates.Clear();
        await q.ResumeAllAsync();
        Assert.False(q.Paused);
        foreach (var id in new[] { a.Id, b.Id, c.Id })
        {
            await QueueHarness.WaitForStatus(q, id, JobStatus.Completed);
        }

        q.ClearFinished();
        Assert.Empty(q.Snapshot());
    }

    [Fact]
    public async Task DlcJobs_InstallIntoTheBaseFolder_AndEveryRunIsSpawnedThroughTheResourcePolicy()
    {
        var resources = new RecordingResources();
        var (h, l) = Harness();
        await using var _ = h;
        var q = await h.StartAsync(Options(new TestPolicy { EffectiveMode = DownloadMode.Background }, resources));

        var dlc = q.Enqueue(new JobRequest("Dlc", JobKind.Install, null, null, null, "1", "DLC", new JobOptions(BaseAppName: "Base")));
        await QueueHarness.WaitForStatus(q, dlc.Id, JobStatus.Completed);

        var args = l.InstallArgs.First();
        Assert.Equal("Dlc", args.AppName);
        Assert.Null(args.BasePath); // Legendary installs a DLC into its base game's folder
        Assert.Same(RecordingResources.Profile, args.Resources);
        Assert.Contains((DownloadMode.Background, JobKind.Install), resources.Asked);
    }

    [Fact]
    public async Task TheMetadataLaneWaitsOutTheWritersPreDownloadPhase()
    {
        var activity = new WriterActivity();
        var (h, l) = Harness();
        await using var _ = h;
        l.Gate("A");
        var q = await h.StartAsync(Options(activity: activity));
        q.Enqueue(Repair("A"));
        await l.StartedFor("A").Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(activity.InPreDownload); // spawned, logging in / refreshing metadata, no progress yet
        var waiter = activity.WhenNotPreDownloadAsync(TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(waiter.IsCompleted);

        l.Gate("A").SetResult(); // the first progress line arrives
        await waiter.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Eventually(() => !q.IsBusy, "job done");
        Assert.False(activity.InPreDownload);
    }

    [Fact]
    public async Task PlannedPhases_AndOutcomes()
    {
        var (h, _) = Harness();
        await using var __ = h;
        var q = await h.StartAsync(Options());
        var v = q.Enqueue(Verify("A"));
        Assert.Equal([JobPhase.Verifying, JobPhase.Repairing, JobPhase.Finalizing], v.Phases);
        var done = await QueueHarness.WaitForStatus(q, v.Id, JobStatus.Completed);
        Assert.Equal(new JobOutcome(OutcomeKind.Success, "All files OK"), done.Outcome);
        Assert.Equal("A", done.Title);
        Assert.Equal([JobPhase.Removing], q.Enqueue(new JobRequest("A", JobKind.Uninstall, null, UninstallMode.GliderOnly, null, null)).Phases);
    }

    private sealed class RecordingResources : IResourcePolicy
    {
        public static readonly ResourceProfile Profile = new(LowPriority: false);

        public ConcurrentQueue<(DownloadMode, JobKind)> Asked { get; } = new();

        public ResourceProfile For(DownloadMode mode, JobKind kind)
        {
            Asked.Enqueue((mode, kind));
            return Profile;
        }
    }
}
