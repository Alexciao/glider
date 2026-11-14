using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Glider.Core.Art;
using Glider.Core.Contracts;
using Glider.Core.Library;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Library;

/// <summary>The pure catalog mapping (docs/v0.2-design.md §2, L§2.3) over Legendary's files built from real catalog JSON.</summary>
public sealed class GameCatalogTests : IDisposable
{
    private const string Fn = FortniteLayout.AppName;
    private const string Alien = LibraryFixture.AlienApp;
    private const string AlienDlc = LibraryFixture.AlienDlcApp;
    private const string Castle = LibraryFixture.CastlevaniaApp;

    private readonly TempDir _dir = new();

    private LibraryFixture Fixture() => new(_dir.Path);

    private async Task<(ImmutableDictionary<string, GameSummary> Map, LegendaryLibrary Lib)> Build(LibraryFixture f, CatalogContext? ctx = null)
    {
        f.Save();
        var lib = await new LegendaryLibraryReader(_dir.Path, retryDelay: TimeSpan.FromMilliseconds(1)).ReadAsync(null, TestContext.Current.CancellationToken);
        return (GameCatalog.Build(lib, ctx ?? CatalogContext.Empty), lib);
    }

    [Fact]
    public async Task RealCatalogItems_MapToSummaries()
    {
        var f = Fixture()
            .AddCatalog(Fn, LibraryFixture.FortniteCatalog, "++Fortnite+Release-43.10-CL-1-Windows")
            .AddCatalog(Alien, LibraryFixture.AlienCatalog, "1.0")
            .AddCatalog(Castle, LibraryFixture.CastlevaniaCatalog, "2.0");
        var (map, lib) = await Build(f);

        var fn = map[Fn];
        Assert.Equal(GameKind.Game, fn.Kind);
        Assert.Equal("Fortnite", fn.Title);
        Assert.Equal("fn", fn.Namespace);
        Assert.Equal("4fe75bbc5a674f4f9b356b5c90567da5", fn.CatalogItemId);
        Assert.Equal("Epic Games", fn.Developer);
        Assert.Equal(InstallStatus.NotInstalled, fn.InstallStatus);
        Assert.Equal("++Fortnite+Release-43.10-CL-1-Windows", fn.LatestVersion);
        Assert.True(fn.CanRunOffline); // no CanRunOffline attribute: Legendary defaults to true
        Assert.False(fn.CloudSaves);
        Assert.Equal(SaveSyncStatus.NotSupported, fn.SaveStatus);
        Assert.Equal(ThirdPartyStore.None, fn.ThirdParty);
        Assert.Equal(new DateTimeOffset(2014, 9, 12, 15, 20, 6, 103, TimeSpan.Zero), fn.AddedAt); // creation date without an entitlement
        // Art is the host's lazy cache (art.glider); the hash names the Epic source: DieselGameBoxTall for the tile, DieselGameBox for the hero.
        var fnItem = lib.Items[Fn];
        Assert.Equal(ArtUrls.Build(Fn, ArtKind.Tall, GameCatalog.ArtSourceOf(fnItem, ArtKind.Tall)!.Url), fn.Art.Tall);
        Assert.Equal(fn.Art.Tall!.Replace("/tall?", "/tall-thumb?", StringComparison.Ordinal), fn.Art.TallThumb);
        Assert.Contains("1200x1600", GameCatalog.ArtSourceOf(fnItem, ArtKind.Tall)!.Url, StringComparison.Ordinal);
        Assert.Contains("2560x1440", GameCatalog.ArtSourceOf(fnItem, ArtKind.Wide)!.Url, StringComparison.Ordinal);
        Assert.StartsWith("#", fn.Art.Placeholder, StringComparison.Ordinal);

        var alien = map[Alien];
        Assert.Equal("Alien: Isolation", alien.Title);
        Assert.True(alien.CloudSaves); // CloudSaveFolder
        Assert.Equal(SaveSyncStatus.Unknown, alien.SaveStatus);
        Assert.True(alien.CanRunOffline);

        Assert.True(map[Castle].CloudSaves);
        Assert.Equal("KONAMI", map[Castle].Developer);
    }

    [Fact]
    public async Task Dlc_LaunchableAddons_AndEntitlementOnlyDlc()
    {
        var addon = LibraryFixture.Item("Fortnite Battle Royale", "fn", "d75daf91c89b49ecb110b69a7bd68996", ["addons/launchable", "addons", "applications"]);
        addon["mainGameItem"] = new JsonObject { ["id"] = "4fe75bbc5a674f4f9b356b5c90567da5", ["releaseInfo"] = new JsonArray(new JsonObject { ["appId"] = Fn }) };
        var f = Fixture()
            .AddCatalog(Fn, LibraryFixture.FortniteCatalog)
            .Add("afdb5a85efcc45d8ae8e406e2121d81c", addon)
            .AddCatalog(Alien, LibraryFixture.AlienCatalog)
            .AddCatalog(AlienDlc, LibraryFixture.AlienDlcCatalog)
            // Owned through an entitlement only (no asset, no metadata file): listed under Alien, not installable.
            .Entitle("df37f065c3f14eadbf011177396e2966", "d53b1719658e4ff1952963528b3e0966", "2021-03-04T05:06:07.000Z");
        var (map, _) = await Build(f);

        var br = map["afdb5a85efcc45d8ae8e406e2121d81c"];
        Assert.Equal(GameKind.LaunchableAddon, br.Kind);
        Assert.Equal(Fn, br.BaseAppName);

        var dlc = map[AlienDlc];
        Assert.Equal(GameKind.Dlc, dlc.Kind);
        Assert.Equal(Alien, dlc.BaseAppName); // mainGameItem.releaseInfo[0].appId
        Assert.Equal("Alien: Isolation - Last Survivor", dlc.Title);

        var crew = map["d3186f73183947389308ad7017fc3eae"]; // "Crew Expendable" from Alien's dlcItemList
        Assert.Equal(GameKind.Dlc, crew.Kind);
        Assert.Equal(InstallStatus.NotInstallable, crew.InstallStatus);
        Assert.Equal(Alien, crew.BaseAppName);

        Assert.Equal(2, map[Alien].DlcCount);
        Assert.Equal(1, map[Fn].DlcCount);
        Assert.Equal(new DateTimeOffset(2021, 3, 4, 5, 6, 7, TimeSpan.Zero), map[Alien].AddedAt); // entitlement grant date (namespace)
    }

    [Fact]
    public async Task HiddenContentDlc_Mods_AndAppsWithoutAWindowsAsset_AreExcluded()
    {
        var hidden = LibraryFixture.Item("Fortnite Save the World Content", "fn", "0b41f0192f7f4f2691684581aedc0778", ["hidden"]);
        hidden["mainGameItem"] = new JsonObject { ["id"] = "4fe75bbc5a674f4f9b356b5c90567da5" };
        var f = Fixture()
            .AddCatalog(Fn, LibraryFixture.FortniteCatalog)
            .Add("aa31f9e94e844b299ca757d1d0b97a09", hidden)
            .Add("mod1", LibraryFixture.Item("Some Mod", categories: ["mods"]))
            .Add("maconly", LibraryFixture.Item("Mac Only"), withAsset: false);
        var (map, _) = await Build(f);

        Assert.Equal([Fn], map.Keys);
        Assert.Equal(0, map[Fn].DlcCount);
    }

    [Fact]
    public async Task ThirdPartyGames_AreVisibleButNotInstallable_UbisoftLinkedOnesInstall()
    {
        var f = Fixture()
            .Add("ea", LibraryFixture.Item("EA Game", attributes: new() { ["ThirdPartyManagedApp"] = "Origin" }), withAsset: false)
            .Add("ubi", LibraryFixture.Item("Ubi Game", attributes: new() { ["partnerLinkType"] = "ubisoft" }))
            .Add("other", LibraryFixture.Item("Other Store", attributes: new() { ["ThirdPartyManagedProvider"] = "Battle.net" }), withAsset: false)
            .Add("offline", LibraryFixture.Item("Online Only", attributes: new() { ["CanRunOffline"] = "false" }));
        var (map, _) = await Build(f);

        Assert.Equal((ThirdPartyStore.Ea, "EA app", InstallStatus.NotInstallable), (map["ea"].ThirdParty, map["ea"].ThirdPartyName, map["ea"].InstallStatus));
        Assert.Equal((ThirdPartyStore.Ubisoft, "Ubisoft Connect", InstallStatus.NotInstalled), (map["ubi"].ThirdParty, map["ubi"].ThirdPartyName, map["ubi"].InstallStatus));
        Assert.Equal((ThirdPartyStore.Other, "Battle.net"), (map["other"].ThirdParty, map["other"].ThirdPartyName));
        Assert.False(map["offline"].CanRunOffline);
    }

    [Fact]
    public async Task ArtFallsBackToTheOtherShape_ThenToAPlaceholder()
    {
        var wideOnly = LibraryFixture.Item("Wide Only");
        wideOnly["keyImages"] = new JsonArray(new JsonObject { ["type"] = "DieselGameBox", ["url"] = "https://cdn1.epicgames.com/w.jpg", ["width"] = 1920, ["height"] = 1080 });
        var tallOnly = LibraryFixture.Item("Tall Only");
        tallOnly["keyImages"] = new JsonArray(new JsonObject { ["type"] = "DieselGameBoxTall", ["url"] = "https://cdn1.epicgames.com/t.jpg" });
        var f = Fixture().Add("w", wideOnly).Add("t", tallOnly).Add("none", LibraryFixture.Item("No Art"));
        var (map, _) = await Build(f);

        // Every URL is the lazy art cache's; the hash in it names the Epic picture the variant is cut from.
        Assert.Equal(ArtUrls.Build("w", ArtKind.Tall, "https://cdn1.epicgames.com/w.jpg"), map["w"].Art.Tall);
        Assert.Equal(ArtUrls.Build("w", ArtKind.TallThumb, "https://cdn1.epicgames.com/w.jpg"), map["w"].Art.TallThumb);
        Assert.Equal(ArtUrls.Build("w", ArtKind.Wide, "https://cdn1.epicgames.com/w.jpg"), map["w"].Art.Wide);
        Assert.Equal(ArtUrls.Build("t", ArtKind.Wide, "https://cdn1.epicgames.com/t.jpg"), map["t"].Art.Wide);
        Assert.Null(map["w"].Art.Logo);
        Assert.Null(map["none"].Art.Tall);
        Assert.Null(map["none"].Art.TallThumb);
        Assert.Null(map["none"].Art.Wide);
        Assert.Matches("^#[0-9a-f]{6}$", map["none"].Art.Placeholder!);
        Assert.Equal(GameCatalog.Placeholder("none"), map["none"].Art.Placeholder); // stable per app
    }

    [Fact]
    public async Task ALearnedCoverColour_ReplacesTheGeneratedPlaceholder_OnlyWhileTheArtIsUnchanged()
    {
        var item = LibraryFixture.Item("Coloured");
        item["keyImages"] = new JsonArray(new JsonObject { ["type"] = "DieselGameBoxTall", ["url"] = "https://cdn1.epicgames.com/c.jpg" });
        var f = Fixture().Add("c", item);
        var learned = new Dictionary<string, ArtPlaceholder> { ["c"] = new(ArtUrls.HashOf("https://cdn1.epicgames.com/c.jpg"), "#445566") };
        var stale = new Dictionary<string, ArtPlaceholder> { ["c"] = new(ArtUrls.HashOf("https://cdn1.epicgames.com/old.jpg"), "#445566") };

        var (withColour, _) = await Build(f, CatalogContext.Empty with { Placeholders = learned });
        var (staleColour, _) = await Build(f, CatalogContext.Empty with { Placeholders = stale });

        Assert.Equal("#445566", withColour["c"].Art.Placeholder);
        Assert.Equal(GameCatalog.Placeholder("c"), staleColour["c"].Art.Placeholder); // the cover changed: back to the generated colour
    }

    [Fact]
    public async Task InstalledState_UpdatesAndGliderRecords()
    {
        var f = Fixture()
            .AddCatalog(Fn, LibraryFixture.FortniteCatalog, "v2")
            .AddCatalog(Alien, LibraryFixture.AlienCatalog, "1.0")
            .Install(Fn, @"D:\Games\Fortnite", "v1", needsVerification: true)
            .Install(Alien, @"D:\Games\AlienIsolation", "1.0")
            .Install("orphan", @"D:\Games\Orphan", "3", title: "Orphan Game"); // installed, metadata pruned
        var entry = GameEntry.Empty with { Favorite = true, PlaytimeSeconds = 42, Install = new InstallRecord(Fn, @"D:\Games\Fortnite", "v1", 1, DateTimeOffset.UnixEpoch, true, "GUID", false) };
        var ctx = CatalogContext.Empty with
        {
            Glider = ImmutableDictionary<string, GameEntry>.Empty.Add(Fn, entry),
            Runtime = ImmutableDictionary<string, GameRuntime>.Empty.Add(Fn, new GameRuntime(RunState.Running, "job-1", JobKind.Update, 123, 456)),
        };
        var (map, lib) = await Build(f, ctx);

        var fn = map[Fn];
        Assert.Equal(InstallStatus.Installed, fn.InstallStatus);
        Assert.Equal("v1", fn.InstalledVersion);
        Assert.True(fn.UpdateAvailable); // assets.json build != installed.json version
        Assert.Equal(456, fn.DownloadSizeBytes);
        Assert.Equal(123, fn.InstalledSizeBytes);
        Assert.True(fn.SharedWithEgl);
        Assert.True(fn.NeedsVerification);
        Assert.True(fn.Favorite);
        Assert.Equal(42, fn.PlaytimeSeconds);
        Assert.Equal(RunState.Running, fn.RunState);
        Assert.Equal(("job-1", JobKind.Update), (fn.ActiveJobId, fn.ActiveJobKind));
        Assert.False(map[Alien].UpdateAvailable);
        Assert.Equal("Orphan Game", map["orphan"].Title);

        // installed.json unreadable: Glider's own record keeps Fortnite installed (never "not installed").
        var unknown = GameCatalog.Build(lib with { Installed = null }, ctx);
        Assert.Equal(InstallStatus.Installed, unknown[Fn].InstallStatus);
        Assert.Equal(InstallStatus.NotInstalled, unknown[Alien].InstallStatus);

        // A broken install shows as such.
        var broken = GameCatalog.Build(lib, ctx with { Runtime = ctx.Runtime.SetItem(Fn, GameRuntime.Idle with { Broken = true }) });
        Assert.Equal(InstallStatus.Broken, broken[Fn].InstallStatus);
    }

    [Fact]
    public void DiffReportsOnlyChangedAndRemovedGames()
    {
        var art = new GameArt(null, null, null, null, "#000000");
        GameSummary G(string app, string title) => new(app, title, GameKind.Game, null, "", "", null, InstallStatus.NotInstalled, null, null, false, null,
            null, null, null, false, SaveSyncStatus.NotSupported, true, ThirdPartyStore.None, null, 0, false, false, null, null, 0, art, RunState.Idle, null, null, false, false);
        var from = new Dictionary<string, GameSummary> { ["a"] = G("a", "A"), ["b"] = G("b", "B"), ["c"] = G("c", "C") };
        var to = new Dictionary<string, GameSummary> { ["a"] = G("a", "A"), ["b"] = G("b", "B2"), ["d"] = G("d", "D") };
        var (upserts, removes) = GameCatalog.Diff(from, to);
        Assert.Equal(["b", "d"], upserts.Select(u => u.AppName));
        Assert.Equal(["c"], removes);
    }

    [Fact]
    public async Task TornFilesAreRetried_AndAFileThatStaysTornKeepsThePreviousValue()
    {
        var f = Fixture().AddCatalog(Fn, LibraryFixture.FortniteCatalog, "v2").Install(Fn, @"D:\G", "v1");
        f.Save();
        var reader = new LegendaryLibraryReader(_dir.Path, retryDelay: TimeSpan.FromMilliseconds(1));
        var first = await reader.ReadAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal("v1", first.Installed![Fn].Version);

        File.WriteAllText(Path.Combine(_dir.Path, "installed.json"), "{\"Fortnite\": {");
        File.WriteAllText(Path.Combine(_dir.Path, "metadata", Fn + ".json"), "{ torn");
        var second = await reader.ReadAsync(first, TestContext.Current.CancellationToken);
        Assert.Equal("v1", second.Installed![Fn].Version);
        Assert.True(second.Items.ContainsKey(Fn));

        File.Delete(Path.Combine(_dir.Path, "assets.json"));
        var third = await reader.ReadAsync(second, TestContext.Current.CancellationToken);
        Assert.Null(third.Assets); // missing = unknown, not "no games"
    }

    public void Dispose() => _dir.Dispose();
}
