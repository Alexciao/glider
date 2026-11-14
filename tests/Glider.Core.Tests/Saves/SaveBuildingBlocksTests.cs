using Glider.Core.Saves;
using Glider.Core.Tests.Services;

namespace Glider.Core.Tests.Saves;

public class SaveBuildingBlocksTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static LocalSaveSnapshot Scan(string path, SaveFilters filters, IReadOnlyList<SaveFileEntry>? previous = null) =>
        SaveFolder.Scan(path, filters, previous, ct: TestContext.Current.CancellationToken);

    // ------------------------------------------------------------------ filters (Legendary's _filename_matches)

    [Theory]
    [InlineData("*.sav", null, "slot1.sav", true)]
    [InlineData("*.sav", null, "Slots/slot1.SAV", true)] // fnmatch is case-insensitive on Windows, * crosses folders
    [InlineData("*.sav", null, "debug.log", false)]
    [InlineData("Saved/", null, "Saved/a.dat", true)] // trailing slash = folder prefix
    [InlineData("Saved/", null, "Other/Saved/a.dat", false)]
    [InlineData("a.dat", null, "x/a.dat", true)] // a suffix of the path matches
    [InlineData(null, "*.log,tmp/", "debug.log", false)]
    [InlineData(null, "*.log,tmp/", "tmp/x.sav", false)]
    [InlineData(null, "*.log,tmp/", "slot1.sav", true)]
    [InlineData("*.sav", "bad*", "bad1.sav", false)] // include first, then exclude
    [InlineData("slot?.sav", null, "slot1.sav", true)]
    [InlineData("slot[12].sav", null, "slot3.sav", false)]
    [InlineData("slot[!1].sav", null, "slot3.sav", true)]
    [InlineData("", "", "anything.bin", true)] // empty lists are no filter
    public void FiltersMatchLikeLegendary(string? include, string? exclude, string path, bool expected) =>
        Assert.Equal(expected, SaveFilters.FromMetadata(include, exclude).Matches(path));

    [Fact]
    public void BackslashesInPathsAndPatternsAreSlashes() =>
        Assert.True(SaveFilters.FromMetadata("Saved\\", null).Matches("Saved\\a.dat"));

    // ------------------------------------------------------------------ save path resolution

    private static readonly SavePathVariables Vars = new("D:/Games/Saver", "acc-1", "C:/Users/me/AppData/Local", "C:/Users/me");

    private static string Norm(string? p) => Path.GetFullPath(p!);

    [Theory]
    [InlineData("{AppData}/Saver/Saved/SaveGames", "C:/Users/me/AppData/Local/Saver/Saved/SaveGames")]
    [InlineData("{appdata}\\Saver\\{epicid}", "C:/Users/me/AppData/Local/Saver/acc-1")]
    [InlineData("{UserDir}/My Games/Saver", "C:/Users/me/Documents/My Games/Saver")]
    [InlineData("{UserProfile}/Saved Games/Saver", "C:/Users/me/Saved Games/Saver")]
    [InlineData("{UserSavedGames}/Saver", "C:/Users/me/Saved Games/Saver")]
    [InlineData("{InstallDir}/Saves", "D:/Games/Saver/Saves")]
    [InlineData("{AppData}/../Roaming/Saver", "C:/Users/me/AppData/Roaming/Saver")] // normalised like realpath
    public void ResolvesLegendarysVariables(string template, string expected)
    {
        var r = SavePathResolver.Resolve(template, Vars);
        Assert.True(r.Resolved, r.UnresolvedVariable);
        Assert.Equal(Norm(expected), r.Path);
    }

    [Theory]
    [InlineData("{Unknown}/Saver")]
    [InlineData("%APPDATA%/Saver")]
    [InlineData("")]
    public void UnknownVariablesAreReported_NotGuessed(string template)
    {
        var r = SavePathResolver.Resolve(template, Vars);
        Assert.False(r.Resolved);
        Assert.NotNull(r.UnresolvedVariable);
    }

    [Fact]
    public void AnEmptyVariableValueIsUnresolved() =>
        Assert.False(SavePathResolver.Resolve("{epicid}/x", Vars with { EpicId = "" }).Resolved);

    [Fact]
    public void RefusesFoldersADownloadMustNotClear()
    {
        var install = Path.Combine(Path.GetTempPath(), "Games", "Saver");
        Assert.NotNull(SavePathResolver.Refusal(install, install, hasIncludeFilter: false)); // the install folder itself
        Assert.NotNull(SavePathResolver.Refusal(Path.GetDirectoryName(install)!, install, hasIncludeFilter: false)); // a parent of it
        Assert.Null(SavePathResolver.Refusal(install, install, hasIncludeFilter: true)); // an include list names the save files
        Assert.Null(SavePathResolver.Refusal(Path.Combine(install, "Saved"), install, hasIncludeFilter: false)); // a folder inside
        Assert.NotNull(SavePathResolver.Refusal(Path.GetPathRoot(install)!, null, hasIncludeFilter: true)); // a drive root
    }

    // ------------------------------------------------------------------ list-saves output

    [Fact]
    public void ParsesListSavesOutput_ForTheRequestedGameOnly()
    {
        const string stdout = """
            Save games:
            - Alien: Isolation ("Alien")
             + 2026.11.12-08.00.00.manifest
             + 2026.11.10-12.00.00.manifest
            - Other ("Other")
             + 2026.01.01-00.00.00.manifest
            """;

        var snapshot = LegendarySavesCli.ParseList(stdout, "Alien", out var header);

        Assert.True(header);
        Assert.Equal(["2026.11.10-12.00.00.manifest", "2026.11.12-08.00.00.manifest"], snapshot.Manifests); // oldest first
        Assert.Equal(new DateTimeOffset(2026, 11, 12, 8, 0, 0, TimeSpan.Zero), snapshot.Head);
        Assert.Empty(LegendarySavesCli.ParseList(stdout, "Missing", out _).Manifests);
    }

    [Fact]
    public void NoSavesStillHasTheHeader_ButNoOutputIsNotSuccess()
    {
        Assert.Empty(LegendarySavesCli.ParseList("Save games:\r\n", "A", out var header).Manifests);
        Assert.True(header);
        LegendarySavesCli.ParseList("", "A", out header);
        Assert.False(header);
    }

    // ------------------------------------------------------------------ folder scan

    [Fact]
    public void ScanAppliesFilters_ReusesHashesWhileSizeAndMtimeMatch_AndSkipsMissingFolders()
    {
        using var dir = new TempDir();
        dir.Write("a.sav", "one", T0);
        dir.Write("sub/b.sav", "two", T0);
        dir.Write("debug.log", "noise", T0);
        var filters = SaveFilters.FromMetadata("*.sav", null);

        var first = Scan(dir.Path, filters);
        Assert.Equal(["a.sav", "sub/b.sav"], first.Files.Select(f => f.Path));
        Assert.All(first.Files, f => Assert.NotNull(f.Hash));
        Assert.Equal(T0, first.LatestMtime);

        var fake = first.Files.Select(f => f with { Hash = "reused" }).ToList();
        Assert.All(Scan(dir.Path, filters, fake).Files, f => Assert.Equal("reused", f.Hash)); // unchanged: no re-read

        dir.Write("a.sav", "changed!", T0.AddMinutes(5));
        var again = Scan(dir.Path, filters, fake);
        Assert.NotEqual("reused", again.Files.Single(f => f.Path == "a.sav").Hash);
        Assert.Equal("reused", again.Files.Single(f => f.Path == "sub/b.sav").Hash);

        Assert.False(Scan(Path.Combine(dir.Path, "nope"), SaveFilters.None).HasFiles);
        Assert.Equal(T0.AddMinutes(5), SaveFolder.LatestMtimeUnfiltered(dir.Path, TestContext.Current.CancellationToken)); // filters ignored, like Legendary's own check
    }

    [Fact]
    public void ATouchedFileKeepsItsContentHash_SoTheLedgerSeesNoChange()
    {
        using var dir = new TempDir();
        dir.Write("a.sav", "same", T0);
        var before = Scan(dir.Path, SaveFilters.None);
        dir.Write("a.sav", "same", T0.AddHours(3)); // only the mtime moved
        var after = Scan(dir.Path, SaveFilters.None, before.Files);
        Assert.False(SaveSyncPlanner.LocalChanged(before.Files, after.Files));
    }

    // ------------------------------------------------------------------ backups

    private static SaveBackups Backups(TempDir root, SaveBackupOptions? o = null) => new(Path.Combine(root.Path, "backups"), new FakeLog(), new ManualTime(), o);

    [Fact]
    public void ABackupIsAWholeCopyWithMtimes_AndRestoreBringsTheOriginalBack()
    {
        using var root = new TempDir();
        var saves = Path.Combine(root.Path, "saves");
        var save = new TempDir(saves);
        save.Write("a.sav", "alpha", T0);
        save.Write("sub/b.sav", "beta", T0.AddMinutes(1));
        save.Write("keep.ini", "excluded from the sync", T0);
        var filters = SaveFilters.FromMetadata("*.sav", null);
        var snapshot = Scan(saves, filters);
        var backups = Backups(root);

        var backup = backups.Create("App", saves, snapshot, BackupReasons.Overwritten)!;

        Assert.Equal(1, backups.Count("App"));
        Assert.Equal("alpha", File.ReadAllText(Path.Combine(backup.FilesDirectory, "a.sav")));
        Assert.Equal(T0.AddMinutes(1).UtcDateTime, File.GetLastWriteTimeUtc(Path.Combine(backup.FilesDirectory, "sub", "b.sav")));
        Assert.False(File.Exists(Path.Combine(backup.FilesDirectory, "keep.ini")));
        Assert.Equal(2, backup.Info.FileCount);

        // simulate Legendary's "delete, then half-write"
        File.Delete(Path.Combine(saves, "a.sav"));
        File.WriteAllText(Path.Combine(saves, "intruder.sav"), "half written");
        File.WriteAllText(Path.Combine(saves, "sub", "b.sav"), "garbage");
        backups.Restore(backup, saves, filters);

        Assert.Equal("alpha", File.ReadAllText(Path.Combine(saves, "a.sav")));
        Assert.Equal("beta", File.ReadAllText(Path.Combine(saves, "sub", "b.sav")));
        Assert.False(File.Exists(Path.Combine(saves, "intruder.sav")));
        Assert.True(File.Exists(Path.Combine(saves, "keep.ini"))); // the filters' blind spot is never touched
        Assert.False(SaveSyncPlanner.LocalChanged(snapshot.Files, Scan(saves, filters).Files));
    }

    [Fact]
    public void NothingToBackUp_IsNull_AndTooBigIsRefused()
    {
        using var root = new TempDir();
        var saves = new TempDir(Path.Combine(root.Path, "saves"));
        saves.Write("a.sav", "0123456789", T0);
        var backups = Backups(root, new SaveBackupOptions { MaxBackupBytes = 5 });

        Assert.Null(backups.Create("App", saves.Path, LocalSaveSnapshot.Empty, BackupReasons.Overwritten));
        Assert.Throws<SaveBackupException>(() => backups.Create("App", saves.Path, Scan(saves.Path, SaveFilters.None), BackupReasons.Overwritten));
        Assert.Equal(0, backups.Count("App"));
    }

    [Fact]
    public void AFileThatVanishesWhileCopying_LeavesNoHalfBackup()
    {
        using var root = new TempDir();
        var saves = new TempDir(Path.Combine(root.Path, "saves"));
        saves.Write("a.sav", "one", T0);
        saves.Write("b.sav", "two", T0);
        var snapshot = Scan(saves.Path, SaveFilters.None);
        File.Delete(Path.Combine(saves.Path, "b.sav"));
        var backups = Backups(root);

        Assert.Throws<SaveBackupException>(() => backups.Create("App", saves.Path, snapshot, BackupReasons.Overwritten));

        Assert.Equal(0, backups.Count("App"));
        Assert.Empty(Directory.Exists(backups.DirectoryFor("App")) ? Directory.GetDirectories(backups.DirectoryFor("App")) : []); // no .partial left
    }

    [Fact]
    public void RetentionKeepsTheNewestOverwrittenBackups_AndTheUploadCopiesGoFirst()
    {
        using var root = new TempDir();
        var saves = new TempDir(Path.Combine(root.Path, "saves"));
        saves.Write("a.sav", "x", T0);
        var snapshot = Scan(saves.Path, SaveFilters.None);
        var time = new ManualTime();
        var backups = new SaveBackups(Path.Combine(root.Path, "backups"), new FakeLog(), time, new SaveBackupOptions { KeepOverwritten = 3, KeepUploaded = 2 });

        for (var i = 0; i < 6; i++)
        {
            backups.Create("App", saves.Path, snapshot, BackupReasons.Overwritten);
            backups.Create("App", saves.Path, snapshot, BackupReasons.Uploaded);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        var list = backups.List("App");
        Assert.Equal(3, list.Count(b => b.Info.Reason == BackupReasons.Overwritten));
        Assert.Equal(2, list.Count(b => b.Info.Reason == BackupReasons.Uploaded));
        Assert.Equal(time.GetUtcNow().AddMinutes(-1), list[0].Info.CreatedAt); // newest first
    }

    [Fact]
    public void TheTotalSizeCapDropsUploadCopiesBeforeOverwrittenBackups_NeverTheNewestOverwritten()
    {
        using var root = new TempDir();
        var saves = new TempDir(Path.Combine(root.Path, "saves"));
        saves.Write("a.sav", "0123456789", T0);
        var snapshot = Scan(saves.Path, SaveFilters.None);
        var time = new ManualTime();
        var backups = new SaveBackups(Path.Combine(root.Path, "backups"), new FakeLog(), time, new SaveBackupOptions { MaxTotalBytes = 25 });

        backups.Create("App", saves.Path, snapshot, BackupReasons.Overwritten);
        time.Advance(TimeSpan.FromMinutes(1));
        backups.Create("App", saves.Path, snapshot, BackupReasons.Uploaded);
        time.Advance(TimeSpan.FromMinutes(1));
        backups.Create("App", saves.Path, snapshot, BackupReasons.Overwritten);
        time.Advance(TimeSpan.FromMinutes(1));
        backups.Create("App", saves.Path, snapshot, BackupReasons.Overwritten);

        var list = backups.List("App");
        Assert.DoesNotContain(list, b => b.Info.Reason == BackupReasons.Uploaded);
        Assert.Equal(2, list.Count); // 25 bytes = two 10-byte copies
        Assert.Equal(time.GetUtcNow(), list[0].Info.CreatedAt); // the newest overwritten backup survived
    }

    [Fact]
    public void TwoBackupsInTheSameSecondDoNotCollide()
    {
        using var root = new TempDir();
        var saves = new TempDir(Path.Combine(root.Path, "saves"));
        saves.Write("a.sav", "x", T0);
        var snapshot = Scan(saves.Path, SaveFilters.None);
        var backups = Backups(root);

        var one = backups.Create("App", saves.Path, snapshot, BackupReasons.Overwritten)!;
        var two = backups.Create("App", saves.Path, snapshot, BackupReasons.Overwritten)!;

        Assert.NotEqual(one.Directory, two.Directory);
        Assert.Equal(2, backups.Count("App"));
    }
}

/// <summary>A temp directory with a helper to write files with a chosen mtime.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "glider-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Write(string rel, string text, DateTimeOffset mtime)
    {
        var full = System.IO.Path.Combine(Path, rel);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        File.SetLastWriteTimeUtc(full, mtime.UtcDateTime);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
