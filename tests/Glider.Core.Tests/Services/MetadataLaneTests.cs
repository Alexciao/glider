using Glider.Core.Jobs;
using Glider.Core.Services;

namespace Glider.Core.Tests.Services;

/// <summary>The metadata lane (L§1.8): one command at a time, never during a writer's pre-download phase; interactive callers go first (review H7).</summary>
public sealed class MetadataLaneTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task Settle() => await Task.Delay(60, Ct);

    /// <summary>Starts a lane job that records its name when it begins and finishes when <paramref name="release"/> completes.</summary>
    private static Task<int> Job(Func<Func<CancellationToken, Task<int>>, CancellationToken, Task<int>> run, string name, List<string> started, Task? release = null) =>
        run(async _ =>
        {
            lock (started)
            {
                started.Add(name);
            }

            if (release is not null)
            {
                await release;
            }

            return 1;
        }, Ct);

    [Fact]
    public async Task AnInteractiveCallJumpsQueuedBackgroundWork_ButNeverPreemptsTheRunningCommand()
    {
        using var lane = new MetadataLane(new WriterActivity());
        var started = new List<string>();
        var runningGate = new TaskCompletionSource();
        var running = Job(lane.RunAsync, "running-bg", started, runningGate.Task);
        await Settle();
        var queuedBg1 = Job(lane.RunAsync, "bg1", started);
        var queuedBg2 = Job(lane.RunAsync, "bg2", started);
        await Settle();
        var interactive = Job(lane.RunInteractiveAsync, "interactive", started);
        await Settle();

        Assert.Equal(["running-bg"], started); // the running command is not preempted; nothing else started

        runningGate.SetResult();
        await Task.WhenAll(running, queuedBg1, queuedBg2, interactive).WaitAsync(Wait, Ct);
        Assert.Equal(["running-bg", "interactive", "bg1", "bg2"], started);
    }

    [Fact]
    public async Task InteractiveCallsKeepArrivalOrderAmongThemselves()
    {
        using var lane = new MetadataLane(new WriterActivity());
        var started = new List<string>();
        var gate = new TaskCompletionSource();
        var running = Job(lane.RunAsync, "running", started, gate.Task);
        await Settle();
        var a = Job(lane.RunInteractiveAsync, "i1", started);
        var b = Job(lane.RunInteractiveAsync, "i2", started);
        await Settle();

        gate.SetResult();
        await Task.WhenAll(running, a, b).WaitAsync(Wait, Ct);
        Assert.Equal(["running", "i1", "i2"], started);
    }

    [Fact]
    public async Task OnlyOneCommandRunsAtATime_AcrossBothPriorities()
    {
        using var lane = new MetadataLane(new WriterActivity());
        var concurrent = 0;
        var peak = 0;

        async Task<int> Work(CancellationToken ct)
        {
            var now = Interlocked.Increment(ref concurrent);
            int seen;
            while ((seen = Volatile.Read(ref peak)) < now && Interlocked.CompareExchange(ref peak, now, seen) != seen)
            {
            }

            await Task.Delay(15, ct);
            Interlocked.Decrement(ref concurrent);
            return 1;
        }

        var all = Enumerable.Range(0, 12).Select(i => i % 3 == 0 ? lane.RunInteractiveAsync(Work, Ct) : lane.RunAsync(Work, Ct)).ToArray();
        await Task.WhenAll(all).WaitAsync(Wait, Ct);

        Assert.Equal(1, peak);
        Assert.False(lane.Busy);
    }

    [Fact]
    public async Task NeitherPriorityRunsDuringTheWritersPreDownloadPhase()
    {
        var activity = new WriterActivity();
        using var lane = new MetadataLane(activity);
        var started = new List<string>();
        activity.BeginPreDownload();

        var bg = Job(lane.RunAsync, "bg", started);
        var interactive = Job(lane.RunInteractiveAsync, "interactive", started);
        await Settle();
        Assert.Empty(started);

        activity.EndPreDownload();
        await Task.WhenAll(bg, interactive).WaitAsync(Wait, Ct);
        Assert.Equal(["interactive", "bg"], started); // and the interactive one still goes first once the phase is over
    }

    [Fact]
    public async Task ABackgroundJobWaitingOutPreDownloadYieldsTheLaneToAnInteractiveCallThatArrivesLater()
    {
        var activity = new WriterActivity();
        using var lane = new MetadataLane(activity);
        var started = new List<string>();
        var bg = Job(lane.RunAsync, "bg", started);
        await Settle(); // bg already ran: the lane is free again
        await bg.WaitAsync(Wait, Ct);
        started.Clear();

        activity.BeginPreDownload();
        var bg2 = Job(lane.RunAsync, "bg2", started);
        await Settle(); // bg2 holds no running command, it is only waiting out the phase
        var interactive = Job(lane.RunInteractiveAsync, "interactive", started);
        await Settle();
        Assert.Empty(started);

        activity.EndPreDownload();
        await Task.WhenAll(bg2, interactive).WaitAsync(Wait, Ct);
        Assert.Equal(["interactive", "bg2"], started);
    }

    [Fact]
    public async Task ACancelledWaiterLeavesTheQueue_AndTheLaneKeepsWorking()
    {
        using var lane = new MetadataLane(new WriterActivity());
        var started = new List<string>();
        var gate = new TaskCompletionSource();
        var running = Job(lane.RunAsync, "running", started, gate.Task);
        await Settle();

        using var cts = new CancellationTokenSource();
        var cancelled = lane.RunInteractiveAsync<int>(_ => throw new InvalidOperationException("must not run"), cts.Token);
        var next = Job(lane.RunAsync, "next", started);
        await Settle();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        gate.SetResult();
        await Task.WhenAll(running, next).WaitAsync(Wait, Ct);
        Assert.Equal(["running", "next"], started);
        Assert.False(lane.Busy);
    }

    [Fact]
    public async Task AFailingCommandReleasesTheLane()
    {
        using var lane = new MetadataLane(new WriterActivity());
        await Assert.ThrowsAsync<InvalidOperationException>(() => lane.RunInteractiveAsync<int>(_ => throw new InvalidOperationException("boom"), Ct));
        Assert.Equal(7, await lane.RunAsync(_ => Task.FromResult(7), Ct).WaitAsync(Wait, Ct));
    }
}
