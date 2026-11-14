using System.Diagnostics;
using Glider.Core.Contracts;
using Glider.Core.Egl;
using Glider.Core.Services;
using Glider.Core.Tests.Egl;

namespace Glider.Core.Tests.EndToEnd;

/// <summary>User journeys through the real runtime (see <see cref="EndToEndHarness"/>). Each runs in well under a few seconds.</summary>
public sealed class EndToEndTests
{
    private const string V1 = EglTestTree.V3310;
    private const string V2 = EglTestTree.V3320;
    private const string Fn = FortniteLayout.AppName;

    [Fact]
    public async Task SignInThenInstallWithProgressEndsReady()
    {
        await using var h = await EndToEndHarness.StartAsync(V1, signedIn: false);
        Assert.Equal(AuthStatus.SignedOut, h.Snapshot.Auth.Status);
        Assert.Equal(0, h.Count("status")); // signed-out state came from user.json, not a spawn
        Assert.Equal(0, h.Count("version"));
        Assert.Equal(InstallStatus.NotInstalled, h.Game()!.InstallStatus); // the library is there before signing in (files on disk)

        var auth = await h.Rpc.SignIn();
        Assert.Equal(AuthStatus.SignedIn, auth.Status);
        Assert.Equal(EndToEndHarness.Account, auth.Account!.DisplayName);
        Assert.Equal(["auth", "--token", "exchange-code", "--disable-webview"], h.Legendary.Invocations().Single(i => i.Command == "auth").Args);

        var job = await h.Rpc.Install(new InstallRequest(Fn, h.GamesRoot, null, null, false));
        var done = await h.WaitForDone(job.Id, "install");
        Assert.True(done.Status == JobStatus.Completed, done.Error?.Detail);
        Assert.Equal(OutcomeKind.Success, done.Outcome!.Kind);

        var progress = h.Sink.Of<JobProgress>(RpcEvents.Progress).Where(p => p.JobId == job.Id).ToList();
        Assert.Contains(progress, p => p.Phase == JobPhase.Downloading && p.Percent is > 0 and < 100);
        Assert.Contains(progress, p => p.Phase == JobPhase.Verifying); // fresh installs are always verified
        var whole = progress.Select(p => p.Percent).ToList();
        Assert.Equal(whole.Order(), whole); // the whole-job bar never goes backwards

        var installArgs = h.Legendary.Invocations().Single(i => i.Command == "install").Args;
        Assert.Contains("--skip-dlcs", installArgs);
        Assert.Contains("--skip-sdl", installArgs);

        await h.Runtime.Service.LibraryForTests.FlushAsync();
        var game = h.Game()!;
        Assert.Equal(InstallStatus.Installed, game.InstallStatus);
        Assert.Equal(V1, game.InstalledVersion);
        Assert.False(game.UpdateAvailable);
        Assert.Equal(1, h.Count("install"));
        Assert.Equal(1, h.Count("verify"));
        Assert.Contains(h.Sink.Of<LibraryDelta>(RpcEvents.Library), d => d.Upserts.Any(u => u.AppName == Fn && u.InstallStatus == InstallStatus.Installed));
    }

    [Fact]
    public async Task PauseKillsTheDownloadAndResumeContinuesFromTheResumeFile()
    {
        await using var h = await EndToEndHarness.StartAsync(V1);
        h.Install(V1, "interval=150");

        var job = await h.Rpc.Install(new InstallRequest(Fn, h.GamesRoot, null, null, false));
        await h.WaitForJob(job.Id, j => j.Progress is { Percent: > 0 }, "first progress");
        await EndToEndHarness.WaitFor(() => File.Exists(h.Legendary.ResumeFile), x => x, "a completed file in the resume list");
        var paused = await h.Rpc.PauseJob(new JobRef(job.Id));
        Assert.Equal(JobStatus.Paused, paused.Status);
        Assert.True(File.Exists(h.Legendary.ResumeFile));
        var before = h.Job(job.Id).Progress!.Percent;

        h.Install(V1, "interval=5");
        await h.Rpc.ResumeJob(new JobRef(job.Id));
        var done = await h.WaitForDone(job.Id, "resumed install");
        Assert.True(done.Status == JobStatus.Completed, done.Error?.Detail);
        Assert.True(before > 0);
        Assert.Equal(2, h.Count("install")); // the identical command, re-run
        await h.Runtime.Service.LibraryForTests.FlushAsync();
        Assert.Equal(InstallStatus.Installed, h.Game()!.InstallStatus);
    }

    [Fact]
    public async Task ImportingAnEglInstallDownloadsNothing()
    {
        await using var h = await EndToEndHarness.StartAsync(V1);
        var (game, _) = h.EglInstall(V1);
        var egl = await h.Rpc.RescanEgl();
        var found = Assert.Single(egl.Installs);
        Assert.Equal(EglInstallStatus.Importable, found.Status);
        Assert.Equal(Fn, found.AppName);
        Assert.Equal("Fortnite", found.Title);
        Assert.True(EglPath.Same(found.InstallPath, game));

        var result = await h.Rpc.ImportEgl(new EglImportRequest([found.Id], VerifyMode.Auto));
        var done = await h.WaitForDone(Assert.Single(result.Jobs).Id, "import");

        Assert.Equal(OutcomeKind.Success, done.Outcome!.Kind); // complete EGL install at the latest version: nothing to verify
        await h.Runtime.Service.LibraryForTests.FlushAsync();
        Assert.Equal(InstallStatus.Installed, h.Game()!.InstallStatus);
        Assert.True(h.Game()!.SharedWithEgl);
        Assert.Equal(EglInstallStatus.Imported, h.Snapshot.Egl.Installs.Single().Status);
        Assert.Equal(1, h.Count("import"));
        Assert.Equal(0, h.Count("install"));
        Assert.Equal(0, h.Count("verify"));
    }

    [Fact]
    public async Task QuickScanFindsAnInstallEglDoesNotKnowAndImportsItInPlace()
    {
        await using var h = await EndToEndHarness.StartAsync(V1);
        var game = h.Tree.CreateGame(h.Tree.Drive("D", "Games", "FN Custom Name"), "22222222222222222222222222222222");
        h.Disk.Drives.Add(new FixedDrive(h.Tree.Drive("D"), 1, 1));
        await h.Rpc.RescanEgl();
        Assert.Empty(h.Snapshot.Egl.Installs); // no .item, no LauncherInstalled.dat entry

        var egl = await h.Rpc.QuickScanEgl();

        var c = Assert.Single(egl.Installs);
        Assert.Equal(CandidateSource.Manual, c.Source);
        Assert.Equal(Fn, c.AppName); // from the folder's .egstore metadata
        Assert.True(EglPath.Same(c.InstallPath, game));
        Assert.Equal(EglInstallStatus.Importable, c.Status);
        var before = EglTreeHasher.Hash(h.Egl, game);

        var result = await h.Rpc.ImportEgl(new EglImportRequest([c.Id], VerifyMode.Auto));
        var done = await h.WaitForDone(Assert.Single(result.Jobs).Id, "import");
        Assert.Equal(JobStatus.Completed, done.Status);
        await EndToEndHarness.WaitFor(() => h.Snapshot.Queue.Jobs.Where(StateHub.IsActive).Count(), n => n == 0, "the background verify");

        // In place: Legendary was only asked to import (no install, no copy), the folder is untouched and now Glider's.
        await h.Runtime.Service.LibraryForTests.FlushAsync();
        Assert.Equal(InstallStatus.Installed, h.Game()!.InstallStatus);
        Assert.True(EglPath.Same(h.Game()!.InstallPath, game));
        Assert.Equal(1, h.Count("import"));
        Assert.Equal(0, h.Count("install"));
        Assert.Contains(game, h.Legendary.Invocations().Single(i => i.Command == "import").Args);
        EglTreeHasher.AssertSame(before, EglTreeHasher.Hash(h.Egl, game));
    }

    [Fact]
    public async Task JobWaitsWhileEglIsBusyThenRunsWhenItClears()
    {
        await using var h = await EndToEndHarness.StartAsync(V1);
        var (game, _) = h.EglInstall(V1);
        await h.ImportAsync(game);

        var bps = Path.Combine(game, ".egstore", "bps");
        Directory.CreateDirectory(bps);
        File.WriteAllText(Path.Combine(bps, "pakchunk0-WindowsClient.ucas"), "partial"); // EGL is patching right now
        h.EglProcesses.EglRunning = true;

        var job = await h.Rpc.Verify(new AppRef(Fn));
        var waiting = await h.WaitForJob(job.Id, j => j.Status == JobStatus.Waiting, "Waiting");
        Assert.NotNull(waiting.BlockedReason);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(0, h.Count("verify"));

        Directory.Delete(bps, recursive: true); // EGL finished
        var done = await h.WaitForDone(job.Id, "verify after EGL");
        Assert.True(done.Status == JobStatus.Completed, done.Error?.Detail);
        Assert.Null(done.BlockedReason);
        Assert.Equal(1, h.Count("verify"));
    }

    [Fact]
    public async Task RemoveFromGliderOnlyLeavesEveryEglFileByteIdentical()
    {
        await using var h = await EndToEndHarness.StartAsync(V1);
        var (game, _) = h.EglInstall(V1);
        h.Tree.WriteLauncherInstalled((game, V1, "Fortnite"));
        await h.ImportAsync(game);
        var before = EglTreeHasher.Hash(h.Egl, game);
        var exe = File.ReadAllBytes(FortniteLayout.ShippingExe(game));

        var job = await h.Rpc.Uninstall(new UninstallRequest(Fn, UninstallMode.GliderOnly));
        var done = await h.WaitForDone(job.Id, "uninstall");
        Assert.True(done.Status == JobStatus.Completed, done.Error?.Detail);

        await h.Runtime.Service.LibraryForTests.FlushAsync();
        Assert.Equal(InstallStatus.NotInstalled, h.Game()!.InstallStatus);
        EglTreeHasher.AssertSame(before, EglTreeHasher.Hash(h.Egl, game));
        Assert.Equal(exe, File.ReadAllBytes(FortniteLayout.ShippingExe(game)));
        var uninstall = h.Legendary.Invocations().Single(i => i.Command == "uninstall").Args;
        Assert.Contains("--keep-files", uninstall);
    }

    [Fact]
    public async Task EglNewerVersionIsPickedUpSilentlyOnTheNextStart()
    {
        await using var h = await EndToEndHarness.StartAsync(V1);
        var (game, guid) = h.EglInstall(V1);
        await h.ImportAsync(game);
        Assert.Equal(V1, h.Game()!.InstalledVersion);

        // EGL updated the game while Glider was closed (and Epic now serves that build).
        h.Tree.WriteItem(game, V2, guid);
        h.Import(V2);
        h.Info(V2);
        h.Library.AddCatalog(Fn, LibraryFixture.FortniteCatalog, V2).Save();
        await h.RestartAsync();

        var game2 = await EndToEndHarness.WaitFor(() => h.Game(), g => g?.InstalledVersion == V2, "the library shows EGL's version");
        Assert.False(game2!.UpdateAvailable);
        Assert.Equal(2, h.Count("import")); // import + the silent re-import
        Assert.Equal(0, h.Count("install"));
        Assert.Equal(0, h.Count("verify"));
        Assert.DoesNotContain(h.Snapshot.Queue.Jobs, j => j.Kind is JobKind.Update or JobKind.Verify or JobKind.Repair);
        Assert.Equal(V2, h.Runtime.Service.Hub.State.Egl!.Installs.Single().Version);
    }

    [Fact]
    public async Task PrepareLaunchThenPlayStartsTheGameWithoutAnotherLegendaryRun()
    {
        await using var h = await EndToEndHarness.StartAsync(V1);
        var (game, _) = h.EglInstall(V1);
        await h.ImportAsync(game);
        LaunchScript(h, game);
        await h.Rpc.PrepareLaunch(new AppRef(Fn));
        await h.Runtime.Service.LaunchServiceForTests.Preparing;
        Assert.Equal(1, h.Count("launch"));

        var result = await h.Rpc.Launch(new LaunchRequest(Fn, null, false));
        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Equal(1, h.Count("launch"));
        Assert.Single(h.GameProcesses.Started);
        Assert.Contains("--skip-version-check", h.Legendary.Invocations().Single(i => i.Command == "launch").Args);
    }

    /// <summary>The critical lane rule (L§1.8): a launch never waits behind another game's download.</summary>
    [Fact]
    public async Task LaunchIsNotBlockedByARunningDownloadOfAnotherGame()
    {
        await using var h = await EndToEndHarness.StartAsync(V1);
        var (game, _) = h.EglInstall(V1);
        await h.ImportAsync(game);
        h.Library.Add("Big", LibraryFixture.Item("Big Game", "ns-big", "item-big"), "1.0").Save();
        await h.Runtime.Service.LibraryForTests.ReloadAsync();
        h.Legendary.Script("install", """
            err [Core] INFO: Trying to re-use existing login session...
            err [cli] INFO: Preparing download for "Big"...
            download chunks=2000 files=40 mib=400 interval=250 app=Big
            installed version=1.0 path={basepath}/Big app=Big
            err [cli] INFO: Finished installation process in 0.20 seconds.
            """);
        LaunchScript(h, game);

        var download = await h.Rpc.Install(new InstallRequest("Big", h.GamesRoot, null, null, false));
        await h.WaitForJob(download.Id, j => j.Status == JobStatus.Running && j.Progress is { Percent: > 0 }, "the download running");

        var sw = Stopwatch.StartNew();
        var result = await h.Rpc.Launch(new LaunchRequest(Fn, null, false));
        sw.Stop();

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Equal(JobStatus.Running, h.Job(download.Id).Status); // the download kept running
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"launch took {sw.Elapsed}");
        await h.Rpc.CancelJob(new JobRef(download.Id));
    }

    private static void LaunchScript(EndToEndHarness h, string game)
    {
        var dir = game.Replace(Path.DirectorySeparatorChar, '/'); // the fake's script language eats backslashes
        h.Legendary.Script("launch", $$"""out {"game_parameters": [], "game_executable": "FortniteGame/Binaries/Win64/FortniteLauncher.exe", "game_directory": "{{dir}}","egl_parameters": ["-AUTH_PASSWORD=code"], "launch_command": [], "working_directory": "", "user_parameters": [], "environment": {}, "pre_launch_command": "", "pre_launch_wait": false}""");
    }
}
