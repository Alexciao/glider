using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Legendary;

public class ErrorClassifierTests
{
    private const double GiB = 1024.0 * 1024 * 1024;

    public static TheoryData<string, string?, int, string, ErrorCode?> Cases => new()
    {
        // ---- real captures (legendary 0.21.1, signed out) ----
        { "install-notloggedin.stderr.txt", null, 1, nameof(LegendaryCommandKind.Install), ErrorCode.NotSignedIn },
        { "list-files-notloggedin.stderr.txt", null, 1, nameof(LegendaryCommandKind.Info), ErrorCode.NotSignedIn },
        { "status-network-failure.stderr.txt", null, 1, nameof(LegendaryCommandKind.Status), ErrorCode.Network },
        { "launch-notinstalled.stderr.txt", null, 1, nameof(LegendaryCommandKind.Launch), ErrorCode.NotInstalled },
        { "verify-notinstalled.stderr.txt", null, 0, nameof(LegendaryCommandKind.Verify), ErrorCode.NotInstalled },
        { "uninstall-notinstalled.stderr.txt", null, 0, nameof(LegendaryCommandKind.Uninstall), null },
        { "move-notinstalled.stderr.txt", null, 0, nameof(LegendaryCommandKind.Move), ErrorCode.NotInstalled },
        { "import-badpath.stderr.txt", null, 0, nameof(LegendaryCommandKind.Import), ErrorCode.InvalidFolder },
        { "auth-delete.stderr.txt", null, 0, nameof(LegendaryCommandKind.AuthDelete), null },
        { "invalid-command.stderr.txt", null, 2, nameof(LegendaryCommandKind.Version), ErrorCode.LegendaryFailed },
        // ---- synthetic renders of the verbatim format strings ----
        { "synthetic-install-fresh.stderr.txt", "synthetic-install-fresh.stdout.txt", 0, nameof(LegendaryCommandKind.Install), null },
        { "synthetic-update-patch.stderr.txt", null, 0, nameof(LegendaryCommandKind.Update), null },
        { "synthetic-update-uptodate.stderr.txt", null, 0, nameof(LegendaryCommandKind.Update), null },
        { "synthetic-repair.stderr.txt", "synthetic-repair.stdout.txt", 0, nameof(LegendaryCommandKind.Repair), null },
        { "synthetic-install-nospace.stderr.txt", "synthetic-install-nospace.stdout.txt", 1, nameof(LegendaryCommandKind.Install), ErrorCode.InsufficientSpace },
        { "synthetic-install-diskfull.stderr.txt", null, 0, nameof(LegendaryCommandKind.Install), ErrorCode.InsufficientSpace },
        { "synthetic-install-lockedfile.stderr.txt", null, 0, nameof(LegendaryCommandKind.Update), ErrorCode.FilesLocked },
        { "synthetic-install-memoryerror.stderr.txt", null, 1, nameof(LegendaryCommandKind.Install), ErrorCode.LegendaryFailed },
        { "synthetic-install-lock.stderr.txt", null, 0, nameof(LegendaryCommandKind.Install), ErrorCode.LegendaryLocked },
        { "synthetic-install-login-expired.stderr.txt", null, 1, nameof(LegendaryCommandKind.Install), ErrorCode.AuthExpired },
        { "synthetic-install-downloader-exception.stderr.txt", null, 0, nameof(LegendaryCommandKind.Install), ErrorCode.LegendaryFailed },
        { "synthetic-install-cdn.stderr.txt", null, 1, nameof(LegendaryCommandKind.Install), ErrorCode.CdnUnavailable },
        { "synthetic-install-permission.stderr.txt", null, 1, nameof(LegendaryCommandKind.Install), ErrorCode.PermissionDenied },
        { "synthetic-launch-login-network.stderr.txt", null, 1, nameof(LegendaryCommandKind.Launch), ErrorCode.Network },
        { "synthetic-launch-outofdate.stderr.txt", null, 1, nameof(LegendaryCommandKind.Launch), ErrorCode.LaunchFailed },
        { "synthetic-auth-success.stderr.txt", null, 0, nameof(LegendaryCommandKind.AuthCode), null },
        { "synthetic-auth-still-valid.stderr.txt", null, 0, nameof(LegendaryCommandKind.AuthCode), null },
        { "synthetic-auth-failed.stderr.txt", null, 0, nameof(LegendaryCommandKind.AuthCode), ErrorCode.AuthFailed },
        { "synthetic-auth-corrective.stderr.txt", null, 0, nameof(LegendaryCommandKind.AuthCode), ErrorCode.AuthFailed },
        { "synthetic-status-expired.stderr.txt", null, 1, nameof(LegendaryCommandKind.Status), ErrorCode.AuthExpired },
        { "synthetic-verify-ok.stderr.txt", "synthetic-verify-ok.stdout.txt", 0, nameof(LegendaryCommandKind.Verify), null },
        { "synthetic-verify-failed.stderr.txt", "synthetic-verify-failed.stdout.txt", 0, nameof(LegendaryCommandKind.Verify), ErrorCode.Corrupted },
        { "synthetic-verify-locked.stderr.txt", "synthetic-verify-locked.stdout.txt", 0, nameof(LegendaryCommandKind.Verify), ErrorCode.FilesLocked },
        { "synthetic-import-egl.stderr.txt", null, 0, nameof(LegendaryCommandKind.Import), null },
        { "synthetic-import-nofiles.stderr.txt", null, 0, nameof(LegendaryCommandKind.Import), ErrorCode.InvalidFolder },
        { "synthetic-import-noexe.stderr.txt", null, 0, nameof(LegendaryCommandKind.Import), ErrorCode.InvalidFolder },
        { "synthetic-import-already.stderr.txt", null, 0, nameof(LegendaryCommandKind.Import), ErrorCode.AlreadyInstalled },
        { "synthetic-move-ok.stderr.txt", null, 0, nameof(LegendaryCommandKind.Move), null },
        { "synthetic-move-crossdrive.stderr.txt", null, 0, nameof(LegendaryCommandKind.Move), ErrorCode.LegendaryFailed },
        { "synthetic-uninstall-ok.stderr.txt", null, 0, nameof(LegendaryCommandKind.Uninstall), null },
        { "synthetic-uninstall-locked.stderr.txt", null, 0, nameof(LegendaryCommandKind.Uninstall), ErrorCode.FilesLocked },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ClassifiesFixture(string stderr, string? stdout, int exitCode, string kind, ErrorCode? expected)
    {
        var (run, _) = Fixture.Replay(stderr, stdout, exitCode);
        var error = ErrorClassifier.Classify(Enum.Parse<LegendaryCommandKind>(kind), run, freeBytes: 1_000_000);
        Assert.Equal(expected, error?.Code);
        if (error is not null)
        {
            Assert.False(string.IsNullOrWhiteSpace(error.Title));
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
        }
    }

    [Fact]
    public void ParsesRequiredAndFreeSpace()
    {
        var (run, _) = Fixture.Replay("synthetic-install-nospace.stderr.txt", "synthetic-install-nospace.stdout.txt", 1);
        var error = ErrorClassifier.Classify(LegendaryCommandKind.Install, run)!;
        Assert.Equal((long)(48.28 * GiB), run.Facts.RequiredBytesFromCheck);
        Assert.Equal((long)(12.34 * GiB), run.Facts.FreeBytesFromCheck);
        Assert.Contains("48.3 GB", error.Message);
        Assert.Contains("12.3 GB", error.Message);
        Assert.Equal(FixAction.FreeSpace, error.Fix);
    }

    [Fact]
    public void ExitZeroWithoutASuccessMarkerIsAFailure()
    {
        var (run, _) = Fixture.Replay("synthetic-install-resume.stderr.txt"); // killed mid-download: no "Finished"
        Assert.Equal(ErrorCode.LegendaryFailed, ErrorClassifier.Classify(LegendaryCommandKind.Install, run)?.Code);
    }

    [Fact]
    public void StallIsANetworkErrorThatKeepsProgress()
    {
        var (run, progress) = Fixture.Replay("synthetic-install-stall.stderr.txt", stalled: true);
        var error = ErrorClassifier.Classify(LegendaryCommandKind.Install, run)!;
        Assert.Equal(ErrorCode.Network, error.Code);
        Assert.Contains("Progress is kept", error.Message);
        Assert.True(run.Facts.ChunkRetries > 0);
        Assert.True(run.Facts.NetworkException);
        // identical blocks while stalled: the runner's liveness check sees no movement
        Assert.Equal(progress[^1].ProcessedChunks, progress[^3].ProcessedChunks);
    }

    [Fact]
    public void FactsCaptureDetails()
    {
        var auth = Fixture.Replay("synthetic-auth-success.stderr.txt").Run.Facts;
        Assert.Equal("Åsa 日本", auth.SignedInAs); // cp1252 + \u escapes decoded

        var memory = Fixture.Replay("synthetic-install-memoryerror.stderr.txt").Run.Facts;
        Assert.Equal(2182, memory.SharedMemorySuggestedMiB);
        Assert.True(memory.Traceback && memory.PyInstallerCrash);

        var sdl = Fixture.Replay("synthetic-install-sdl-failed.stderr.txt").Run.Facts;
        Assert.True(sdl.SdlDownloadFailed);

        var resume = Fixture.Replay("synthetic-install-resume.stderr.txt").Run.Facts;
        Assert.True(resume.ResumeFound);
        Assert.Equal(1210, resume.ResumeSkippedFiles);
        Assert.Equal((long)(18310.10 * 1024 * 1024), resume.DownloadSizeBytes!.Value, tolerance: 1);
        Assert.Equal((long)(19792.06 * 1024 * 1024), resume.UnchangedBytes!.Value, tolerance: 1);
        Assert.True(resume.SummaryComplete);

        var verify = Fixture.Replay("synthetic-verify-failed.stderr.txt").Run.Facts;
        Assert.Equal((1, 2), (verify.VerifyCorrupted, verify.VerifyMissing));

        var corrective = ErrorClassifier.Classify(LegendaryCommandKind.AuthCode, Fixture.Replay("synthetic-auth-corrective.stderr.txt").Run)!;
        Assert.Contains("https://www.epicgames.com/id/login/correction", corrective.Detail);

        var network = Fixture.Replay("status-network-failure.stderr.txt").Run.Facts;
        Assert.True(network.VersionCheckCrash);
        Assert.StartsWith("requests.exceptions.ProxyError", network.ExceptionLine);
    }

    [Fact]
    public void ACodeErrorInsideTheVersionCheckIsNotCalledANetworkProblem()
    {
        // 0.21.1-glider.1: Legendary's update check ran int('1-glider') and crashed; Glider showed "Can't reach Epic".
        var run = Fixture.Replay("status-version-parse-crash.stderr.txt").Run;
        Assert.True(run.Facts.VersionCheckCrash);
        var error = ErrorClassifier.Classify(LegendaryCommandKind.Status, run)!;
        Assert.Equal(ErrorCode.LegendaryFailed, error.Code);
        Assert.Contains("1-glider", error.Detail);

        var network = ErrorClassifier.Classify(LegendaryCommandKind.Status, Fixture.Replay("status-network-failure.stderr.txt").Run)!;
        Assert.Equal(ErrorCode.Network, network.Code); // a real connection failure in the same place still is one
    }

    [Fact]
    public void WriteFailuresBeatTheSuccessMarker()
    {
        var run = Fixture.Replay("synthetic-install-diskfull.stderr.txt").Run;
        Assert.True(run.Facts.InstallFinished);
        Assert.Equal(2, run.Facts.WriteFailures);
        var error = ErrorClassifier.Classify(LegendaryCommandKind.Install, run, freeBytes: 0)!;
        Assert.Equal(ErrorCode.InsufficientSpace, error.Code);
        Assert.Contains("pakchunk10", error.Detail);
    }
}

public class LegendaryCliParsingTests
{
    [Fact]
    public void BuildsTheLaunchCommandInLegendaryOrder()
    {
        using var doc = JsonDocument.Parse(Fixture.Lines("synthetic-launch-fortnite.stdout.txt", stdout: true).Single());
        var cmd = LegendaryCli.ParseLaunch(doc.RootElement, CommandLineSplitter.Split("-d3d12 \"-name=a b\""));

        Assert.Equal(Path.GetFullPath(Path.Combine(@"D:\Games\Fortnite", "FortniteGame", "Binaries", "Win64", "FortniteLauncher.exe")), cmd.Executable);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(Path.GetDirectoryName(cmd.Executable), cmd.WorkingDirectory);
        }

        Assert.Equal("-d3d12", cmd.Arguments[0]);
        Assert.Equal("-name=a b", cmd.Arguments[1]);
        Assert.Equal("-AUTH_LOGIN=unused", cmd.Arguments[2]);
        Assert.Contains("-epicusername=Åsa 日本", cmd.Arguments);
        Assert.Equal("-epicsandboxid=fn", cmd.Arguments[^1]);
    }

    [Fact]
    public void SizesFollowTheChosenInstallTags()
    {
        using var doc = JsonDocument.Parse(Fixture.Lines("synthetic-info-fortnite.stdout.txt", stdout: true).Single());
        var manifest = doc.RootElement.GetProperty("manifest");
        var (fullDl, fullDisk) = LegendaryCli.Sizes(manifest, null);
        Assert.Equal(52_000_000_000, fullDisk);

        var tags = new HashSet<string>(["", "Startup", "FrontEnd", "FortniteBR"]);
        var (dl, disk) = LegendaryCli.Sizes(manifest, tags);
        Assert.Equal(39_000_000_000, disk);
        Assert.True(dl < fullDl);
    }

    [Fact]
    public void InstallArgumentsMatchTheUnattendedRecipe()
    {
        var games = Path.Combine(Path.GetTempPath(), "Games");
        Assert.Equal(
            ["-y", "install", "Fortnite", "--base-path", games, "--skip-sdl", "--skip-dlcs",
             "--max-workers", "12", "--max-shared-memory", "3072", "--preferred-cdn", "fastly"],
            LegendaryCli.InstallArguments(new InstallArgs("Fortnite", InstallMode.Install, games + Path.DirectorySeparatorChar, 12, 3072, " fastly "), installed: false, 3072, false));

        Assert.Equal(
            ["-y", "update", "Fortnite", "--skip-sdl", "--skip-dlcs", "--update-only", "--max-workers", "8"],
            LegendaryCli.InstallArguments(new InstallArgs("Fortnite", InstallMode.Update, @"D:\Games", 8, 0, null), installed: true, 0, false));

        Assert.Equal(
            ["-y", "repair", "Fortnite", "--skip-sdl", "--skip-dlcs", "--repair-and-update"],
            LegendaryCli.InstallArguments(new InstallArgs("Fortnite", InstallMode.Repair, null, 0, 0, ""), installed: true, 0, true));
    }
}
