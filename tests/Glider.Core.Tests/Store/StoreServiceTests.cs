using Glider.Core.Contracts;
using Glider.Core.Legendary;
using Glider.Core.Links;
using Glider.Core.Store;
using Glider.Core.Tests.Services;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Store;

public sealed class CheckoutRefresherTests : IDisposable
{
    private readonly ManualTime _time = new();
    private int _calls;
    private int _newGames;
    private TaskCompletionSource? _gate;
    private CheckoutRefresher? _r;

    private CheckoutRefresher Make(bool present = true)
    {
        _r?.Dispose();
        _r = new CheckoutRefresher(_time, async ct =>
        {
            Interlocked.Increment(ref _calls);
            if (_gate is { } g)
            {
                await g.Task.WaitAsync(ct);
            }

            return _newGames;
        }, () => present);
        return _r;
    }

    public void Dispose() => _r?.Dispose();

    private static async Task WaitUntil(Func<bool> c)
    {
        for (var i = 0; i < 400 && !c(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(c(), "condition not reached");
    }

    [Fact]
    public async Task NothingHappensWithoutCheckoutActivity()
    {
        var r = Make();
        await r.StoreLeftAsync();
        await r.WindowActivatedAsync();
        Assert.Equal(0, _calls);
        Assert.False(r.Pending);
    }

    [Fact]
    public async Task LeavingTheTabAfterACheckoutRefreshesOnce()
    {
        var r = Make();
        r.CheckoutSeen();
        Assert.True(r.Pending);
        await r.StoreLeftAsync();
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task FindingANewGameEndsTheEpisode()
    {
        _newGames = 1;
        var r = Make();
        r.CheckoutSeen();
        await r.StoreLeftAsync();
        Assert.False(r.Pending);
        _time.Advance(TimeSpan.FromMinutes(1));
        await r.WindowActivatedAsync();
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task NothingNewKeepsItPendingForTheWindowThenGivesUp()
    {
        var r = Make();
        r.CheckoutSeen();
        await r.StoreLeftAsync();
        Assert.True(r.Pending); // a claim can lag the asset list

        _time.Advance(TimeSpan.FromMinutes(6));
        await r.WindowActivatedAsync();
        Assert.False(r.Pending);
    }

    [Fact]
    public async Task ThePollRefreshesEveryTwentySecondsForFiveMinutesAndStopsWhenFound()
    {
        var r = Make();
        r.CheckoutSeen();
        await Task.Delay(50, TestContext.Current.CancellationToken); // let the poll register its timer
        for (var i = 1; i <= 3; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(20));
            await WaitUntil(() => _calls == i);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        _newGames = 1;
        _time.Advance(TimeSpan.FromSeconds(20));
        await WaitUntil(() => _calls == 4);
        await WaitUntil(() => !r.Pending);

        var after = _calls;
        _time.Advance(TimeSpan.FromMinutes(2));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(after, _calls);
    }

    [Fact]
    public async Task ThePollStopsAfterTheWindow()
    {
        var r = Make();
        r.CheckoutSeen();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        for (var i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(20));
            await Task.Delay(30, TestContext.Current.CancellationToken);
        }

        await WaitUntil(() => !r.Pending);
        var calls = _calls;
        Assert.InRange(calls, 10, 16);
        _time.Advance(TimeSpan.FromMinutes(3));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(calls, _calls);
    }

    [Fact]
    public async Task ThePollPausesWhileTheUserIsAway()
    {
        var r = Make(present: false);
        r.CheckoutSeen();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, _calls);
    }

    [Fact]
    public async Task ConcurrentTriggersShareOneRefresh()
    {
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var r = Make();
        r.CheckoutSeen();
        var a = r.StoreLeftAsync();
        var b = r.WindowActivatedAsync();
        await WaitUntil(() => _calls == 1);
        _gate.SetResult();
        await Task.WhenAll(a, b);
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task RefreshesAreSpacedTenSeconds()
    {
        var r = Make();
        r.CheckoutSeen();
        await r.StoreLeftAsync();
        Assert.Equal(1, _calls);

        var second = r.WindowActivatedAsync();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, _calls); // waits for the spacing
        _time.Advance(TimeSpan.FromSeconds(11));
        await second;
        Assert.Equal(2, _calls);
    }
}

public class StoreIntentRouterTests
{
    private readonly List<string> _navigations = [];
    private readonly List<string> _launched = [];
    private readonly List<(NoticeLevel Level, string Title)> _notices = [];
    private readonly List<GameSummary> _library = [];
    private int _refreshes;
    private Action? _onRefresh;

    private StoreIntentRouter Router() => new(new StoreIntentEnvironment
    {
        Library = () => _library,
        RefreshLibrary = _ =>
        {
            _refreshes++;
            _onRefresh?.Invoke();
            return Task.CompletedTask;
        },
        Launch = (app, _) =>
        {
            _launched.Add(app);
            return Task.CompletedTask;
        },
        Navigate = _navigations.Add,
        Notify = (level, title, _, _) => _notices.Add((level, title)),
        Log = _ => { },
    });

    private static EpicLauncherLink Link(string raw)
    {
        Assert.True(EpicLauncherUri.TryParse(raw, out var l));
        return l;
    }

    private void Own(string app, InstallStatus status, string ns = "fn", string cat = "4fe75bbc5a674f4f9b356b5c90567da5", GameKind kind = GameKind.Game) =>
        _library.Add(LibraryFixtureHelpers.Summary(app, ns, cat, status, kind));

    [Fact]
    public async Task LaunchOfAnInstalledGameLaunchesOnAUserGestureOnly()
    {
        Own("Fortnite", InstallStatus.Installed);
        var link = Link("com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=launch&silent=true");

        await Router().HandleAsync(link, trusted: true, TestContext.Current.CancellationToken);
        Assert.Equal(["Fortnite"], _launched);
        Assert.Empty(_navigations);

        _launched.Clear();
        await Router().HandleAsync(link, trusted: false, TestContext.Current.CancellationToken);
        Assert.Empty(_launched);
        Assert.Equal(["game/Fortnite"], _navigations);
        Assert.Contains(_notices, n => n.Title.Contains("asked to open", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("verify")]
    [InlineData("updatecheck")]
    public async Task InstallVerifyAndUpdateOnlyOpenTheGamePage(string action)
    {
        Own("Fortnite", InstallStatus.Installed);
        await Router().HandleAsync(Link($"com.epicgames.launcher://apps/fn%3Acat%3AFortnite?action={action}"), trusted: true, TestContext.Current.CancellationToken);
        Assert.Empty(_launched);
        Assert.Equal(["game/Fortnite"], _navigations);
    }

    [Fact]
    public async Task LaunchOfAGameThatIsNotInstalledOpensItsPage()
    {
        Own("Fortnite", InstallStatus.NotInstalled);
        await Router().HandleAsync(Link("com.epicgames.launcher://apps/fn%3Acat%3AFortnite"), trusted: true, TestContext.Current.CancellationToken);
        Assert.Empty(_launched);
        Assert.Equal(["game/Fortnite"], _navigations);
    }

    [Fact]
    public async Task UninstallAndUnknownLinksAreIgnored()
    {
        Own("Fortnite", InstallStatus.Installed);
        var r = Router();
        await r.HandleAsync(Link("com.epicgames.launcher://apps/fn%3Acat%3AFortnite?action=uninstall"), true, TestContext.Current.CancellationToken);
        await r.HandleAsync(Link("com.epicgames.launcher://social"), true, TestContext.Current.CancellationToken);
        await r.HandleAsync(EpicLauncherLink.Unknown, true, TestContext.Current.CancellationToken);
        Assert.Empty(_launched);
        Assert.Empty(_navigations);
        Assert.Empty(_notices);
    }

    [Fact]
    public async Task TheNamespaceAndCatalogIdFindTheGameWhenTheArtifactIdDiffers()
    {
        Own("Sugar", InstallStatus.NotInstalled, ns: "ns1", cat: "cafe");
        await Router().HandleAsync(Link("com.epicgames.launcher://apps/ns1%3Acafe%3Asomething-else"), true, TestContext.Current.CancellationToken);
        Assert.Equal(["game/Sugar"], _navigations);
    }

    [Fact]
    public async Task AppNameMatchingIsExactNotAPrefix()
    {
        Own("FortniteTest", InstallStatus.Installed);
        await Router().HandleAsync(Link("com.epicgames.launcher://apps/fn%3Acat%3AFortnite"), true, TestContext.Current.CancellationToken);
        Assert.Empty(_launched);
        Assert.Contains(_notices, n => n.Title.Contains("isn't in your library", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AGameThatJustAppearedIsFoundAfterOneRefresh()
    {
        _onRefresh = () => Own("Knightica", InstallStatus.NotInstalled, "ns", "id");
        await Router().HandleAsync(Link("com.epicgames.launcher://apps/ns%3Aid%3AKnightica?action=install"), true, TestContext.Current.CancellationToken);
        Assert.Equal(1, _refreshes);
        Assert.Equal(["game/Knightica"], _navigations);
    }

    [Fact]
    public async Task AnUnknownGameRefreshesOnceThenSaysSo()
    {
        await Router().HandleAsync(Link("com.epicgames.launcher://apps/ns%3Aid%3AMissing"), true, TestContext.Current.CancellationToken);
        Assert.Equal(1, _refreshes);
        Assert.Equal(["library"], _navigations);
        Assert.Single(_notices);
    }

    [Fact]
    public async Task DlcIsNeverLaunched()
    {
        Own("Pack", InstallStatus.Installed, kind: GameKind.Dlc);
        await Router().HandleAsync(Link("com.epicgames.launcher://apps/fn%3Acat%3APack"), true, TestContext.Current.CancellationToken);
        Assert.Empty(_launched);
        Assert.Equal(["game/Pack"], _navigations);
    }

    [Fact]
    public async Task StoreAndLibraryLinksNavigate()
    {
        var r = Router();
        await r.HandleAsync(Link("com.epicgames.launcher://store/p/knightica-3be45c"), true, TestContext.Current.CancellationToken);
        await r.HandleAsync(Link("com.epicgames.launcher://store/bundles/lisa-the-definitive-edition"), true, TestContext.Current.CancellationToken);
        await r.HandleAsync(Link("com.epicgames.launcher://store"), true, TestContext.Current.CancellationToken);
        await r.HandleAsync(Link("com.epicgames.launcher://library"), true, TestContext.Current.CancellationToken);
        Assert.Equal(["store/product/knightica-3be45c", "store/bundle/lisa-the-definitive-edition", "store", "library"], _navigations);
    }

    [Fact]
    public async Task AppNamesAreEscapedInTheRoute()
    {
        Own("odd.name_1", InstallStatus.NotInstalled);
        await Router().HandleAsync(Link("com.epicgames.launcher://apps/odd.name_1"), true, TestContext.Current.CancellationToken);
        Assert.Equal(["game/odd.name_1"], _navigations);
    }
}

public sealed class FakeStoreHost : IStoreHost
{
    public event Action<StoreState>? StateChanged;
    public event Action<StoreLinkClick>? LauncherLink;
    public event Action? CheckoutActivity;
    public event Action? WindowActivated;

    public Func<CancellationToken, Task<string?>>? ExchangeCodeProvider { get; set; }
    public StoreState State { get; private set; } = new(false, false, false, false, null);
    public List<string?> Shown { get; } = [];
    public List<StoreBounds> Bounds { get; } = [];
    public List<string> Calls { get; } = [];

    public Task<StoreState> ShowAsync(string? url, StoreBounds bounds)
    {
        Shown.Add(url);
        Bounds.Add(bounds);
        State = State with { Created = true, Visible = true, Phase = StorePhase.Ready, Url = url is null ? State.Url : new Uri(url).GetLeftPart(UriPartial.Path), Host = "store.epicgames.com" };
        return Task.FromResult(State);
    }

    public void SetBounds(StoreBounds bounds) => Bounds.Add(bounds);

    public Task<StoreState> HideAsync()
    {
        Calls.Add("hide");
        State = State with { Visible = false, Phase = StorePhase.Hidden };
        return Task.FromResult(State);
    }

    public Task<StoreState> BackAsync()
    {
        Calls.Add("back");
        return Task.FromResult(State);
    }

    public Task<StoreState> ReloadAsync()
    {
        Calls.Add("reload");
        return Task.FromResult(State);
    }

    public Task<StoreState> SetCoveredAsync(bool covered)
    {
        Calls.Add($"cover:{covered}");
        State = State with { Covered = covered };
        return Task.FromResult(State);
    }

    public void OpenInBrowser() => Calls.Add("browser");

    public Task DestroyAsync()
    {
        Calls.Add("destroy");
        State = new StoreState(false, false, false, false, null);
        return Task.CompletedTask;
    }

    public void RaiseState(StoreState s) => StateChanged?.Invoke(s);
    public void RaiseLink(string uri, bool user = true, string? origin = "https://store.epicgames.com") => LauncherLink?.Invoke(new StoreLinkClick(uri, user, origin));
    public void RaiseCheckout() => CheckoutActivity?.Invoke();
    public void RaiseActivated() => WindowActivated?.Invoke();
}

public class StoreServiceTests
{
    private static readonly StoreBounds Box = new(72, 40, 1000, 700);

    private static async Task WaitUntil(Func<bool> c)
    {
        for (var i = 0; i < 400 && !c(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(c(), "condition not reached");
    }

    [Fact]
    public async Task WithoutAHostTheStoreReportsAnErrorStateInsteadOfThrowing()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        var state = await rig.Service.ShowStore(new StoreShowRequest(new StoreTarget(StoreTargetKind.Home, null), Box));
        Assert.Equal(StorePhase.Error, state.Phase);
        Assert.NotNull(state.Error);
        Assert.False(state.Created);
        await rig.Service.SetStoreBounds(Box);
        Assert.Equal(StorePhase.Idle, (await rig.Service.HideStore()).Phase);
        await rig.Service.OpenStoreInBrowser();
        Assert.Contains("https://store.epicgames.com/", rig.Host.Opened[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowBuildsTheUrlFromTheTypedTarget()
    {
        using var rig = new Rig();
        var host = new FakeStoreHost();
        rig.Host.StoreHost = host;
        await rig.StartWarmAsync();

        var state = await rig.Service.ShowStore(new StoreShowRequest(new StoreTarget(StoreTargetKind.Product, "knightica-3be45c"), Box));
        Assert.True(state.Created);
        Assert.Equal(StoreLocale.FromCurrentCulture(), StoreLocale.FromCurrentCulture());
        Assert.EndsWith("/p/knightica-3be45c", host.Shown[0], StringComparison.Ordinal);
        Assert.StartsWith("https://store.epicgames.com/", host.Shown[0], StringComparison.Ordinal);
        Assert.Equal(Box, host.Bounds[0]);

        await rig.Service.ShowStore(new StoreShowRequest(null, Box));
        Assert.Null(host.Shown[1]); // no target: keep the page
    }

    [Fact]
    public async Task HostileSlugsAndBoundsAreCleaned()
    {
        using var rig = new Rig();
        var host = new FakeStoreHost();
        rig.Host.StoreHost = host;
        await rig.StartWarmAsync();

        await rig.Service.ShowStore(new StoreShowRequest(new StoreTarget(StoreTargetKind.Product, "https://evil.test/"), new StoreBounds(double.NaN, -50, double.PositiveInfinity, 1e12)));
        Assert.EndsWith("/free-games", host.Shown[0], StringComparison.Ordinal);
        Assert.Equal(new StoreBounds(0, 0, 0, 20000), host.Bounds[0]);
    }

    [Fact]
    public async Task HostStateIsRepublishedAsAStoreEvent()
    {
        using var rig = new Rig();
        var host = new FakeStoreHost();
        rig.Host.StoreHost = host;
        await rig.StartWarmAsync();
        await rig.Service.GetStoreState(); // subscribes

        var s = new StoreState(true, true, false, true, "Epic", StorePhase.Ready, "https://store.epicgames.com/en-US/", "store.epicgames.com", true);
        host.RaiseState(s);
        Assert.Contains(s, rig.Sink.Of<StoreState>(RpcEvents.Store));
    }

    [Fact]
    public async Task LastVisitedResumesTheLastStorePath()
    {
        using var rig = new Rig();
        var host = new FakeStoreHost();
        rig.Host.StoreHost = host;
        await rig.StartWarmAsync();

        await rig.Service.ShowStore(new StoreShowRequest(new StoreTarget(StoreTargetKind.Product, "knightica-3be45c"), Box));
        await rig.Service.HideStore();
        await rig.Service.ShowStore(new StoreShowRequest(new StoreTarget(StoreTargetKind.LastVisited, null), Box));
        Assert.EndsWith("/p/knightica-3be45c", host.Shown[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALauncherLinkFromTheStoreOpensTheGamePageAndDuplicatesAreDropped()
    {
        using var rig = new Rig();
        var host = new FakeStoreHost();
        rig.Host.StoreHost = host;
        await rig.StartWarmAsync();
        await rig.Service.GetStoreState();

        const string uri = "com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=install";
        host.RaiseLink(uri);
        host.RaiseLink(uri); // the same click through the second event
        host.RaiseLink(uri);
        await WaitUntil(() => rig.Sink.Of<NavigateEvent>(RpcEvents.Navigate).Count >= 1);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(["game/Fortnite"], rig.Sink.Of<NavigateEvent>(RpcEvents.Navigate).Select(e => e.Route).ToArray());
    }

    [Fact]
    public async Task MalformedLinksAndForeignOriginsNeverLaunch()
    {
        using var rig = new Rig();
        var host = new FakeStoreHost();
        rig.Host.StoreHost = host;
        await rig.StartWarmAsync();
        await rig.Service.GetStoreState();

        host.RaiseLink("com.epicgames.launcher://apps/D%3A%2FGames%2FX");
        host.RaiseLink("com.epicgames.launcher://apps/fn%3Acat%3AFortnite?action=launch", user: true, origin: "https://evil.test");
        host.RaiseLink("com.epicgames.launcher://apps/fn%3Acat%3AFortnite?action=launch&arg=--evil", user: false);
        await Task.Delay(300, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(rig.Calls.All, c => c.StartsWith("legendary.launch", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckoutActivityThenLeavingTheTabRefreshesTheLibraryAndAnnouncesNewGames()
    {
        using var rig = new Rig();
        var host = new FakeStoreHost();
        rig.Host.StoreHost = host;
        await rig.StartWarmAsync();
        await rig.Service.ShowStore(new StoreShowRequest(new StoreTarget(StoreTargetKind.Home, null), Box));

        var before = rig.Calls.Count("legendary.list");
        host.RaiseCheckout();
        await rig.Service.HideStore();
        await WaitUntil(() => rig.Calls.Count("legendary.list") > before);
    }

    [Fact]
    public async Task LeavingWithoutACheckoutDoesNotRefresh()
    {
        using var rig = new Rig();
        var host = new FakeStoreHost();
        rig.Host.StoreHost = host;
        await rig.StartWarmAsync();
        await rig.Service.ShowStore(new StoreShowRequest(new StoreTarget(StoreTargetKind.Home, null), Box));
        var before = rig.Calls.Count("legendary.list");
        await rig.Service.HideStore();
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(before, rig.Calls.Count("legendary.list"));
    }

    [Fact]
    public async Task CoverBackAndReloadAreForwarded()
    {
        using var rig = new Rig();
        var host = new FakeStoreHost();
        rig.Host.StoreHost = host;
        await rig.StartWarmAsync();
        await rig.Service.ShowStore(new StoreShowRequest(null, Box));

        Assert.True((await rig.Service.SetStoreCovered(new StoreCoverRequest(true))).Covered);
        await rig.Service.StoreBack();
        await rig.Service.StoreReload();
        await rig.Service.OpenStoreInBrowser();
        Assert.Equal(["cover:True", "back", "reload", "browser"], host.Calls.ToArray());
    }

    [Fact]
    public async Task TheExchangeCodeProviderIsRateLimitedAndNeverLogged()
    {
        using var rig = new Rig();
        var host = new FakeStoreHost();
        rig.Host.StoreHost = host;
        await rig.StartWarmAsync();
        await rig.Service.GetStoreState();
        Assert.NotNull(host.ExchangeCodeProvider);
        rig.Legendary.WebExchangeCode = "0123456789abcdef0123456789abcdef";

        var code = await host.ExchangeCodeProvider!(TestContext.Current.CancellationToken);
        Assert.Equal("0123456789abcdef0123456789abcdef", code);
        Assert.Null(await host.ExchangeCodeProvider!(TestContext.Current.CancellationToken)); // < 10 s later

        rig.Time.Advance(TimeSpan.FromSeconds(11));
        Assert.NotNull(await host.ExchangeCodeProvider!(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("0123456789abcdef", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoExchangeCodeWhenSignedOut()
    {
        using var rig = new Rig();
        rig.Legendary.Account = null;
        var host = new FakeStoreHost();
        rig.Host.StoreHost = host;
        await rig.StartWarmAsync();
        await rig.Service.GetStoreState();
        rig.Legendary.WebExchangeCode = "0123456789abcdef0123456789abcdef";
        Assert.Null(await host.ExchangeCodeProvider!(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FreeGamesAreEmptyUntilTheBackgroundFeedLoadsAndNeverFetchInTests()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        var r = await rig.Service.GetFreeGames();
        Assert.Empty(r.Items);
    }
}

public class WebExchangeCodeCliTests
{
    private const string Code = "0123456789abcdef0123456789abcdef";

    private static LegendaryCli Cli(FakeLegendaryEnv env) => new(env.Paths, env.Log, new LegendaryOptions
    {
        StallTimeout = TimeSpan.FromSeconds(30),
        PinnedVersion = null,
        ReadAccountFromUserJson = false,
    });

    [Fact]
    public async Task ReadsOnlyTheCodeAndNeverLogsIt()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("get-token", $$"""out {"expiresInSeconds": 299, "code": "{{Code}}", "creatingClientId": "34a02cf8f4414e29b15921876da36f9a"}""");
        using var cli = Cli(env);

        var code = await cli.GetWebExchangeCodeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Code, code);
        Assert.Equal(["get-token", "--json"], env.Invocations().Single().Args);
        Assert.DoesNotContain(env.Log.Lines, l => l.Contains(Code, StringComparison.Ordinal));
        Assert.DoesNotContain(env.Log.Lines, l => l.Contains("34a02cf8", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""out {"code": "NOT-HEX"}""")]
    [InlineData("""out {"code": "0123456789ABCDEF0123456789ABCDEF"}""")]
    [InlineData("""out {"expiresInSeconds": 5}""")]
    [InlineData("out not json")]
    [InlineData("out")]
    public async Task AnythingButA32HexCodeIsNull(string script)
    {
        using var env = new FakeLegendaryEnv();
        env.Script("get-token", script);
        using var cli = Cli(env);
        Assert.Null(await cli.GetWebExchangeCodeAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANonZeroExitIsNull()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("get-token", $$"""
            out {"code": "{{Code}}"}
            exit 1
            """);
        using var cli = Cli(env);
        Assert.Null(await cli.GetWebExchangeCodeAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void TheRedactorMasksCodesEvenIfSomethingLogsThem()
    {
        var redacted = Glider.Core.Infrastructure.SecretRedactor.Redact($$"""{"expiresInSeconds": 299, "code": "{{Code}}"}""");
        Assert.DoesNotContain(Code, redacted, StringComparison.Ordinal);
        var url = Glider.Core.Infrastructure.SecretRedactor.UrlForLog(StoreSso.Build(Code, "https://store.epicgames.com/en-US/"));
        Assert.DoesNotContain(Code, url, StringComparison.Ordinal);
        Assert.Equal("https://www.epicgames.com/id/exchange?…", url);
    }
}
