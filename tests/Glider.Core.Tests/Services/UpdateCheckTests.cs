using Glider.Core.Contracts;

namespace Glider.Core.Tests.Services;

/// <summary>Update = assets.json build vs installed.json version (L§10), both read directly; a check refreshes assets.json first.</summary>
public class UpdateCheckTests
{
    private const string Fn = FortniteLayout.AppName;

    [Fact]
    public async Task InstalledVersionChangedOutsideGlider_IsWhatTheLibraryShows()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never });
        rig.Install("v1"); // our record says v1
        rig.Legendary.Installed = FakeLegendary.MakeInstalled(rig.Games.Record()!.InstallPath, "v2"); // EGL/Legendary already moved to v2
        await rig.StartWarmAsync();

        var g = (await rig.SummaryAsync())!;
        Assert.False(g.UpdateAvailable);
        Assert.Equal("v2", g.InstalledVersion);
        Assert.Empty(rig.Jobs.Requests);
    }

    [Fact]
    public async Task NewBuildPublishedAfterAnEarlierCheck_IsSeenOnTheNextCheck()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never });
        rig.Install("v2");
        await rig.StartWarmAsync();
        Assert.False((await rig.SummaryAsync())!.UpdateAvailable);

        // `list-installed --check-updates` rewrites assets.json with the new build.
        rig.Legendary.OnSync = () => rig.Library.AddCatalog(Fn, LibraryFixture.FortniteCatalog, "v3").Save();
        await rig.Service.CheckForUpdates();
        await rig.Service.LibraryForTests.SyncTask;

        var g = (await rig.SummaryAsync())!;
        Assert.True(g.UpdateAvailable);
        Assert.Equal("v2", g.InstalledVersion);
        Assert.Equal("v3", g.LatestVersion);
        Assert.True(rig.Calls.Has("legendary.checkUpdates"));
        Assert.Equal(1, (await rig.Service.GetSnapshot()).Library.UpdatesAvailable);
    }

    [Fact]
    public async Task ATornInstalledJson_KeepsThePreviousLibrary_NeverNotInstalled()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never });
        rig.Install("v1");
        await rig.StartWarmAsync();
        Assert.Equal(InstallStatus.Installed, (await rig.SummaryAsync())!.InstallStatus);

        File.WriteAllText(Path.Combine(rig.Paths.LegendaryConfig, "installed.json"), "{\"Fortnite\": {\"app_na"); // Legendary mid-write
        var g = (await rig.SummaryAsync())!;
        Assert.Equal(InstallStatus.Installed, g.InstallStatus);
        Assert.Equal("v1", g.InstalledVersion);
    }
}
