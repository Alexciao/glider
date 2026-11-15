using System.Collections.Immutable;
using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Tests.Services;

/// <summary>
/// Final UI review M1/M2: offline holds the queue instead of failing every job, the casualties of an outage resume when Epic is
/// back (bounded), and the queue says why it is held.
/// </summary>
public class OfflineQueueTests
{
    private const string OfflineHold = "Waiting for an internet connection";

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static void SetState(Rig rig, Connectivity c) => rig.Service.Hub.Mutate(s => s with { Connectivity = c });

    private static void Play(Rig rig) =>
        rig.Service.Hub.Mutate(s => s with
        {
            Sessions = s.Sessions.SetItem("Fortnite", new RunningGame("Fortnite", "Fortnite", RunState.Running, rig.Time.GetUtcNow(), false, false)),
        });

    private static async Task<Rig> StartAsync()
    {
        var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never, UpdateCheckIntervalMinutes = 24 * 60 });
        rig.Install("v1");
        await rig.StartWarmAsync();
        SetState(rig, Connectivity.Online);
        return rig;
    }

    private static JobInfo Failed(Rig rig, string app, GliderError error, DateTimeOffset? finished = null)
    {
        var job = rig.Jobs.Enqueue(new JobRequest(app, JobKind.Update, null, null, "v1", "v2", app));
        var failed = job with { Status = JobStatus.Failed, Error = error, FinishedAt = finished ?? rig.Time.GetUtcNow() };
        rig.Jobs.Set(failed);
        return failed;
    }

    /// <summary>Goes offline and comes back, the way the probe does.</summary>
    private static async Task OutageAsync(Rig rig)
    {
        SetState(rig, Connectivity.Offline);
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        SetState(rig, Connectivity.Online);
        await Task.Delay(30);
    }

    // ------------------------------------------------------------------ hold

    [Fact]
    public async Task Offline_HoldsTheQueue_WithItsReason_AndOnlineReleasesIt()
    {
        using var rig = await StartAsync();
        Assert.Null(rig.Service.QueuePolicy.HoldReason);
        Assert.Null(rig.Service.Hub.BuildSnapshot().Queue.HoldReason);

        var before = rig.Jobs.ReevaluateCount;
        SetState(rig, Connectivity.Offline);
        Assert.Equal(OfflineHold, rig.Service.QueuePolicy.HoldReason);
        Assert.Equal(OfflineHold, rig.Service.Hub.BuildSnapshot().Queue.HoldReason);
        await Eventually(() => rig.Jobs.ReevaluateCount > before); // the queue stops what runs and starts nothing

        before = rig.Jobs.ReevaluateCount;
        SetState(rig, Connectivity.Online);
        Assert.Null(rig.Service.QueuePolicy.HoldReason);
        Assert.Null(rig.Service.Hub.BuildSnapshot().Queue.HoldReason);
        await Eventually(() => rig.Jobs.ReevaluateCount > before); // and starts again
    }

    [Fact]
    public async Task OfflineWins_OverAGameRunning_AndTheGameReasonShowsOnceOnline()
    {
        using var rig = await StartAsync();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never, WhilePlaying = WhilePlayingPolicy.Pause });
        Play(rig);
        Assert.Equal("Paused while Fortnite is running", rig.Service.QueuePolicy.HoldReason);
        Assert.Equal("Paused while Fortnite is running", rig.Service.Hub.BuildSnapshot().Queue.HoldReason);

        SetState(rig, Connectivity.Offline);
        Assert.Equal(OfflineHold, rig.Service.QueuePolicy.HoldReason);

        SetState(rig, Connectivity.Online);
        Assert.Equal("Paused while Fortnite is running", rig.Service.QueuePolicy.HoldReason);
    }

    [Fact]
    public async Task AGameRunning_WithThrottleOrContinue_HoldsNothing()
    {
        using var rig = await StartAsync();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never, WhilePlaying = WhilePlayingPolicy.Throttle });
        Play(rig);
        Assert.Null(rig.Service.QueuePolicy.HoldReason);
        Assert.Null(rig.Service.Hub.BuildSnapshot().Queue.HoldReason);
    }

    // ------------------------------------------------------------------ resume on reconnect

    [Fact]
    public async Task WhenEpicIsBack_TheJobsTheOutageFailedResume_AndNoOthers()
    {
        using var rig = await StartAsync();
        rig.AddGame("B", "B", latest: "2", installedVersion: "1");
        rig.AddGame("C", "C", latest: "2", installedVersion: "1");
        rig.AddGame("D", "D", latest: "2", installedVersion: "1");
        var network = Failed(rig, "Fortnite", Errors.Network("no route"));
        var cdn = Failed(rig, "B", Errors.CdnUnavailable("timeout"));
        var corrupted = Failed(rig, "C", Errors.Corrupted("Fortnite"));
        var before = Failed(rig, "D", Errors.Network("an hour ago"), finished: rig.Time.GetUtcNow() - TimeSpan.FromHours(1));

        await OutageAsync(rig);

        await Eventually(() => rig.Jobs.Get(network.Id).Status == JobStatus.Queued && rig.Jobs.Get(cdn.Id).Status == JobStatus.Queued);
        Assert.Equal(JobStatus.Failed, rig.Jobs.Get(corrupted.Id).Status); // not an outage's doing
        Assert.Equal(JobStatus.Failed, rig.Jobs.Get(before.Id).Status); // failed long before this spell
    }

    [Fact]
    public async Task AJobThatFailedJustBeforeTheProbeFlipped_IsResumedToo()
    {
        using var rig = await StartAsync();
        var job = Failed(rig, "Fortnite", Errors.Network("no route"));
        rig.Time.Advance(TimeSpan.FromSeconds(45)); // Glider only noticed the outage 45 s after the download died
        await OutageAsync(rig);

        await Eventually(() => rig.Jobs.Get(job.Id).Status == JobStatus.Queued);
    }

    [Fact]
    public async Task ADismissedJob_IsNotBroughtBack()
    {
        using var rig = await StartAsync();
        var job = Failed(rig, "Fortnite", Errors.Network("no route"));
        rig.Jobs.Dismiss(job.Id);

        await OutageAsync(rig);

        Assert.Empty(rig.Jobs.Snapshot());
    }

    [Fact]
    public async Task APausedQueue_StaysPaused_WhenEpicIsBack()
    {
        using var rig = await StartAsync();
        var job = Failed(rig, "Fortnite", Errors.Network("no route"));
        rig.Jobs.Paused = true;

        await OutageAsync(rig);

        await Task.Delay(100);
        Assert.Equal(JobStatus.Failed, rig.Jobs.Get(job.Id).Status);
    }

    [Fact]
    public async Task ResumingAfterAnOutage_IsBounded()
    {
        using var rig = await StartAsync();
        var job = Failed(rig, "Fortnite", Errors.Network("no route"));

        for (var i = 0; i < GliderService.MaxNetworkResumes; i++)
        {
            await OutageAsync(rig);
            await Eventually(() => rig.Jobs.Get(job.Id).Status == JobStatus.Queued);
            rig.Jobs.Set(rig.Jobs.Get(job.Id) with { Status = JobStatus.Failed, Error = Errors.Network("still no route"), FinishedAt = rig.Time.GetUtcNow() }); // it fails again
        }

        await OutageAsync(rig);
        await Task.Delay(100);
        Assert.Equal(JobStatus.Failed, rig.Jobs.Get(job.Id).Status);
    }

    // ------------------------------------------------------------------ a network failure says "offline"

    [Fact]
    public async Task ANetworkFailure_MarksCoreOffline_SoTheQueueHolds_OtherFailuresDoNot()
    {
        using var rig = await StartAsync();
        rig.Jobs.Set(rig.Jobs.Enqueue(new JobRequest("Fortnite", JobKind.Update, null, null, "v1", "v2", "Fortnite")) with
        {
            Status = JobStatus.Failed,
            Error = Errors.Corrupted("Fortnite"),
            FinishedAt = rig.Time.GetUtcNow(),
        });
        await rig.Service.Hooks.OnFailedAsync(rig.Jobs.Snapshot()[0], TestContext.Current.CancellationToken);
        Assert.Equal(Connectivity.Online, rig.Service.Hub.State.Connectivity);

        await rig.Service.Hooks.OnFailedAsync(rig.Jobs.Snapshot()[0] with { Error = Errors.CdnUnavailable("x") }, TestContext.Current.CancellationToken);
        Assert.Equal(Connectivity.Online, rig.Service.Hub.State.Connectivity); // Epic's servers being down isn't this PC being offline

        await rig.Service.Hooks.OnFailedAsync(rig.Jobs.Snapshot()[0] with { Error = Errors.Network("no route") }, TestContext.Current.CancellationToken);
        Assert.Equal(Connectivity.Offline, rig.Service.Hub.State.Connectivity);
        Assert.Equal(OfflineHold, rig.Service.QueuePolicy.HoldReason);
    }
}
