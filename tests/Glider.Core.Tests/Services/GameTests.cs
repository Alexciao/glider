using Glider.Core.Contracts;
using Glider.Core.Game;
using Glider.Core.Services;

namespace Glider.Core.Tests.Services;

public class ImportTests
{
    private static async Task<Rig> ReadyRig(string eglVersion, string latest)
    {
        var rig = new Rig(latest: latest);
        var game = rig.MakeGameFolder();
        rig.Egl.Item = new EglItem("item", "guid-9", game, eglVersion, 100, false, false, FortniteLayout.AppName);
        rig.Egl.Scan = rig.Egl.Scan with
        {
            Installs = [new EglFoundInstall("egl-1", CandidateSource.EpicGamesLauncher, FortniteLayout.AppName, "Fortnite", game, eglVersion, 100, true, true, false, "guid-9")],
        };
        rig.Legendary.ImportVersion = eglVersion;
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never });
        await rig.StartWarmAsync();
        return rig;
    }

    /// <summary>The fake queue runs hooks only; the real queue would have run `legendary import` first.</summary>
    private static async Task<JobInfo> RunImport(Rig rig, JobInfo job)
    {
        await rig.Legendary.ImportAsync(job.AppName, job.TargetPath!, CancellationToken.None);
        return await rig.Jobs.RunAsync(job.Id, rig.Service.Hooks);
    }

    [Fact]
    public async Task SameVersionAsLatest_SkipsVerify()
    {
        using var rig = await ReadyRig("v5", "v5");
        var result = await rig.Service.ImportEgl(new EglImportRequest(["egl-1"], VerifyMode.Auto));
        var job = Assert.Single(result.Jobs);
        Assert.Equal(JobKind.Import, job.Kind);
        Assert.Equal(FortniteLayout.AppName, job.AppName);
        await RunImport(rig, job);

        Assert.Single(rig.Jobs.Requests); // no verify queued
        var rec = rig.Games.Record()!;
        Assert.True(rec.AdoptedFromEgl);
        Assert.Equal("guid-9", rec.EglInstallationGuid);
        Assert.False(rec.NeedsVerification);
        var summary = await rig.SummaryAsync();
        Assert.True(summary!.SharedWithEgl);
        Assert.Equal(InstallStatus.Installed, summary.InstallStatus);
    }

    [Fact]
    public async Task DifferentVersion_QueuesBackgroundVerify_WithNeedsRepairOutcome()
    {
        using var rig = await ReadyRig("v4", "v5");
        var job = Assert.Single((await rig.Service.ImportEgl(new EglImportRequest(["egl-1"], VerifyMode.Auto))).Jobs);
        await RunImport(rig, job);

        Assert.Equal([JobKind.Import, JobKind.Verify], rig.Jobs.Requests.Select(r => r.Kind));
        Assert.True(rig.Games.Record()!.NeedsVerification);
    }

    [Theory]
    [InlineData(VerifyMode.Background)]
    [InlineData(VerifyMode.Full)]
    public async Task ExplicitVerifyModes_AlwaysVerify(VerifyMode mode)
    {
        using var rig = await ReadyRig("v5", "v5");
        var job = Assert.Single((await rig.Service.ImportEgl(new EglImportRequest(["egl-1"], mode))).Jobs);
        await RunImport(rig, job);
        Assert.Equal(JobKind.Verify, rig.Jobs.Requests.Last().Kind);
    }

    [Fact]
    public async Task ManualFolderWithoutEgl_IsVerified()
    {
        using var rig = new Rig(latest: "v5");
        var game = rig.MakeGameFolder();
        rig.Legendary.ImportVersion = "v5";
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never });
        await rig.StartWarmAsync();

        // The parent folder is accepted; the profile probe finds the game inside.
        var job = await rig.Service.ImportFolder(new LocateRequest(Path.GetDirectoryName(game)!, VerifyMode.Auto));
        Assert.Equal(game, job.TargetPath);
        await RunImport(rig, job);
        Assert.Equal(JobKind.Verify, rig.Jobs.Requests.Last().Kind);
        Assert.False(rig.Games.Record()!.AdoptedFromEgl);
        Assert.Equal(game, rig.Games.Record()!.InstallPath);
    }

    [Fact]
    public async Task ImportValidatesInput()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.ImportEgl(new EglImportRequest([], VerifyMode.Auto)));
        Assert.Equal(ErrorCode.InvalidRequest, e.Error.Code);
        e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.ImportFolder(new LocateRequest(rig.Root, VerifyMode.Auto)));
        Assert.Equal(ErrorCode.InvalidFolder, e.Error.Code);
        var none = await rig.Service.ImportEgl(new EglImportRequest(["nope"], VerifyMode.Auto));
        Assert.Empty(none.Jobs); // unknown ids are skipped
    }

    [Fact]
    public async Task ImportRefusesWhileEglBusy()
    {
        using var rig = new Rig();
        var game = rig.MakeGameFolder();
        await rig.StartWarmAsync();
        rig.Egl.Busy = true;
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.ImportFolder(new LocateRequest(game, VerifyMode.Auto)));
        Assert.Equal(ErrorCode.EglBusy, e.Error.Code);
        Assert.DoesNotContain(rig.Calls.All, c => c.StartsWith("legendary.import", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BulkImport_OneJobPerGame_SkipsTheOnesThatCantBeImported()
    {
        using var rig = new Rig();
        rig.AddGame("Alien", "Alien: Isolation");
        rig.AddGame("Castle", "Castlevania");
        var fortnite = rig.MakeGameFolder();
        var alien = rig.MakeOtherGameFolder("Alien");
        rig.Egl.Scan = rig.Egl.Scan with
        {
            Installs =
            [
                new EglFoundInstall("a", CandidateSource.EpicGamesLauncher, FortniteLayout.AppName, "Fortnite", fortnite, "v2", 1, true, true, false, "g1"),
                new EglFoundInstall("b", CandidateSource.EpicGamesLauncher, "Alien", "Alien", alien, "1.0", 1, true, true, false, "g2"),
                new EglFoundInstall("c", CandidateSource.EpicGamesLauncher, "Castle", "Castlevania", Path.Combine(rig.Root, "gone"), "1.0", 1, false, false, false, "g3"),
                new EglFoundInstall("d", CandidateSource.EpicGamesLauncher, "NotMine", "Not mine", alien + "2", "1.0", 1, true, true, false, "g4"),
            ],
        };
        await rig.StartWarmAsync();

        var state = (await rig.Service.GetSnapshot()).Egl;
        Assert.Equal(EglInstallStatus.Importable, state.Installs.Single(i => i.Id == "a").Status);
        Assert.Equal(EglInstallStatus.Stale, state.Installs.Single(i => i.Id == "c").Status);
        Assert.Equal("Alien: Isolation", state.Installs.Single(i => i.Id == "b").Title);

        var result = await rig.Service.ImportEgl(new EglImportRequest(["a", "b", "c"], VerifyMode.Auto));
        Assert.Equal([FortniteLayout.AppName, "Alien"], result.Jobs.Select(j => j.AppName));
        Assert.All(result.Jobs, j => Assert.Equal(JobKind.Import, j.Kind));
    }
}

public class FolderAndPlanTests
{
    [Fact]
    public async Task ValidateFolder_AcceptsGameFolderOrParent_ReadsVersion_AndNamesTheApp()
    {
        using var rig = new Rig();
        var game = rig.MakeGameFolder();
        var buildDir = Path.Combine(game, "Engine", "Build");
        Directory.CreateDirectory(buildDir);
        File.WriteAllText(Path.Combine(buildDir, "Build.version"), """{"MajorVersion":5,"Changelist":4242,"BranchName":"++Fortnite+Release-33.00"}""");
        await rig.StartWarmAsync();

        var v = await rig.Service.ValidateFolder(new ValidateFolderRequest(game));
        Assert.True(v.Valid);
        Assert.Equal(FortniteLayout.AppName, v.AppName);
        Assert.Equal("Fortnite", v.Title);
        Assert.Equal("++Fortnite+Release-33.00-CL-4242", v.Version);

        v = await rig.Service.ValidateFolder(new ValidateFolderRequest(rig.Root));
        Assert.True(v.Valid);
        Assert.Equal(game, v.GamePath);

        rig.Egl.Item = new EglItem("item", "g", game, "++Fortnite+Release-33.00-CL-4242-Windows", 1, false, false, FortniteLayout.AppName);
        v = await rig.Service.ValidateFolder(new ValidateFolderRequest(game));
        Assert.Equal("++Fortnite+Release-33.00-CL-4242-Windows", v.Version);

        v = await rig.Service.ValidateFolder(new ValidateFolderRequest(Path.Combine(rig.Root, "missing")));
        Assert.False(v.Valid);
        Assert.NotNull(v.Reason);
        v = await rig.Service.ValidateFolder(new ValidateFolderRequest("relative\\path"));
        Assert.False(v.Valid);
    }

    [Fact]
    public async Task ValidateFolder_DetectsTheAppFromEglMetadata()
    {
        using var rig = new Rig();
        rig.AddGame("Alien", "Alien: Isolation");
        var alien = rig.MakeOtherGameFolder("AlienIsolation");
        rig.Egl.Declared[alien] = "Alien";
        rig.Egl.Declared[rig.MakeOtherGameFolder("Stranger")] = "SomeoneElsesGame";
        await rig.StartWarmAsync();

        var v = await rig.Service.ValidateFolder(new ValidateFolderRequest(alien));
        Assert.True(v.Valid);
        Assert.Equal("Alien", v.AppName);
        Assert.Equal("Alien: Isolation", v.Title);

        v = await rig.Service.ValidateFolder(new ValidateFolderRequest(Path.Combine(rig.Root, "Stranger")));
        Assert.False(v.Valid); // found, but not on this account
        Assert.Equal("SomeoneElsesGame", v.AppName);
    }

    [Fact]
    public async Task PlanInstall_DefaultsToLargestDrive_UsesCatalogFolderName_AndChecksSpace()
    {
        using var rig = new Rig();
        var small = Path.Combine(rig.Root, "small");
        var big = Path.Combine(rig.Root, "big");
        rig.Disk.Drives.Add(new FixedDrive(small, 10, 100));
        rig.Disk.Drives.Add(new FixedDrive(big, 900, 1000));
        await rig.StartWarmAsync();

        var plan = await rig.Service.PlanInstall(new InstallPlanRequest(FortniteLayout.AppName, null, null, null));
        Assert.Equal(Path.Combine(big, "Games"), plan.BasePath);
        Assert.Equal(Path.Combine(big, "Games", "Fortnite"), plan.GamePath);
        Assert.Equal("v2", plan.Version);
        Assert.Equal(100_000_000_000, plan.InstallSizeBytes);
        Assert.True(plan.HasEnoughSpace);
        Assert.False(plan.ExistingInstallFound);

        rig.Disk.Free = 100_000_000_000; // exactly the install size: no margin left
        plan = await rig.Service.PlanInstall(new InstallPlanRequest(FortniteLayout.AppName, rig.Root, null, null));
        Assert.False(plan.HasEnoughSpace);

        rig.MakeGameFolder();
        plan = await rig.Service.PlanInstall(new InstallPlanRequest(FortniteLayout.AppName, rig.Root, null, null));
        Assert.True(plan.ExistingInstallFound);

        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Install(new InstallRequest(FortniteLayout.AppName, rig.Root, null, null, false)));
        Assert.Equal(ErrorCode.InsufficientSpace, e.Error.Code);
    }

    [Fact]
    public async Task PlanInstall_UsesSettingsDefaultBase()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { DefaultInstallBase = rig.Root });
        await rig.StartWarmAsync();
        var plan = await rig.Service.PlanInstall(new InstallPlanRequest(FortniteLayout.AppName, null, null, null));
        Assert.Equal(rig.Root, plan.BasePath);
    }

    [Fact]
    public async Task Install_RequiresSignIn_AndQueuesJob_IdempotentPerGame()
    {
        using var rig = new Rig();
        rig.Legendary.Account = null;
        await rig.StartWarmAsync();
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Install(new InstallRequest(FortniteLayout.AppName, rig.Root, null, null, false)));
        Assert.Equal(ErrorCode.NotSignedIn, e.Error.Code);

        await rig.Service.SignIn();
        var job = await rig.Service.Install(new InstallRequest(FortniteLayout.AppName, rig.Root, null, null, false));
        Assert.Equal(JobKind.Install, job.Kind);
        Assert.Equal(FortniteLayout.AppName, rig.Jobs.Requests[0].AppName);
        Assert.Equal(rig.Root, rig.Jobs.Requests[0].TargetPath);
        Assert.Equal(job.Id, (await rig.SummaryAsync())!.ActiveJobId);
        Assert.Same(job, await rig.Service.Install(new InstallRequest(FortniteLayout.AppName, rig.Root, null, null, false))); // idempotent while queued
    }

    [Fact]
    public async Task Install_QueuesEachSelectedDlcAsItsOwnJob_AfterTheBase()
    {
        using var rig = new Rig();
        rig.AddGame("Base", "Base Game");
        rig.AddGame("DlcA", "DLC A", baseApp: "Base");
        rig.AddGame("DlcB", "DLC B", baseApp: "Base");
        await rig.StartWarmAsync();

        var plan = await rig.Service.PlanInstall(new InstallPlanRequest("Base", rig.Root, null, ["DlcA"]));
        Assert.Equal(["DlcA", "DlcB"], plan.Dlc.Select(d => d.AppName));
        Assert.Equal([true, false], plan.Dlc.Select(d => d.Selected));
        Assert.Equal(Path.Combine(rig.Root, "Base"), plan.GamePath); // no FolderName: the app name

        await rig.Service.Install(new InstallRequest("Base", rig.Root, null, ["DlcA"], false));
        Assert.Equal([("Base", JobKind.Install), ("DlcA", JobKind.Install)], rig.Jobs.Requests.Select(r => (r.AppName, r.Kind)));
        Assert.Equal("Base", rig.Jobs.Requests[1].Options!.BaseAppName);
        Assert.Null(rig.Jobs.Requests[1].TargetPath); // DLC go into the base game's folder
    }

    [Fact]
    public async Task Install_PassesComponentsAsInstallTags()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        await rig.Service.Install(new InstallRequest(FortniteLayout.AppName, rig.Root, ["Lang_fr"], null, true));
        var r = rig.Jobs.Requests.Single();
        Assert.Equal(["Lang_fr"], r.Options!.Components!);
        Assert.True(r.Priority);
    }

    [Fact]
    public void DecideVerifyPolicy()
    {
        var item = new EglItem("i", "g", "p", "v1", 1, false, false);
        Assert.False(InstallService.DecideVerify(VerifyMode.Auto, item, "v1"));
        Assert.True(InstallService.DecideVerify(VerifyMode.Auto, item, "v2"));
        Assert.True(InstallService.DecideVerify(VerifyMode.Auto, item with { NeedsValidation = true }, "v1"));
        Assert.True(InstallService.DecideVerify(VerifyMode.Auto, item with { IsIncomplete = true }, "v1"));
        Assert.True(InstallService.DecideVerify(VerifyMode.Auto, null, "v1"));
        Assert.True(InstallService.DecideVerify(VerifyMode.Auto, item, null));
        Assert.True(InstallService.DecideVerify(VerifyMode.Background, item, "v1"));
    }
}

public class UninstallAndMoveTests
{
    [Fact]
    public async Task UninstallGliderOnly_NeverTouchesFolderOrEgl()
    {
        using var rig = new Rig();
        var rec = rig.Install("v1", sharedWithEgl: true);
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never });
        await rig.StartWarmAsync();

        var job = await rig.Service.Uninstall(new UninstallRequest(FortniteLayout.AppName, UninstallMode.GliderOnly));
        Assert.Equal(UninstallMode.GliderOnly, rig.Jobs.Requests.Single().UninstallMode);
        await rig.Jobs.RunAsync(job.Id, rig.Service.Hooks);

        Assert.Null(rig.Games.Record());
        Assert.True(Directory.Exists(rec.InstallPath));
        Assert.True(FortniteLayout.LooksLikeInstall(rec.InstallPath));
        Assert.Empty(rig.Egl.WriteBacks);
        Assert.Empty(rig.Egl.LocationUpdates);
        Assert.DoesNotContain(rig.Calls.All, c => c.StartsWith("legendary.uninstall", StringComparison.Ordinal) || c.StartsWith("egl.writeBack", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UninstallRefusedWhileTheGameRuns_ButNotWhileAnotherGameRuns()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { AutoUpdate = AutoUpdatePolicy.Never });
        rig.Install();
        rig.AddGame("Other", "Other Game", installedVersion: "1.0");
        await rig.StartWarmAsync();
        rig.Processes.Add(FortniteLayout.ShippingProcessName);
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Uninstall(new UninstallRequest(FortniteLayout.AppName, UninstallMode.DeleteFiles)));
        Assert.Equal(ErrorCode.GameRunning, e.Error.Code);
        Assert.Equal(FortniteLayout.AppName, e.Error.AppName);

        var other = await rig.Service.Uninstall(new UninstallRequest("Other", UninstallMode.DeleteFiles));
        Assert.Equal("Other", other.AppName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MoveSharedInstall_UpdatesEglOrWarns(bool sync)
    {
        using var rig = new Rig();
        rig.Install("v1", sharedWithEgl: true);
        rig.Settings.Set(Settings.Default with { EglSync = sync, AutoUpdate = AutoUpdatePolicy.Never });
        await rig.StartWarmAsync();

        var dest = Path.Combine(rig.Root, "D");
        var job = await rig.Service.Move(new MoveRequest(FortniteLayout.AppName, dest));
        var newPath = rig.MakeGameFolder("Fortnite", dest);
        rig.Legendary.Installed = FakeLegendary.MakeInstalled(newPath, "v1");
        await rig.Jobs.RunAsync(job.Id, rig.Service.Hooks);

        Assert.Equal(newPath, rig.Games.Record()!.InstallPath);
        if (sync)
        {
            Assert.Equal((FortniteLayout.AppName, "guid-1", newPath), Assert.Single(rig.Egl.LocationUpdates));
            Assert.Equal("guid-1", rig.Games.Record()!.EglInstallationGuid);
        }
        else
        {
            Assert.Empty(rig.Egl.LocationUpdates);
            Assert.Null(rig.Games.Record()!.EglInstallationGuid);
            Assert.Contains(rig.Sink.Of<Notice>(RpcEvents.Notice), n => n.Level == NoticeLevel.Warning && n.Title.Contains("lose track", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task MoveValidatesDestination()
    {
        using var rig = new Rig();
        var rec = rig.Install();
        await rig.StartWarmAsync();
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Move(new MoveRequest(FortniteLayout.AppName, Path.GetDirectoryName(rec.InstallPath)!)));
        Assert.Equal(ErrorCode.InvalidRequest, e.Error.Code);
        e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Move(new MoveRequest(FortniteLayout.AppName, "not/absolute")));
        Assert.Equal(ErrorCode.InvalidRequest, e.Error.Code);
        e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Move(new MoveRequest("Unknown", rig.Root)));
        Assert.Equal(ErrorCode.NotFound, e.Error.Code);
    }
}
