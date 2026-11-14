using Glider.Core.Contracts;
using Glider.Core.Egl;
using Glider.Core.Legendary;
using Glider.Core.Services;
using Glider.Core.Tests.Egl;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.EndToEnd;

/// <summary>
/// "Migrate my whole library from EGL" on a throw-away machine (runs on any CI box: no real drives, no real ProgramData, no real
/// Epic): several games on several fake drives with EGL manifests, the real runtime + job queue + EglService over a scripted
/// Legendary. The point of every scenario: nothing is downloaded, EGL's files are not disturbed, per-game results are honest.
/// </summary>
public class EglMigrationScenarioTests
{
    private const string Old = EglTestTree.V3310;
    private const string New = EglTestTree.V3320;
    private const string Fn = FortniteLayout.AppName;

    private sealed record Game(string App, string Title, string Path, string Guid);

    /// <summary>Fortnite on E: plus three other games on D:, F: and G:, all installed by EGL at <see cref="Old"/>.</summary>
    private static async Task<(EndToEndHarness H, List<Game> Games)> StartAsync()
    {
        var h = await EndToEndHarness.StartAsync(Old);
        var games = new List<Game>();
        var (fn, fnGuid) = h.EglInstall(Old);
        games.Add(new Game(Fn, "Fortnite", fn, fnGuid));
        foreach (var (app, title, drive, folder) in new[]
                 {
                     ("hades-app", "Hades", "D", "Hades"),
                     ("celeste-app", "Celeste", "F", "Celeste"),
                     ("control-app", "Control", "G", "Control Ultimate"),
                 })
        {
            var guid = (app.GetHashCode(StringComparison.Ordinal) & 0xFFFFFF).ToString("X6", System.Globalization.CultureInfo.InvariantCulture).PadRight(32, '0');
            var path = h.Tree.CreateOtherGame(h.Tree.Drive(drive, "Epic Games", folder), app, guid);
            h.Tree.WriteOtherItem(path, app, Old, guid, title);
            h.Library.Add(app, LibraryFixture.Item(title, "ns-" + app, "item-" + app), Old);
            games.Add(new Game(app, title, path, guid));
        }

        h.Library.Save();
        await h.Runtime.Service.LibraryForTests.ReloadAsync();
        h.Legendary.Script("import", $"""
            err [Core] INFO: Trying to re-use existing login session...
            installed version={Old} path={"{arg:1}"} app={"{arg:0}"}
            err [cli] INFO: Game "{"{arg:0}"}" has been imported.
            """);
        h.Legendary.Script("uninstall", "uninstalled app={arg:0}\nerr [cli] INFO: Game has been uninstalled.");
        return (h, games);
    }

    /// <summary>Runs of a Legendary command that change things: the update-size dry run (`update` without -y) is a read.</summary>
    private static int Real(EndToEndHarness h, string command) => h.Legendary.Invocations().Count(i => i.Command == command && (command == "verify" || i.Args.Contains("-y")));

    private static IEnumerable<string> ReadShared(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd().ReplaceLineEndings("|").Split('|');
    }

    private static string[] EglPaths(IEnumerable<Game> games) => [.. games.Select(g => g.Path)];

    private static async Task WaitAllDone(EndToEndHarness h, IEnumerable<JobInfo> jobs)
    {
        foreach (var j in jobs)
        {
            var done = await h.WaitForDone(j.Id, $"job {j.AppName}");
            Assert.True(done.Status == JobStatus.Completed, $"{j.AppName}: {done.Error?.Detail}");
        }

        await EndToEndHarness.WaitFor(() => h.Snapshot.Queue.Jobs.Count(StateHub.IsActive), n => n == 0, "an idle queue");
        await h.Runtime.Service.LibraryForTests.FlushAsync();
    }

    [Fact]
    public async Task BulkImportOfTheWholeLibrary_NeverDownloads_AndLeavesEglUntouched()
    {
        var (h, games) = await StartAsync();
        await using var harness = h;
        var before = EglTreeHasher.Hash(h.Egl, EglPaths(games));

        var egl = await h.Rpc.RescanEgl();

        Assert.Equal(games.Count, egl.Installs.Count);
        Assert.All(egl.Installs, i =>
        {
            Assert.Equal(EglInstallStatus.Importable, i.Status);
            Assert.Equal(Old, i.Version);
            Assert.False(string.IsNullOrEmpty(i.Drive));
            Assert.Null(i.Reason);
        });
        Assert.Equal(games.Select(g => g.Title).Order(), egl.Installs.Select(i => i.Title).Order()); // titles come from the library

        var result = await h.Rpc.ImportEgl(new EglImportRequest([.. egl.Installs.Select(i => i.Id)], VerifyMode.Auto));
        Assert.Equal(games.Count, result.Jobs.Count);
        Assert.Empty(result.Skipped ?? []);
        await WaitAllDone(h, result.Jobs);

        // zero re-download: Legendary was only ever asked to import
        Assert.Equal(games.Count, h.Count("import"));
        foreach (var forbidden in new[] { "install", "update", "repair", "verify", "download" })
        {
            Assert.Equal(0, Real(h, forbidden));
        }

        foreach (var g in games)
        {
            var summary = h.Game(g.App)!;
            Assert.Equal(InstallStatus.Installed, summary.InstallStatus);
            Assert.True(EglPath.Same(summary.InstallPath, g.Path));
            Assert.Equal(Old, summary.InstalledVersion);
            Assert.True(summary.SharedWithEgl || h.Runtime.Service.Hub.State.Egl!.Installs.Any(i => i.AppName == g.App));
        }

        Assert.All(result.Jobs, j => Assert.Equal(OutcomeKind.Success, h.Job(j.Id).Outcome!.Kind));
        EglTreeHasher.AssertSame(before, EglTreeHasher.Hash(h.Egl, EglPaths(games)));
        Assert.Empty(h.Tree.TempLeftovers());

        // idempotent: the same request again queues nothing and says why
        var again = await h.Rpc.ImportEgl(new EglImportRequest([.. egl.Installs.Select(i => i.Id)], VerifyMode.Auto));
        Assert.Empty(again.Jobs);
        Assert.Equal(games.Count, again.Skipped!.Count);
        Assert.All(again.Skipped!, s => Assert.Contains("already in Glider", s.Reason));
        Assert.Equal(games.Count, h.Count("import"));
    }

    [Fact]
    public async Task AGameEglIsWorkingOn_IsSkippedWithItsReason_AndImportsOnceEglIsDone()
    {
        var (h, games) = await StartAsync();
        await using var harness = h;
        var busy = games[1];
        var bps = Path.Combine(busy.Path, ".egstore", "bps");
        Directory.CreateDirectory(bps);
        File.WriteAllText(Path.Combine(bps, "chunk"), "partial"); // EGL is patching right now
        h.EglProcesses.EglRunning = true;

        var egl = await h.Rpc.RescanEgl();
        Assert.Equal(EglInstallStatus.Busy, egl.Installs.Single(i => i.AppName == busy.App).Status);
        Assert.Contains(busy.App, egl.BusyApps);

        var result = await h.Rpc.ImportEgl(new EglImportRequest([.. egl.Installs.Select(i => i.Id)], VerifyMode.Auto));
        Assert.Equal(games.Count - 1, result.Jobs.Count);
        var skip = Assert.Single(result.Skipped!);
        Assert.Equal(busy.App, skip.AppName);
        Assert.Contains("working on it", skip.Reason);
        await WaitAllDone(h, result.Jobs);
        Assert.Equal(InstallStatus.NotInstalled, h.Game(busy.App)!.InstallStatus);

        Directory.Delete(bps, recursive: true); // EGL finished
        h.EglProcesses.EglRunning = false;
        egl = await h.Rpc.RescanEgl();
        var retry = await h.Rpc.ImportEgl(new EglImportRequest([egl.Installs.Single(i => i.AppName == busy.App).Id], VerifyMode.Auto));
        await WaitAllDone(h, retry.Jobs);
        Assert.Equal(InstallStatus.Installed, h.Game(busy.App)!.InstallStatus);
        Assert.Equal(0, h.Count("install") + h.Count("repair") + h.Count("update"));
    }

    [Fact]
    public async Task EglNewerForOneGame_IsPickedUpSilentlyForThatGameOnly()
    {
        var (h, games) = await StartAsync();
        await using var harness = h;
        var egl = await h.Rpc.RescanEgl();
        await WaitAllDone(h, (await h.Rpc.ImportEgl(new EglImportRequest([.. egl.Installs.Select(i => i.Id)], VerifyMode.Auto))).Jobs);
        var target = games[2];
        var others = games.Where(g => g != target).ToList();
        var imports = h.Count("import");

        // EGL updated one game while Glider was closed.
        h.Tree.WriteOtherItem(target.Path, target.App, New, target.Guid, target.Title);
        h.Library.Add(target.App, LibraryFixture.Item(target.Title, "ns-" + target.App, "item-" + target.App), New).Save();
        h.Legendary.Script("import", $"""
            installed version={New} path={"{arg:1}"} app={"{arg:0}"}
            err [cli] INFO: Game "{"{arg:0}"}" has been imported.
            """, invocation: imports + 1);
        await h.RestartAsync();

        await EndToEndHarness.WaitFor(() => h.Game(target.App), g => g?.InstalledVersion == New, "EGL's newer build");
        await EndToEndHarness.WaitFor(() => h.Snapshot.Queue.Jobs.Count(StateHub.IsActive), n => n == 0, "an idle queue");
        Assert.Equal(imports + 1, h.Count("import"));
        var forgotten = Assert.Single(h.Legendary.Invocations(), i => i.Command == "uninstall").Args;
        Assert.Contains("--keep-files", forgotten);
        Assert.Contains(target.App, forgotten);
        foreach (var o in others)
        {
            Assert.Equal(Old, h.Game(o.App)!.InstalledVersion);
        }

        foreach (var forbidden in new[] { "install", "update", "repair", "verify" })
        {
            Assert.Equal(0, Real(h, forbidden));
        }
    }

    [Fact]
    public async Task GliderNewerForOneGame_IsWrittenBackWithABackup_WhenSyncIsOn()
    {
        var (h, games) = await StartAsync();
        await using var harness = h;
        var egl = await h.Rpc.RescanEgl();
        await WaitAllDone(h, (await h.Rpc.ImportEgl(new EglImportRequest([.. egl.Installs.Select(i => i.Id)], VerifyMode.Auto))).Jobs);
        var target = games[3];
        var untouched = games.Where(g => g != target).ToList();
        var untouchedBefore = EglTreeHasher.Hash(h.Egl, EglPaths(untouched)).Where(kv => !kv.Key.Contains(target.Guid, StringComparison.Ordinal)).ToDictionary();
        var itemPath = Path.Combine(h.Egl.ManifestsDir, target.Guid + ".item");
        var oldItem = File.ReadAllBytes(itemPath);

        // A Glider update: Legendary registers the new build and has its manifest on disk.
        var manifest = LegendaryFiles.CleanFilename($"{target.App}_Windows_{New}") + ".manifest";
        h.Library.Add(target.App, LibraryFixture.Item(target.Title, "ns-" + target.App, "item-" + target.App), New).Save();
        h.Legendary.Script("update", $"""
            err [Core] INFO: Trying to re-use existing login session...
            writefile manifests/{manifest} NEW-MANIFEST
            installed version={New} path="{target.Path}" app={target.App}
            err [cli] INFO: Finished installation process in 0.10 seconds.
            """);
        h.Legendary.Script("verify", "verify files=3 interval=1 app=" + target.App);
        var job = await h.Rpc.Update(new AppRef(target.App));
        var done = await h.WaitForDone(job.Id, "the update");
        Assert.True(done.Status == JobStatus.Completed, done.Error?.Detail);
        await EndToEndHarness.WaitFor(() => File.ReadAllBytes(itemPath).SequenceEqual(oldItem), v => !v,
            "the write-back to EGL; notices: " + string.Join(" | ", h.Sink.Of<Notice>("notice").Select(n => n.Title + ": " + n.Message)) + " log: " + string.Join(" / ", Directory.EnumerateFiles(h.Legendary.Paths.Logs, "*.log").SelectMany(ReadShared).Where(l => l.Contains("egl", StringComparison.OrdinalIgnoreCase) || l.Contains("WARN", StringComparison.Ordinal)).TakeLast(8)));

        Assert.Contains(New, File.ReadAllText(itemPath));
        Assert.Equal("NEW-MANIFEST", File.ReadAllText(Path.Combine(target.Path, ".egstore", target.Guid + ".manifest")));
        // timestamped backup of exactly what was replaced; everything else of EGL is byte-identical
        var backup = Assert.Single(Directory.EnumerateFiles(h.Legendary.Paths.EglBackups, target.Guid + ".item", SearchOption.AllDirectories));
        Assert.Equal(oldItem, File.ReadAllBytes(backup));
        EglTreeHasher.AssertSame(untouchedBefore, EglTreeHasher.Hash(h.Egl, EglPaths(untouched)).Where(kv => !kv.Key.Contains(target.Guid, StringComparison.Ordinal)).ToDictionary());
        Assert.Empty(h.Tree.TempLeftovers());
        Assert.Equal(1, h.Count("update"));
    }

    [Fact]
    public async Task RemoveFromGliderOnly_ForEveryGame_LeavesEglFilesAndGameFilesByteIdentical()
    {
        var (h, games) = await StartAsync();
        await using var harness = h;
        h.Tree.WriteLauncherInstalled(games.Select(x => (x.Path, Old, x.App)).ToArray());

        var egl = await h.Rpc.RescanEgl();
        await WaitAllDone(h, (await h.Rpc.ImportEgl(new EglImportRequest([.. egl.Installs.Select(i => i.Id)], VerifyMode.Auto))).Jobs);
        var before = EglTreeHasher.Hash(h.Egl, EglPaths(games));
        var files = games.ToDictionary(g => g.App, g => Directory.EnumerateFiles(g.Path, "*", SearchOption.AllDirectories).Order().ToList());

        foreach (var g in games)
        {
            var job = await h.Rpc.Uninstall(new UninstallRequest(g.App, UninstallMode.GliderOnly));
            var done = await h.WaitForDone(job.Id, "uninstall " + g.App);
            Assert.True(done.Status == JobStatus.Completed, done.Error?.Detail);
        }

        await h.Runtime.Service.LibraryForTests.FlushAsync();
        EglTreeHasher.AssertSame(before, EglTreeHasher.Hash(h.Egl, EglPaths(games)));
        foreach (var g in games)
        {
            Assert.Equal(InstallStatus.NotInstalled, h.Game(g.App)!.InstallStatus);
            Assert.Equal(files[g.App], Directory.EnumerateFiles(g.Path, "*", SearchOption.AllDirectories).Order().ToList());
        }

        Assert.All(h.Legendary.Invocations().Where(i => i.Command == "uninstall"), i => Assert.Contains("--keep-files", i.Args));
        // and EGL still lists every game as installed there (it is importable again, nothing was taken from it)
        Assert.All((await h.Rpc.RescanEgl()).Installs, i => Assert.Equal(EglInstallStatus.Importable, i.Status));
    }

    [Fact]
    public async Task QuickScanOnEveryDriveFindsGamesEglDoesNotListAnymore_AndTheyImportInPlace()
    {
        var (h, games) = await StartAsync();
        await using var harness = h;
        // EGL forgot one game (its .item is gone) and another sits in an unusual place; both keep their .egstore.
        var lost = games[1];
        File.Delete(Path.Combine(h.Egl.ManifestsDir, lost.Guid + ".item"));
        var odd = h.Tree.CreateOtherGame(h.Tree.Drive("H", "Stuff", "Epic Games", "Odd"), "odd-app", "ABCDEF0123456789ABCDEF0123456789");
        h.Library.Add("odd-app", LibraryFixture.Item("Odd One", "ns-odd", "item-odd"), Old).Save();
        await h.Runtime.Service.LibraryForTests.ReloadAsync();
        foreach (var d in new[] { "D", "E", "F", "G", "H" })
        {
            h.Disk.Drives.Add(new FixedDrive(h.Tree.Drive(d), 1, 1));
        }

        var state = await h.Rpc.QuickScanEgl();

        var found = state.Installs.Where(i => i.Source == CandidateSource.Manual).ToDictionary(i => i.AppName);
        Assert.Equal(["hades-app", "odd-app"], found.Keys.Order());
        Assert.True(EglPath.Same(found["odd-app"].InstallPath, odd));
        Assert.All(found.Values, i => Assert.Equal(EglInstallStatus.Importable, i.Status));
        Assert.Equal("Odd One", found["odd-app"].Title);

        var before = EglTreeHasher.Hash(h.Egl, odd, lost.Path);
        var result = await h.Rpc.ImportEgl(new EglImportRequest([.. found.Values.Select(i => i.Id)], VerifyMode.Auto));
        await WaitAllDone(h, result.Jobs);
        Assert.Equal(InstallStatus.Installed, h.Game("odd-app")!.InstallStatus);
        EglTreeHasher.AssertSame(before, EglTreeHasher.Hash(h.Egl, odd, lost.Path));
    }

    [Fact]
    public async Task ReadinessGoesFromBlockedToReady_AsTheLibraryIsImported()
    {
        var (h, games) = await StartAsync();
        await using var harness = h;
        var first = await h.Rpc.CheckEglReadiness();
        Assert.False(first.Ready);
        Assert.Equal(games.Count, first.Items.Count(i => i.Severity == ReadinessSeverity.Blocker && i.Fix == ReadinessFix.Import));

        var egl = await h.Rpc.RescanEgl();
        await WaitAllDone(h, (await h.Rpc.ImportEgl(new EglImportRequest([.. egl.Installs.Select(i => i.Id)], VerifyMode.Auto))).Jobs);

        var after = await h.Rpc.CheckEglReadiness();
        Assert.True(after.Ready);
        Assert.DoesNotContain(after.Items, i => i.Severity == ReadinessSeverity.Blocker);
        Assert.Contains(after.Items, i => i.Fix == ReadinessFix.TurnOnEpicLinks); // links still open EGL until the user takes them over
    }
}
