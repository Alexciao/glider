using Glider.Core.Contracts;
using Glider.Core.Links;
using Glider.Core.Tests.Services;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Links;

public class LinkServiceTests
{
    private static Rig NewRig(out FakeShell shell, bool managesGlider = false)
    {
        var rig = new Rig();
        shell = new FakeShell(rig.Root, managesGlider);
        rig.Shell = shell;
        return rig;
    }

    /// <summary>Alpha installed + Beta owned-but-not-installed, UI already ready, library loaded.</summary>
    private static async Task<Rig> Ready(Action<Rig>? configure = null)
    {
        var rig = NewRig(out _);
        rig.AddGame("Alpha", "Alpha Quest", "1.0", "1.0");
        rig.AddGame("Beta", "Beta", "1.0");
        configure?.Invoke(rig);
        await rig.StartWarmAsync();
        await rig.Service.Ready();
        return rig;
    }

    private static string[] Routes(Rig rig) => [.. rig.Sink.Of<NavigateEvent>(RpcEvents.Navigate).Select(e => e.Route)];

    private static Task<LinkOutcome> Handle(Rig rig, string link, LinkSource source = LinkSource.SecondInstance) => rig.Service.HandleLinkAsync(link, source);

    // ---------------------------------------------------------------- launch

    [Fact]
    public async Task LaunchLinkStartsTheGameWithoutShowingTheWindowOrNavigating()
    {
        using var rig = await Ready();

        var outcome = await Handle(rig, "glider://launch/Alpha");

        Assert.Equal(LinkOutcome.Handled, outcome);
        Assert.Single(rig.Processes.Started);
        Assert.Equal(0, rig.Host.RestoreCount);
        Assert.Empty(Routes(rig));
    }

    [Fact]
    public async Task LaunchLinkResolvesTheAppNameCaseInsensitively()
    {
        using var rig = await Ready();

        await Handle(rig, "glider://launch/alpha");

        Assert.Single(rig.Processes.Started);
    }

    [Fact]
    public async Task LaunchLinkForAGameThatIsNotInstalledOpensTheInstallDialogAndNeverInstalls()
    {
        using var rig = await Ready();

        await Handle(rig, "glider://launch/Beta");

        Assert.Equal(["game/Beta/install"], Routes(rig));
        Assert.Equal(1, rig.Host.RestoreCount);
        Assert.Empty(rig.Processes.Started);
        Assert.Empty(rig.Jobs.Requests);
    }

    [Fact]
    public async Task InstallLinkOpensTheDialogOnly()
    {
        using var rig = await Ready();

        await Handle(rig, "glider://install/Beta");

        Assert.Equal(["game/Beta/install"], Routes(rig));
        Assert.Equal(1, rig.Host.RestoreCount);
        Assert.Empty(rig.Jobs.Requests);
    }

    [Fact]
    public async Task InstallLinkForAnInstalledGameShowsItsPage()
    {
        using var rig = await Ready();

        await Handle(rig, "glider://install/Alpha");

        Assert.Equal(["game/Alpha"], Routes(rig));
        Assert.Empty(rig.Jobs.Requests);
    }

    [Fact]
    public async Task LaunchLinkForDlcShowsTheBaseGame()
    {
        using var rig = await Ready(configure: r => r.AddGame("AlphaDlc", "Alpha DLC", "1.0", baseApp: "Alpha"));

        await Handle(rig, "glider://launch/AlphaDlc");

        Assert.Equal(["game/Alpha"], Routes(rig));
        Assert.Empty(rig.Processes.Started);
    }

    [Fact]
    public async Task ALaunchThatFailsToastsShowsTheWindowAndOpensTheGame()
    {
        using var rig = await Ready();
        rig.Processes.StartError = new IOException("boom");

        await Handle(rig, "glider://launch/Alpha");

        Assert.Single(rig.Host.Toasts);
        Assert.Equal(["game/Alpha"], Routes(rig));
        Assert.Equal(1, rig.Host.RestoreCount);
    }

    [Fact]
    public async Task LaunchingAGameThatAlreadyRunsIsAToastNotAnError()
    {
        using var rig = await Ready();
        await Handle(rig, "glider://launch/Alpha");
        rig.Time.Advance(TimeSpan.FromSeconds(5)); // not a double click any more

        await Handle(rig, "glider://launch/Alpha");

        Assert.Single(rig.Processes.Started);
        Assert.Contains(rig.Host.Toasts, t => t.Message.Contains("running", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, rig.Host.RestoreCount);
    }

    [Fact]
    public async Task ADoubleClickIsOneLink()
    {
        using var rig = await Ready();

        await Handle(rig, "glider://downloads");
        await Handle(rig, "glider://downloads");
        rig.Time.Advance(TimeSpan.FromSeconds(3));
        await Handle(rig, "glider://downloads");

        Assert.Equal(["downloads", "downloads"], Routes(rig));
    }

    // ---------------------------------------------------------------- pages

    [Theory]
    [InlineData("glider://game/Alpha", "game/Alpha")]
    [InlineData("glider://downloads", "downloads")]
    [InlineData("glider://store", "store")]
    [InlineData("glider://store/p/some-game", "store/p/some-game")]
    [InlineData("glider://library", "library")]
    public async Task PageLinksShowTheWindowAndNavigate(string link, string route)
    {
        using var rig = await Ready();

        Assert.Equal(LinkOutcome.Handled, await Handle(rig, link));

        Assert.Equal([route], Routes(rig));
        Assert.Equal(1, rig.Host.RestoreCount);
    }

    [Fact]
    public async Task AnUnknownGameGoesToTheLibraryWithAMessage()
    {
        using var rig = await Ready();

        await Handle(rig, "glider://launch/NotOwned");

        Assert.Equal(["library"], Routes(rig));
        Assert.Single(rig.Host.Toasts);
        Assert.Empty(rig.Processes.Started);
    }

    [Theory]
    [InlineData("glider://launch/../../Windows")]
    [InlineData("glider://launch/Alpha%20--evil")]
    [InlineData("glider://nothing")]
    [InlineData("http://example.com")]
    [InlineData("")]
    [InlineData(null)]
    public async Task RefusedLinksNeverActButStillShowTheWindow(string? link)
    {
        using var rig = await Ready();

        Assert.Equal(LinkOutcome.Ignored, await Handle(rig, link!));

        Assert.Empty(rig.Processes.Started);
        Assert.Empty(Routes(rig));
        Assert.Equal(1, rig.Host.RestoreCount); // never leave an invisible process
    }

    [Fact]
    public async Task ARouteRequestedBeforeTheUiIsReadyIsDeliveredWhenItIs()
    {
        using var rig = NewRig(out _);
        rig.AddGame("Alpha", "Alpha Quest", "1.0", "1.0");
        await rig.StartWarmAsync();

        await Handle(rig, "glider://game/Alpha", LinkSource.CommandLine);
        await Handle(rig, "glider://downloads", LinkSource.CommandLine);
        Assert.Empty(Routes(rig)); // the UI has not called app.ready yet

        await rig.Service.Ready();

        Assert.Equal(["downloads"], Routes(rig)); // the latest request wins
        Assert.Equal(2, rig.Host.RestoreCount);
    }

    // ---------------------------------------------------------------- Epic links

    [Fact]
    public async Task EpicLinksAreIgnoredWhileTheSettingIsOff()
    {
        using var rig = await Ready();

        var outcome = await Handle(rig, "com.epicgames.launcher://apps/ns-Alpha%3Aitem-Alpha%3AAlpha?action=launch");

        Assert.Equal(LinkOutcome.Ignored, outcome);
        Assert.Empty(rig.Processes.Started);
    }

    [Fact]
    public async Task EpicLaunchLinkStartsTheGameResolvedByNamespaceAndCatalogId()
    {
        using var rig = await Ready(configure: r => r.Settings.Set(Settings.Default with { HandleEpicLinks = true }));

        // the app part is not our app name; (namespace, catalog item) identifies the game
        var outcome = await Handle(rig, "com.epicgames.launcher://apps/ns-Alpha%3Aitem-Alpha%3AEpicsOwnName?action=launch&silent=true");

        Assert.Equal(LinkOutcome.Handled, outcome);
        Assert.Single(rig.Processes.Started);
        Assert.Equal(0, rig.Host.RestoreCount);
    }

    [Fact]
    public async Task EpicInstallLinkOpensTheInstallDialog_UninstallIsIgnored()
    {
        using var rig = await Ready(configure: r => r.Settings.Set(Settings.Default with { HandleEpicLinks = true }));

        await Handle(rig, "com.epicgames.launcher://apps/ns-Beta%3Aitem-Beta%3ABeta?action=install");
        Assert.Equal(["game/Beta/install"], Routes(rig));

        var outcome = await Handle(rig, "com.epicgames.launcher://apps/ns-Beta%3Aitem-Beta%3ABeta?action=uninstall");
        Assert.Equal(LinkOutcome.Ignored, outcome);
        Assert.Empty(rig.Jobs.Requests);
    }

    [Fact]
    public async Task EpicStoreLinkOpensTheStoreAtThatProduct()
    {
        using var rig = await Ready(configure: r => r.Settings.Set(Settings.Default with { HandleEpicLinks = true }));

        await Handle(rig, "com.epicgames.launcher://store/p/some-game");

        Assert.Equal(["store/p/some-game"], Routes(rig));
    }

    // ---------------------------------------------------------------- protocol registration through settings

    private const string EpicKey = @"Software\Classes\com.epicgames.launcher";
    private const string GliderKey = @"Software\Classes\glider";

    [Fact]
    public async Task TurningOnEpicLinksBacksUpAndRegistersGlider_TurningOffRestoresIt()
    {
        using var rig = NewRig(out var shell);
        var egl = FakeRegistry.EglKey();
        shell.FakeRegistry.Seed(EpicKey, egl);
        await rig.StartWarmAsync();

        var on = await rig.Service.UpdateSettings(Settings.Default with { HandleEpicLinks = true });
        Assert.True(on.HandleEpicLinks);
        Assert.Equal($"\"{shell.LaunchExePath}\" \"%1\"", shell.FakeRegistry.Read(EpicKey)!.Find("shell", "open", "command")!.GetString());
        Assert.True(File.Exists(Path.Combine(rig.Paths.State, "protocol-backup.json")));

        var off = await rig.Service.UpdateSettings(Settings.Default with { HandleEpicLinks = false });
        Assert.False(off.HandleEpicLinks);
        Assert.True(egl.SameAs(shell.FakeRegistry.Read(EpicKey)));
        Assert.False(File.Exists(Path.Combine(rig.Paths.State, "protocol-backup.json")));
    }

    [Fact]
    public async Task IfTheRegistryRefusesTheSettingStaysOff()
    {
        using var rig = NewRig(out var shell);
        await rig.StartWarmAsync();
        shell.FakeRegistry.FailWrites = new UnauthorizedAccessException("denied");

        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.UpdateSettings(Settings.Default with { HandleEpicLinks = true }));

        Assert.Equal(ErrorCode.PermissionDenied, e.Error.Code);
        Assert.False(rig.Settings.Current.HandleEpicLinks);
    }

    [Fact]
    public async Task StartupRegistersGliderWhenInstalled_AndKeepsTheEpicSchemeInStepWithTheSetting()
    {
        using var rig = NewRig(out var shell, managesGlider: true);
        rig.Settings.Set(Settings.Default with { HandleEpicLinks = true });
        var egl = FakeRegistry.EglKey();
        shell.FakeRegistry.Seed(EpicKey, egl);

        await rig.StartWarmAsync();
        await WaitFor(() => shell.FakeRegistry.Read(GliderKey) is not null);

        Assert.NotNull(shell.FakeRegistry.Read(GliderKey));
        await WaitFor(() => shell.FakeRegistry.Read(EpicKey)?.GetString("GliderManaged") == "1");
        Assert.Equal("1", shell.FakeRegistry.Read(EpicKey)!.GetString("GliderManaged"));
    }

    [Fact]
    public async Task StartupLeavesTheRegistryAloneForPortableRuns_AndRestoresAStaleEpicHandler()
    {
        var rig = NewRig(out var shell); // ManagesGliderScheme = false
        using var _ = rig;
        // a previous run enabled the Epic handler, then settings.json was reset to defaults (HandleEpicLinks = false)
        var egl = FakeRegistry.EglKey();
        shell.FakeRegistry.Seed(EpicKey, egl);
        var registrar = new ProtocolRegistrar(shell.FakeRegistry, shell.LaunchExePath, Path.Combine(rig.Paths.State, "protocol-backup.json"));
        registrar.EnableEpicHandler();

        await rig.StartWarmAsync();
        await WaitFor(() => egl.SameAs(shell.FakeRegistry.Read(EpicKey)));

        Assert.Null(shell.FakeRegistry.Read(GliderKey));
        Assert.True(egl.SameAs(shell.FakeRegistry.Read(EpicKey)));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
