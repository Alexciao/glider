using Glider.Core.Contracts;
using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Legendary;

/// <summary>`eos-overlay`, `activate --uplay` and `launch --ubisoft|--origin --json` through the real runner against the scripted fake exe.</summary>
public class LegendaryOverlayCliTests
{
    private static LegendaryCli Cli(FakeLegendaryEnv env) => new(env.Paths, env.Log, new LegendaryOptions
    {
        StallTimeout = TimeSpan.FromDays(1),
        PinnedVersion = null,
        ReadAccountFromUserJson = false,
    });

    private sealed class Collect : IProgress<LegendaryProgress>
    {
        public List<LegendaryProgress> Reports { get; } = [];

        public void Report(LegendaryProgress value) => Reports.Add(value);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string InstallJson = """writefile overlay_install.json {"app_name":"98bc04bc842e4906993fd6d6644ffb8d","version":"{VERSION}","install_path":"C:/glider-test/overlay"}""";

    [Fact]
    public async Task InstallDownloadsIntoGlidersFolderWithProgressAndRegisters()
    {
        using var env = new FakeLegendaryEnv();
        var path = Path.Combine(env.Root, "overlay");
        env.Script("eos-overlay-install", $"""
            download app=eos-overlay chunks=40 files=4 mib=20 interval=5
            err [cli] INFO: Finished downloading, setting up overlay...
            {InstallJson.Replace("{VERSION}", "1.2.3", StringComparison.Ordinal)}
            err [cli] INFO: Done.
            """);
        using var cli = Cli(env);
        var progress = new Collect();

        var outcome = await cli.InstallOverlayAsync(path, progress, Ct);

        Assert.True(outcome.Success, outcome.Error?.Detail);
        Assert.NotEmpty(progress.Reports);
        var (command, args) = env.Invocations().Single();
        Assert.Equal("eos-overlay-install", command);
        Assert.Equal(["-y", "eos-overlay", "install", "--path", path], args);
    }

    [Fact]
    public async Task AManagedOverlayIsUpdatedWithoutAPath_AndUpToDateIsSuccess()
    {
        using var env = new FakeLegendaryEnv();
        File.WriteAllText(Path.Combine(env.Config, "overlay_install.json"), """{"version":"1","install_path":"C:/x"}""");
        env.Script("eos-overlay-update", "err [cli] INFO: Preparing to start overlay install...\nerr [cli] INFO: Installed version is up to date, nothing to do.");
        using var cli = Cli(env);

        var outcome = await cli.InstallOverlayAsync(Path.Combine(env.Root, "ignored"), new Collect(), Ct);

        Assert.True(outcome.Success, outcome.Error?.Detail);
        Assert.Equal(["-y", "eos-overlay", "update"], env.Invocations().Single().Args);
    }

    [Fact]
    public async Task ADownloaderFailureIsAnErrorAndNothingIsRegistered()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("eos-overlay-install", """
            err [cli] INFO: Preparing to start overlay install...
            err [cli] WARNING: The following exception occurred while waiting for the downloader to finish: ConnectionError('Max retries exceeded'). Try restarting the process.
            """);
        using var cli = Cli(env);

        var outcome = await cli.InstallOverlayAsync(Path.Combine(env.Root, "overlay"), new Collect(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCode.Network, outcome.Error!.Code);
    }

    [Fact]
    public async Task ExitingCleanWithoutRegisteringTheOverlayIsAFailure()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("eos-overlay-install", "err [cli] INFO: Finished downloading, setting up overlay...");
        using var cli = Cli(env);

        var outcome = await cli.InstallOverlayAsync(Path.Combine(env.Root, "overlay"), new Collect(), Ct);

        Assert.False(outcome.Success);
        Assert.Equal(ErrorCode.OverlayFailed, outcome.Error!.Code);
    }

    [Fact]
    public async Task RemoveSucceedsWhenTheFilesAreGoneEvenIfClearingHklmThrew()
    {
        using var env = new FakeLegendaryEnv();
        File.WriteAllText(Path.Combine(env.Config, "overlay_install.json"), """{"version":"1","install_path":"C:/x"}""");
        env.Script("eos-overlay-remove", """
            err [cli] INFO: Deleting overlay installation...
            deletefile overlay_install.json
            err [cli] INFO: Removing registry entries...
            err Traceback (most recent call last):
            err   File "legendary\lfs\windows_helpers.py", line 60, in remove_registry_value
            err PermissionError: [WinError 5] Access is denied
            exit 1
            """);
        using var cli = Cli(env);

        var outcome = await cli.RemoveOverlayAsync(Ct);

        Assert.True(outcome.Success, outcome.Error?.Detail);
        Assert.Equal(["-y", "eos-overlay", "remove"], env.Invocations().Single().Args);
    }

    [Fact]
    public async Task RemoveThatLeavesTheInstallBehindFails()
    {
        using var env = new FakeLegendaryEnv();
        File.WriteAllText(Path.Combine(env.Config, "overlay_install.json"), """{"version":"1","install_path":"C:/x"}""");
        env.Script("eos-overlay-remove", "err [cli] ERROR: something went wrong");
        using var cli = Cli(env);

        var outcome = await cli.RemoveOverlayAsync(Ct);

        Assert.False(outcome.Success);
        Assert.NotNull(outcome.Error);
    }

    [Theory]
    [InlineData("err [cli] INFO: Redeemed all outstanding Uplay codes.", true, null)]
    [InlineData("err [cli] INFO: All of your 3 titles have already been activated on your Ubisoft account.", true, null)]
    [InlineData("err [cli] ERROR: No linked ubisoft account found! Link your accounts via your browser and try again.", false, ErrorCode.ActivationRequired)]
    [InlineData("err [cli] ERROR: Claiming \"Game\" failed with: nope\nerr [cli] INFO: Redeemed all outstanding Uplay codes.", false, ErrorCode.ActivationRequired)]
    public async Task ActivateUplayMapsLegendarysOutput(string script, bool success, ErrorCode? code)
    {
        using var env = new FakeLegendaryEnv();
        env.Script("activate", script);
        using var cli = Cli(env);

        var outcome = await cli.ActivateUplayAsync(Ct);

        Assert.Equal(success, outcome.Success);
        Assert.Equal(code, outcome.Error?.Code);
        Assert.Equal(["-y", "activate", "--uplay"], env.Invocations().Single().Args);
    }

    [Fact]
    public async Task AnUnlinkedUbisoftAccountAsksToLinkInTheBrowser()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("activate", "err [cli] ERROR: No linked ubisoft account found! Link your accounts via your browser and try again.");
        using var cli = Cli(env);

        var outcome = await cli.ActivateUplayAsync(Ct);

        Assert.Equal(FixAction.Activate, outcome.Error!.Fix);
        Assert.Contains("browser", outcome.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ThirdPartyStore.Ubisoft, "--ubisoft", false)]
    [InlineData(ThirdPartyStore.Ea, "--origin", false)]
    [InlineData(ThirdPartyStore.Ubisoft, "--ubisoft", true)]
    public async Task TheLaunchLinkComesFromJsonAndTheExchangeCodeIsNeverLogged(ThirdPartyStore store, string flag, bool offline)
    {
        using var env = new FakeLegendaryEnv();
        env.Script("launch", """out {"uri": "uplay://launch/abc?AUTH_PASSWORD=SUPERSECRETCODE&AUTH_TYPE=exchangecode"}""");
        using var cli = Cli(env);

        var uri = await cli.GetThirdPartyUriAsync("app1", store, offline, Ct);

        Assert.StartsWith("uplay://launch/abc", uri, StringComparison.Ordinal);
        var args = env.Invocations().Single().Args;
        Assert.Equal(offline ? ["launch", "app1", flag, "--json", "--offline"] : ["launch", "app1", flag, "--json"], args);
        Assert.False(env.Log.Contains("SUPERSECRETCODE"));
    }

    [Fact]
    public async Task AGameLegendaryDoesNotKnowIsAReadableLaunchError()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("launch", "err [cli] ERROR: Unknown game \"app1\", run \"legendary list-games --third-party\" to fetch data for Origin titles before using this command.");
        using var cli = Cli(env);

        var e = await Assert.ThrowsAsync<GliderException>(() => cli.GetThirdPartyUriAsync("app1", ThirdPartyStore.Ea, false, Ct));

        Assert.Equal(ErrorCode.LaunchFailed, e.Error.Code);
    }

    [Fact]
    public async Task OtherStoresHaveNoLauncherLink()
    {
        using var env = new FakeLegendaryEnv();
        using var cli = Cli(env);

        await Assert.ThrowsAsync<GliderException>(() => cli.GetThirdPartyUriAsync("app1", ThirdPartyStore.Other, false, Ct));
        Assert.Empty(env.Invocations());
    }
}
