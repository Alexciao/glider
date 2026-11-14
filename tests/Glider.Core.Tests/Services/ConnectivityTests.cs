using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Tests.Services;

/// <summary>Review H2: once Core decided it was offline, nothing ever looked again.</summary>
public class ConnectivityTests
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(60);

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    /// <summary>The monitor has worked out when its next probe is due (the clock may move now).</summary>
    private static Task Armed(Rig rig) => Eventually(() => rig.Service.NetworkForTests.NextProbe is not null);

    private static int Syncs(Rig rig) => rig.Calls.Count("legendary.checkUpdates") + rig.Calls.Count("legendary.list");

    private static Connectivity State(Rig rig) => rig.Service.Hub.State.Connectivity;

    private static void SetState(Rig rig, Connectivity c) => rig.Service.Hub.Mutate(s => s with { Connectivity = c });

    /// <summary>The laptop starts Glider before Wi-Fi is up: pre-warm gets a network error and Core goes Offline.</summary>
    private static async Task<Rig> StartOfflineAsync(AutoUpdatePolicy policy = AutoUpdatePolicy.Never)
    {
        var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = policy, UpdateCheckIntervalMinutes = 24 * 60 });
        rig.Install("v1");
        rig.Legendary.SyncError = Errors.Network("no route to host");
        await rig.StartWarmAsync();
        Assert.Equal(Connectivity.Offline, State(rig));
        await Armed(rig);
        return rig;
    }

    /// <summary>Wi-Fi works again: Epic answers the probe, and Legendary's own calls work too.</summary>
    private static void NetworkIsBack(Rig rig)
    {
        rig.Legendary.SyncError = null;
        rig.Probe.Reachable = true;
    }

    // ------------------------------------------------------------------ the scenario from the review

    [Fact]
    public async Task StartedBeforeWiFiWasUp_RecoversWhenEpicIsReachable_AndChecksForUpdatesAgain()
    {
        using var rig = await StartOfflineAsync();
        var before = Syncs(rig);

        rig.Legendary.SyncError = null; // Wi-Fi is up now
        rig.Probe.Reachable = true;
        rig.Time.Advance(TimeSpan.FromSeconds(20));

        await Eventually(() => State(rig) == Connectivity.Online);
        await Eventually(() => Syncs(rig) > before); // not after the 24 h interval
    }

    [Fact]
    public async Task AutoUpdates_WaitWhileOffline_AndAreQueuedOnceEpicIsBack()
    {
        using var rig = await StartOfflineAsync(AutoUpdatePolicy.Always);
        Assert.Empty(rig.Jobs.Requests); // v1 -> v2 is known from the cache, but there is no connection to download it
        Assert.Contains("Fortnite", rig.Service.UpdatesForTests.Deferred);

        NetworkIsBack(rig);
        rig.Time.Advance(TimeSpan.FromSeconds(20));

        await Eventually(() => rig.Jobs.Requests.Count == 1);
        Assert.Equal(JobKind.Update, rig.Jobs.Requests[0].Kind);
        rig.Time.Advance(TimeSpan.FromMinutes(10)); // the retry timer finds nothing left
        await Task.Delay(Settle);
        Assert.Single(rig.Jobs.Requests);
    }

    // ------------------------------------------------------------------ probing: backoff

    [Fact]
    public void Backoff_Starts_At_15_Seconds_Doubles_AndStopsAt_5_Minutes()
    {
        var initial = TimeSpan.FromSeconds(15);
        var max = TimeSpan.FromMinutes(5);
        Assert.Equal(
            [15, 30, 60, 120, 240, 300, 300, 300],
            Enumerable.Range(0, 8).Select(i => (int)ConnectivityMonitor.Backoff(i, initial, max).TotalSeconds));
        Assert.Equal(max, ConnectivityMonitor.Backoff(10_000, initial, max));
    }

    [Fact]
    public async Task WhileOffline_ProbesOnTheBackoffSchedule()
    {
        using var rig = await StartOfflineAsync();
        var start = rig.Probe.Calls;

        rig.Time.Advance(TimeSpan.FromSeconds(14));
        await Task.Delay(Settle);
        Assert.Equal(start, rig.Probe.Calls);

        rig.Time.Advance(TimeSpan.FromSeconds(1)); // 15 s after going offline
        await Eventually(() => rig.Probe.Calls == start + 1);
        await Armed(rig);

        rig.Time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(Settle);
        Assert.Equal(start + 1, rig.Probe.Calls);
        rig.Time.Advance(TimeSpan.FromSeconds(1)); // +30 s
        await Eventually(() => rig.Probe.Calls == start + 2);
        await Armed(rig);

        rig.Time.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(Settle);
        Assert.Equal(start + 2, rig.Probe.Calls);
        rig.Time.Advance(TimeSpan.FromSeconds(1)); // +60 s
        await Eventually(() => rig.Probe.Calls == start + 3);
        Assert.Equal(Connectivity.Offline, State(rig));
    }

    [Fact]
    public async Task AFlappingNetwork_KeepsItsDelays_InsteadOfProbingEvery15Seconds()
    {
        using var rig = await StartOfflineAsync();
        rig.Probe.Reachable = true; // Epic answers the probe, but Legendary's own calls keep failing: Offline again at once
        rig.Time.Advance(TimeSpan.FromSeconds(15));
        await Eventually(() => rig.Probe.Calls == 1);
        await Eventually(() => rig.Service.Hub.State.SyncError is not null && State(rig) == Connectivity.Offline && rig.Calls.Count("legendary.checkUpdates") + rig.Calls.Count("legendary.list") >= 2);
        await Task.Delay(Settle);

        rig.Time.Advance(TimeSpan.FromSeconds(29)); // the second probe of a spell waits 30 s, not 15
        await Task.Delay(Settle);
        Assert.Equal(1, rig.Probe.Calls);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => rig.Probe.Calls == 2);
    }

    [Fact]
    public async Task ASlowProbe_IsGivenUpOn_AndTheScheduleGoesOn()
    {
        using var rig = await StartOfflineAsync();
        rig.Probe.Gate = new TaskCompletionSource(); // the network never answers
        NetworkIsBack(rig);

        rig.Time.Advance(TimeSpan.FromSeconds(15));
        await Eventually(() => rig.Probe.Calls == 1);
        rig.Time.Advance(TimeSpan.FromSeconds(10)); // the probe's own timeout
        await Armed(rig); // given up on: the next probe is scheduled
        Assert.Equal(Connectivity.Offline, State(rig)); // a timeout is not "reachable"

        rig.Probe.Gate = null;
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        await Eventually(() => State(rig) == Connectivity.Online);
    }

    // ------------------------------------------------------------------ probing: network changes

    [Fact]
    public async Task ANetworkChange_ProbesAtOnce_AndStartsTheDelaysOver()
    {
        using var rig = await StartOfflineAsync();
        rig.Time.Advance(TimeSpan.FromSeconds(15));
        await Eventually(() => rig.Probe.Calls == 1);
        await Armed(rig);
        rig.Time.Advance(TimeSpan.FromSeconds(30));
        await Eventually(() => rig.Probe.Calls == 2);
        await Armed(rig); // the next one would be due in 60 s

        rig.NetworkSignal.Raise(); // Wi-Fi came up
        await Eventually(() => rig.Probe.Calls == 3);
        await Armed(rig);

        rig.Time.Advance(TimeSpan.FromSeconds(14)); // back to 15 s, not 60 s
        await Task.Delay(Settle);
        Assert.Equal(3, rig.Probe.Calls);
        rig.Time.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => rig.Probe.Calls == 4);
    }

    [Fact]
    public async Task ANetworkChange_ThatBringsEpicBack_FlipsOnlineWithoutWaitingForTheTimer()
    {
        using var rig = await StartOfflineAsync();
        NetworkIsBack(rig);

        rig.NetworkSignal.Raise();

        await Eventually(() => State(rig) == Connectivity.Online);
    }

    // ------------------------------------------------------------------ probing: online means silence

    [Fact]
    public async Task WhileOnline_NothingIsProbed_NoMatterHowLong_OrHowOftenTheNetworkChanges()
    {
        using var rig = new Rig();
        rig.Install("v1");
        await rig.StartWarmAsync();
        Assert.Equal(Connectivity.Online, State(rig));

        for (var i = 0; i < 3; i++)
        {
            rig.Time.Advance(TimeSpan.FromHours(1));
            rig.NetworkSignal.Raise();
            await Task.Delay(Settle);
        }

        Assert.Equal(0, rig.Probe.Calls);
        Assert.Equal(0, rig.Service.NetworkForTests.Probes);
    }

    [Fact]
    public async Task OnceBackOnline_ProbingStops()
    {
        using var rig = await StartOfflineAsync();
        NetworkIsBack(rig);
        rig.Time.Advance(TimeSpan.FromSeconds(15));
        await Eventually(() => State(rig) == Connectivity.Online);
        var calls = rig.Probe.Calls;

        rig.Time.Advance(TimeSpan.FromHours(2));
        rig.NetworkSignal.Raise();
        await Task.Delay(Settle);

        Assert.Equal(calls, rig.Probe.Calls);
    }

    // ------------------------------------------------------------------ any successful Epic call

    [Fact]
    public async Task AnOnlineStateSetByARealEpicCall_IsAFlipBack_WithoutAProbe()
    {
        using var rig = await StartOfflineAsync();
        var cameOnline = 0;
        rig.Service.NetworkForTests.CameOnline += () => Interlocked.Increment(ref cameOnline);
        var before = Syncs(rig);

        rig.Legendary.SyncError = null;
        await rig.Service.RefreshLibrary(); // the user pressed Refresh: LibraryService sets Online when the sync works
        await rig.Service.LibraryForTests.SyncTask;

        Assert.Equal(Connectivity.Online, State(rig));
        Assert.Equal(1, cameOnline);
        Assert.Equal(0, rig.Probe.Calls);
        Assert.True(Syncs(rig) > before);
    }

    [Fact]
    public async Task DownloadBytesFlowing_ProveEpicIsReachable()
    {
        using var rig = await StartOfflineAsync();
        var progress = new JobProgress("job-1", "Fortnite", JobPhase.Downloading, 5, 5, 10, null, new NetworkStats(1000, 10_000, 5_000_000, 5_000_000, 5_000_000, null, 4), null, null, null, null, DownloadMode.Balanced, Limiter.Unknown, null);

        rig.Jobs.RaiseProgress(progress);

        Assert.Equal(Connectivity.Online, State(rig));
    }

    [Fact]
    public async Task CameOnline_IsRaisedForOfflineToOnlineOnly()
    {
        using var rig = new Rig();
        rig.Install("v1");
        var cameOnline = 0;
        await rig.Service.StartAsync();
        rig.Service.NetworkForTests.CameOnline += () => Interlocked.Increment(ref cameOnline);
        await rig.Service.WarmupTask;
        await rig.Service.PrewarmForTests.LibrarySync; // Unknown -> Online
        Assert.Equal(0, cameOnline);

        SetState(rig, Connectivity.Offline);
        SetState(rig, Connectivity.Online);
        SetState(rig, Connectivity.Online);
        SetState(rig, Connectivity.Offline);
        SetState(rig, Connectivity.Online);

        Assert.Equal(2, cameOnline);
    }
}
