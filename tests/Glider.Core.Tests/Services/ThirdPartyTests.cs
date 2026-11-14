using Glider.Core.Contracts;
using Glider.Core.Game;
using Glider.Core.Tests;

namespace Glider.Core.Tests.Services;

public class ThirdPartyTests
{
    private const string Ubi = "UbiManaged";
    private const string Ea = "EaManaged";
    private const string Linked = "UbiLinked";
    private static readonly Dictionary<string, string> UbiAttr = new() { ["ThirdPartyManagedApp"] = "ubisoftconnect" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Rig> ReadyAsync(bool signedIn = true)
    {
        var rig = new Rig();
        rig.Library.Add(Ubi, LibraryFixture.Item("Ubi Game", "ns-u", "item-u", attributes: UbiAttr), withAsset: false);
        rig.Library.Add(Ea, LibraryFixture.Item("EA Game", "ns-e", "item-e", attributes: new() { ["ThirdPartyManagedApp"] = "Origin" }), withAsset: false);
        rig.Library.Add("Gog", LibraryFixture.Item("GOG Game", "ns-g", "item-g", attributes: new() { ["ThirdPartyManagedApp"] = "GOG" }), withAsset: false);
        // Installs from Epic, but Ubisoft must know the account owns it.
        rig.Library.Add(Linked, LibraryFixture.Item("Linked Game", "ns-l", "item-l", attributes: new() { ["partnerLinkType"] = "ubisoft" }), "1.0");
        rig.SaveLibrary();
        var path = rig.MakeOtherGameFolder(Linked);
        rig.Legendary.Set(Linked, FakeLegendary.MakeInstalled(path, "1.0", Linked, "Game.exe"));
        if (!signedIn)
        {
            rig.Legendary.Account = null;
        }

        await rig.StartWarmAsync();
        return rig;
    }

    private static Task<LaunchResult> Launch(Rig rig, string app, LaunchMode? mode = null) => rig.Service.Launch(new LaunchRequest(app, mode, false));

    [Fact]
    public async Task TheCatalogMapsEachKindOfThirdPartyGame()
    {
        using var rig = await ReadyAsync();

        var ubi = await rig.SummaryAsync(Ubi);
        Assert.Equal((ThirdPartyStore.Ubisoft, InstallStatus.NotInstallable), (ubi!.ThirdParty, ubi.InstallStatus));
        var ea = await rig.SummaryAsync(Ea);
        Assert.Equal((ThirdPartyStore.Ea, "EA app"), (ea!.ThirdParty, ea.ThirdPartyName));
        var gog = await rig.SummaryAsync("Gog");
        Assert.Equal(ThirdPartyStore.Other, gog!.ThirdParty);
        var linked = await rig.SummaryAsync(Linked);
        Assert.Equal((ThirdPartyStore.Ubisoft, InstallStatus.Installed), (linked!.ThirdParty, linked.InstallStatus));
    }

    [Fact]
    public async Task APlayBeforeActivationAsksToActivate_AndOpensNothing()
    {
        using var rig = await ReadyAsync();

        var result = await Launch(rig, Ubi);

        Assert.Equal(LaunchOutcome.NeedsActivation, result.Outcome);
        Assert.Contains("Ubisoft Connect", result.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Host.ThirdPartyUris);
        Assert.DoesNotContain("legendary.thirdPartyUri:UbiManaged:Ubisoft", rig.Calls.All);
        var info = (await rig.Service.GetGameDetail(new AppRef(Ubi))).ThirdPartyInfo!;
        Assert.Equal(new ThirdPartyStatus(ThirdPartyStore.Ubisoft, "Ubisoft Connect", true, false, true, true), info);
    }

    [Fact]
    public async Task ActivateThenPlayHandsTheLinkToUbisoftConnect_AsAnUntrackedSessionThatEnds()
    {
        using var rig = await ReadyAsync();
        await rig.Service.ActivateGame(new AppRef(Ubi));
        Assert.Contains("legendary.activateUplay", rig.Calls.All);
        Assert.Contains("ubisoft/acc-1", await File.ReadAllTextAsync(Path.Combine(rig.Paths.State, "thirdparty.json"), Ct), StringComparison.Ordinal);

        var result = await Launch(rig, Ubi);

        Assert.Equal(LaunchOutcome.ThirdPartyLauncher, result.Outcome);
        Assert.Equal(rig.Legendary.ThirdPartyUri, Assert.Single(rig.Host.ThirdPartyUris));
        Assert.False(rig.Legendary.LastThirdPartyOffline);
        var session = rig.Service.Hub.State.Sessions[Ubi];
        Assert.True(session.Untracked);
        Assert.Equal(RunState.Launching, session.State);
        Assert.Empty(rig.Processes.Started); // Glider spawns nothing: the other launcher owns the game
        var again = await Assert.ThrowsAsync<GliderException>(() => Launch(rig, Ubi));
        Assert.Equal(ErrorCode.GameRunning, again.Error.Code); // a double click does not open it twice

        rig.Time.Advance(TimeSpan.FromSeconds(9));
        await WaitUntilAsync(() => !rig.Service.Hub.State.Sessions.ContainsKey(Ubi));
        Assert.Single(rig.Host.ThirdPartyUris);
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("AUTH_PASSWORD", StringComparison.Ordinal)); // the link holds an exchange code
        Assert.True((await rig.Service.GetGameDetail(new AppRef(Ubi))).ThirdPartyInfo!.Activated);
    }

    [Fact]
    public async Task ActivationIsKeptPerAccount()
    {
        using var rig = await ReadyAsync();
        await rig.Service.ActivateGame(new AppRef(Ubi));
        Assert.True((await rig.Service.GetGameDetail(new AppRef(Linked))).ThirdPartyInfo!.Activated); // one account link covers every Ubisoft game

        rig.Service.Hub.Mutate(s => s with { Auth = new AuthState(AuthStatus.SignedIn, new Account("acc-2", "Bob", null)) });

        Assert.False((await rig.Service.GetGameDetail(new AppRef(Ubi))).ThirdPartyInfo!.Activated);
        Assert.Equal(LaunchOutcome.NeedsActivation, (await Launch(rig, Ubi)).Outcome);
    }

    [Fact]
    public async Task ALauncherThatIsNotInstalledIsNamedInTheError()
    {
        using var rig = await ReadyAsync();
        rig.Launchers.Installed.Clear();

        var e = await Assert.ThrowsAsync<GliderException>(() => Launch(rig, Ubi));

        Assert.Equal(ErrorCode.ThirdPartyRequired, e.Error.Code);
        Assert.Contains("Ubisoft Connect isn't installed", e.Error.Title, StringComparison.Ordinal);
        Assert.Contains("Ubi Game", e.Error.Message, StringComparison.Ordinal);
        Assert.Equal(Ubi, e.Error.AppName);
        Assert.Empty(rig.Host.ThirdPartyUris);
        Assert.False((await rig.Service.GetGameDetail(new AppRef(Ubi))).ThirdPartyInfo!.LauncherInstalled);
    }

    [Fact]
    public async Task EaGamesAreNotGated_AndActivateOpensTheEaApp()
    {
        using var rig = await ReadyAsync();
        rig.Legendary.ThirdPartyUri = "link2ea://launchgame/EaManaged?AUTH_PASSWORD=secret";

        var result = await Launch(rig, Ea);

        Assert.Equal(LaunchOutcome.ThirdPartyLauncher, result.Outcome);
        Assert.Contains("EA app", result.Message, StringComparison.Ordinal);
        Assert.Contains("legendary.thirdPartyUri:EaManaged:Ea", rig.Calls.All);
        Assert.StartsWith("link2ea://", Assert.Single(rig.Host.ThirdPartyUris), StringComparison.Ordinal);
        Assert.DoesNotContain("legendary.activateUplay", rig.Calls.All); // `activate --origin` always prompts: never used

        rig.Time.Advance(TimeSpan.FromSeconds(9));
        await WaitUntilAsync(() => !rig.Service.Hub.State.Sessions.ContainsKey(Ea));
        await rig.Service.ActivateGame(new AppRef(Ea));
        Assert.Equal(2, rig.Host.ThirdPartyUris.Count);
    }

    [Fact]
    public async Task ALinkWithTheWrongSchemeIsNeverOpened()
    {
        using var rig = await ReadyAsync();
        await rig.Service.ActivateGame(new AppRef(Ubi));
        rig.Legendary.ThirdPartyUri = "https://evil.example/?x=1";

        var e = await Assert.ThrowsAsync<GliderException>(() => Launch(rig, Ubi));

        Assert.Equal(ErrorCode.LaunchFailed, e.Error.Code);
        Assert.Empty(rig.Host.ThirdPartyUris);
        Assert.False(rig.Service.Hub.State.Sessions.ContainsKey(Ubi));
    }

    [Fact]
    public async Task ALegendaryErrorClearsTheSessionAndSurfaces()
    {
        using var rig = await ReadyAsync();
        await rig.Service.ActivateGame(new AppRef(Ubi));
        rig.Legendary.ThirdPartyUriError = Errors.Network();

        var e = await Assert.ThrowsAsync<GliderException>(() => Launch(rig, Ubi));

        Assert.Equal(ErrorCode.Network, e.Error.Code);
        Assert.False(rig.Service.Hub.State.Sessions.ContainsKey(Ubi));
        rig.Legendary.ThirdPartyUriError = null;
        Assert.Equal(LaunchOutcome.ThirdPartyLauncher, (await Launch(rig, Ubi)).Outcome); // and a retry works
    }

    [Fact]
    public async Task OfflinePlayUsesTheOfflineLink_AndNeedsNoSignIn()
    {
        using var rig = await ReadyAsync(signedIn: false);

        var result = await Launch(rig, Ubi, LaunchMode.Offline);

        Assert.Equal(LaunchOutcome.ThirdPartyLauncher, result.Outcome);
        Assert.True(result.Offline);
        Assert.True(rig.Legendary.LastThirdPartyOffline);
        var online = await Assert.ThrowsAsync<GliderException>(() => Launch(rig, Ea, LaunchMode.Online));
        Assert.Equal(ErrorCode.NotSignedIn, online.Error.Code);
    }

    [Fact]
    public async Task ActivationFailuresKeepTheFixAndRecordNothing()
    {
        using var rig = await ReadyAsync();
        rig.Legendary.ActivateError = Errors.AccountNotLinked("Ubisoft Connect");

        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.ActivateGame(new AppRef(Ubi)));

        Assert.Equal(ErrorCode.ActivationRequired, e.Error.Code);
        Assert.Equal(FixAction.Activate, e.Error.Fix);
        Assert.Equal(Ubi, e.Error.AppName);
        Assert.False(File.Exists(Path.Combine(rig.Paths.State, "thirdparty.json")));
        Assert.Equal(LaunchOutcome.NeedsActivation, (await Launch(rig, Ubi)).Outcome);
    }

    [Fact]
    public async Task AnInstalledPartnerLinkedGameNeedsActivationBeforeItStarts()
    {
        using var rig = await ReadyAsync();

        var first = await Launch(rig, Linked);
        Assert.Equal(LaunchOutcome.NeedsActivation, first.Outcome);
        Assert.Empty(rig.Processes.Started);
        Assert.Equal(RunState.Idle, rig.Service.Hub.State.RunStateOf(Linked));

        await rig.Service.ActivateGame(new AppRef(Linked));
        var second = await Launch(rig, Linked);

        Assert.Equal(LaunchOutcome.Started, second.Outcome);
        Assert.Single(rig.Processes.Started);
        Assert.Empty(rig.Host.ThirdPartyUris); // it launches from its Epic files, not through a link
        Assert.Null(second.Message);
    }

    [Fact]
    public async Task APartnerLinkedGameStartsEvenWithoutItsLauncher_WithAHint()
    {
        using var rig = await ReadyAsync();
        await rig.Service.ActivateGame(new AppRef(Linked));
        rig.Launchers.Installed.Clear();

        var result = await Launch(rig, Linked);

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        Assert.Contains("Ubisoft Connect isn't installed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherStoresAreNamedAndRefused()
    {
        using var rig = await ReadyAsync();

        var launch = await Assert.ThrowsAsync<GliderException>(() => Launch(rig, "Gog"));
        var activate = await Assert.ThrowsAsync<GliderException>(() => rig.Service.ActivateGame(new AppRef("Gog")));

        Assert.All(new[] { launch, activate }, e =>
        {
            Assert.Equal(ErrorCode.ThirdPartyRequired, e.Error.Code);
            Assert.Contains("GOG", e.Error.Title, StringComparison.Ordinal);
        });
        Assert.Null((await rig.Service.GetGameDetail(new AppRef(Fortnite()))).ThirdPartyInfo);
    }

    [Fact]
    public async Task ActivateRejectsOrdinaryAndUnknownGames_AndNeedsASignIn()
    {
        using var rig = await ReadyAsync();
        var ordinary = await Assert.ThrowsAsync<GliderException>(() => rig.Service.ActivateGame(new AppRef(Fortnite())));
        Assert.Equal(ErrorCode.InvalidRequest, ordinary.Error.Code);
        var unknown = await Assert.ThrowsAsync<GliderException>(() => rig.Service.ActivateGame(new AppRef("Nope")));
        Assert.Equal(ErrorCode.NotFound, unknown.Error.Code);

        using var signedOut = await ReadyAsync(signedIn: false);
        var e = await Assert.ThrowsAsync<GliderException>(() => signedOut.Service.ActivateGame(new AppRef(Ubi)));
        Assert.Equal(ErrorCode.NotSignedIn, e.Error.Code);
    }

    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\UbisoftConnect.exe\" \"%1\"", "C:\\Program Files (x86)\\Ubisoft\\Ubisoft Game Launcher\\UbisoftConnect.exe")]
    [InlineData("C:\\Games\\EA\\link2ea.exe %1", "C:\\Games\\EA\\link2ea.exe")]
    [InlineData("  ", null)]
    [InlineData("\"unterminated", null)]
    public void TheLauncherProgramIsParsedOutOfTheProtocolHandlerCommand(string command, string? expected) =>
        Assert.Equal(expected, RegistryThirdPartyLaunchers.ProgramOf(command));

    private static string Fortnite() => FortniteLayout.AppName;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(condition());
    }
}
