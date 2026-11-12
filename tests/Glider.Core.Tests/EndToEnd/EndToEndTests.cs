using Glider.Core.Contracts;
using Glider.Core.Tests.Egl;

namespace Glider.Core.Tests.EndToEnd;

/// <summary>User journeys through the real runtime (see <see cref="EndToEndHarness"/>). Each runs in well under a few seconds.</summary>
public sealed class EndToEndTests
{
    private const string V1 = EglTestTree.V3310;
    private const string V2 = EglTestTree.V3320;

    [Fact]
    public async Task SignInThenInstallWithProgressEndsReady()
    {
        await using var h = await EndToEndHarness.StartAsync(V1, signedIn: false);
        Assert.Equal(AuthStatus.SignedOut, h.Snapshot.Auth.Status);
        Assert.Equal(0, h.Count("status")); // signed-out state came from user.json, not a spawn
        Assert.Equal(0, h.Count("version"));

        var auth = await h.Rpc.SignIn();
        Assert.Equal(AuthStatus.SignedIn, auth.Status);
        Assert.Equal(EndToEndHarness.Account, auth.Account!.DisplayName);
        Assert.Equal(["auth", "--code", "auth-code", "--disable-webview"], h.Legendary.Invocations().Single(i => i.Command == "auth").Args);

        var job = await h.Rpc.Install(new InstallRequest(h.GamesRoot));
        var done = await h.WaitForJob(job.Id, j => j.Status is JobStatus.Completed or JobStatus.Failed, "install");
        Assert.True(done.Status == JobStatus.Completed, done.Error?.Detail);

        var progress = h.Sink.Of<JobProgress>(RpcEvents.Progress).Where(p => p.JobId == job.Id).ToList();
        Assert.Contains(progress, p => p.Phase == JobPhase.Downloading && p.Percent is > 0 and < 100);
        Assert.Contains(progress, p => p.Phase == JobPhase.Verifying); // fresh installs are always verified
        var download = progress.Where(p => p.Phase == JobPhase.Downloading).Select(p => p.Percent).ToList();
        Assert.Equal(download.Order(), download); // never goes backwards within a phase

        var game = h.Snapshot.Game;
        Assert.Equal(InstallStatus.Installed, game.InstallStatus);
        Assert.Equal(V1, game.Installed!.Version);
        Assert.False(game.UpdateAvailable);
        Assert.Equal(1, h.Count("install"));
        Assert.Equal(1, h.Count("verify"));
    }

    [Fact]
    public async Task PauseKillsTheDownloadAndResumeContinuesFromTheResumeFile()
    {
        await using var h = await EndToEndHarness.StartAsync(V1);
        h.Install(V1, "interval=150");

        var job = await h.Rpc.Install(new InstallRequest(h.GamesRoot));
        await h.WaitForJob(job.Id, j => j.Progress is { Percent: > 0 }, "first progress");
        await EndToEndHarness.WaitFor(() => File.Exists(h.Legendary.ResumeFile), x => x, "a completed file in the resume list");
        var paused = await h.Rpc.PauseJob(new JobRef(job.Id));
        Assert.Equal(JobStatus.Paused, paused.Status);
        Assert.True(File.Exists(h.Legendary.ResumeFile));
        var before = h.Job(job.Id).Progress!.Percent;

        h.Install(V1, "interval=5");
        await h.Rpc.ResumeJob(new JobRef(job.Id));
        var done = await h.WaitForJob(job.Id, j => j.Status is JobStatus.Completed or JobStatus.Failed, "resumed install");
        Assert.True(done.Status == JobStatus.Completed, done.Error?.Detail);
        Assert.True(before > 0);
        Assert.Equal(2, h.Count("install")); // the identical command, re-run
        Assert.Equal(InstallStatus.Installed, h.Snapshot.Game.InstallStatus);
    }

    [Fact]
    public async Task AdoptingAnEglInstallImportsWithoutDownloading()
    {
        await using var h = await EndToEndHarness.StartAsync(V1);
        var (game, _) = h.EglInstall(V1);
        await h.Rpc.RescanEgl();
        Assert.Contains(h.Snapshot.Egl.Candidates, c => string.Equals(c.InstallPath, game, StringComparison.OrdinalIgnoreCase));

        var result = await h.Rpc.Adopt(new AdoptRequest(null, game, VerifyMode.Auto));

        Assert.Null(result.VerifyJob); // complete EGL install at the latest version: nothing to verify
        Assert.Equal(InstallStatus.Installed, result.Game.InstallStatus);
        Assert.True(result.Game.Installed!.SharedWithEgl);
        Assert.True(h.Snapshot.Egl.SyncAvailable);
        Assert.Equal(1, h.Count("import"));
        Assert.Equal(0, h.Count("install"));
        Assert.Equal(0, h.Count("verify"));
    }

    [Fact]
    public async Task JobWaitsWhileEglIsBusyThenRunsWhenItClears()
    {
        await using var h = await EndToEndHarness.StartAsync(V1);
        var (game, _) = h.EglInstall(V1);
        await h.Rpc.Adopt(new AdoptRequest(null, game, VerifyMode.Auto));

        var bps = Path.Combine(game, ".egstore", "bps");
        Directory.CreateDirectory(bps);
        File.WriteAllText(Path.Combine(bps, "pakchunk0-WindowsClient.ucas"), "partial"); // EGL is patching right now
        h.EglProcesses.EglRunning = true;

        var job = await h.Rpc.Verify();
        var waiting = await h.WaitForJob(job.Id, j => j.Status == JobStatus.Waiting, "Waiting");
        Assert.NotNull(waiting.BlockedReason);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(0, h.Count("verify"));

        Directory.Delete(bps, recursive: true); // EGL finished
        var done = await h.WaitForJob(job.Id, j => j.Status is JobStatus.Completed or JobStatus.Failed, "verify after EGL");
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
        await h.Rpc.Adopt(new AdoptRequest(null, game, VerifyMode.Auto));
        var before = EglTreeHasher.Hash(h.Egl, game);
        var exe = File.ReadAllBytes(FortniteLayout.ShippingExe(game));

        var job = await h.Rpc.Uninstall(new UninstallRequest(UninstallMode.GliderOnly));
        var done = await h.WaitForJob(job.Id, j => j.Status is JobStatus.Completed or JobStatus.Failed, "uninstall");
        Assert.True(done.Status == JobStatus.Completed, done.Error?.Detail);

        Assert.Equal(InstallStatus.NotInstalled, h.Snapshot.Game.InstallStatus);
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
        await h.Rpc.Adopt(new AdoptRequest(null, game, VerifyMode.Auto));
        Assert.Equal(V1, h.Snapshot.Game.Installed!.Version);

        // EGL updated the game while Glider was closed.
        h.Tree.WriteItem(game, V2, guid);
        h.Import(V2);
        h.Info(V2);
        await h.RestartAsync();

        var game2 = await EndToEndHarness.WaitFor(() => h.Snapshot.Game, g => g.Installed?.Version == V2, "record refreshed to EGL's version");
        Assert.False(game2.UpdateAvailable);
        Assert.Equal(2, h.Count("import")); // adopt + the silent re-import
        Assert.Equal(0, h.Count("install"));
        Assert.Equal(0, h.Count("verify"));
        Assert.DoesNotContain(h.Snapshot.Jobs, j => j.Kind is JobKind.Update or JobKind.Verify or JobKind.Repair);
    }

    [Fact]
    public async Task PrepareLaunchThenPlayStartsTheGameWithoutAnotherLegendaryRun()
    {
        await using var h = await EndToEndHarness.StartAsync(V1);
        var (game, _) = h.EglInstall(V1);
        await h.Rpc.Adopt(new AdoptRequest(null, game, VerifyMode.Auto));
        var dir = game.Replace(Path.DirectorySeparatorChar, '/'); // the fake's script language eats backslashes
        h.Legendary.Script("launch", $$"""out {"game_parameters": [], "game_executable": "FortniteGame/Binaries/Win64/FortniteLauncher.exe", "game_directory": "{{dir}}","egl_parameters": ["-AUTH_PASSWORD=code"], "launch_command": [], "working_directory": "", "user_parameters": [], "environment": {}, "pre_launch_command": "", "pre_launch_wait": false}""");
        await h.Rpc.PrepareLaunch();
        await h.Runtime.Service.LaunchServiceForTests.Preparing;
        Assert.Equal(1, h.Count("launch"));

        var result = await h.Rpc.Launch();
        Assert.True(result.Started);
        Assert.Equal(1, h.Count("launch"));
        Assert.Single(h.GameProcesses.Started);
    }
}
