using System.Text;
using Glider.Core.Contracts;
using Glider.Core.Egl;

namespace Glider.Core.Tests.Egl;

public sealed class EglWriteBackTests
{
    private const string FixtureGame = @"D:\Games\Epic Games\Fortnite";
    private const string FixtureGuid = "6A1B3C5D7E9F40A1B2C3D4E5F6A7B8C9";

    private static string Text(byte[] b) => Encoding.UTF8.GetString(b);

    private static string PyEscaped(string s)
    {
        var sb = new StringBuilder();
        PyJson.WriteString(sb, s);
        return sb.ToString();
    }

    /// <summary>The golden file with the fixture's install path swapped for <paramref name="path"/> (JSON-escaped).</summary>
    private static string GoldenFor(string golden, string fixturePath, string path) =>
        Text(EglTestTree.Fixture(golden)).Replace(PyEscaped(fixturePath)[1..^1], PyEscaped(path)[1..^1], StringComparison.Ordinal);

    // ------------------------------------------------ pure format (golden from Legendary's own code) ------------------------------------------------

    [Theory]
    [InlineData("fortnite-egl.item", "writeback-fortnite.golden.item")]
    [InlineData("edge-cases.item", "writeback-edge.golden.item")]
    public void ExportMatchesLegendaryGolden(string input, string golden)
    {
        var raw = EglJson.TryParseObject(EglTestTree.Fixture(input), out var error) ?? throw new InvalidOperationException(error);
        var m = EglManifest.FromJson(raw);
        var data = input == "edge-cases.item"
            ? new Glider.Core.EglWriteBack("Fortnite", m.InstallLocation, "++Fortnite+Release-33.30-CL-47000000-Windows", 5,
                ["", "Startup"], null, null, null, null, null)
            : Make.WriteBack(FixtureGame, EglTestTree.V3320, null);
        EglService.ApplyExport(m, data, m.InstallationGuid, m.InstallLocation);

        Assert.Equal(Text(EglTestTree.Fixture(golden)), PyJson.Dump(m.ToJson()));
    }

    [Fact]
    public void NewExportMatchesLegendaryGolden()
    {
        var m = EglManifest.Empty();
        var data = Make.WriteBack(@"F:\FN", EglTestTree.V3320, null, tags: ["Startup"]) with { InstallSizeBytes = 42 };
        EglService.ApplyExport(m, data, "0123456789ABCDEF0123456789ABCDEF", @"F:\FN");
        Assert.Equal(Text(EglTestTree.Fixture("writeback-new.golden.item")), PyJson.Dump(m.ToJson()));
    }

    [Theory]
    [InlineData(1.5, "1.5")]
    [InlineData(0.1, "0.1")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(1e-05, "1e-05")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(123456789.125, "123456789.125")]
    [InlineData(-2.0, "-2.0")]
    [InlineData(1.0, "1.0")]
    [InlineData(3e300, "3e+300")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(0.0, "0.0")]
    [InlineData(123.456, "123.456")]
    public void FloatReprMatchesPython(double value, string python) => Assert.Equal(python, PyJson.FloatRepr(value));

    // ------------------------------------------------ WriteBackAsync on a real (temp) tree ------------------------------------------------

    [Fact]
    public async Task GliderNewerWritesBackExactlyLikeLegendaryWithBackups()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Games", "Epic Games", "Fortnite"), egstoreGuid: FixtureGuid);
        var itemPath = Path.Combine(t.Environment.ManifestsDir, FixtureGuid + ".item");
        // The realistic EGL-written fixture, relocated to the temp game folder (EGL formatting kept).
        File.WriteAllBytes(itemPath, EglTestTree.Fixture("fortnite-egl.item").AsSpan().ToArray());
        var original = File.ReadAllText(itemPath).Replace(FixtureGame.Replace("\\", "\\\\"), game.Replace("\\", "\\\\"), StringComparison.Ordinal);
        File.WriteAllText(itemPath, original);
        var originalItem = File.ReadAllBytes(itemPath);
        var datPath = t.WriteLauncherInstalled((t.Drive("C", "UE"), "5.4", "UE_5.4"), (game, EglTestTree.V3310, "Fortnite"));
        var originalDat = File.ReadAllBytes(datPath);
        var egstore = Path.Combine(game, ".egstore");
        var originalEglManifest = File.ReadAllBytes(Path.Combine(egstore, FixtureGuid + ".manifest"));
        var originalMancpn = File.ReadAllBytes(Path.Combine(egstore, FixtureGuid + ".mancpn"));
        var legendaryManifest = t.LegendaryManifest(EglTestTree.V3320);

        using var svc = t.CreateService();
        Assert.Equal(Glider.Core.DriftKind.GliderNewer, svc.CheckDrift(Make.For(game, EglTestTree.V3320, FixtureGuid)).Kind);

        var guid = await svc.WriteBackAsync(Make.WriteBack(game, EglTestTree.V3320, legendaryManifest), TestContext.Current.CancellationToken);

        Assert.Equal(FixtureGuid, guid);
        // .item == golden produced by Legendary's to_json + json.dump, byte for byte.
        Assert.Equal(GoldenFor("writeback-fortnite.golden.item", FixtureGame, game), Text(File.ReadAllBytes(itemPath)));
        // .egstore: manifest is a byte copy of Legendary's, mancpn matches Legendary's json.dump.
        Assert.Equal(File.ReadAllBytes(legendaryManifest), File.ReadAllBytes(Path.Combine(egstore, guid + ".manifest")));
        Assert.Equal(EglTestTree.Fixture("fortnite.golden.mancpn"), File.ReadAllBytes(Path.Combine(egstore, guid + ".mancpn")));
        // LauncherInstalled.dat: only the AppVersion value changed; every other byte (tabs, CRLF, other entries) kept.
        var expectedDat = Text(originalDat).Replace($"\"AppVersion\": \"{EglTestTree.V3310}\"", $"\"AppVersion\": \"{EglTestTree.V3320}\"", StringComparison.Ordinal);
        Assert.Equal(expectedDat, Text(File.ReadAllBytes(datPath)));

        // Backups: one timestamped set with the original bytes of every file that was replaced.
        var backup = Assert.Single(Directory.GetDirectories(t.Paths.EglBackups));
        Assert.Equal(originalItem, File.ReadAllBytes(Path.Combine(backup, "Manifests", FixtureGuid + ".item")));
        Assert.Equal(originalDat, File.ReadAllBytes(Path.Combine(backup, "UnrealEngineLauncher", "LauncherInstalled.dat")));
        Assert.Equal(originalEglManifest, File.ReadAllBytes(Path.Combine(backup, "egstore", FixtureGuid + ".manifest")));
        Assert.Equal(originalMancpn, File.ReadAllBytes(Path.Combine(backup, "egstore", FixtureGuid + ".mancpn")));
        Assert.Contains(itemPath, File.ReadAllText(Path.Combine(backup, "backup-index.txt")), StringComparison.Ordinal);
        Assert.Empty(t.TempLeftovers());

        // Afterwards EGL and Glider agree, and a second write-back is a no-op (no new backup set).
        Assert.Equal(Glider.Core.DriftKind.InSync, svc.CheckDrift(Make.For(game, EglTestTree.V3320, guid)).Kind);
        await svc.WriteBackAsync(Make.WriteBack(game, EglTestTree.V3320, legendaryManifest), TestContext.Current.CancellationToken);
        Assert.Single(Directory.GetDirectories(t.Paths.EglBackups));
    }

    [Fact]
    public async Task WriteBackWithoutExistingItemCreatesGuidNamedItem()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("F", "FN"));
        var manifest = t.LegendaryManifest(EglTestTree.V3320);
        using var svc = t.CreateService();

        var guid = await svc.WriteBackAsync(Make.WriteBack(game, EglTestTree.V3320, manifest, tags: ["Startup"]) with { InstallSizeBytes = 42 },
            TestContext.Current.CancellationToken);

        Assert.Matches("^[0-9A-F]{32}$", guid);
        var item = Path.Combine(t.Environment.ManifestsDir, guid + ".item");
        var expected = GoldenFor("writeback-new.golden.item", @"F:\FN", game)
            .Replace("0123456789ABCDEF0123456789ABCDEF", guid, StringComparison.Ordinal);
        Assert.Equal(expected, Text(File.ReadAllBytes(item)));
        Assert.True(File.Exists(Path.Combine(game, ".egstore", guid + ".manifest")));
        Assert.False(File.Exists(t.Environment.LauncherInstalledDat)); // never created

        // Caller-provided guid is honoured when EGL has no item for the folder.
        var game2 = t.CreateGame(t.Drive("F", "FN2"));
        Assert.Equal("ABCDEFABCDEFABCDEFABCDEFABCDEF12",
            await svc.WriteBackAsync(Make.WriteBack(game2, EglTestTree.V3320, manifest, "ABCDEFABCDEFABCDEFABCDEFABCDEF12"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EglsGuidForTheFolderWinsOverCallerGuid()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Fortnite"));
        var item = t.WriteItem(game, guid: "11112222333344445555666677778888");
        using var svc = t.CreateService();
        var guid = await svc.WriteBackAsync(Make.WriteBack(game, EglTestTree.V3320, t.LegendaryManifest(EglTestTree.V3320), "99999999999999999999999999999999"),
            TestContext.Current.CancellationToken);
        Assert.Equal("11112222333344445555666677778888", guid);
        Assert.Single(Directory.GetFiles(t.Environment.ManifestsDir, "*.item"));
        Assert.Contains(EglTestTree.V3320, File.ReadAllText(item), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureMidWayLeavesNoPartialFileAndRollsBack()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Fortnite"), egstoreGuid: FixtureGuid);
        var item = t.WriteItem(game, guid: FixtureGuid);
        t.WriteLauncherInstalled((game, EglTestTree.V3310, "Fortnite"));
        var before = EglTreeHasher.Hash(t.Environment, game);
        var options = EglTestTree.FastOptions(target =>
        {
            if (target.EndsWith(".item", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("simulated crash while writing the .item");
            }
        });
        using var svc = t.CreateService(options);

        var ex = await Assert.ThrowsAsync<GliderException>(() =>
            svc.WriteBackAsync(Make.WriteBack(game, EglTestTree.V3320, t.LegendaryManifest(EglTestTree.V3320)), TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCode.Unknown, ex.Error.Code);
        Assert.Empty(t.TempLeftovers());
        // .manifest and .mancpn had already been swapped in; they were restored from the backup. Contents are identical
        // to before (timestamps of rolled-back files legitimately change, so compare content only).
        var after = EglTreeHasher.Hash(t.Environment, game);
        Assert.Equal(before.Keys, after.Keys);
        foreach (var (k, v) in before)
        {
            Assert.Equal(v.Split(' ')[0], after[k].Split(' ')[0]);
        }

        Assert.Equal(before[item], after[item]); // the .item itself was never touched
    }

    [Fact]
    public async Task LockedFileIsRetriedThenReportedClearly()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Fortnite"));
        t.WriteItem(game);
        var manifest = t.LegendaryManifest(EglTestTree.V3320);

        var failures = 0;
        var transient = EglTestTree.FastOptions(target =>
        {
            if (target.EndsWith(".item", StringComparison.Ordinal) && Interlocked.Increment(ref failures) <= 2)
            {
                throw new IOException("The process cannot access the file because it is being used by another process.", unchecked((int)0x80070020));
            }
        });
        using (var svc = t.CreateService(transient))
        {
            await svc.WriteBackAsync(Make.WriteBack(game, EglTestTree.V3320, manifest), TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, failures);
        Assert.Contains(EglTestTree.V3320, File.ReadAllText(Directory.GetFiles(t.Environment.ManifestsDir, "*.item").Single()), StringComparison.Ordinal);

        var always = EglTestTree.FastOptions(target =>
        {
            if (target.EndsWith(".item", StringComparison.Ordinal))
            {
                throw new IOException("locked", unchecked((int)0x80070020));
            }
        });
        using (var svc = t.CreateService(always))
        {
            var ex = await Assert.ThrowsAsync<GliderException>(() =>
                svc.WriteBackAsync(Make.WriteBack(game, "++Fortnite+Release-33.30-CL-47000000-Windows", manifest), TestContext.Current.CancellationToken));
            Assert.Equal(ErrorCode.FilesLocked, ex.Error.Code);
            Assert.Contains(".item", ex.Error.Detail, StringComparison.Ordinal);
        }

        Assert.Empty(t.TempLeftovers());
    }

    [Fact]
    public async Task RealLockOnWindowsIsReportedAsFilesLocked()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Mandatory file locks are Windows-only");
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Fortnite"));
        var item = t.WriteItem(game);
        var original = File.ReadAllBytes(item);
        using var svc = t.CreateService();
        using (new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var ex = await Assert.ThrowsAsync<GliderException>(() =>
                svc.WriteBackAsync(Make.WriteBack(game, EglTestTree.V3320, t.LegendaryManifest(EglTestTree.V3320)), TestContext.Current.CancellationToken));
            Assert.True(ex.Error.Code is ErrorCode.FilesLocked or ErrorCode.PermissionDenied, ex.Error.Code.ToString());
        }

        Assert.Equal(original, File.ReadAllBytes(item));
        Assert.Empty(t.TempLeftovers());
    }

    [Fact]
    public async Task RefusesToWriteWithoutLegendaryManifestOrEglOrWhileBusy()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Fortnite"), egstoreGuid: FixtureGuid);
        t.WriteItem(game, guid: FixtureGuid);
        using var svc = t.CreateService();
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<GliderException>(() => svc.WriteBackAsync(Make.WriteBack(game, EglTestTree.V3320, null), ct));
        await Assert.ThrowsAsync<GliderException>(() => svc.WriteBackAsync(Make.WriteBack(game, EglTestTree.V3320, Path.Combine(t.Root, "nope.manifest")), ct));

        Directory.CreateDirectory(Path.Combine(game, ".egstore", "bps", "chunks"));
        File.WriteAllText(Path.Combine(game, ".egstore", "bps", "chunks", "a.chunk"), "x");
        var before = EglTreeHasher.Hash(t.Environment, game);
        t.Processes.EglRunning = true;
        var busy = await Assert.ThrowsAsync<GliderException>(() => svc.WriteBackAsync(Make.WriteBack(game, EglTestTree.V3320, t.LegendaryManifest(EglTestTree.V3320)), ct));
        Assert.Equal(ErrorCode.EglBusy, busy.Error.Code);
        EglTreeHasher.AssertSame(before, EglTreeHasher.Hash(t.Environment, game));
        Assert.Empty(Directory.GetDirectories(t.Paths.EglBackups));

        using var noEgl = new EglTestTree(eglInstalled: false);
        var g2 = noEgl.CreateGame(noEgl.Drive("D", "Fortnite"));
        using var svc2 = noEgl.CreateService();
        await Assert.ThrowsAsync<GliderException>(() => svc2.WriteBackAsync(Make.WriteBack(g2, EglTestTree.V3320, noEgl.LegendaryManifest(EglTestTree.V3320)), ct));
        Assert.False(Directory.Exists(Path.Combine(noEgl.Environment.ProgramData, "Epic")));
    }

    // ------------------------------------------------ move ------------------------------------------------

    [Fact]
    public async Task MoveUpdatesLocationInItemAndLauncherInstalled()
    {
        using var t = new EglTestTree();
        var oldGame = t.CreateGame(t.Drive("D", "Games", "Epic Games", "Fortnite"), egstoreGuid: FixtureGuid);
        var itemPath = Path.Combine(t.Environment.ManifestsDir, FixtureGuid + ".item");
        File.WriteAllText(itemPath, Text(EglTestTree.Fixture("fortnite-egl.item"))
            .Replace(FixtureGame.Replace("\\", "\\\\"), oldGame.Replace("\\", "\\\\"), StringComparison.Ordinal));
        var originalItem = File.ReadAllBytes(itemPath);
        var dat = t.WriteLauncherInstalled((oldGame, EglTestTree.V3310, "Fortnite"));
        var newGame = t.CreateGame(t.Drive("G", "Moved", "Fortnite"), egstoreGuid: FixtureGuid);
        using var svc = t.CreateService();

        await svc.UpdateLocationAsync("Fortnite", FixtureGuid.ToLowerInvariant(), newGame + Path.DirectorySeparatorChar, TestContext.Current.CancellationToken);

        Assert.Equal(GoldenFor("move-fortnite.golden.item", @"G:\Moved\Fortnite", newGame), Text(File.ReadAllBytes(itemPath)));
        Assert.Equal(itemPath, svc.FindByLocation(newGame)!.ItemPath);
        Assert.Null(svc.FindByLocation(oldGame));
        Assert.Contains(LauncherInstalledDat.EncodeUnrealString(newGame), Text(File.ReadAllBytes(dat)), StringComparison.Ordinal);
        var backup = Assert.Single(Directory.GetDirectories(t.Paths.EglBackups));
        Assert.Equal(originalItem, File.ReadAllBytes(Path.Combine(backup, "Manifests", FixtureGuid + ".item")));

        await Assert.ThrowsAsync<GliderException>(() => svc.UpdateLocationAsync("Fortnite", "00000000000000000000000000000000", newGame, TestContext.Current.CancellationToken));
    }

    // ------------------------------------------------ LauncherInstalled.dat patching ------------------------------------------------

    [Fact]
    public void LauncherInstalledPatchKeepsEveryOtherByte()
    {
        var src = EglTestTree.Fixture("LauncherInstalled.dat");
        var patched = LauncherInstalledDat.Patch(src, "Fortnite", @"d:/games/epic games/fortnite/", "E:\\Neu\\Fortnit\u00e9", EglTestTree.V3320)!;
        var expected = Text(src)
            .Replace("\"D:\\\\Games\\\\Epic Games\\\\Fortnite\"", "\"E:\\\\Neu\\\\Fortnit\\u00e9\"", StringComparison.Ordinal)
            .Replace(EglTestTree.V3310, EglTestTree.V3320, StringComparison.Ordinal);
        Assert.Equal(expected, Text(patched));
        var entry = LauncherInstalledDat.Parse(patched).Single(e => e.AppName == "Fortnite");
        Assert.Equal("E:\\Neu\\Fortnit\u00e9", entry.InstallLocation);

        Assert.Null(LauncherInstalledDat.Patch(src, "Fortnite", @"X:\Nope", "a", "b"));
        Assert.Null(LauncherInstalledDat.Patch(src, "Fortnite", @"D:\Games\Epic Games\Fortnite", null, EglTestTree.V3310)); // nothing to change

        // UTF-16LE with BOM (Unreal's AutoDetect encoding for non-ANSI text) stays UTF-16LE with BOM.
        byte[] utf16 = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Text(src))];
        var p16 = LauncherInstalledDat.Patch(utf16, "Fortnite", @"D:\Games\Epic Games\Fortnite", null, EglTestTree.V3320)!;
        Assert.Equal(new byte[] { 0xFF, 0xFE }, p16[..2]);
        Assert.Equal(Text(src).Replace(EglTestTree.V3310, EglTestTree.V3320, StringComparison.Ordinal), Encoding.Unicode.GetString(p16, 2, p16.Length - 2));
    }
}
