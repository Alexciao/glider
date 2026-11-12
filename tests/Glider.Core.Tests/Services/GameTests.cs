using Glider.Core.Contracts;
using Glider.Core.Game;
using Glider.Core.Services;

namespace Glider.Core.Tests.Services;

public class AdoptionTests
{
    private static async Task<Rig> ReadyRig(string eglVersion, string latest)
    {
        var rig = new Rig();
        var game = rig.MakeGameFolder();
        rig.Egl.Item = new EglInstall("item", "guid-9", game, eglVersion, 100, false, false);
        rig.Egl.State = rig.Egl.State with
        {
            Candidates = [new InstallCandidate("egl-1", CandidateSource.EpicGamesLauncher, game, eglVersion, true, true, false, 100)],
        };
        rig.Legendary.ImportVersion = eglVersion;
        rig.Legendary.Info = FakeLegendary.MakeInfo(latest);
        rig.Settings.Set(Settings.Default with { AutoUpdate = false });
        await rig.StartWarmAsync();
        return rig;
    }

    [Fact]
    public async Task SameVersionAsLatest_SkipsVerify()
    {
        using var rig = await ReadyRig("v5", "v5");
        var result = await rig.Service.Adopt(new AdoptRequest("egl-1", null, VerifyMode.Auto));

        Assert.Null(result.VerifyJob);
        Assert.Empty(rig.Jobs.Requests);
        Assert.True(rig.Calls.Has("legendary.import"));
        var rec = rig.Records.Current!;
        Assert.True(rec.AdoptedFromEgl);
        Assert.Equal("guid-9", rec.EglInstallationGuid);
        Assert.False(rec.NeedsVerification);
        Assert.True(result.Game.Installed!.SharedWithEgl);
        Assert.Equal(InstallStatus.Installed, result.Game.InstallStatus);
    }

    [Fact]
    public async Task DifferentVersion_QueuesBackgroundVerify()
    {
        using var rig = await ReadyRig("v4", "v5");
        var result = await rig.Service.Adopt(new AdoptRequest("egl-1", null, VerifyMode.Auto));

        Assert.NotNull(result.VerifyJob);
        Assert.Equal(JobKind.Verify, Assert.Single(rig.Jobs.Requests).Kind);
        Assert.True(rig.Records.Current!.NeedsVerification);
    }

    [Theory]
    [InlineData(VerifyMode.Background)]
    [InlineData(VerifyMode.Full)]
    public async Task ExplicitVerifyModes_AlwaysVerify(VerifyMode mode)
    {
        using var rig = await ReadyRig("v5", "v5");
        var result = await rig.Service.Adopt(new AdoptRequest("egl-1", null, mode));
        Assert.Equal(JobKind.Verify, result.VerifyJob!.Kind);
    }

    [Fact]
    public async Task ManualFolderWithoutEgl_IsVerified()
    {
        using var rig = new Rig();
        var game = rig.MakeGameFolder();
        rig.Legendary.Info = FakeLegendary.MakeInfo("v5");
        rig.Legendary.ImportVersion = "v5";
        rig.Settings.Set(Settings.Default with { AutoUpdate = false });
        await rig.StartWarmAsync();

        // Parent folder is accepted.
        var result = await rig.Service.Adopt(new AdoptRequest(null, Path.GetDirectoryName(game), VerifyMode.Auto));
        Assert.NotNull(result.VerifyJob);
        Assert.False(rig.Records.Current!.AdoptedFromEgl);
        Assert.Equal(game, rig.Records.Current.InstallPath);
    }

    [Fact]
    public async Task AdoptValidatesInput()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Adopt(new AdoptRequest("a", "b", VerifyMode.Auto)));
        Assert.Equal(ErrorCode.InvalidRequest, e.Error.Code);
        e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Adopt(new AdoptRequest(null, rig.Root, VerifyMode.Auto)));
        Assert.Equal(ErrorCode.InvalidFolder, e.Error.Code);
        e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Adopt(new AdoptRequest("nope", null, VerifyMode.Auto)));
        Assert.Equal(ErrorCode.InvalidRequest, e.Error.Code);
    }

    [Fact]
    public async Task AdoptRefusesWhileEglBusy()
    {
        using var rig = await ReadyRig("v5", "v5");
        rig.Egl.Busy = true;
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Adopt(new AdoptRequest("egl-1", null, VerifyMode.Auto)));
        Assert.Equal(ErrorCode.EglBusy, e.Error.Code);
        Assert.False(rig.Calls.Has("legendary.import"));
    }
}

public class FolderAndPlanTests
{
    [Fact]
    public async Task ValidateFolder_AcceptsGameFolderOrParent_ReadsVersion()
    {
        using var rig = new Rig();
        var game = rig.MakeGameFolder();
        var buildDir = Path.Combine(game, "Engine", "Build");
        Directory.CreateDirectory(buildDir);
        File.WriteAllText(Path.Combine(buildDir, "Build.version"), """{"MajorVersion":5,"Changelist":4242,"BranchName":"++Fortnite+Release-33.00"}""");

        var v = await rig.Service.ValidateFolder(new ValidateFolderRequest(game));
        Assert.True(v.Valid);
        Assert.Equal("++Fortnite+Release-33.00-CL-4242", v.Version);

        v = await rig.Service.ValidateFolder(new ValidateFolderRequest(rig.Root));
        Assert.True(v.Valid);
        Assert.Equal(game, v.GamePath);

        rig.Egl.Item = new EglInstall("item", "g", game, "++Fortnite+Release-33.00-CL-4242-Windows", 1, false, false);
        v = await rig.Service.ValidateFolder(new ValidateFolderRequest(game));
        Assert.Equal("++Fortnite+Release-33.00-CL-4242-Windows", v.Version);

        v = await rig.Service.ValidateFolder(new ValidateFolderRequest(Path.Combine(rig.Root, "missing")));
        Assert.False(v.Valid);
        Assert.NotNull(v.Reason);
        v = await rig.Service.ValidateFolder(new ValidateFolderRequest("relative\\path"));
        Assert.False(v.Valid);
    }

    [Fact]
    public async Task PlanInstall_DefaultsToLargestDrive_AndChecksSpace()
    {
        using var rig = new Rig();
        var small = Path.Combine(rig.Root, "small");
        var big = Path.Combine(rig.Root, "big");
        rig.Disk.Drives.Add(new FixedDrive(small, 10, 100));
        rig.Disk.Drives.Add(new FixedDrive(big, 900, 1000));
        await rig.StartWarmAsync();

        var plan = await rig.Service.PlanInstall(new InstallPlanRequest(null));
        Assert.Equal(Path.Combine(big, "Games"), plan.BasePath);
        Assert.Equal(Path.Combine(big, "Games", "Fortnite"), plan.GamePath);
        Assert.Equal("v2", plan.Version);
        Assert.Equal(100_000_000_000, plan.InstallSizeBytes);
        Assert.True(plan.HasEnoughSpace);
        Assert.False(plan.ExistingInstallFound);

        rig.Disk.Free = 100_000_000_000; // exactly the install size: no margin left
        plan = await rig.Service.PlanInstall(new InstallPlanRequest(rig.Root));
        Assert.False(plan.HasEnoughSpace);

        rig.MakeGameFolder();
        plan = await rig.Service.PlanInstall(new InstallPlanRequest(rig.Root));
        Assert.True(plan.ExistingInstallFound);

        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Install(new InstallRequest(rig.Root)));
        Assert.Equal(ErrorCode.InsufficientSpace, e.Error.Code);
    }

    [Fact]
    public async Task PlanInstall_UsesSettingsDefaultBase()
    {
        using var rig = new Rig();
        rig.Settings.Set(Settings.Default with { DefaultInstallBase = rig.Root });
        await rig.StartWarmAsync();
        var plan = await rig.Service.PlanInstall(new InstallPlanRequest(null));
        Assert.Equal(rig.Root, plan.BasePath);
    }

    [Fact]
    public async Task Install_RequiresSignIn_AndQueuesJob()
    {
        using var rig = new Rig();
        rig.Legendary.Account = null;
        await rig.StartWarmAsync();
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Install(new InstallRequest(rig.Root)));
        Assert.Equal(ErrorCode.NotSignedIn, e.Error.Code);

        await rig.Service.SignIn();
        var job = await rig.Service.Install(new InstallRequest(rig.Root));
        Assert.Equal(JobKind.Install, job.Kind);
        Assert.Equal(rig.Root, rig.Jobs.Requests[0].TargetPath);
        Assert.Equal(InstallStatus.Installing, (await rig.Service.GetSnapshot()).Game.InstallStatus);
        Assert.Same(job, await rig.Service.Install(new InstallRequest(rig.Root))); // idempotent while queued
    }

    [Fact]
    public void DecideVerifyPolicy()
    {
        var item = new EglInstall("i", "g", "p", "v1", 1, false, false);
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
        rig.Settings.Set(Settings.Default with { AutoUpdate = false });
        await rig.StartWarmAsync();

        var job = await rig.Service.Uninstall(new UninstallRequest(UninstallMode.GliderOnly));
        Assert.Equal(UninstallMode.GliderOnly, rig.Jobs.Requests.Single().UninstallMode);
        await rig.Jobs.RunAsync(job.Id, rig.Service.Hooks);

        Assert.Null(rig.Records.Current);
        Assert.True(Directory.Exists(rec.InstallPath));
        Assert.True(FortniteLayout.LooksLikeInstall(rec.InstallPath));
        Assert.Empty(rig.Egl.WriteBacks);
        Assert.Empty(rig.Egl.LocationUpdates);
        Assert.DoesNotContain(rig.Calls.All, c => c.StartsWith("legendary.uninstall", StringComparison.Ordinal) || c == "egl.writeBack");
    }

    [Fact]
    public async Task UninstallRefusedWhileGameRunning()
    {
        using var rig = new Rig();
        rig.Install();
        rig.Processes.Add(FortniteLayout.ShippingProcessName);
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Uninstall(new UninstallRequest(UninstallMode.DeleteFiles)));
        Assert.Equal(ErrorCode.GameRunning, e.Error.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MoveSharedInstall_UpdatesEglOrWarns(bool sync)
    {
        using var rig = new Rig();
        rig.Install("v1", sharedWithEgl: true);
        rig.Settings.Set(Settings.Default with { EglSync = sync, AutoUpdate = false });
        await rig.StartWarmAsync();

        var dest = Path.Combine(rig.Root, "D");
        var job = await rig.Service.Move(new MoveRequest(dest));
        var newPath = rig.MakeGameFolder("Fortnite", dest);
        rig.Legendary.Installed = FakeLegendary.MakeInstalled(newPath, "v1");
        await rig.Jobs.RunAsync(job.Id, rig.Service.Hooks);

        Assert.Equal(newPath, rig.Records.Current!.InstallPath);
        if (sync)
        {
            Assert.Equal(("guid-1", newPath), Assert.Single(rig.Egl.LocationUpdates));
            Assert.Equal("guid-1", rig.Records.Current.EglInstallationGuid);
        }
        else
        {
            Assert.Empty(rig.Egl.LocationUpdates);
            Assert.Null(rig.Records.Current.EglInstallationGuid);
            Assert.Contains(rig.Sink.Of<Notice>(RpcEvents.Notice), n => n.Level == NoticeLevel.Warning && n.Title.Contains("lose track", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task MoveValidatesDestination()
    {
        using var rig = new Rig();
        var rec = rig.Install();
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Move(new MoveRequest(Path.GetDirectoryName(rec.InstallPath)!)));
        Assert.Equal(ErrorCode.InvalidRequest, e.Error.Code);
        e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.Move(new MoveRequest("not/absolute")));
        Assert.Equal(ErrorCode.InvalidRequest, e.Error.Code);
    }
}
