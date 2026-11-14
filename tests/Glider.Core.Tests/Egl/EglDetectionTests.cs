using System.Diagnostics;
using System.Text;
using Glider.Core.Contracts;
using Glider.Core.Egl;

namespace Glider.Core.Tests.Egl;

public sealed class EglDetectionTests
{
    [Fact]
    public async Task DetectsInstallAtDefaultPath()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("C", "Program Files", "Epic Games", "Fortnite"), egstoreGuid: "AAAA0000BBBB1111CCCC2222DDDD3333");
        t.WriteItem(game, guid: "AAAA0000BBBB1111CCCC2222DDDD3333");
        var before = EglTreeHasher.Hash(t.Environment, game);

        using var svc = t.CreateService();
        var state = await svc.ScanAsync(TestContext.Current.CancellationToken);

        Assert.True(state.LauncherInstalled);
        Assert.False(state.LauncherRunning);
        var c = Assert.Single(state.Candidates);
        Assert.Equal(CandidateSource.EpicGamesLauncher, c.Source);
        Assert.Equal(game, c.InstallPath);
        Assert.Equal(EglTestTree.V3310, c.Version);
        Assert.True(c.FolderExists);
        Assert.True(c.ExecutableFound);
        Assert.False(c.IsStale);
        Assert.Equal(98765432109, c.SizeBytes);
        Assert.StartsWith("egl-", c.Id, StringComparison.Ordinal);
        Assert.NotNull(state.LastScan);

        // Stable ids across scans and spellings.
        var again = await svc.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Equal(c.Id, Assert.Single(again.Candidates).Id);
        Assert.Equal(c.Id, EglPath.CandidateId(game.ToUpperInvariant().Replace('\\', '/') + "/"));

        EglTreeHasher.AssertSame(before, EglTreeHasher.Hash(t.Environment, game));
    }

    [Fact]
    public async Task DetectsNonDefaultDriveAndMergesLauncherInstalledDat()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("E", "My Games", "FN Custom"));
        t.WriteItem(game);
        // Same install, spelled differently in LauncherInstalled.dat: case, forward slashes, trailing separator.
        var spelled = game.ToUpperInvariant().Replace('\\', '/') + "/";
        var other = t.CreateGame(t.Drive("F", "Fortnite"));
        t.WriteLauncherInstalled((spelled, EglTestTree.V3310, "Fortnite"), (other, EglTestTree.V3300, "Fortnite"),
            (t.Drive("C", "UE_5.4"), "5.4.4", "UE_5.4"));

        using var svc = t.CreateService();
        var state = await svc.ScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, state.Candidates.Count);
        var fromItem = Assert.Single(state.Candidates, c => EglPath.Same(c.InstallPath, game));
        Assert.Equal(game, fromItem.InstallPath); // .item spelling wins
        Assert.Equal(98765432109, fromItem.SizeBytes);
        var datOnly = Assert.Single(state.Candidates, c => EglPath.Same(c.InstallPath, other));
        Assert.Equal(EglTestTree.V3300, datOnly.Version);
        Assert.Null(datOnly.SizeBytes);
        Assert.True(datOnly.ExecutableFound);
    }

    [Fact]
    public async Task StaleManifestsAreReportedNotHidden()
    {
        using var t = new EglTestTree();
        var gone = t.Drive("D", "Deleted", "Fortnite");
        t.WriteItem(gone);
        var empty = t.CreateGame(t.Drive("D", "Emptied", "Fortnite"), withExe: false);
        t.WriteItem(empty);

        using var svc = t.CreateService();
        var state = await svc.ScanAsync(TestContext.Current.CancellationToken);

        var g = Assert.Single(state.Candidates, c => c.InstallPath == gone);
        Assert.False(g.FolderExists);
        Assert.False(g.ExecutableFound);
        Assert.True(g.IsStale);
        var e = Assert.Single(state.Candidates, c => c.InstallPath == empty);
        Assert.True(e.FolderExists);
        Assert.False(e.ExecutableFound);
        Assert.True(e.IsStale);
    }

    [Fact]
    public async Task MultipleCandidates_EveryGameButNoDlcItems()
    {
        using var t = new EglTestTree();
        var a = t.CreateGame(t.Drive("C", "Epic", "Fortnite"));
        var b = t.CreateGame(t.Drive("G", "Fortnite"));
        t.WriteItem(a);
        t.WriteItem(b, EglTestTree.V3320);
        // Duplicate .item for the same folder (older): deduped.
        var dup = t.WriteItem(a.ToLowerInvariant() + Path.DirectorySeparatorChar, EglTestTree.V3300);
        EglTestTree.Age(dup, TimeSpan.FromDays(30));
        // Another game is listed too (whole library); a DLC whose main game is Fortnite and an item of another main game are not.
        t.WriteItem(t.Drive("C", "Epic", "Other"), edit: j => { j["AppName"] = "Sugar"; j["MainGameAppName"] = "Sugar"; });
        t.WriteItem(t.Drive("C", "Epic", "Fortnite", "dlc"), edit: j => { j["AppName"] = "FortniteDLC"; });
        t.WriteItem(t.Drive("C", "Epic", "Weird"), edit: j => { j["MainGameAppName"] = "SomethingElse"; });

        using var svc = t.CreateService();
        var state = await svc.ScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, state.Candidates.Count);
        Assert.Equal(EglTestTree.V3310, Assert.Single(state.Candidates, c => EglPath.Same(c.InstallPath, a)).Version);
        Assert.Equal(EglTestTree.V3320, Assert.Single(state.Candidates, c => EglPath.Same(c.InstallPath, b)).Version);
        Assert.Equal("Sugar", Assert.Single(state.Candidates, c => c.InstallPath.EndsWith("Other", StringComparison.Ordinal)).AppName);
        Assert.Equal(3, state.Candidates.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public async Task MalformedFilesAreSkippedAndTolerantJsonIsRead()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("H", "Fortnite"));
        var dir = t.Environment.ManifestsDir;
        var escaped = game.Replace("\\", "\\\\");
        // BOM + comments + trailing commas + most keys missing.
        File.WriteAllBytes(Path.Combine(dir, "tolerant.item"), [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(
            $"// written by hand\n{{\n\t\"AppName\": \"Fortnite\", /* inline */\n\t\"InstallLocation\": \"{escaped}\",\n\t\"AppVersionString\": \"{EglTestTree.V3310}\",\n}}\n")]);
        File.WriteAllText(Path.Combine(dir, "garbage.item"), "{ this is not json");
        File.WriteAllBytes(Path.Combine(dir, "empty.item"), []);
        File.WriteAllText(Path.Combine(dir, "array.item"), "[1,2,3]");
        File.WriteAllText(Path.Combine(dir, "noappname.item"), $"{{\"InstallLocation\": \"{escaped}\"}}");
        File.WriteAllText(Path.Combine(dir, "wrongtypes.item"), "{\"AppName\": \"Fortnite\", \"InstallLocation\": 5, \"InstallSize\": \"x\", \"bIsIncompleteInstall\": \"maybe\"}");
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "not an item");
        Directory.CreateDirectory(t.Environment.LauncherInstalledDat[..t.Environment.LauncherInstalledDat.LastIndexOf(Path.DirectorySeparatorChar)]);
        File.WriteAllText(t.Environment.LauncherInstalledDat, "{\"InstallationList\": [ {\"AppName\": ");

        using var svc = t.CreateService();
        var state = await svc.ScanAsync(TestContext.Current.CancellationToken);

        var c = Assert.Single(state.Candidates);
        Assert.Equal(game, c.InstallPath);
        Assert.Equal(EglTestTree.V3310, c.Version);
        Assert.Null(c.SizeBytes);
        Assert.Contains(t.Log.Lines, l => l.Contains("garbage.item", StringComparison.Ordinal));
        Assert.Contains(t.Log.Lines, l => l.Contains("LauncherInstalled.dat", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Utf16ItemIsRead()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("J", "Spiele \u65e5\u672c", "Fortnite"));
        var path = t.WriteItem(game);
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(path));
        File.WriteAllBytes(path, [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(text)]);

        using var svc = t.CreateService();
        var c = Assert.Single((await svc.ScanAsync(TestContext.Current.CancellationToken)).Candidates);
        Assert.Equal(game, c.InstallPath);
        Assert.True(c.ExecutableFound);
    }

    [Fact]
    public async Task NoEglMeansNotInstalledAndNoCandidates()
    {
        using var t = new EglTestTree(eglInstalled: false);
        using var svc = t.CreateService();
        var state = await svc.ScanAsync(TestContext.Current.CancellationToken);
        Assert.False(state.LauncherInstalled);
        Assert.Empty(state.Candidates);
        Assert.False(Directory.Exists(Path.Combine(t.Environment.ProgramData, "Epic")));
    }

    [Fact]
    public async Task ScanReportsTheItemOfEveryApp_WithItsGuidAndDisplayName()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Fortnite"));
        var item = t.WriteItem(game, guid: "33333333333333333333333333333333");
        var alien = t.CreateOtherGame(t.Drive("E", "Epic Games", "AlienIsolation"), "8935bb3e1420443a9789fe01758039a5", "44444444444444444444444444444444");
        t.WriteOtherItem(alien, "8935bb3e1420443a9789fe01758039a5", "1.0", "44444444444444444444444444444444", "Alien: Isolation");
        // A DLC .item shares its base game's folder: never a separate install.
        t.WriteItem(alien, "1.0", "55555555555555555555555555555555", n =>
        {
            n["AppName"] = "6b61b3692dae45be889bf38cb1f9ed84";
            n["MainGameAppName"] = "8935bb3e1420443a9789fe01758039a5";
        });
        using var svc = t.CreateService();

        var state = await svc.ScanAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, state.Installs.Count);
        var fn = state.Installs.Single(i => i.AppName == "Fortnite");
        Assert.Equal("33333333333333333333333333333333", fn.InstallationGuid);
        var a = state.Installs.Single(i => i.AppName == "8935bb3e1420443a9789fe01758039a5");
        Assert.Equal("Alien: Isolation", a.DisplayName);
        Assert.True(a.LooksInstalled);
        Assert.Equal(item, svc.FindByLocation(game + Path.DirectorySeparatorChar, "Fortnite")!.ItemPath);
        Assert.Null(svc.FindByLocation(game, "8935bb3e1420443a9789fe01758039a5"));
        Assert.Equal("8935bb3e1420443a9789fe01758039a5", svc.DetectAppName(alien));
        Assert.False(state.BusyWithGame);
    }

    [Fact]
    public void ScanIsFast()
    {
        using var t = new EglTestTree();
        for (var i = 0; i < 6; i++)
        {
            var g = t.CreateGame(t.Drive("K", "Fortnite" + i));
            t.WriteItem(g);
        }

        t.WriteItem(t.Drive("K", "Other"), edit: j => j["AppName"] = "Other");
        t.WriteLauncherInstalled((t.Drive("K", "Fortnite0"), EglTestTree.V3310, "Fortnite"));
        using var svc = t.CreateService();
        svc.Scan(CancellationToken.None); // warm-up (JIT)

        var times = new List<double>();
        for (var i = 0; i < 11; i++)
        {
            var sw = Stopwatch.StartNew();
            var s = svc.Scan(CancellationToken.None);
            times.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal(6, s.Candidates.Count);
        }

        times.Sort();
        TestContext.Current.SendDiagnosticMessage($"EGL scan median {times[5]:0.00} ms");
        Assert.True(times[5] < 20, $"median scan {times[5]:0.00} ms");
    }

    [Fact]
    public void FortniteLayoutRecognisesFolderWithoutManifest()
    {
        using var t = new EglTestTree();
        var withExe = t.CreateGame(t.Drive("L", "Fortnite"));
        var without = t.CreateGame(t.Drive("L", "NotFortnite"), withExe: false);
        Assert.True(FortniteLayout.LooksLikeInstall(withExe));
        Assert.False(FortniteLayout.LooksLikeInstall(without));
        Assert.False(FortniteLayout.LooksLikeInstall(t.Drive("L", "Missing")));

        using var svc = t.CreateService();
        Assert.Null(svc.FindByLocation(withExe)); // no EGL manifest: the caller's own folder validation handles it
    }

    [Fact]
    public void FindByLocationIgnoresCaseSlashesAndTrailingSeparators()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("M", "Games", "Fortnite"));
        var item = t.WriteItem(game, guid: "0123456789ABCDEF0123456789ABCDEF", edit: j => j["bNeedsValidation"] = true);
        using var svc = t.CreateService();

        var found = svc.FindByLocation(game.ToLowerInvariant().Replace('\\', '/') + "//");
        Assert.NotNull(found);
        Assert.Equal(item, found.ItemPath);
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF", found.InstallationGuid);
        Assert.Equal(EglTestTree.V3310, found.Version);
        Assert.True(found.NeedsValidation);
        Assert.False(found.IsIncomplete);
        Assert.Null(svc.FindByLocation(t.Drive("M", "Games")));
    }

    [Theory]
    [InlineData(@"D:\Games\Fortnite", @"d:/games/fortnite/", true)]
    [InlineData(@"D:\Games\Fortnite\\", @"D:\Games\Fortnite", true)]
    [InlineData(@"\\?\D:\Games\Fortnite", @"D:\Games\Fortnite", true)]
    [InlineData(@"\\server\share\Fortnite", @"//SERVER/share/fortnite", true)]
    [InlineData(@"D:\Games\Fortnite", @"D:\Games\Fortnite2", false)]
    [InlineData(@"D:\Games\Fortnite", @"E:\Games\Fortnite", false)]
    [InlineData("", "", false)]
    public void PathNormalization(string a, string b, bool same) => Assert.Equal(same, EglPath.Same(a, b));

    [Fact]
    public void InspectForAdoptionPredictsLegendaryImport()
    {
        using var t = new EglTestTree();
        var clean = t.CreateGame(t.Drive("N", "Clean"), egstoreGuid: "11111111111111111111111111111111");
        t.WriteItem(clean, guid: "11111111111111111111111111111111");
        var staging = t.CreateGame(t.Drive("N", "Staging"), egstoreGuid: "22222222222222222222222222222222");
        Directory.CreateDirectory(Path.Combine(staging, ".egstore", "bps"));
        var noManifest = t.CreateGame(t.Drive("N", "NoManifest"), egstoreGuid: "33333333333333333333333333333333");
        File.Delete(Path.Combine(noManifest, ".egstore", "33333333333333333333333333333333.manifest"));
        var plain = t.CreateGame(t.Drive("N", "Plain"));
        var before = EglTreeHasher.Hash(t.Environment, clean, staging, noManifest, plain);
        using var svc = t.CreateService();

        var a = svc.InspectForAdoption(clean, "Fortnite");
        Assert.True(a.HasEglMetadata && a.HasEglManifest && !a.StagingPresent);
        Assert.False(a.LegendaryWillRequireVerification);
        Assert.False(a.VerifyRecommended);
        Assert.Equal("11111111111111111111111111111111", a.ManifestGuid);
        Assert.Equal(EglTestTree.V3310, a.EglVersion);

        var b = svc.InspectForAdoption(staging, "Fortnite");
        Assert.True(b.StagingPresent);
        Assert.True(b.LegendaryWillRequireVerification);

        var c = svc.InspectForAdoption(noManifest, "Fortnite");
        Assert.True(c.HasEglMetadata);
        Assert.False(c.HasEglManifest);
        Assert.False(c.LegendaryWillRequireVerification); // Legendary's quirk...
        Assert.True(c.VerifyRecommended);                // ...which Glider compensates for

        var d = svc.InspectForAdoption(plain, "Fortnite");
        Assert.False(d.HasEglMetadata);
        Assert.True(d.LegendaryWillRequireVerification);
        Assert.Null(d.EglVersion);

        EglTreeHasher.AssertSame(before, EglTreeHasher.Hash(t.Environment, clean, staging, noManifest, plain));
    }
}
