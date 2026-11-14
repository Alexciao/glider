using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Legendary;

namespace Glider.Core.Tests.RealLegendary;

/// <summary>
/// Launch, resolved by the real exe (<c>launch &lt;app&gt; --json --offline</c>, which only prints the command) and turned into a process
/// start by Core. Nothing is spawned: the host's process starter is a recorder.
/// </summary>
[Collection(RealLegendaryCollection.Name)]
public sealed class RealLaunchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [RealLegendaryFact]
    public async Task OfflineLaunchCommandIsBuiltFromTheExesJson()
    {
        using var env = new RealLegendaryEnv();
        var folder = VerifyFixture.Install(env);
        env.WriteFakeUser();
        using var cli = env.Cli();

        var cmd = await cli.GetLaunchCommandAsync(VerifyFixture.App, skipVersionCheck: true, "-windowed \"two words\" -x=1", offline: true, Ct);

        Assert.Equal(Path.Combine(folder, "Game.exe"), cmd.Executable);
        Assert.Equal(folder, cmd.WorkingDirectory);
        // Legendary's order: the game's own parameters (AdditionalCommandLine), the user's, then the EGL auth parameters.
        Assert.Equal(
            ["-meta_arg", "-windowed", "two words", "-x=1",
             "-AUTH_LOGIN=unused", "-AUTH_PASSWORD=", "-AUTH_TYPE=exchangecode", "-epicapp=GliderVerify", "-epicenv=Prod", "-EpicPortal",
             $"-epicusername={RealLegendaryEnv.FakeDisplayName}", $"-epicuserid={RealLegendaryEnv.FakeAccountId}", "-epiclocale=" + Locale(cmd), "-epicsandboxid=fakens"],
            cmd.Arguments);
        Assert.Empty(cmd.Environment);
        Assert.False(env.Log.Contains("AUTH_PASSWORD=")); // the exchange code position never reaches the log
    }

    [RealLegendaryFact]
    public async Task DryRunAndJsonDescribeTheSameCommand()
    {
        using var env = new RealLegendaryEnv();
        var folder = VerifyFixture.Install(env);
        env.WriteFakeUser();
        using var cli = env.Cli();

        var cmd = await cli.GetLaunchCommandAsync(VerifyFixture.App, true, "", true, Ct);
        var dry = await env.RunAsync("launch", VerifyFixture.App, "--offline", "--dry-run");

        Assert.Equal(0, dry.ExitCode);
        Assert.Contains($"Launch parameters: '{Path.Combine(folder, "Game.exe")}' -meta_arg -AUTH_LOGIN=unused", dry.Stderr, StringComparison.Ordinal);
        Assert.Contains($"Working directory: {cmd.WorkingDirectory}", dry.Stderr, StringComparison.Ordinal);
    }

    [RealLegendaryFact]
    public async Task LaunchOfAnUninstalledGameIsNotInstalled()
    {
        using var env = new RealLegendaryEnv();
        env.WriteFakeUser();
        using var cli = env.Cli();

        var e = await Assert.ThrowsAsync<GliderException>(() => cli.GetLaunchCommandAsync("NoSuchGame", true, "", true, Ct));

        Assert.Equal(ErrorCode.NotInstalled, e.Error.Code);
    }

    [RealLegendaryFact]
    public async Task OfflineLaunchWhileSignedOutSaysSignInNotAPythonTraceback()
    {
        // `launch` reads the account from user.json even with --offline (core.py:896), so without it the exe crashes with a TypeError.
        using var env = new RealLegendaryEnv();
        VerifyFixture.Install(env);
        using var cli = env.Cli();

        var e = await Assert.ThrowsAsync<GliderException>(() => cli.GetLaunchCommandAsync(VerifyFixture.App, true, "", true, Ct));

        Assert.Equal(ErrorCode.NotSignedIn, e.Error.Code);
        Assert.Equal(FixAction.SignIn, e.Error.Fix);
    }

    [RealLegendaryFact]
    public async Task LaunchWithoutTheGamesMetadataFileIsAFailureWithADetailNotAHang()
    {
        using var env = new RealLegendaryEnv();
        VerifyFixture.Install(env);
        env.WriteFakeUser();
        File.Delete(Path.Combine(env.Config, "metadata", VerifyFixture.App + ".json"));
        using var cli = env.Cli();

        var e = await Assert.ThrowsAsync<GliderException>(() => cli.GetLaunchCommandAsync(VerifyFixture.App, true, "", true, Ct));

        Assert.NotEqual(ErrorCode.NotSignedIn, e.Error.Code);
        Assert.Contains("additional_command_line", e.Error.Detail ?? "", StringComparison.Ordinal);
    }

    [RealLegendaryFact]
    public async Task OverrideExeIsAcceptedRelativeToTheInstallAndRejectedWhenItDoesNotExist()
    {
        // The flag Core must pass for "Executable override" (Game settings): relative, must exist, working directory follows the exe.
        using var env = new RealLegendaryEnv();
        var folder = VerifyFixture.Install(env);
        env.WriteFakeUser();

        var ok = await env.RunAsync("launch", VerifyFixture.App, "--offline", "--json", "--override-exe", "Data/assets.bin");
        var missing = await env.RunAsync("launch", VerifyFixture.App, "--offline", "--json", "--override-exe", "Nope/x.exe");

        using var doc = JsonDocument.Parse(ok.Stdout);
        Assert.Equal("Data/assets.bin", doc.RootElement.GetProperty("game_executable").GetString());
        Assert.Equal(Path.Combine(folder, "Data"), doc.RootElement.GetProperty("working_directory").GetString());
        Assert.Contains("Executable path is invalid", missing.Stderr, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ through the runtime (RPC → LaunchService → real exe → process start)

    [RealLegendaryFact]
    public async Task PlayStartsTheExeLegendaryResolvedWithTheUsersArguments()
    {
        await using var h = await RealRuntimeHarness.StartAsync();
        await h.Rpc.UpdateGameSettings(new GameSettingsRequest(h.App, GameSettings.Default with { LaunchArgs = "-windowed \"two words\"", LaunchMode = LaunchMode.Offline }));

        var result = await h.Rpc.Launch(new LaunchRequest(h.App, null, false));

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.True(result.Offline);
        var started = Assert.Single(h.GameProcesses.Started);
        Assert.Equal(Path.Combine(h.Folder, "Game.exe"), started.Executable);
        Assert.Equal(h.Folder, started.WorkingDirectory);
        Assert.Equal(["-meta_arg", "-windowed", "two words"], started.Arguments.Take(3));
        Assert.Equal("-epicapp=GliderVerify", started.Arguments.Single(a => a.StartsWith("-epicapp=", StringComparison.Ordinal)));
    }

    [RealLegendaryFact]
    public async Task PerGameExecutableAndWorkingDirectoryOverridesReachTheProcessStart()
    {
        await using var h = await RealRuntimeHarness.StartAsync();
        var workDir = Path.Combine(h.Env.Root, "Work");
        Directory.CreateDirectory(workDir);
        await h.Rpc.UpdateGameSettings(new GameSettingsRequest(h.App, GameSettings.Default with
        {
            ExecutableOverride = "Data/assets.bin",
            WorkingDirectory = workDir,
            LaunchMode = LaunchMode.Offline,
        }));

        await h.Rpc.Launch(new LaunchRequest(h.App, null, false));

        var started = Assert.Single(h.GameProcesses.Started);
        Assert.Equal(Path.Combine(h.Folder, "Data", "assets.bin"), started.Executable);
        Assert.Equal(workDir, started.WorkingDirectory);
    }

    [RealLegendaryFact]
    public async Task ExecutableOverrideWithoutWorkingDirectoryRunsInTheOverridesFolder()
    {
        await using var h = await RealRuntimeHarness.StartAsync();
        await h.Rpc.UpdateGameSettings(new GameSettingsRequest(h.App, GameSettings.Default with { ExecutableOverride = "Data/assets.bin", LaunchMode = LaunchMode.Offline }));

        await h.Rpc.Launch(new LaunchRequest(h.App, null, false));

        var started = Assert.Single(h.GameProcesses.Started);
        Assert.Equal(Path.Combine(h.Folder, "Data"), started.WorkingDirectory);
    }

    [RealLegendaryFact]
    public async Task ChosenLaunchOptionArgumentsComeBeforeTheUsersOwn()
    {
        await using var h = await RealRuntimeHarness.StartAsync();
        var detail = await h.Rpc.GetGameDetail(new AppRef(h.App));
        Assert.Equal([new LaunchOption("Safe mode", "-safe -nosound")], detail.LaunchOptions);
        await h.Rpc.UpdateGameSettings(new GameSettingsRequest(h.App, GameSettings.Default with { LaunchOption = "Safe mode", LaunchArgs = "-windowed", LaunchMode = LaunchMode.Offline }));

        await h.Rpc.Launch(new LaunchRequest(h.App, null, false));

        var started = Assert.Single(h.GameProcesses.Started);
        Assert.Equal(["-meta_arg", "-safe", "-nosound", "-windowed"], started.Arguments.Take(4));
    }

    [RealLegendaryFact]
    public async Task AnOverrideThatPointsAtNothingFailsTheLaunchBeforeAnythingStarts()
    {
        await using var h = await RealRuntimeHarness.StartAsync();
        await h.Rpc.UpdateGameSettings(new GameSettingsRequest(h.App, GameSettings.Default with { ExecutableOverride = "Nope/x.exe", LaunchMode = LaunchMode.Offline }));

        var e = await Assert.ThrowsAsync<GliderException>(() => h.Rpc.Launch(new LaunchRequest(h.App, null, false)));

        Assert.Equal(ErrorCode.LaunchFailed, e.Error.Code);
        Assert.Empty(h.GameProcesses.Started);
    }

    [RealLegendaryFact]
    public async Task OfflineLaunchWhileSignedOutIsRefusedWithSignIn()
    {
        await using var h = await RealRuntimeHarness.StartAsync(signedIn: false);

        var e = await Assert.ThrowsAsync<GliderException>(() => h.Rpc.Launch(new LaunchRequest(h.App, LaunchMode.Offline, false)));

        Assert.Equal(ErrorCode.NotSignedIn, e.Error.Code);
        Assert.Empty(h.GameProcesses.Started);
    }

    private static string Locale(LaunchCommand cmd) =>
        cmd.Arguments.Single(a => a.StartsWith("-epiclocale=", StringComparison.Ordinal))["-epiclocale=".Length..];
}
