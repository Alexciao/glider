using Glider.Core.Contracts;

namespace Glider.Core.Tests.Services;

/// <summary>Installed version (Legendary's installed.json) is compared with a freshly fetched latest version, every check.</summary>
public class UpdateCheckTests
{
    [Fact]
    public async Task InstalledVersionChangedOutsideGlider_IsReReadBeforeComparing()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = false });
        rig.Install("v1"); // our record says v1
        rig.Legendary.Installed = FakeLegendary.MakeInstalled(rig.Records.Current!.InstallPath, "v2"); // EGL/Legendary already moved to v2
        rig.Legendary.Info = FakeLegendary.MakeInfo("v2");
        await rig.StartWarmAsync();

        var result = await rig.Service.CheckForUpdates();

        Assert.False(result.UpdateAvailable);
        Assert.Equal("v2", result.InstalledVersion);
        Assert.Equal("v2", rig.Records.Current!.Version);
        Assert.Empty(rig.Jobs.Requests);
    }

    [Fact]
    public async Task NewBuildPublishedAfterAnEarlierCheck_IsSeenOnTheNextCheck()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = false });
        rig.Install("v2");
        rig.Legendary.Info = FakeLegendary.MakeInfo("v2");
        await rig.StartWarmAsync();
        Assert.False((await rig.Service.GetSnapshot()).Game.UpdateAvailable);

        rig.Legendary.Info = FakeLegendary.MakeInfo("v3"); // no clock advance: the 10-minute info cache must not hide it
        var result = await rig.Service.CheckForUpdates();

        Assert.True(result.UpdateAvailable);
        Assert.Equal("v2", result.InstalledVersion);
        Assert.Equal("v3", result.LatestVersion);
    }

    [Fact]
    public async Task Reconcile_IsSkippedWhileAJobIsRunning()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = false });
        rig.Install("v1");
        rig.Legendary.Info = FakeLegendary.MakeInfo("v2");
        await rig.StartWarmAsync();
        var job = rig.Jobs.Enqueue(new JobRequest(JobKind.Update, null, null, "v1", "v2"));
        rig.Jobs.Set(job with { Status = JobStatus.Running });
        rig.Legendary.Installed = FakeLegendary.MakeInstalled(rig.Records.Current!.InstallPath, "v-half-written");

        await rig.Service.CheckForUpdates();

        Assert.Equal("v1", rig.Records.Current!.Version);
    }
}
