using System.Text.Json;
using System.Text.Json.Nodes;
using Glider.Core.Contracts;
using Glider.Core.Egl;
using Glider.Core.Legendary;

namespace Glider.Core.Tests.Egl;

/// <summary>
/// EGL 20+ keeps installs in EOSH's <c>InstalledItems\*.egi</c> and may have no <c>Data\Manifests</c> at all. Fixture
/// <c>eosh-fortnite.egi</c> is the owner's real record (Fortnite at D:\EpicLibrary, Nov 2026) with the signed CDN URLs removed
/// and the obfuscation id redacted. Before v0.2.2 Glider saw nothing on that machine.
/// </summary>
public class EoshTests
{
    private const string Version4310 = "++Fortnite+Release-43.10-CL-60118918-Windows";

    /// <summary>A machine like the owner's: EGL's Data folder exists, no Manifests folder, one .egi pointing at <paramref name="gameDir"/>.</summary>
    private static string WriteEgi(EglTestTree tree, string gameDir, Action<JsonObject>? edit = null, string name = "E1A9AC3E917D46C1F323A3F5B39F465E.egi")
    {
        var node = JsonNode.Parse(EglTestTree.Fixture("eosh-fortnite.egi"))!.AsObject();
        var v = node["v4"]!.AsObject();
        v["dir"] = gameDir;
        v["metaDir"] = Path.Combine(gameDir, ".egstore");
        v["manifestPath"] = Path.Combine(gameDir, ".egstore", "E1A9AC3E917D46C1F323A3F5B39F465E.manifest");
        v["pendingManifestPath"] = Path.Combine(gameDir, ".egstore", "Pending", "E1A9AC3E917D46C1F323A3F5B39F465E.manifest");
        edit?.Invoke(v);
        Directory.CreateDirectory(tree.Environment.EoshInstalledItemsDir);
        var path = Path.Combine(tree.Environment.EoshInstalledItemsDir, name);
        File.WriteAllText(path, node.ToJsonString());
        return path;
    }

    private static EglTestTree OwnersMachine(out string game)
    {
        var tree = new EglTestTree(eglInstalled: false); // no Data\Manifests
        Directory.CreateDirectory(Path.GetDirectoryName(tree.Environment.ManifestsDir)!); // but EGL's Data folder exists
        game = tree.CreateGame(tree.Drive("D", "EpicLibrary", "Fortnite"), egstoreGuid: "E1A9AC3E917D46C1F323A3F5B39F465E");
        return tree;
    }

    [Fact]
    public void TheRealRecordMapsOntoTheItemFields()
    {
        var egi = EglJson.TryParseObject(EglTestTree.Fixture("eosh-fortnite.egi"), out _)!;
        var raw = EoshInstall.ToItemJson(egi, out var reason)!;
        Assert.Null(reason);
        var m = EglManifest.FromJson(raw);
        Assert.Equal("Fortnite", m.AppName);
        Assert.Equal(Version4310, m.AppVersionString);
        Assert.Equal(@"D:\EpicLibrary\Fortnite", m.InstallLocation);
        Assert.Equal("E1A9AC3E917D46C1F323A3F5B39F465E", m.InstallationGuid);
        Assert.Equal("fn", m.CatalogNamespace);
        Assert.Equal("4fe75bbc5a674f4f9b356b5c90567da5", m.CatalogItemId);
        Assert.Equal("FortniteGame/Binaries/Win64/FortniteBootstrapper.exe", m.GetString("LaunchExecutable"));
        Assert.Equal(37170389839, m.InstallSize);
        Assert.False(m.IsIncompleteInstall);
        Assert.Contains("FortniteBR", (raw["InstallTags"] as List<object?>)!.OfType<string>());
        Assert.DoesNotContain("manifestUris", raw.Keys); // signed CDN URLs never travel further
    }

    [Fact]
    public void ScanFindsAnEoshOnlyInstall_WithNoManifestsFolder()
    {
        using var tree = OwnersMachine(out var game);
        WriteEgi(tree, game);

        var scan = tree.CreateService().Scan(CancellationToken.None);

        var found = Assert.Single(scan.Installs);
        Assert.Equal("Fortnite", found.AppName);
        Assert.Equal(Version4310, found.Version);
        Assert.True(EglPath.Same(game, found.InstallPath));
        Assert.Equal(CandidateSource.EpicGamesLauncher, found.Source);
        Assert.True(found.FolderExists);
    }

    [Fact]
    public void FindByLocationCarriesEglsInstallTags()
    {
        using var tree = OwnersMachine(out var game);
        WriteEgi(tree, game);

        var item = tree.CreateService().FindByLocation(game, "Fortnite")!;

        Assert.Equal(Version4310, item.Version);
        Assert.False(item.IsIncomplete);
        Assert.Contains("FortniteBR", item.InstallTags!);
        Assert.DoesNotContain("", item.InstallTags!);
    }

    [Fact]
    public void EoshWinsOverAStaleItemMirrorForTheSameFolder()
    {
        using var tree = OwnersMachine(out var game);
        Directory.CreateDirectory(tree.Environment.ManifestsDir);
        tree.WriteItem(game, EglTestTree.V3310, "E1A9AC3E917D46C1F323A3F5B39F465E");
        WriteEgi(tree, game);

        var found = Assert.Single(tree.CreateService().Scan(CancellationToken.None).Installs);
        Assert.Equal(Version4310, found.Version);
    }

    [Fact]
    public void EosComponentsAndBrokenRecordsAreNotGames()
    {
        using var tree = OwnersMachine(out var game);
        WriteEgi(tree, game, v => v["installationId"] = "overlay", "overlay.egi");
        Directory.CreateDirectory(tree.Environment.EoshInstalledItemsDir);
        File.WriteAllText(Path.Combine(tree.Environment.EoshInstalledItemsDir, "broken.egi"), "{ not json");
        File.WriteAllText(Path.Combine(tree.Environment.EoshInstalledItemsDir, "future.egi"), """{"v9":{"dir":"x"}}""");

        Assert.Empty(tree.CreateService().Scan(CancellationToken.None).Installs);
    }

    [Theory]
    [InlineData("Installing")]
    [InlineData("Updating")]
    public void ARecordThatIsNotInstalledIsBusy(string state)
    {
        using var tree = OwnersMachine(out var game);
        WriteEgi(tree, game, v => v["state"] = state);

        Assert.True(tree.CreateService().FindByLocation(game, "Fortnite")!.IsIncomplete);
    }

    [Fact]
    public async Task WritePathsNeverTouchTheEgi_NorCreateAnItemForAnEoshInstall()
    {
        using var tree = OwnersMachine(out var game);
        Directory.CreateDirectory(tree.Environment.ManifestsDir); // even when the mirror folder exists
        var egi = WriteEgi(tree, game);
        var before = File.ReadAllBytes(egi);
        var svc = tree.CreateService();

        Assert.Empty(svc.GameItems(strict: true));
        await Assert.ThrowsAnyAsync<Exception>(() => svc.UpdateLocationAsync("Fortnite", "E1A9AC3E917D46C1F323A3F5B39F465E", tree.Drive("E", "Fortnite"), CancellationToken.None));

        Assert.Equal(before, File.ReadAllBytes(egi));
        Assert.Empty(Directory.EnumerateFiles(tree.Environment.ManifestsDir));
    }

    [Fact]
    public void QuickScanLooksInEpicLibraryFolders()
    {
        using var tree = new EglTestTree();
        var game = tree.CreateGame(tree.Drive("D", "EpicLibrary", "Fortnite"));

        var found = tree.CreateService().QuickScan([tree.Drive("D")], CancellationToken.None);

        Assert.Contains(found, f => EglPath.Same(f.InstallPath, game) && f.AppName == "Fortnite");
    }
}

public class LegendaryGameConfigTests
{
    [Fact]
    public void InstallTagsGoInTheGamesSection_AndRewritingTheSameValueChangesNothing()
    {
        var existing = "[Legendary]\ndisable_update_notice = true\n";
        var once = LegendaryConfigFile.Apply(existing, [new("install_tags", "FortniteBR,Langde")], "Fortnite")!;
        Assert.Contains("[Fortnite]\ninstall_tags = FortniteBR,Langde", once);
        Assert.StartsWith("[Legendary]", once);
        Assert.Null(LegendaryConfigFile.Apply(once, [new("install_tags", "FortniteBR,Langde")], "Fortnite"));

        var changed = LegendaryConfigFile.Apply(once, [new("install_tags", "FortniteBR")], "Fortnite")!;
        Assert.Single(changed.Split('\n'), l => l.StartsWith("install_tags", StringComparison.Ordinal));
    }

    [Fact]
    public void TheValueDropsEmptyAndDuplicateTags() =>
        Assert.Equal("Startup,FortniteBR", LegendaryConfigFile.InstallTagsValue(["Startup", "", "FortniteBR", "Startup", " ", "Bad,Tag"]));

    [Fact]
    public async Task EnsureWritesQueuedGameValuesWithTheManagedKeys()
    {
        var dir = Path.Combine(Path.GetTempPath(), "glider-cfg-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Assert.True(await LegendaryConfigFile.EnsureAsync(dir, new Dictionary<string, IReadOnlyList<KeyValuePair<string, string>>>
            {
                ["Fortnite"] = [new("install_tags", "FortniteBR")],
            }, CancellationToken.None));
            var text = await File.ReadAllTextAsync(LegendaryConfigFile.PathIn(dir), TestContext.Current.CancellationToken);
            Assert.Contains("egl_sync = false", text);
            Assert.Contains("[Fortnite]\ninstall_tags = FortniteBR", text);
            Assert.False(await LegendaryConfigFile.EnsureAsync(dir, new Dictionary<string, IReadOnlyList<KeyValuePair<string, string>>>
            {
                ["Fortnite"] = [new("install_tags", "FortniteBR")],
            }, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
