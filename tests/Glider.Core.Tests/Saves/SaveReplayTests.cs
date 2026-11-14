using Glider.Core.Contracts;
using Glider.Core.Saves;
using Glider.Core.Tests.Services;

namespace Glider.Core.Tests.Saves;

/// <summary>Review H2: a post-exit upload skipped while offline used to be dropped; it is replayed once Epic is reachable again.</summary>
public class SaveReplayTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);
    private const string App = SaveRig.App;

    private static readonly SaveOptions WithRetries = new() { PostExitRetryDelays = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)], PreLaunchTimeout = TimeSpan.FromSeconds(30) };

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private static void SetState(SaveRig s, Connectivity c) => s.Service.Hub.Mutate(st => st with { Connectivity = c });

    /// <summary>A synced baseline, then progress on this PC, then the game closes while Glider is offline: the upload is skipped.</summary>
    private static async Task<SaveRig> ClosedWhileOfflineAsync()
    {
        var s = new SaveRig(options: WithRetries);
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        await s.SyncAsync(); // baseline: 1 upload
        s.Write("slot1.sav", "v2 played offline", T0.AddHours(1));
        SetState(s, Connectivity.Offline);
        await Eventually(() => s.Service.NetworkForTests.NextProbe is not null);

        s.Saves.AfterExit(App);
        await s.Saves.PostExitIdle;

        Assert.Equal(1, s.Cloud.SyncCalls(SyncMode.Upload));
        Assert.Equal([App], s.Saves.PendingReplay);
        return s;
    }

    [Fact]
    public async Task AnUploadSkippedWhileOffline_IsNotRetriedOnATimer_AndIsNotAnError()
    {
        using var s = await ClosedWhileOfflineAsync();

        s.Rig.Time.Advance(TimeSpan.FromMinutes(10)); // the old retries at 30 s / 2 min would only have skipped again
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(1, s.Cloud.SyncCalls(SyncMode.Upload));
        Assert.Equal(SaveSyncStatus.LocalNewer, s.State().Status);
        Assert.Contains("sync the next time", s.State().Note, StringComparison.Ordinal);
        Assert.DoesNotContain(s.Notices(), n => n.Level == NoticeLevel.Warning);
        Assert.Equal("v2 played offline", s.Read("slot1.sav"));
    }

    [Fact]
    public async Task WhenEpicIsReachableAgain_TheSkippedUploadIsReplayed_Once()
    {
        using var s = await ClosedWhileOfflineAsync();

        s.Rig.Probe.Reachable = true;
        s.Rig.Time.Advance(TimeSpan.FromSeconds(15)); // the monitor's first probe
        await Eventually(() => s.Cloud.SyncCalls(SyncMode.Upload) == 2);
        await s.Saves.PostExitIdle;

        Assert.Equal(SaveSyncStatus.InSync, s.State().Status);
        Assert.Empty(s.Saves.PendingReplay);
        Assert.Equal("v2 played offline", s.Read("slot1.sav"));
        Assert.DoesNotContain(s.Notices(), n => n.Level == NoticeLevel.Warning);

        // The connection drops and comes back twice more: nothing is waiting, nothing is uploaded again.
        SetState(s, Connectivity.Offline);
        SetState(s, Connectivity.Online);
        SetState(s, Connectivity.Offline);
        SetState(s, Connectivity.Online);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        await s.Saves.PostExitIdle;
        Assert.Equal(2, s.Cloud.SyncCalls(SyncMode.Upload));
    }

    [Fact]
    public async Task TwoFlipsBackToBack_AreOneReplay()
    {
        using var s = await ClosedWhileOfflineAsync();

        SetState(s, Connectivity.Online);
        s.Saves.ReplayPending(); // a second trigger arrives before the first replay finished
        s.Saves.ReplayPending();
        await Eventually(() => s.Cloud.SyncCalls(SyncMode.Upload) == 2);
        await s.Saves.PostExitIdle;
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(2, s.Cloud.SyncCalls(SyncMode.Upload));
        Assert.Equal(SaveSyncStatus.InSync, s.State().Status);
    }

    [Fact]
    public async Task NothingIsReplayedWhileTheGameRunsAgain_TheNextSessionsOwnSyncCoversIt()
    {
        using var s = await ClosedWhileOfflineAsync();

        // Play again (offline), and stay in the game.
        Glider.Core.Services.ProcessEntry? root = null;
        s.Rig.Processes.OnStart = (_, p) => root = p;
        var result = await s.Service.Launch(new LaunchRequest(App, null, false));
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        var sessions = s.Service.LaunchServiceForTests.Sessions;
        s.Rig.Time.Advance(TimeSpan.FromSeconds(2));
        sessions.Tick();
        Assert.Equal(RunState.Running, s.Service.Hub.State.RunStateOf(App));

        // Epic comes back while the game is running.
        SetState(s, Connectivity.Online);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(1, s.Cloud.SyncCalls(SyncMode.Upload)); // not while the game runs
        Assert.Equal([App], s.Saves.PendingReplay);

        // The game closes: one post-exit sync, which also settles what was waiting.
        s.Rig.Processes.Exit(root!.Id);
        sessions.Tick();
        s.Rig.Time.Advance(TimeSpan.FromSeconds(6));
        sessions.Tick();
        await Eventually(() => s.Cloud.SyncCalls(SyncMode.Upload) == 2);
        await s.Saves.PostExitIdle;
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(2, s.Cloud.SyncCalls(SyncMode.Upload));
        Assert.Empty(s.Saves.PendingReplay);
        Assert.Equal(SaveSyncStatus.InSync, s.State().Status);
    }

    [Fact]
    public async Task AnUploadSkippedBecauseNobodyIsSignedIn_IsNotQueuedForReplay()
    {
        using var s = new SaveRig(options: WithRetries);
        await s.StartAsync();
        s.Write("slot1.sav", "v1", T0);
        await s.SyncAsync();
        s.Write("slot1.sav", "v2", T0.AddHours(1));
        await s.Service.SignOut();

        s.Saves.AfterExit(App);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        SetState(s, Connectivity.Offline);
        SetState(s, Connectivity.Online);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Empty(s.Saves.PendingReplay);
        Assert.Equal(1, s.Cloud.SyncCalls(SyncMode.Upload));
    }

    // ------------------------------------------------------------------ Auto launch mode reads the state at click time

    [Fact]
    public async Task AutoLaunch_UsesTheConnectivityOfTheMomentOfTheClick()
    {
        using var s = new SaveRig(options: WithRetries);
        await s.StartAsync();
        SetState(s, Connectivity.Offline);
        await Eventually(() => s.Service.NetworkForTests.NextProbe is not null);

        var offline = await s.LaunchAsync(skipSync: true);
        Assert.True(offline.Offline);

        s.Rig.Probe.Reachable = true;
        s.Rig.Time.Advance(TimeSpan.FromSeconds(15));
        await Eventually(() => s.Service.Hub.State.Connectivity == Connectivity.Online);

        var online = await s.LaunchAsync(skipSync: true);
        Assert.False(online.Offline);
        Assert.False(s.Rig.Legendary.LastOffline);

        SetState(s, Connectivity.Offline);
        Assert.True((await s.LaunchAsync(skipSync: true)).Offline); // and back again: no memory of the earlier decision
    }
}
