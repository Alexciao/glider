using Glider.Core.Contracts;

namespace Glider.Core.Tests.Services;

public class JobHooksTests
{
    private const string Fn = FortniteLayout.AppName;

    [Fact]
    public async Task EglBusy_JobWaitsWithReason_ThenRuns()
    {
        using var rig = new Rig();
        rig.Install("v1");
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never });
        await rig.StartWarmAsync();
        rig.Egl.Busy = true;

        var job = await rig.Service.Verify(new AppRef(Fn));
        var run = rig.Jobs.RunAsync(job.Id, rig.Service.Hooks);

        await WaitFor(() => rig.Jobs.Get(job.Id).Status == JobStatus.Waiting);
        Assert.Equal(rig.Egl.BusyReason, rig.Jobs.Get(job.Id).BlockedReason);
        Assert.False(run.IsCompleted);

        rig.Egl.NotBusy.SetResult();
        var done = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(JobStatus.Completed, done.Status);
        Assert.Null(done.BlockedReason);
        Assert.False(rig.Games.Record()!.NeedsVerification);
    }

    [Fact]
    public async Task AJobOnARunningGame_WaitsForItToClose_AnotherGameIsNotAffected()
    {
        using var rig = new Rig();
        rig.Install("v1");
        rig.AddGame("Other", "Other Game", latest: "2.0", installedVersion: "1.0");
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never });
        await rig.StartWarmAsync();
        rig.Processes.Add(FortniteLayout.ShippingProcessName);

        var other = rig.Jobs.Enqueue(new JobRequest("Other", JobKind.Update, null, null, "1.0", "2.0"));
        Assert.Equal(JobStatus.Completed, (await rig.Jobs.RunAsync(other.Id, rig.Service.Hooks)).Status);

        var job = rig.Jobs.Enqueue(new JobRequest(Fn, JobKind.Update, null, null, "v1", "v2"));
        var run = rig.Jobs.RunAsync(job.Id, rig.Service.Hooks);
        await WaitFor(() => rig.Jobs.Get(job.Id).Status == JobStatus.Waiting);
        Assert.Contains("Fortnite", rig.Jobs.Get(job.Id).BlockedReason, StringComparison.Ordinal);

        rig.Processes.Remove(FortniteLayout.ShippingProcessName);
        for (var i = 0; i < 50 && !run.IsCompleted; i++)
        {
            rig.Time.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(10);
        }

        Assert.Equal(JobStatus.Completed, (await run.WaitAsync(TimeSpan.FromSeconds(5))).Status);
    }

    [Fact]
    public async Task NetworkJobsRequireAuth()
    {
        using var rig = new Rig();
        rig.Legendary.Account = null;
        await rig.StartWarmAsync();
        var job = rig.Jobs.Enqueue(new JobRequest(Fn, JobKind.Install, rig.Root, null, null, "v2"));
        var result = await rig.Jobs.RunAsync(job.Id, rig.Service.Hooks);
        Assert.Equal(ErrorCode.NotSignedIn, result.Error!.Code);
        Assert.Contains(rig.Sink.Of<Notice>(RpcEvents.Notice), n => n.Level == NoticeLevel.Error && n.JobId == job.Id && n.AppName == Fn);
    }

    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    public async Task UpdateSuccess_WritesBackToEgl_OnlyWhenSyncOnAndShared(bool sync, bool shared, int expected)
    {
        using var rig = new Rig();
        rig.Install("v1", sharedWithEgl: shared);
        rig.Settings.Set(Settings.Default with { EglSync = sync, AutoUpdate = AutoUpdatePolicy.Never });
        await rig.StartWarmAsync();

        var job = await rig.Service.Update(new AppRef(Fn));
        rig.Legendary.Installed = rig.Legendary.Installed! with { Version = "v2" };
        await rig.Jobs.RunAsync(job.Id, rig.Service.Hooks);

        Assert.Equal("v2", rig.Games.Record()!.Version);
        Assert.Equal(expected, rig.Egl.WriteBacks.Count);
        if (expected == 1)
        {
            var wb = rig.Egl.WriteBacks[0];
            Assert.Equal("v2", wb.Version);
            Assert.Equal("manifests/v2.manifest", wb.LegendaryManifestPath);
            Assert.Equal("4fe75bbc5a674f4f9b356b5c90567da5", wb.CatalogItemId); // from the catalog
            Assert.Equal("fn", wb.Namespace);
        }

        Assert.False((await rig.SummaryAsync())!.UpdateAvailable);
    }

    [Fact]
    public async Task ToastOnlyWhenWindowNotFocused_AndNotificationsOn()
    {
        using var rig = new Rig();
        rig.Install("v1");
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never });
        await rig.StartWarmAsync();
        rig.Host.Toasts.Clear();

        rig.Host.Focused = true;
        var j1 = rig.Jobs.Enqueue(new JobRequest(Fn, JobKind.Repair, null, null, "v1", "v1"));
        await rig.Jobs.RunAsync(j1.Id, rig.Service.Hooks);
        Assert.Empty(rig.Host.Toasts);

        rig.Host.Focused = false;
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never, Notifications = false });
        var j2 = rig.Jobs.Enqueue(new JobRequest(Fn, JobKind.Repair, null, null, "v1", "v1"));
        await rig.Jobs.RunAsync(j2.Id, rig.Service.Hooks);
        Assert.Empty(rig.Host.Toasts);

        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never });
        var j3 = rig.Jobs.Enqueue(new JobRequest(Fn, JobKind.Repair, null, null, "v1", "v1"));
        await rig.Jobs.RunAsync(j3.Id, rig.Service.Hooks);
        Assert.Single(rig.Host.Toasts);
    }

    internal static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(5);
        }

        Assert.True(condition());
    }
}
