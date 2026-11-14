using Glider.Core.Contracts;

namespace Glider.Core.Tests.Services;

/// <summary>Update policy (Always / Ask / Never, per game over a global default), background checks and lazy sizes, all on fakes and manual time.</summary>
public class UpdatePolicyTests
{
    private const string Fn = FortniteLayout.AppName;

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private static int Syncs(Rig rig) => rig.Calls.Count("legendary.checkUpdates") + rig.Calls.Count("legendary.list");

    [Fact]
    public async Task Ask_RaisesOneNoticeWithAnUpdateAction_AndAToast_NeverQueues()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Ask });
        rig.Install("v1");
        await rig.StartWarmAsync();

        var notice = Assert.Single(rig.Sink.Of<Notice>(RpcEvents.Notice), n => n.AppName == Fn);
        Assert.Equal(FixAction.Update, notice.Action);
        Assert.Single(rig.Host.Toasts); // the tray/toast notification (window not focused)
        Assert.Empty(rig.Jobs.Requests);
        Assert.Equal(1, (await rig.Service.GetSnapshot()).Library.UpdatesAvailable);
    }

    [Fact]
    public async Task Never_IsBadgeOnly_NoNoticeNoToastNoJob()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never });
        rig.Install("v1");
        await rig.StartWarmAsync();

        Assert.Empty(rig.Sink.Of<Notice>(RpcEvents.Notice));
        Assert.Empty(rig.Host.Toasts);
        Assert.Empty(rig.Jobs.Requests);
        Assert.True((await rig.SummaryAsync())!.UpdateAvailable);
        Assert.Equal(1, (await rig.Service.GetSnapshot()).Library.UpdatesAvailable); // the badge still counts it
    }

    [Fact]
    public async Task APerGamePolicy_OverridesTheGlobalDefault_BothWays()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never });
        rig.Install("v1");
        rig.AddGame("Keen", "Keen Game", latest: "2.0", installedVersion: "1.0");
        rig.Games.Put("Keen", rig.Games.Get("Keen") with { Settings = GameSettings.Default with { AutoUpdate = AutoUpdatePolicy.Always } });
        await rig.StartWarmAsync();

        Assert.Equal(["Keen"], rig.Jobs.Requests.Select(r => r.AppName)); // Fortnite follows Never, Keen overrides it
        Assert.Equal(AutoUpdatePolicy.Never, rig.Service.UpdatesForTests.PolicyFor(Fn));
        Assert.Equal(AutoUpdatePolicy.Always, rig.Service.UpdatesForTests.PolicyFor("Keen"));
    }

    [Fact]
    public async Task Always_WaitsForIdle_AndThenQueuesWithoutAnyOtherTrigger()
    {
        var idle = new FakeIdle { IdleTime = TimeSpan.FromSeconds(5) };
        using var rig = new Rig(idle);
        rig.Install("v1");
        await rig.StartWarmAsync();
        Assert.Empty(rig.Jobs.Requests);
        Assert.Equal([Fn], rig.Service.UpdatesForTests.Deferred);

        rig.Time.Advance(TimeSpan.FromSeconds(60)); // still active: tried again, still waiting, timer re-armed
        Assert.Empty(rig.Jobs.Requests);

        idle.IdleTime = TimeSpan.FromMinutes(11);
        rig.Time.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(JobKind.Update, Assert.Single(rig.Jobs.Requests).Kind);
        Assert.Empty(rig.Service.UpdatesForTests.Deferred);
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Single(rig.Jobs.Requests); // nothing keeps firing
    }

    [Fact]
    public async Task Always_WaitsForTheGameToClose()
    {
        using var rig = new Rig();
        rig.Install("v1");
        rig.Processes.Add(FortniteLayout.ShippingProcessName);
        await rig.StartWarmAsync();
        Assert.Empty(rig.Jobs.Requests);

        rig.Processes.Remove(FortniteLayout.ShippingProcessName);
        rig.Time.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(JobKind.Update, Assert.Single(rig.Jobs.Requests).Kind);
    }

    [Fact]
    public async Task AVersionTheUserWasToldAbout_IsNotAnnouncedAgainAfterARestart()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Ask });
        rig.Install("v1");
        Directory.CreateDirectory(rig.Paths.Cache);
        await File.WriteAllTextAsync(Path.Combine(rig.Paths.Cache, "update-notices.json"), """{"Fortnite":"v2"}""");

        await rig.StartWarmAsync();

        Assert.Empty(rig.Sink.Of<Notice>(RpcEvents.Notice));
        Assert.Empty(rig.Host.Toasts);

        // A newer build is announced, and remembered on disk.
        rig.Legendary.OnSync = () => rig.Library.AddCatalog(Fn, LibraryFixture.FortniteCatalog, "v3").Save();
        await rig.Service.CheckForUpdates();
        await rig.Service.LibraryForTests.SyncTask;
        rig.Service.UpdatesForTests.Evaluate();
        Assert.Single(rig.Sink.Of<Notice>(RpcEvents.Notice));
        await Eventually(() => File.ReadAllText(Path.Combine(rig.Paths.Cache, "update-notices.json")).Contains("v3", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ background checks

    [Fact]
    public async Task ChecksRunAtTheConfiguredInterval_NotBefore()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never, UpdateCheckIntervalMinutes = 60 });
        rig.Install("v1");
        await rig.StartWarmAsync();
        await Task.Delay(50);
        var before = Syncs(rig);

        rig.Time.Advance(TimeSpan.FromMinutes(59));
        await Task.Delay(100);
        Assert.Equal(before, Syncs(rig));

        rig.Time.Advance(TimeSpan.FromMinutes(2));
        await Eventually(() => Syncs(rig) > before);
    }

    [Fact]
    public async Task ChangingTheInterval_TakesEffectWithoutWaitingForTheOldOne()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never, UpdateCheckIntervalMinutes = 24 * 60 });
        rig.Install("v1");
        await rig.StartWarmAsync();
        await Task.Delay(50);
        rig.Time.Advance(TimeSpan.FromMinutes(40));
        var before = Syncs(rig);

        await rig.Service.UpdateSettings(rig.Settings.Current with { UpdateCheckIntervalMinutes = 30 }); // 40 min elapsed > 30

        await Eventually(() => Syncs(rig) > before);
    }

    [Fact]
    public async Task WhileOffline_NoCheckIsAttempted_UntilTheShorterRetry()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never, UpdateCheckIntervalMinutes = 30 });
        rig.Install("v1");
        await rig.StartWarmAsync();
        await Task.Delay(50);
        rig.Service.Hub.Mutate(s => s with { Connectivity = Connectivity.Offline });
        var before = Syncs(rig);

        rig.Time.Advance(TimeSpan.FromMinutes(31));
        await Task.Delay(150);
        Assert.Equal(before, Syncs(rig));

        rig.Service.Hub.Mutate(s => s with { Connectivity = Connectivity.Online });
        rig.Time.Advance(TimeSpan.FromMinutes(6)); // the offline retry is 5 minutes, not another full interval
        await Eventually(() => Syncs(rig) > before);
    }

    // ------------------------------------------------------------------ lazy sizes

    [Fact]
    public async Task OpeningAGame_MeasuresItsPendingUpdateSizeOnce()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never });
        rig.Install("v1");
        rig.Legendary.UpdateSizes = new LegendarySizes(1_200_000_000, 100_000_000_000);
        await rig.StartWarmAsync();
        await rig.Service.LibraryForTests.SizesTask;
        var measured = rig.Calls.Count($"legendary.updateSizes:{Fn}");

        await rig.Service.GetGameDetail(new AppRef(Fn));
        await rig.Service.LibraryForTests.SizesTask;

        Assert.Equal(1_200_000_000, (await rig.SummaryAsync())!.DownloadSizeBytes);
        Assert.Equal(measured, rig.Calls.Count($"legendary.updateSizes:{Fn}")); // known: not asked again
    }
}
