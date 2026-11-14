using Glider.Core.Egl;
using static Glider.Core.Tests.ManifestBuilder;

namespace Glider.Core.Tests.Egl;

public class EpicManifestReaderTests
{
    private static readonly ManifestFile[] Files =
    [
        F("Game.exe", 100),
        F("Content/Paks/base.pak", 5_000, "Base"),
        F("Content/Paks/voice-it.pak", 700, "Langit", "Voices"),
        F("Content/ünï/çödé.txt", 3),
    ];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsVersionFilesSizesAndTags(bool compressed)
    {
        var m = EpicManifestReader.TryRead(Build("++Game+Release-1.2-CL-9-Windows", Files, compressed, launchExe: "Game.exe"))!;
        Assert.Equal("++Game+Release-1.2-CL-9-Windows", m.BuildVersion);
        Assert.Equal("Game.exe", m.LaunchExecutable);
        Assert.Equal(Files.Select(f => f.Name), m.Files.Select(f => f.Name));
        Assert.Equal(Files.Select(f => f.Size), m.Files.Select(f => f.Size));
        Assert.Equal(["Base", "Langit", "Voices"], m.AllTags);
        Assert.True(m.HasTags);
        Assert.Equal(["Langit", "Voices"], m.Files[2].Tags);
    }

    [Fact]
    public void ReadsABuildWithoutTags()
    {
        var m = EpicManifestReader.TryRead(Build("v1", [F("a.bin"), F("b.bin")]))!;
        Assert.False(m.HasTags);
        Assert.Empty(m.AllTags);
    }

    [Fact]
    public void NotUnderstoodInputIsNullNeverAnException()
    {
        var good = Build("v1", Files);
        Assert.Null(EpicManifestReader.TryRead([]));
        Assert.Null(EpicManifestReader.TryRead("{\"ManifestFileVersion\":\"21\"}"u8.ToArray())); // JSON manifest
        Assert.Null(EpicManifestReader.TryRead(Build("v1", Files, encrypted: true)));
        Assert.Null(EpicManifestReader.TryRead(good[..(good.Length / 2)])); // truncated
        var garbled = (byte[])good.Clone();
        for (var i = 50; i < garbled.Length; i += 7)
        {
            garbled[i] ^= 0xFF;
        }

        Assert.Null(EpicManifestReader.TryRead(garbled)); // must not throw
        Assert.Null(EpicManifestReader.TryReadFile(Path.Combine(Path.GetTempPath(), "glider-no-such-manifest.manifest")));
    }

    [Fact]
    public void ReadsAFileWhileAnotherProcessHasItOpen()
    {
        using var tree = new EglTestTree();
        var path = Path.Combine(tree.Root, "m.manifest");
        File.WriteAllBytes(path, Build("v7", Files));
        using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.Equal("v7", EpicManifestReader.TryReadFile(path)!.BuildVersion);
    }
}

public class InstallTagPlannerTests : IDisposable
{
    private readonly EglTestTree _tree = new();
    private readonly string _game;

    public InstallTagPlannerTests() => _game = Path.Combine(_tree.Root, "Game");

    public void Dispose()
    {
        _tree.Dispose();
        GC.SuppressFinalize(this);
    }

    private static EpicManifestSummary Manifest(params ManifestFile[] files) => EpicManifestReader.TryRead(Build("v1", files))!;

    [Fact]
    public void NoTagsInTheBuild_NothingToCarry() =>
        Assert.Null(InstallTagPlanner.Plan(Manifest(F("a"), F("b")), _game, ["x"], TestContext.Current.CancellationToken));

    /// <summary>
    /// The shape of the 35 GB case (Fortnite 43.00 on D:): the folder has the required packs but not the optional ones. Without the
    /// carried tags verify counts every optional file as missing (247 there); with the derived tags nothing is missing.
    /// </summary>
    [Fact]
    public void OptionalPacksNeverInstalled_AreNotWanted_SoNothingIsMissing()
    {
        var files = new List<ManifestFile> { F("Game.exe") };
        files.AddRange(Enumerable.Range(0, 5).Select(i => F($"req/{i}.pak", 10, "Required")));
        files.AddRange(Enumerable.Range(0, 8).Select(i => F($"opt/{i}.pak", 10, "OnDemand")));
        files.Add(F("DefaultTags/TagPlaceholder_Required.txt", 1, "Required"));
        files.Add(F("DefaultTags/TagPlaceholder_OnDemandOptional.txt", 1, "OnDemandOptional"));
        ManifestBuilder.Touch(_game, "Game.exe", "DefaultTags/TagPlaceholder_Required.txt");
        ManifestBuilder.Touch(_game, [.. Enumerable.Range(0, 5).Select(i => $"req/{i}.pak")]);

        var plan = InstallTagPlanner.Plan(Manifest([.. files]), _game, null, TestContext.Current.CancellationToken)!;

        Assert.Equal(TagSource.OnDisk, plan.Source);
        Assert.Equal(["", "Required"], plan.Tags);
        Assert.Equal(0, plan.FilesWantedMissing);
        Assert.Equal(0, plan.FilesOutsideTagsOnDisk);
        Assert.Equal(3, plan.TagsInBuild);
        Assert.Equal(7, plan.FilesWanted); // exe + 5 paks + the Required placeholder
    }

    [Fact]
    public void ASharedFileDoesNotInstallThePackItIsSharedWith()
    {
        var files = new[] { F("a.pak", 1, "A"), F("shared.pak", 1, "A", "B"), F("b.pak", 1, "B") };
        ManifestBuilder.Touch(_game, "a.pak", "shared.pak");
        var plan = InstallTagPlanner.Plan(Manifest(files), _game, null, TestContext.Current.CancellationToken)!;
        Assert.Equal(["", "A"], plan.Tags); // shared.pak is explained by A; B has nothing of its own on disk
    }

    [Fact]
    public void ASharedFileNothingElseExplains_KeepsAllItsTags()
    {
        // Wrongly dropping a tag lets Legendary delete files; wrongly keeping one only costs a download.
        var files = new[] { F("shared.pak", 1, "A", "B"), F("a.pak", 1, "A"), F("b.pak", 1, "B") };
        ManifestBuilder.Touch(_game, "shared.pak");
        var plan = InstallTagPlanner.Plan(Manifest(files), _game, null, TestContext.Current.CancellationToken)!;
        Assert.Equal(["", "A", "B"], plan.Tags);
    }

    [Fact]
    public void EglsTagsStaySelected_AndTheDiskNeverLosesAVote()
    {
        // EGL says Voices; the disk also holds Base files EGL treats as implicit. Base must not be deselected (Legendary would delete it).
        var files = new[] { F("base.pak", 1, "Base"), F("voices.pak", 1, "Voices"), F("hd.pak", 1, "Hd") };
        ManifestBuilder.Touch(_game, "base.pak");
        var plan = InstallTagPlanner.Plan(Manifest(files), _game, ["Voices"], TestContext.Current.CancellationToken)!;
        Assert.Equal(TagSource.EglItem, plan.Source);
        Assert.Equal(["", "Base", "Voices"], plan.Tags);
        Assert.Equal(0, plan.FilesOutsideTagsOnDisk);
        Assert.Equal(1, plan.FilesWantedMissing); // voices.pak: EGL installed the pack, a repair completes it
    }

    [Fact]
    public void AnEmptyFolderHasNoTagsOfItsOwn()
    {
        var plan = InstallTagPlanner.Plan(Manifest(F("a", 1, "A"), F("b", 1)), _game, null, TestContext.Current.CancellationToken)!;
        Assert.Equal([""], plan.Tags);
    }
}

public class AdoptionInspectionTests : IDisposable
{
    private readonly EglTestTree _tree = new();

    public void Dispose()
    {
        _tree.Dispose();
        GC.SuppressFinalize(this);
    }

    private static readonly ManifestFile[] One = [F("Game.exe", 1)];

    /// <summary>Fortnite on this machine: .egstore with the old manifest, a Pending manifest of the new build, bps — and no .mancpn.</summary>
    [Fact]
    public void MidUpdateFolderWithoutMancpn_IsRecognised()
    {
        var game = _tree.Drive("D", "EpicLibrary", "Fortnite");
        _tree.CreateGame(game);
        var egstore = Path.Combine(game, ".egstore");
        Directory.CreateDirectory(Path.Combine(egstore, "Pending"));
        Directory.CreateDirectory(Path.Combine(egstore, "bps"));
        const string guid = "E1A9AC3E917D46C1F323A3F5B39F465E";
        File.WriteAllBytes(Path.Combine(egstore, guid + ".manifest"), Build(EglTestTree.V3300, One));
        File.WriteAllBytes(Path.Combine(egstore, "Pending", guid + ".manifest"), Build(EglTestTree.V3310, One));
        _tree.WriteItem(game, EglTestTree.V3300, guid);
        using var svc = _tree.CreateService();

        var info = svc.InspectForAdoption(game, "Fortnite");

        Assert.False(info.HasEglMetadata);
        Assert.True(info.LegendaryWillRecordLatest);
        Assert.True(info.StagingPresent);
        Assert.Equal(EglTestTree.V3300, info.EglManifestVersion);
        Assert.Equal(EglTestTree.V3310, info.PendingVersion);
        Assert.Equal(EglTestTree.V3300, info.EglVersion);
    }

    [Fact]
    public void CompleteInstallWithMancpnAndManifest_NeedsNothing()
    {
        var game = _tree.CreateGame(_tree.Drive("E", "Epic Games", "Fortnite"), "AAAA");
        File.WriteAllBytes(Path.Combine(game, ".egstore", "AAAA.manifest"), Build(EglTestTree.V3310, One));
        _tree.WriteItem(game, EglTestTree.V3310, "AAAA");
        using var svc = _tree.CreateService();

        var info = svc.InspectForAdoption(game, "Fortnite");

        Assert.True(info.HasEglMetadata);
        Assert.True(info.HasEglManifest);
        Assert.False(info.LegendaryWillRecordLatest);
        Assert.False(info.StagingPresent);
        Assert.Null(info.PendingVersion);
        Assert.Equal(EglTestTree.V3310, info.EglManifestVersion);
    }

    [Fact]
    public void PendingManifestsFolderListsUnfinishedUpdates()
    {
        var game = _tree.CreateGame(_tree.Drive("E", "Fortnite"), "BBBB");
        _tree.WriteItem(game, EglTestTree.V3320, "BBBB", dir: Path.Combine(_tree.Environment.ManifestsDir, "Pending"));
        using var svc = _tree.CreateService();

        var pending = Assert.Single(svc.PendingUpdates());
        Assert.Equal("Fortnite", pending.AppName);
        Assert.Equal(EglTestTree.V3320, pending.Version);
    }
}

public class GenericQuickScanTests : IDisposable
{
    private readonly EglTestTree _tree = new();

    public void Dispose()
    {
        _tree.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void FindsGamesUnderAnyEpicGamesFolderAndAnyEgstoreFolder_OnEveryDrive()
    {
        var a = _tree.CreateOtherGame(_tree.Drive("D", "Stuff", "Epic Games", "Hades"), "app-hades", "G1");
        var b = _tree.CreateOtherGame(_tree.Drive("F", "Epic Games", "Celeste"), "app-celeste", "G2");
        var c = _tree.CreateOtherGame(_tree.Drive("F", "Whatever", "Control"), "app-control", "G3"); // only an .egstore marks it
        _tree.CreateOtherGame(_tree.Drive("F", "Windows", "Hidden"), "app-hidden", "G4"); // system folders are never entered
        Directory.CreateDirectory(_tree.Drive("D", "Games", "NotAGame"));
        using var svc = _tree.CreateService();

        var found = svc.QuickScan([_tree.Drive("D"), _tree.Drive("F")], TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { ("app-celeste", b), ("app-control", c), ("app-hades", a) }.OrderBy(x => x.Item1),
            found.Select(f => (f.AppName, f.InstallPath)).OrderBy(x => x.AppName));
        Assert.All(found, f => Assert.True(f.LooksInstalled));
    }

    [Fact]
    public void AFolderWithoutAnyEglMetadataIsNamedByTheCallerOrSkipped()
    {
        var folder = _tree.Drive("D", "Epic Games", "Hollow Knight");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "hk.exe"), "x");
        using var svc = _tree.CreateService();

        Assert.Empty(svc.QuickScan([_tree.Drive("D")], TestContext.Current.CancellationToken));

        var found = Assert.Single(svc.QuickScan([_tree.Drive("D")], f => Path.GetFileName(f) == "Hollow Knight" ? "app-hk" : null, TestContext.Current.CancellationToken));
        Assert.Equal("app-hk", found.AppName);
    }

    [Fact]
    public void ADriveThatIsGoneIsSkippedQuietly()
    {
        using var svc = _tree.CreateService();
        Assert.Empty(svc.QuickScan([Path.Combine(_tree.Root, "drive-Q")], TestContext.Current.CancellationToken));
    }
}
