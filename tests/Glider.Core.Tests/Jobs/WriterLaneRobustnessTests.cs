using System.Collections.Concurrent;
using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;
using Glider.Core.Infrastructure.Migrations;
using Glider.Core.Jobs;
using Glider.Core.Tests.Infrastructure;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Jobs;

/// <summary>A per-app gate for queue tests: apps in <see cref="Blocked"/> can't start, with that reason.</summary>
internal sealed class TestGate : IJobGate
{
    public ConcurrentDictionary<string, string> Blocked { get; } = new(StringComparer.Ordinal);

    public string? ReasonNotToStart(JobInfo job) => Blocked.TryGetValue(job.AppName, out var reason) ? reason : null;
}

/// <summary>Writer-lane robustness (docs/review-v0.2-architecture.md WP3: H3, M1, M2, M8, L10, L11).</summary>
public class WriterLaneRobustnessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JobRequest Verify(string app) => new(app, JobKind.Verify, null, null, null, null, app);

    private static JobRequest Repair(string app) => new(app, JobKind.Repair, null, null, null, null, app);

    private static JobQueueOptions Options(IJobGate? gate = null) => new()
    {
        AutoTune = new AutoTuneResult(7, 1536),
        ProgressInterval = TimeSpan.FromMilliseconds(1),
        LockRetryDelay = TimeSpan.FromMilliseconds(20),
        Gate = gate,
        GateRecheckInterval = TimeSpan.FromMilliseconds(20),
    };

    private static JobInfo Job(JobQueue q, string id) => q.Snapshot().Single(j => j.Id == id);

    /// <summary>The hooks' side of the gate contract: re-check at the claim, never block.</summary>
    private static void GateHooks(QueueHarness h, TestGate gate) =>
        h.Hooks.Before = (job, _, _) => gate.ReasonNotToStart(job) is { } reason ? throw new JobGatedException(reason) : Task.CompletedTask;

    // ------------------------------------------------------------------ H3

    [Fact]
    public async Task AJobWaitingForItsOwnGame_YieldsTheWriterSlot_AndRunsFirstOnceUnblocked()
    {
        var l = new GatedLegendary();
        await using var h = new QueueHarness(legendary: l);
        var gate = new TestGate();
        GateHooks(h, gate);
        gate.Blocked["A"] = "Waiting for A to close";
        l.Gate("B");
        var q = await h.StartAsync(Options(gate));

        var a = q.Enqueue(Verify("A"));
        var b = q.Enqueue(Verify("B"));
        var c = q.Enqueue(Verify("C"));
        var aRepair = q.Enqueue(Repair("A"));

        // Another game's download runs while A waits for its game; A shows why it waits.
        await l.StartedFor("B").Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var waiting = await QueueHarness.WaitFor(q, a.Id, j => j.Status == JobStatus.Waiting);
        Assert.Equal("Waiting for A to close", waiting.BlockedReason);
        Assert.Equal(JobStatus.Running, Job(q, b.Id).Status);
        Assert.Equal(JobStatus.Queued, Job(q, aRepair.Id).Status); // the same game's later job stays behind it

        // Unblocked while B runs: A goes back to queued (nothing is pre-empted) and runs next, in queue order.
        gate.Blocked.TryRemove("A", out _);
        await QueueHarness.WaitFor(q, a.Id, j => j.Status == JobStatus.Queued && j.BlockedReason is null);
        l.Gate("B").SetResult();
        await QueueHarness.WaitForStatus(q, aRepair.Id, JobStatus.Completed);
        Assert.Equal(JobStatus.Completed, Job(q, c.Id).Status);
        Assert.Equal(["verify:B", "verify:A", "verify:C", "repair:A"], l.Calls);
        Assert.DoesNotContain(h.Hooks.Calls, c => c.StartsWith("failed:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGateThatClosesAtTheClaim_PutsTheJobBack_NeverFailsIt()
    {
        var l = new GatedLegendary();
        await using var h = new QueueHarness(legendary: l);
        var gate = new TestGate();
        var raced = 0;
        h.Hooks.Before = (job, _, _) =>
        {
            if (job.AppName == "A" && Interlocked.Exchange(ref raced, 1) == 0)
            {
                gate.Blocked["A"] = "Epic Games Launcher is updating this game";
            }

            return gate.ReasonNotToStart(job) is { } reason ? throw new JobGatedException(reason) : Task.CompletedTask;
        };
        var q = await h.StartAsync(Options(gate));

        var a = q.Enqueue(Verify("A"));
        var b = q.Enqueue(Verify("B"));
        await QueueHarness.WaitForStatus(q, b.Id, JobStatus.Completed);
        var waiting = Job(q, a.Id);
        Assert.Equal(JobStatus.Waiting, waiting.Status);
        Assert.Equal("Epic Games Launcher is updating this game", waiting.BlockedReason);
        Assert.False(q.IsBusy);

        gate.Blocked.Clear();
        var done = await QueueHarness.WaitForStatus(q, a.Id, JobStatus.Completed);
        Assert.Null(done.Error);
        Assert.Equal(["verify:B", "verify:A"], l.Calls);
    }

    [Fact]
    public async Task PauseAll_PausesAWaitingJob_AndARestartQueuesItAgain()
    {
        var l = new GatedLegendary();
        await using var h = new QueueHarness(legendary: l);
        var gate = new TestGate();
        GateHooks(h, gate);
        gate.Blocked["A"] = "Waiting for A to close";
        var q = await h.StartAsync(Options(gate));
        var a = q.Enqueue(Verify("A"));
        await QueueHarness.WaitForStatus(q, a.Id, JobStatus.Waiting);
        await q.DisposeAsync();

        var restarted = await h.StartAsync(Options(gate));
        await QueueHarness.WaitFor(restarted, a.Id, j => j.Status == JobStatus.Waiting && j.BlockedReason == "Waiting for A to close");
        await restarted.PauseAllAsync();
        Assert.Equal(JobStatus.Paused, Job(restarted, a.Id).Status);
        Assert.Empty(l.Calls);
    }

    // ------------------------------------------------------------------ M2

    [Fact]
    public async Task AThrowingSubscriber_NeverWedgesTheLane_AndPauseAndCancelReturn()
    {
        var l = new GatedLegendary();
        await using var h = new QueueHarness(legendary: l);
        var q = await h.StartAsync(Options());
        q.Changed += () => throw new InvalidOperationException("a subscriber bug");
        q.ProgressChanged += _ => throw new ObjectDisposedException("host tap");
        l.Gate("A");

        var a = q.Enqueue(Verify("A"));
        await l.StartedFor("A").Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var paused = await q.PauseAsync(a.Id).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(JobStatus.Paused, paused.Status);

        var b = q.Enqueue(Verify("B"));
        await QueueHarness.WaitForStatus(q, b.Id, JobStatus.Completed);
        var cancelled = await q.CancelAsync(a.Id).WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);

        var c = q.Enqueue(Verify("C"));
        await QueueHarness.WaitForStatus(q, c.Id, JobStatus.Completed);
        Assert.Contains(h.Env.Log.Lines, line => line.Contains("a subscriber bug", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ M8

    [Fact]
    public async Task ARunningUninstall_IsNotCancellable_AndCancelIsRefused()
    {
        var l = new GatedLegendary();
        await using var h = new QueueHarness(legendary: l);
        var q = await h.StartAsync(Options());
        l.Gate("A");

        var a = q.Enqueue(new JobRequest("A", JobKind.Uninstall, null, UninstallMode.DeleteFiles, null, null, "A"));
        var b = q.Enqueue(new JobRequest("B", JobKind.Uninstall, null, UninstallMode.DeleteFiles, null, null, "B"));
        await l.StartedFor("A").Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var running = Job(q, a.Id);
        Assert.Equal(JobStatus.Running, running.Status);
        Assert.False(running.Cancellable);
        Assert.True(Job(q, b.Id).Cancellable); // queued: nothing deleted yet

        var refused = await Assert.ThrowsAsync<GliderException>(() => q.CancelAsync(a.Id));
        Assert.Equal(ErrorCode.InvalidRequest, refused.Error.Code);
        Assert.Equal(JobStatus.Running, Job(q, a.Id).Status);

        var cancelled = await q.CancelAsync(b.Id);
        Assert.Equal(JobStatus.Cancelled, cancelled.Status);
        l.Gate("A").SetResult();
        Assert.True((await QueueHarness.WaitForStatus(q, a.Id, JobStatus.Completed)).Cancellable);
    }

    [Fact]
    public async Task ARunningMove_IsCancellableOnlyWhileItCopies()
    {
        var l = new GatedLegendary();
        await using var h = new QueueHarness(legendary: l);
        var q = await h.StartAsync(Options());
        var target = Path.Combine(h.Env.Root, "Elsewhere");

        // Same volume: Legendary renames and rewrites its metadata; there's no copy to discard.
        l.Gate("A");
        var rename = q.Enqueue(new JobRequest("A", JobKind.Move, target, null, null, null, "A"));
        await l.StartedFor("A").Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.False(Job(q, rename.Id).Cancellable);
        await Assert.ThrowsAsync<GliderException>(() => q.CancelAsync(rename.Id));
        l.Gate("A").SetResult();
        await QueueHarness.WaitForStatus(q, rename.Id, JobStatus.Completed);

        // Cross volume: the copy phase reports progress and its copy is discarded on cancel.
        l.EarlyProgress["B"] = GatedLegendary.Line(10);
        l.Gate("B");
        var copy = q.Enqueue(new JobRequest("B", JobKind.Move, target, null, null, null, "B"));
        await QueueHarness.WaitFor(q, copy.Id, j => j.Status == JobStatus.Running && j.Cancellable);
        Assert.Equal(JobStatus.Cancelled, (await q.CancelAsync(copy.Id)).Status);
    }

    // ------------------------------------------------------------------ M1

    [Fact]
    public async Task RestoreNeverOverwritesAJobsFileItCouldNotFullyRead_AndKeepsItsBytesBeforeTheFirstWrite()
    {
        await using var h = new QueueHarness(legendary: new GatedLegendary());
        var path = JobQueue.PathFor(h.Env.Paths);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v0.1-data", "state", "jobs.json"), path, overwrite: true);
        var original = await File.ReadAllBytesAsync(path, Ct);

        var q = await h.StartAsync(Options());
        Assert.Empty(q.Snapshot()); // v0.1 entries have no appName: unusable for this reader
        await Task.Delay(100, Ct);
        Assert.Equal(original, await File.ReadAllBytesAsync(path, Ct)); // left for the migration retry

        var b = q.Enqueue(Verify("B"));
        await QueueHarness.WaitForStatus(q, b.Id, JobStatus.Completed);
        await q.DisposeAsync();
        var kept = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, "jobs.json.unreadable-*"));
        Assert.Equal(original, await File.ReadAllBytesAsync(kept, Ct));
        Assert.Contains("\"B\"", await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ L10

    [Fact]
    public async Task APersistFailureOfAnyKind_IsLogged_NeverThrown()
    {
        var l = new GatedLegendary();
        await using var h = new QueueHarness(legendary: l);
        var q = await h.StartAsync(Options());
        l.Gate("A");

        // A garbled progress line (NaN rates) can't be written as JSON: an ArgumentException, not an IOException.
        l.EarlyProgress["A"] = GatedLegendary.Line(double.NaN, double.NaN);
        var a = q.Enqueue(Verify("A"));
        await QueueHarness.WaitFor(q, a.Id, j => j.Progress is not null);
        Assert.Equal(JobStatus.Paused, (await q.PauseAsync(a.Id)).Status);
        Assert.Contains(h.Env.Log.Lines, line => line.Contains("jobs.json", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ L11

    [Fact]
    public async Task AnEnqueueBeforeRestore_MergesWithTheRestoredJob_AndTheRestoredOrderComesFirst()
    {
        var l = new GatedLegendary();
        await using var h = new QueueHarness(legendary: l);
        var first = await h.StartAsync(Options());
        await first.PauseAllAsync();
        var a = first.Enqueue(Verify("A"));
        var b = first.Enqueue(Verify("B"));
        await first.DisposeAsync();

        var q = h.NewQueue(Options());
        q.Enqueue(Verify("C")); // a click during startup, before the queue restored jobs.json
        q.Enqueue(Verify("A"));
        await q.RestoreAsync(Ct);

        var unfinished = q.Snapshot().Where(j => j.Status is not (JobStatus.Completed or JobStatus.Cancelled or JobStatus.Failed)).ToList();
        Assert.Equal(["A", "B", "C"], unfinished.Select(j => j.AppName));
        Assert.Equal(a.Id, unfinished[0].Id);
        Assert.Equal(b.Id, unfinished[1].Id);

        await q.ResumeAllAsync();
        await QueueHarness.WaitForStatus(q, unfinished[2].Id, JobStatus.Completed);
        Assert.Equal(["verify:A", "verify:B", "verify:C"], l.Calls);
    }
}

/// <summary>M1 end to end: a failed v0.1 migration, a start in between, then the retry (MigrationV1ToV2.FailAfter is static).</summary>
[Collection(MigrationCollection.Name)]
public sealed class JobsMigrationRetryTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task AFailedMigration_ThenAStart_LeavesTheV01JobsForTheRetry()
    {
        using var env = new FakeLegendaryEnv();
        var paths = new AppPaths(_dir.Combine("Glider"), env.Paths.LegendaryExe);
        foreach (var sub in new[] { "state", "cache" })
        {
            var dest = Path.Combine(paths.Root, sub);
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "v0.1-data", sub)))
            {
                File.Copy(f, Path.Combine(dest, Path.GetFileName(f)));
            }
        }

        MigrationV1ToV2.FailAfter = "games";
        try
        {
            await using var runtime = await Glider.Core.Services.GliderRuntime.StartAsync(paths, new Services.FakeHost(new Services.CallLog()), new Services.FakeSink(),
                new EglEnvironment(_dir.Combine("ProgramData")), options: new Glider.Core.Services.GliderRuntimeOptions
                {
                    ProcessMonitor = new Services.FakeProcesses(),
                    ProcessStarter = new Services.FakeProcesses(),
                    DiskInfo = new Services.FakeDisk(),
                    FreeGamesBackground = false,
                    EpicPlaytimeBackground = false,
                    Legendary = new Glider.Core.Legendary.LegendaryOptions { PinnedVersion = "0.21.1" },
                    OverlayRegistry = new Services.FakeOverlayRegistry(),
                    OverlayEnvironment = new Glider.Core.Overlay.OverlayEnvironment(null),
                    ThirdPartyLaunchers = new Services.FakeLaunchers(),
                }, ct: TestContext.Current.CancellationToken);
            Assert.Equal(MigrationStatus.Failed, runtime.Migration.Status);
            await runtime.Service.WarmupTask.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken); // jobs restored
        }
        finally
        {
            MigrationV1ToV2.FailAfter = null;
        }

        var retried = await Migrator.RunAsync(paths, new TestLog(), ct: TestContext.Current.CancellationToken);
        Assert.Equal(MigrationStatus.Migrated, retried.Status);
        using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(JobQueue.PathFor(paths), TestContext.Current.CancellationToken));
        var job = Assert.Single(doc.RootElement.GetProperty("jobs").EnumerateArray());
        Assert.Equal("Fortnite", job.GetProperty("request").GetProperty("appName").GetString());
        Assert.Equal("c987321d1b474215ba80fe35900e1201", job.GetProperty("info").GetProperty("id").GetString());
    }
}
