using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Library;

namespace Glider.Core.Tests.Services;

public class StartupTests
{
    private const string Fn = FortniteLayout.AppName;

    [Fact]
    public async Task StartPublishesSnapshotImmediately_ThenPrewarmsInOrder()
    {
        using var rig = new Rig();
        rig.Install("v1", sharedWithEgl: true);
        rig.Legendary.VersionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await rig.Service.StartAsync();

        // First paint: published synchronously from cached state while Legendary is still blocked.
        var first = Assert.Single(rig.Sink.Of<AppSnapshot>(RpcEvents.State));
        Assert.False(first.App.Warm);
        Assert.Equal(AuthStatus.Unknown, first.Auth.Status);

        var snap = await rig.Service.GetSnapshot().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(snap.App.Warm);
        Assert.False(rig.Calls.Has("legendary.offline"));

        rig.Legendary.VersionGate.SetResult();
        await rig.Service.WarmupTask.WaitAsync(TimeSpan.FromSeconds(5));
        await rig.Service.PrewarmForTests.LibrarySync;

        var order = rig.Calls.All.Where(c => c is "legendary.version" or "legendary.offline" or "egl.scan" or "egl.drift:Fortnite"
            or "jobs.restore" or "legendary.checkUpdates" or "legendary.list").Distinct().ToList();
        Assert.Equal(["legendary.version", "legendary.offline", "egl.scan", "egl.drift:Fortnite", "jobs.restore", "legendary.checkUpdates", "legendary.list"], order);
        Assert.DoesNotContain(rig.Calls.All, c => c.StartsWith("legendary.info", StringComparison.Ordinal)); // no per-game `info` at startup

        snap = await rig.Service.GetSnapshot();
        Assert.True(snap.App.Warm);
        Assert.Equal("0.21.1", snap.App.LegendaryVersion);
        Assert.Equal(AuthStatus.SignedIn, snap.Auth.Status);
        Assert.Equal(Connectivity.Online, snap.Connectivity);
        Assert.Null(snap.Auth.Account!.AvatarUrl);
        Assert.NotNull(snap.Library.LastSyncedAt);
        var g = (await rig.SummaryAsync())!;
        Assert.Equal("v2", g.LatestVersion);
        Assert.True(g.UpdateAvailable);
        Assert.NotNull(g.Art.Tall);
    }

    [Fact]
    public async Task NetworkFailureDuringWarmUpIsNonFatal_AndMarksOffline()
    {
        using var rig = new Rig();
        rig.Legendary.SyncError = Errors.Network("offline");

        await rig.StartWarmAsync();

        var snap = await rig.Service.GetSnapshot();
        Assert.True(snap.App.Warm);
        Assert.Equal(AuthStatus.SignedIn, snap.Auth.Status);
        Assert.Equal(Connectivity.Offline, snap.Connectivity);
        Assert.Equal(ErrorCode.Network, snap.Library.SyncError!.Code);
        Assert.True(rig.Calls.Has("jobs.restore"));
        Assert.Equal(1, snap.Library.GameCount); // the cached files still give the library
    }

    [Fact]
    public async Task ExpiredSessionIsReported()
    {
        using var rig = new Rig();
        rig.Legendary.SyncError = Errors.AuthExpired();
        await rig.StartWarmAsync();
        Assert.Equal(AuthStatus.Expired, (await rig.Service.GetSnapshot()).Auth.Status);
    }

    [Fact]
    public async Task EglNewer_ReimportsSilently_NeverDownloads()
    {
        using var rig = new Rig();
        var rec = rig.Install("v1", sharedWithEgl: true);
        rig.Egl.Drift = new DriftResult(DriftKind.EglNewer, "v2", "v1", rig.Egl.Item);
        rig.Legendary.ImportVersion = "v2";

        await rig.StartWarmAsync();

        // The re-import is a writer-lane Import job (the real queue runs `legendary import` for it); the fake queue runs hooks only.
        var request = Assert.Single(rig.Jobs.Requests, r => r.Kind == JobKind.Import);
        Assert.Equal(rec.InstallPath, request.TargetPath);
        Assert.DoesNotContain(rig.Jobs.Requests, r => r.Kind is JobKind.Install or JobKind.Update or JobKind.Repair or JobKind.Verify);
        await rig.Legendary.ImportAsync(request.AppName, request.TargetPath!, CancellationToken.None);
        await rig.Jobs.RunAsync(rig.Jobs.Snapshot().Single(j => j.Kind == JobKind.Import).Id, rig.Service.Hooks);
        Assert.Equal("v2", rig.Games.Record()!.Version);
        Assert.Equal("guid-1", rig.Games.Record()!.EglInstallationGuid);
        Assert.Equal(rec.InstallPath, rig.Games.Record()!.InstallPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GliderNewer_WritesBackOnlyWhenSyncOn(bool sync)
    {
        using var rig = new Rig();
        rig.Install("v2", sharedWithEgl: true);
        rig.Settings.Set(Settings.Default with { EglSync = sync, AutoUpdate = AutoUpdatePolicy.Never });
        rig.Egl.Drift = new DriftResult(DriftKind.GliderNewer, "v1", "v2", rig.Egl.Item);

        await rig.StartWarmAsync();

        Assert.Equal(sync ? 1 : 0, rig.Egl.WriteBacks.Count);
        if (sync)
        {
            Assert.Equal("v2", rig.Egl.WriteBacks[0].Version);
            Assert.Equal("guid-1", rig.Egl.WriteBacks[0].ExistingInstallationGuid);
            Assert.Equal(Fn, rig.Egl.WriteBacks[0].AppName);
            Assert.Equal("Fortnite", rig.Egl.WriteBacks[0].Title);
        }

        Assert.DoesNotContain(rig.Calls.All, c => c.StartsWith("legendary.import", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unreconcilable_QueuesOnlyAQuickVerify_AfterRestore()
    {
        using var rig = new Rig();
        rig.Install("v2", sharedWithEgl: true);
        rig.Egl.Drift = new DriftResult(DriftKind.Unreconcilable, "weird", "v2", rig.Egl.Item);

        await rig.StartWarmAsync();

        var req = Assert.Single(rig.Jobs.Requests);
        Assert.Equal(JobKind.Verify, req.Kind);
        Assert.Equal(Fn, req.AppName);
        Assert.DoesNotContain(rig.Calls.All, c => c.StartsWith("legendary.import", StringComparison.Ordinal));
        Assert.Empty(rig.Egl.WriteBacks);
        var all = rig.Calls.All.ToList();
        Assert.True(all.IndexOf("jobs.restore") < all.IndexOf("jobs.enqueue:Verify:Fortnite"));
    }

    [Fact]
    public async Task EglMissing_UnlinksTheInstall()
    {
        using var rig = new Rig();
        rig.Install("v2", sharedWithEgl: true);
        rig.Egl.Drift = new DriftResult(DriftKind.EglMissing, null, "v2", null);
        await rig.StartWarmAsync();
        Assert.Null(rig.Games.Record()!.EglInstallationGuid);
        Assert.Empty(rig.Jobs.Requests);
    }

    [Fact]
    public async Task UpdateAvailable_NotifiesAndAutoUpdatesWhenGameClosed()
    {
        using var rig = new Rig();
        rig.Install("v1");
        await rig.StartWarmAsync();

        var req = Assert.Single(rig.Jobs.Requests);
        Assert.Equal((JobKind.Update, Fn), (req.Kind, req.AppName));
        Assert.Contains(rig.Sink.Of<Notice>(RpcEvents.Notice), n => n.Title.Contains("update", StringComparison.OrdinalIgnoreCase) && n.AppName == Fn);
        Assert.Single(rig.Host.Toasts); // window not focused, notifications on
    }

    [Fact]
    public async Task AutoUpdate_WaitsWhileGameRuns()
    {
        using var rig = new Rig();
        rig.Install("v1");
        rig.Processes.Add(FortniteLayout.ShippingProcessName);
        await rig.StartWarmAsync();

        Assert.Empty(rig.Jobs.Requests);
        Assert.Equal(RunState.Running, (await rig.SummaryAsync())!.RunState);
    }

    [Fact]
    public async Task AutoUpdate_OnlyWhenIdle_RespectsIdleSignal()
    {
        var idle = new FakeIdle { IdleTime = TimeSpan.FromSeconds(5) };
        using var rig = new Rig(idle);
        rig.Install("v1");
        await rig.StartWarmAsync();
        Assert.Empty(rig.Jobs.Requests);

        rig.Settings.Set(Settings.Default with { AutoUpdateOnlyWhenIdle = false });
        rig.Service.UpdatesForTests.Evaluate();
        Assert.Equal(JobKind.Update, Assert.Single(rig.Jobs.Requests).Kind);
    }

    [Fact]
    public async Task PerGamePolicy_AskOnlyNotifies_NeverStaysQuiet()
    {
        using var rig = new Rig();
        rig.Install("v1");
        rig.AddGame("Quiet", "Quiet Game", latest: "2.0", installedVersion: "1.0");
        rig.Games.Put(Fn, rig.Games.Get(Fn) with { Settings = GameSettings.Default with { AutoUpdate = AutoUpdatePolicy.Ask } });
        rig.Games.Put("Quiet", rig.Games.Get("Quiet") with { Settings = GameSettings.Default with { AutoUpdate = AutoUpdatePolicy.Never } });
        await rig.StartWarmAsync();

        Assert.Empty(rig.Jobs.Requests);
        var notices = rig.Sink.Of<Notice>(RpcEvents.Notice);
        Assert.Contains(notices, n => n.AppName == Fn);
        Assert.DoesNotContain(notices, n => n.AppName == "Quiet");
    }

    [Fact]
    public async Task UpdateNoticeIsShownOncePerVersion()
    {
        using var rig = new Rig();
        rig.Install("v1");
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Ask });
        await rig.StartWarmAsync();
        await rig.Service.CheckForUpdates();
        await rig.Service.LibraryForTests.SyncTask;
        rig.Service.UpdatesForTests.Evaluate();
        Assert.Single(rig.Sink.Of<Notice>(RpcEvents.Notice));
    }

    [Fact]
    public async Task ColdStart_ServesTheLibraryFromCache_WithoutTouchingLegendary()
    {
        using var rig = new Rig();
        var art = new GameArt("https://cdn/t.jpg", "https://cdn/t.jpg", "https://cdn/w.jpg", null, "#123456");
        var cached = new GameSummary("Cached", "Cached Game", GameKind.Game, null, "ns", "id", null, InstallStatus.Installed, "1", "1", false,
            @"C:\Games\Cached", 1, null, null, false, SaveSyncStatus.NotSupported, true, ThirdPartyStore.None, null, 0, true, false, null, null, 0,
            art, RunState.Running, "stale-job", JobKind.Update, false, false);
        Directory.CreateDirectory(rig.Paths.Cache);
        File.WriteAllBytes(LibraryService.PathFor(rig.Paths),
            JsonSerializer.SerializeToUtf8Bytes(new LibraryCacheFile { Games = [cached] }, Glider.Core.Infrastructure.EngineJsonContext.Default.LibraryCacheFile));
        rig.Legendary.VersionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); // pre-warm blocked

        await rig.Service.StartAsync();
        var lib = rig.Service.LibraryForTests.Games; // straight from the cache, no flush, no Legendary file read yet
        var g = Assert.Single(lib.Values);
        Assert.Equal("Cached Game", g.Title);
        Assert.Equal(RunState.Idle, g.RunState); // runtime facts don't survive a restart
        Assert.Null(g.ActiveJobId);
        Assert.False(rig.Service.LibraryForTests.FilesRead);
        Assert.All(rig.Calls.All.Where(c => c.StartsWith("legendary.", StringComparison.Ordinal)), c => Assert.Equal("legendary.version", c));

        rig.Legendary.VersionGate.SetResult();
        await rig.Service.WarmupTask;
        await rig.Service.LibraryForTests.FlushAsync();
        Assert.Contains(Fn, rig.Service.LibraryForTests.Games.Keys); // then the files replace the cache with a delta
        Assert.DoesNotContain("Cached", rig.Service.LibraryForTests.Games.Keys);
        Assert.Contains(rig.Sink.Of<LibraryDelta>(RpcEvents.Library), d => d.Removes.Contains("Cached") && d.Upserts.Any(u => u.AppName == Fn));
        Assert.True(File.Exists(LibraryService.PathFor(rig.Paths)));
    }
}
