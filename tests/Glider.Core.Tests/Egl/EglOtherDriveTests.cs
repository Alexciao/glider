using Glider.Core.Contracts;
using Glider.Core.Egl;
using Glider.Core.Game;

namespace Glider.Core.Tests.Egl;

/// <summary>
/// Installs on drives other than C: with unusual names (fake drives are folders under the test root, see
/// <see cref="EglTestTree.Drive"/>): detection from .item / LauncherInstalled.dat, the quick scan, locating by folder,
/// and write-back on such a folder.
/// </summary>
public sealed class EglOtherDriveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ItemOnAnotherDriveWithSpacesAndForwardSlashesIsUsable()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "My Games", "Epic Stuff", "Fortnite Live"));
        t.WriteItem(game.Replace('\\', '/')); // EGL writes forward slashes
        using var svc = t.CreateService();

        var c = Assert.Single((await svc.ScanAsync(Ct)).Candidates);
        Assert.True(c.FolderExists && c.ExecutableFound && !c.IsStale);
        Assert.Null(c.Reason);
        Assert.True(EglPath.Same(c.InstallPath, game));
    }

    [Fact]
    public async Task EveryItemCountsNotJustTheFirst()
    {
        using var t = new EglTestTree();
        var a = t.CreateGame(t.Drive("D", "A Games", "Fortnite"));
        var b = t.CreateGame(t.Drive("E", "FN B"));
        var c = t.CreateGame(t.Drive("F", "Third Drive", "Fort nite"));
        t.WriteItem(a);
        t.WriteItem(b);
        t.WriteItem(c);
        using var svc = t.CreateService();

        var state = await svc.ScanAsync(Ct);

        Assert.Equal(3, state.Candidates.Count);
        Assert.All(state.Candidates, x => Assert.True(x.ExecutableFound));
    }

    [Fact]
    public async Task LauncherInstalledDatOnAnotherDriveWithNonDefaultNameIsFound()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("E", "Spiele & Co", "FN (custom)"));
        t.WriteLauncherInstalled((game.Replace('\\', '/'), EglTestTree.V3310, "Fortnite"));
        using var svc = t.CreateService();

        var c = Assert.Single((await svc.ScanAsync(Ct)).Candidates);
        Assert.True(c.ExecutableFound);
        Assert.Equal(EglTestTree.V3310, c.Version);
        Assert.Null(c.Reason);
    }

    [Fact]
    public async Task StaleEntriesExplainThemselves()
    {
        using var t = new EglTestTree();
        var gone = t.Drive("D", "Offline Drive", "Fortnite");
        var empty = t.CreateGame(t.Drive("D", "Emptied"), withExe: false);
        t.WriteItem(gone);
        t.WriteLauncherInstalled((empty, EglTestTree.V3310, "Fortnite"));
        using var svc = t.CreateService();

        var state = await svc.ScanAsync(Ct);

        var missing = Assert.Single(state.Candidates, c => c.InstallPath == gone);
        Assert.True(missing.IsStale);
        Assert.Contains("doesn't exist", missing.Reason, StringComparison.Ordinal);
        var noExe = Assert.Single(state.Candidates, c => c.InstallPath == empty);
        Assert.True(noExe.IsStale);
        Assert.Contains("missing", noExe.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItemEglIsWritingStillCountsAsAnInstall()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("D", "Games", "Fortnite"));
        var item = t.WriteItem(game);
        using var svc = t.CreateService();

        // EGL (running) holds its manifest open for writing; File.ReadAllBytes would throw a sharing violation.
        using var eglHandle = new FileStream(item, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        var c = Assert.Single((await svc.ScanAsync(Ct)).Candidates);
        Assert.True(c.ExecutableFound);
        Assert.Equal(EglTestTree.V3310, c.Version);
    }

    // ------------------------------------------------------------------ quick scan

    [Fact]
    public void QuickScanFindsKnownFoldersOnEveryDriveAndNonDefaultNamesUnderKnownParents()
    {
        using var t = new EglTestTree();
        var d1 = t.CreateGame(t.Drive("D", "Games", "Epic Games", "Fortnite"));
        var d2 = t.CreateGame(t.Drive("D", "Fortnite"));
        var e1 = t.CreateGame(t.Drive("E", "Games", "My FN Copy"));
        var e2 = t.CreateGame(t.Drive("E", "Program Files", "Epic Games", "Fortnite"));
        t.CreateGame(t.Drive("E", "Stuff", "Deep", "Fortnite")); // not a known place: a drive is never walked
        t.CreateGame(t.Drive("E", "Games", "Not Fortnite"), withExe: false);

        var found = InstallLocator.QuickScan([t.Drive("D"), t.Drive("E"), t.Drive("Z")], Ct);

        Assert.Equal(4, found.Count);
        foreach (var expected in new[] { d1, d2, e1, e2 })
        {
            Assert.Contains(found, f => EglPath.Same(f, expected));
        }
    }

    [Fact]
    public void QuickScanIsCancellableAndSkipsMissingDrives()
    {
        using var t = new EglTestTree();
        t.CreateGame(t.Drive("D", "Fortnite"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => InstallLocator.QuickScan([t.Drive("D")], cts.Token));
        Assert.Empty(InstallLocator.QuickScan([t.Drive("Q"), ""], Ct));
    }

    // ------------------------------------------------------------------ locate by folder

    [Fact]
    public void LocateAcceptsAParentWithADifferentlyNamedGameFolderAndAFolderInsideTheInstall()
    {
        using var t = new EglTestTree();
        var game = t.CreateGame(t.Drive("F", "My Games", "FN Custom Name"));
        using var svc = t.CreateService();

        var parent = FolderInspector.Inspect(Path.GetDirectoryName(game)!, svc, "Fortnite");
        Assert.True(parent.Valid, parent.Reason);
        Assert.Equal(game, parent.GamePath);

        var inside = FolderInspector.Inspect(Path.Combine(game, "FortniteGame", "Binaries"), svc, "Fortnite");
        Assert.True(inside.Valid, inside.Reason);
        Assert.Equal(game, inside.GamePath);

        var wrong = FolderInspector.Inspect(t.Drive("F", "Other"), svc, "Fortnite");
        Assert.False(wrong.Valid);
    }

    // ------------------------------------------------------------------ write-back on such a folder

    [Fact]
    public async Task WriteBackOnForwardSlashFolderWithSpacesKeepsTheSameManifestAndBacksItUp()
    {
        using var t = new EglTestTree();
        var guid = "ABABABABABABABABABABABABABABABAB";
        var game = t.CreateGame(t.Drive("D", "My Games", "FN Custom"), egstoreGuid: guid);
        var item = t.WriteItem(game.Replace('\\', '/'), guid: guid);
        var original = File.ReadAllBytes(item);
        var manifest = t.LegendaryManifest(EglTestTree.V3320);
        using var svc = t.CreateService();

        var written = await svc.WriteBackAsync(Make.WriteBack(game, EglTestTree.V3320, manifest), Ct);

        Assert.Equal(guid, written);
        Assert.Equal(item, Assert.Single(Directory.GetFiles(t.Environment.ManifestsDir, "*.item")));
        var found = svc.FindByLocation(game);
        Assert.NotNull(found);
        Assert.Equal(EglTestTree.V3320, found.Version);
        Assert.True(EglPath.Same(found.InstallLocation, game));
        var backup = Assert.Single(Directory.GetDirectories(t.Paths.EglBackups));
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(backup, "Manifests", guid + ".item")));
        Assert.Empty(t.TempLeftovers());
    }
}
