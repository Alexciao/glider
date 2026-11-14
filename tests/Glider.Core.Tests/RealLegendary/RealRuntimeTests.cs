using Glider.Core.Contracts;

namespace Glider.Core.Tests.RealLegendary;

/// <summary>
/// The whole runtime (RPC → services → job queue → LegendaryCli) over the real exe on a fixture install, with the network cut off
/// (<see cref="RealLegendaryEnv.NoNetwork"/>): startup without Epic, jobs that need no login, and what survives a failed warm-up.
/// </summary>
[Collection(RealLegendaryCollection.Name)]
public sealed class RealRuntimeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [RealLegendaryFact]
    public async Task StartupWithoutEpicKeepsTheAccountTheLibraryAndLegendarysVersion()
    {
        await using var h = await RealRuntimeHarness.StartAsync();

        var snapshot = h.Snapshot;

        Assert.Equal(AuthStatus.SignedIn, snapshot.Auth.Status); // from user.json; the failed online warm-up does not sign anyone out
        Assert.Equal(RealLegendaryEnv.FakeDisplayName, snapshot.Auth.Account!.DisplayName);
        Assert.Equal(Connectivity.Offline, snapshot.Connectivity);
        Assert.StartsWith("0.21.1", snapshot.App.LegendaryVersion, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(h.Env.Config, "user.json"))); // a refused connection must not delete the credentials
        var game = h.Game()!;
        Assert.Equal(InstallStatus.Installed, game.InstallStatus);
        Assert.Equal(VerifyFixture.Version, game.InstalledVersion);
        Assert.False(game.UpdateAvailable);
        Assert.Equal(h.Folder, game.InstallPath);
    }

    [RealLegendaryFact]
    public async Task StartupSignedOutStillShowsTheLibraryAndVerifyNeedsNoAccount()
    {
        await using var h = await RealRuntimeHarness.StartAsync(signedIn: false);
        Assert.Equal(AuthStatus.SignedOut, h.Snapshot.Auth.Status);
        Assert.Equal(InstallStatus.Installed, h.Game()!.InstallStatus);

        var job = await h.Rpc.Verify(new AppRef(h.App));
        var done = await h.WaitForDone(job.Id, "verify while signed out");

        Assert.True(done.Status == JobStatus.Completed, done.Error?.Detail);
        Assert.Equal(OutcomeKind.Success, done.Outcome!.Kind);
        Assert.Contains(h.Snapshot.Queue.Jobs, j => j.Id == job.Id && j.Kind == JobKind.Verify);
    }

    [RealLegendaryFact]
    public async Task AutoLaunchGoesOfflineWhenEpicIsUnreachableAndTheGameAllowsIt()
    {
        await using var h = await RealRuntimeHarness.StartAsync();

        var result = await h.Rpc.Launch(new LaunchRequest(h.App, null, false)); // the game's setting: Auto

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.True(result.Offline);
        var started = Assert.Single(h.GameProcesses.Started);
        Assert.Equal(Path.Combine(h.Folder, "Game.exe"), started.Executable);
        Assert.Contains("-AUTH_PASSWORD=", started.Arguments); // offline: no exchange code
    }

    [RealLegendaryFact]
    public async Task VerifyJobOnACorruptedInstallEndsInAnErrorTheUserCanActOn()
    {
        // verify → repair → verify: the repair step needs Epic (login + manifest download); with no connection it must fail cleanly and say so.
        await using var h = await RealRuntimeHarness.StartAsync();
        var bad = VerifyFixture.FilePath(h.Folder, "Data/assets.bin");
        var bytes = File.ReadAllBytes(bad);
        bytes[7] ^= 0x55;
        File.WriteAllBytes(bad, bytes);

        var job = await h.Rpc.Verify(new AppRef(h.App));
        var done = await h.WaitForDone(job.Id, "verify + repair attempt");

        Assert.Equal(JobStatus.Failed, done.Status);
        Assert.NotNull(done.Error);
        Assert.Contains(done.Error.Code, new[] { ErrorCode.Network, ErrorCode.Offline, ErrorCode.Corrupted, ErrorCode.NotSignedIn });
        Assert.NotEqual(ErrorCode.LegendaryFailed, done.Error.Code); // not the generic "stopped unexpectedly"
        Assert.False(string.IsNullOrWhiteSpace(done.Error.Message));
        await h.Runtime.Service.LibraryForTests.FlushAsync();
        Assert.Equal(InstallStatus.Installed, h.Game()!.InstallStatus); // a failed repair doesn't forget or delete the install
        Assert.Equal(bytes, File.ReadAllBytes(bad)); // and does not touch the file it could not fetch a replacement for
    }

    [RealLegendaryFact]
    public async Task UninstallThatKeepsFilesForgetsTheGameAndLeavesEveryFile()
    {
        await using var h = await RealRuntimeHarness.StartAsync();

        var job = await h.Rpc.Uninstall(new UninstallRequest(h.App, UninstallMode.GliderOnly));
        var done = await h.WaitForDone(job.Id, "uninstall keeping files");

        Assert.True(done.Status == JobStatus.Completed, done.Error?.Detail);
        await h.Runtime.Service.LibraryForTests.FlushAsync();
        Assert.Equal(InstallStatus.NotInstalled, h.Game()!.InstallStatus);
        foreach (var (path, size, seed) in VerifyFixture.Files)
        {
            Assert.Equal(VerifyFixture.Pattern(size, seed), File.ReadAllBytes(VerifyFixture.FilePath(h.Folder, path)));
        }

        Assert.Empty(VerifyFixture.ReadInstalled(h.Env));
    }

    [RealLegendaryFact]
    public async Task UninstallThatDeletesFilesRemovesTheGameAndKeepsWhatItDoesNotOwn()
    {
        await using var h = await RealRuntimeHarness.StartAsync();
        var userFile = Path.Combine(h.Folder, "Saved", "slot1.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
        await File.WriteAllTextAsync(userFile, "precious", Ct);

        var job = await h.Rpc.Uninstall(new UninstallRequest(h.App, UninstallMode.DeleteFiles));
        var done = await h.WaitForDone(job.Id, "uninstall deleting files");

        Assert.True(done.Status == JobStatus.Completed, done.Error?.Detail);
        await h.Runtime.Service.LibraryForTests.FlushAsync();
        Assert.Equal(InstallStatus.NotInstalled, h.Game()!.InstallStatus);
        Assert.False(File.Exists(VerifyFixture.FilePath(h.Folder, "Game.exe")));
        Assert.False(File.Exists(VerifyFixture.FilePath(h.Folder, "Data/assets.bin")));
        Assert.True(File.Exists(userFile)); // not in the manifest: Legendary leaves it, and so does Glider
    }
}
