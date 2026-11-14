using Glider.Core.Contracts;
using Glider.Core.Game;

namespace Glider.Core.Tests.Services;

public class PrerequisiteTests
{
    private const string Redist = @"Engine\Extras\Redist\en-us\PrereqSetup.exe";
    private static readonly string[] SharedIds = ["{A-1}"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>An installed generic game whose manifest asks for <see cref="Redist"/> (the installer file exists unless told otherwise).</summary>
    private static async Task<(Rig Rig, string Dir)> ReadyAsync(string app = "Indie", string[]? ids = null, bool withInstaller = true, string relative = Redist, bool start = true)
    {
        var rig = new Rig();
        rig.AddGame(app, app + " Game", installedVersion: "1.0");
        var game = rig.Legendary.Get(app)!;
        if (withInstaller)
        {
            var exe = Path.Combine(game.InstallPath, relative.Replace('\\', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "MZ");
        }

        rig.Legendary.Set(app, game with { Prerequisites = new PrerequisiteInfo("Game components", relative, "/quiet /norestart", ids ?? SharedIds) });
        if (start)
        {
            await rig.StartWarmAsync();
        }

        return (rig, game.InstallPath);
    }

    private static Task<LaunchResult> Launch(Rig rig, string app = "Indie") => rig.Service.Launch(new LaunchRequest(app, null, false));

    /// <summary>Ends the game's session (process gone, grace period over) so the next Play starts clean.</summary>
    private static async Task EndSessionAsync(Rig rig, string app = "Indie")
    {
        rig.Service.LaunchServiceForTests.Sessions.Tick(); // the game is seen running first
        foreach (var p in rig.Processes.Snapshot())
        {
            rig.Processes.Exit(p.Id);
        }

        rig.Service.LaunchServiceForTests.Sessions.Tick();
        rig.Time.Advance(TimeSpan.FromSeconds(6));
        rig.Service.LaunchServiceForTests.Sessions.Tick();
        Assert.Equal(RunState.Idle, rig.Service.Hub.State.RunStateOf(app));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task FirstLaunchRunsThePrerequisiteElevatedOnce_ThenRemembersIt()
    {
        var (rig, dir) = await ReadyAsync();
        using var _ = rig;
        var states = new List<RunState>();
        rig.Service.Hub.StateChanged += s =>
        {
            lock (states)
            {
                var r = s.RunStateOf("Indie");
                if (states.Count == 0 || states[^1] != r)
                {
                    states.Add(r);
                }
            }
        };

        var result = await Launch(rig);

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        var (exe, args) = Assert.Single(rig.Host.Elevated);
        Assert.Equal(Path.Combine(dir, Redist.Replace('\\', Path.DirectorySeparatorChar)), exe);
        Assert.Equal("/quiet /norestart", args); // verbatim, never split
        Assert.Contains(RunState.InstallingPrerequisites, states);
        Assert.True(states.IndexOf(RunState.InstallingPrerequisites) < states.IndexOf(RunState.Launching));
        Assert.Single(rig.Processes.Started);
        Assert.Contains("{A-1}", await File.ReadAllTextAsync(Path.Combine(rig.Paths.State, "prereqs.json"), Ct), StringComparison.Ordinal);

        await EndSessionAsync(rig);
        await Launch(rig);
        Assert.Single(rig.Host.Elevated); // not asked again
    }

    [Fact]
    public async Task ASharedIdRunsOnceAcrossGames()
    {
        var (rig, _) = await ReadyAsync(start: false);
        using var _ = rig;
        rig.AddGame("Other", "Other Game", installedVersion: "1.0");
        var other = rig.Legendary.Get("Other")!;
        File.WriteAllText(Path.Combine(other.InstallPath, "redist.exe"), "MZ");
        rig.Legendary.Set("Other", other with { Prerequisites = new PrerequisiteInfo("VC++", "redist.exe", "/q", SharedIds) });
        await rig.StartWarmAsync();

        await Launch(rig, "Indie");
        await Launch(rig, "Other");

        Assert.Single(rig.Host.Elevated);
        Assert.Equal(2, rig.Processes.Started.Count);
        Assert.True((await rig.Service.GetGameDetail(new AppRef("Other"))).Prerequisites.Single().Installed);
    }

    [Theory]
    [InlineData(3010)]
    [InlineData(1638)]
    public async Task RebootAndAlreadyInstalledCodesCountAsDone(int code)
    {
        var (rig, _) = await ReadyAsync();
        using var _ = rig;
        rig.Host.ElevatedResult = code;

        Assert.Equal(LaunchOutcome.Started, (await Launch(rig)).Outcome);
        Assert.True((await rig.Service.GetGameDetail(new AppRef("Indie"))).Prerequisites.Single().Installed);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(null)]
    public async Task AFailedOrDeclinedInstallerStopsTheLaunchWithARetryableError(int? code)
    {
        var (rig, _) = await ReadyAsync();
        using var _ = rig;
        rig.Host.ElevatedResult = code;

        var e = await Assert.ThrowsAsync<GliderException>(() => Launch(rig));

        Assert.Equal(ErrorCode.PrerequisiteFailed, e.Error.Code);
        Assert.Equal(FixAction.Retry, e.Error.Fix);
        Assert.Equal("Indie", e.Error.AppName);
        Assert.Contains("play anyway", e.Error.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Processes.Started);
        Assert.Equal(RunState.Idle, rig.Service.Hub.State.RunStateOf("Indie"));
        Assert.False((await rig.Service.GetGameDetail(new AppRef("Indie"))).Prerequisites.Single().Installed);
    }

    [Fact]
    public async Task PlayAnywayNeverBlocksTheGameAndStopsAskingUntilInstalledOnPurpose()
    {
        var (rig, _) = await ReadyAsync();
        using var _ = rig;
        rig.Host.ElevatedResult = 5;
        await Assert.ThrowsAsync<GliderException>(() => Launch(rig));

        await rig.Service.SkipPrerequisites(new AppRef("Indie"));
        Assert.Equal(LaunchOutcome.Started, (await Launch(rig)).Outcome);
        Assert.Single(rig.Host.Elevated); // only the failed attempt

        // The explicit button runs it again, and success clears the skip.
        await EndSessionAsync(rig);
        rig.Host.ElevatedResult = 0;
        await rig.Service.InstallPrerequisites(new AppRef("Indie"));
        Assert.Equal(2, rig.Host.Elevated.Count);
        Assert.True((await rig.Service.GetGameDetail(new AppRef("Indie"))).Prerequisites.Single().Installed);
        Assert.Equal(RunState.Idle, rig.Service.Hub.State.RunStateOf("Indie"));
    }

    [Fact]
    public async Task AnUnsignedInstallerIsNeverRunElevated()
    {
        var (rig, _) = await ReadyAsync();
        using var _ = rig;
        rig.PrerequisiteCheck.Reason = "it has no valid digital signature";

        var e = await Assert.ThrowsAsync<GliderException>(() => Launch(rig));

        Assert.Equal(ErrorCode.PrerequisiteFailed, e.Error.Code);
        Assert.Contains("signature", e.Error.Detail, StringComparison.Ordinal);
        Assert.Empty(rig.Host.Elevated);
        Assert.EndsWith("PrereqSetup.exe", Assert.Single(rig.PrerequisiteCheck.Checked), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APathThatEscapesTheGameFolderIsNeverRun()
    {
        var (rig, _) = await ReadyAsync(withInstaller: false, relative: @"..\evil.exe");
        using var _ = rig;

        var e = await Assert.ThrowsAsync<GliderException>(() => Launch(rig));

        Assert.Equal(ErrorCode.PrerequisiteFailed, e.Error.Code);
        Assert.Empty(rig.Host.Elevated);
        Assert.Empty(rig.PrerequisiteCheck.Checked);
    }

    [Fact]
    public async Task AMissingInstallerIsAnErrorNotAHang()
    {
        var (rig, _) = await ReadyAsync(withInstaller: false);
        using var _ = rig;

        var e = await Assert.ThrowsAsync<GliderException>(() => Launch(rig));

        Assert.Equal(ErrorCode.PrerequisiteFailed, e.Error.Code);
        Assert.Contains("Repair", e.Error.Detail, StringComparison.Ordinal);
        Assert.Empty(rig.Host.Elevated);
    }

    [Fact]
    public async Task AGameWithoutPrerequisitesLaunchesWithoutAnyPrompt()
    {
        using var rig = new Rig();
        rig.AddGame("Indie", "Indie Game", installedVersion: "1.0");
        await rig.StartWarmAsync();

        await Launch(rig);

        Assert.Empty(rig.Host.Elevated);
        Assert.Empty((await rig.Service.GetGameDetail(new AppRef("Indie"))).Prerequisites);
        await rig.Service.InstallPrerequisites(new AppRef("Indie")); // nothing to do, not an error
    }

    [Fact]
    public async Task InstallPrerequisitesRefusesWhileTheGameRuns_AndForUnknownGames()
    {
        var (rig, _) = await ReadyAsync();
        using var _ = rig;
        await Launch(rig);

        var running = await Assert.ThrowsAsync<GliderException>(() => rig.Service.InstallPrerequisites(new AppRef("Indie")));
        Assert.Equal(ErrorCode.GameRunning, running.Error.Code);
        var unknown = await Assert.ThrowsAsync<GliderException>(() => rig.Service.InstallPrerequisites(new AppRef("Nope")));
        Assert.Equal(ErrorCode.NotFound, unknown.Error.Code);
    }

    [Fact]
    public async Task ManifestWithoutIdsIsRememberedPerGame()
    {
        var (rig, _) = await ReadyAsync(ids: []);
        using var _ = rig;

        await Launch(rig);
        var detail = await rig.Service.GetGameDetail(new AppRef("Indie"));

        Assert.True(detail.Prerequisites.Single().Installed);
        Assert.StartsWith("app:Indie:", detail.Prerequisites.Single().Id, StringComparison.Ordinal);
    }
}
