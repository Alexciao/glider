using Glider.Core.Contracts;

namespace Glider.Core.Tests.Services;

public class StartupTests
{
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
        Assert.Equal(InstallStatus.Installed, first.Game.InstallStatus);
        Assert.Equal(AuthStatus.Unknown, first.Auth.Status);

        var snap = await rig.Service.GetSnapshot().WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(snap.App.Warm);
        Assert.False(rig.Calls.Has("legendary.offline"));

        rig.Legendary.VersionGate.SetResult();
        await rig.Service.WarmupTask.WaitAsync(TimeSpan.FromSeconds(5));

        var order = rig.Calls.All.Where(c => c is "legendary.version" or "legendary.offline" or "egl.scan" or "egl.drift"
            or "legendary.refresh" or "legendary.info" or "jobs.restore").ToList();
        Assert.Equal(["legendary.version", "legendary.offline", "egl.scan", "egl.drift", "legendary.refresh", "legendary.info", "jobs.restore"], order);

        snap = await rig.Service.GetSnapshot();
        Assert.True(snap.App.Warm);
        Assert.Equal("0.21.1", snap.App.LegendaryVersion);
        Assert.Equal(AuthStatus.SignedIn, snap.Auth.Status);
        Assert.Null(snap.Auth.Account!.AvatarUrl);
        Assert.Equal("v2", snap.Game.LatestVersion);
        Assert.True(snap.Game.UpdateAvailable);
        Assert.NotNull(snap.Game.Art);
    }

    [Fact]
    public async Task NetworkFailureDuringRefreshIsNonFatal()
    {
        using var rig = new Rig();
        rig.Legendary.RefreshError = new HttpRequestException("offline");
        rig.Legendary.InfoError = new GliderException(Errors.Network());

        await rig.StartWarmAsync();

        var snap = await rig.Service.GetSnapshot();
        Assert.True(snap.App.Warm);
        Assert.Equal(AuthStatus.SignedIn, snap.Auth.Status);
        Assert.True(rig.Calls.Has("jobs.restore"));
    }

    [Fact]
    public async Task ExpiredSessionIsReported()
    {
        using var rig = new Rig();
        rig.Legendary.RefreshError = new GliderException(Errors.AuthExpired());
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

        Assert.True(rig.Calls.Has("legendary.import"));
        Assert.DoesNotContain(rig.Jobs.Requests, r => r.Kind is JobKind.Install or JobKind.Update or JobKind.Repair or JobKind.Verify);
        Assert.Equal("v2", rig.Records.Current!.Version);
        Assert.Equal("guid-1", rig.Records.Current.EglInstallationGuid);
        Assert.Equal(rec.InstallPath, rig.Records.Current.InstallPath);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GliderNewer_WritesBackOnlyWhenSyncOn(bool sync)
    {
        using var rig = new Rig();
        rig.Install("v2", sharedWithEgl: true);
        rig.Settings.Set(Settings.Default with { EglSync = sync, AutoUpdate = false });
        rig.Egl.Drift = new DriftResult(DriftKind.GliderNewer, "v1", "v2", rig.Egl.Item);

        await rig.StartWarmAsync();

        Assert.Equal(sync ? 1 : 0, rig.Egl.WriteBacks.Count);
        if (sync)
        {
            Assert.Equal("v2", rig.Egl.WriteBacks[0].Version);
            Assert.Equal("guid-1", rig.Egl.WriteBacks[0].ExistingInstallationGuid);
        }

        Assert.False(rig.Calls.Has("legendary.import"));
    }

    [Fact]
    public async Task Unreconcilable_QueuesOnlyAQuickVerify_AfterRestore()
    {
        using var rig = new Rig();
        rig.Install("v2", sharedWithEgl: true);
        rig.Legendary.Info = FakeLegendary.MakeInfo("v2");
        rig.Egl.Drift = new DriftResult(DriftKind.Unreconcilable, "weird", "v2", rig.Egl.Item);

        await rig.StartWarmAsync();

        var req = Assert.Single(rig.Jobs.Requests);
        Assert.Equal(JobKind.Verify, req.Kind);
        Assert.False(rig.Calls.Has("legendary.import"));
        Assert.Empty(rig.Egl.WriteBacks);
        var all = rig.Calls.All.ToList();
        Assert.True(all.IndexOf("jobs.restore") < all.IndexOf("jobs.enqueue:Verify"));
    }

    [Fact]
    public async Task EglMissing_UnlinksTheInstall()
    {
        using var rig = new Rig();
        rig.Install("v2", sharedWithEgl: true);
        rig.Egl.Drift = new DriftResult(DriftKind.EglMissing, null, "v2", null);
        await rig.StartWarmAsync();
        Assert.Null(rig.Records.Current!.EglInstallationGuid);
        Assert.Empty(rig.Jobs.Requests);
    }

    [Fact]
    public async Task UpdateAvailable_NotifiesAndAutoUpdatesWhenGameClosed()
    {
        using var rig = new Rig();
        rig.Install("v1");
        await rig.StartWarmAsync();

        Assert.Equal(JobKind.Update, Assert.Single(rig.Jobs.Requests).Kind);
        Assert.Contains(rig.Sink.Of<Notice>(RpcEvents.Notice), n => n.Title.Contains("update", StringComparison.OrdinalIgnoreCase));
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
        Assert.Equal(RunState.Running, (await rig.Service.GetSnapshot()).Game.RunState);
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
        await rig.Service.CheckForUpdates();
        Assert.Equal(JobKind.Update, Assert.Single(rig.Jobs.Requests).Kind);
    }

    [Fact]
    public async Task UpdateNoticeIsShownOncePerVersion()
    {
        using var rig = new Rig();
        rig.Install("v1");
        rig.Settings.Set(Settings.Default with { AutoUpdate = false });
        await rig.StartWarmAsync();
        rig.Time.Advance(TimeSpan.FromMinutes(11));
        await rig.Service.CheckForUpdates();
        Assert.Single(rig.Sink.Of<Notice>(RpcEvents.Notice));
    }
}
