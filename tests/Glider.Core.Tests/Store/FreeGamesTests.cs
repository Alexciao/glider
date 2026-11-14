using System.Net;
using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Store;
using Glider.Core.Tests.Services;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Store;

public class FreeGamesParserTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "store", "freeGamesPromotions.json");

    private static string Fixture => File.ReadAllText(FixturePath);

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void BeforeAnyPromotionNothingIsFreeNowButTheFirstWindowIsSoon()
    {
        var p = FreeGamesParser.Parse(Fixture, At("2026-11-10T12:00:00Z"));
        // Knightica's window opens 2026-11-12: upcoming from here. Alien/Mark too.
        Assert.Empty(p.Now);
        Assert.Equal(["Knightica", "Alien: Isolation", "Mark of the Deep"], p.Soon.Select(o => o.Title).ToArray());
    }

    [Fact]
    public void OnTheFixtureDayKnighticaIsFreeNowAndAlienAndMarkAreSoon()
    {
        var p = FreeGamesParser.Parse(Fixture, At("2026-11-14T09:00:00Z"));
        var knightica = Assert.Single(p.Now);
        Assert.Equal("Knightica", knightica.Title);
        Assert.Equal(FreeGameStatus.FreeNow, knightica.Status);
        Assert.Equal(At("2026-11-12T16:00:00Z"), knightica.StartsAt);
        Assert.Equal(At("2026-11-19T16:00:00Z"), knightica.EndsAt);
        Assert.Equal("knightica-3be45c", knightica.StoreSlug);
        Assert.Equal("$14.99", knightica.RegularPrice);
        Assert.Equal("Goblinz Studio SAS", knightica.Seller);
        Assert.Equal(["Alien: Isolation", "Mark of the Deep"], p.Soon.Select(o => o.Title).ToArray());
        Assert.All(p.Soon, o => Assert.Equal(At("2026-11-19T16:00:00Z"), o.StartsAt));
    }

    [Fact]
    public void AtTheBoundaryTheStripFlips()
    {
        var p = FreeGamesParser.Parse(Fixture, At("2026-11-19T16:00:00Z"));
        Assert.Equal(["Alien: Isolation", "Mark of the Deep"], p.Now.Select(o => o.Title).ToArray());
        Assert.DoesNotContain(p.Now.Concat(p.Soon), o => o.Title == "Knightica");
        Assert.All(p.Now, o => Assert.Equal(FreeGameStatus.FreeNow, o.Status));
    }

    [Fact]
    public void DeepDiscountsAreNotGiveaways()
    {
        var p = FreeGamesParser.Parse(Fixture, At("2026-11-14T09:00:00Z"));
        var all = p.Now.Concat(p.Soon).Select(o => o.Title).ToList();
        foreach (var discounted in new[] { "Ghostrunner 2", "Monument Valley", "LISA: The Definitive Edition", "TerraScape" })
        {
            Assert.DoesNotContain(discounted, all);
        }

        // and nothing without a promotion
        Assert.DoesNotContain("Luftrausers", all);
        Assert.DoesNotContain("Them's Fightin' Herds", all);
    }

    [Fact]
    public void LongAfterEverythingEndedTheFeedIsEmpty()
    {
        var p = FreeGamesParser.Parse(Fixture, At("2027-03-01T00:00:00Z"));
        Assert.Empty(p.Now);
        Assert.Empty(p.Soon);
        Assert.Empty(p.Boundaries);
    }

    [Fact]
    public void BoundariesAreTheFreeWindowEdgesOnly()
    {
        var p = FreeGamesParser.Parse(Fixture, At("2026-11-14T09:00:00Z"));
        Assert.Equal(
            [At("2026-11-12T16:00:00Z"), At("2026-11-19T16:00:00Z"), At("2026-11-26T16:00:00Z")],
            p.Boundaries.ToArray());
    }

    [Fact]
    public void ImagesPreferWideAndTallArtOnEpicHostsOnly()
    {
        var k = FreeGamesParser.Parse(Fixture, At("2026-11-14T09:00:00Z")).Now.Single();
        Assert.Equal("https://cdn1.epicgames.com/spt-assets/357100ca5613420e98208c5f14585e9d/knightica-1nuxm.png", k.WideImage);
        Assert.Equal("https://cdn1.epicgames.com/spt-assets/357100ca5613420e98208c5f14585e9d/knightica-1xc3d.png", k.TallImage);
    }

    [Theory]
    [InlineData("https://cdn1.epicgames.com/a/b.png?x=1#f", "https://cdn1.epicgames.com/a/b.png")]
    [InlineData("https://epicgames.com/a.png", "https://epicgames.com/a.png")]
    [InlineData("http://cdn1.epicgames.com/a.png", null)]
    [InlineData("https://evil.test/a.png", null)]
    [InlineData("https://cdn1.epicgames.com.evil.test/a.png", null)]
    [InlineData("https://u@cdn1.epicgames.com/a.png", null)]
    [InlineData("https://cdn1.epicgames.com:8443/a.png", null)]
    [InlineData("com.epicgames.video.qs://abc", null)]
    [InlineData("javascript:1", null)]
    [InlineData(null, null)]
    public void ImageUrlsAreValidated(string? raw, string? expected) => Assert.Equal(expected, FreeGamesParser.SafeImageUrl(raw));

    private static string Feed(string elementJson) =>
        "{'data':{'Catalog':{'searchStore':{'elements':[".Replace('\'', '"') + elementJson + "]}}}}";

    private const string FreeNowPromos =
        "{'promotionalOffers':[{'promotionalOffers':[{'startDate':'2026-11-12T16:00:00.000Z','endDate':'2026-11-19T16:00:00.000Z','discountSetting':{'discountType':'PERCENTAGE','discountPercentage':0}}]}],'upcomingPromotionalOffers':[]}";

    private const string PlainSlugs = "'productSlug':null,'urlSlug':'fallback-slug','catalogNs':{'mappings':[]},'offerMappings':[]";

    private static string Element(
        string id = "o1",
        string title = "Game",
        string offerType = "BASE_GAME",
        string promos = FreeNowPromos,
        string slugs = PlainSlugs,
        string images = "[{'type':'OfferImageWide','url':'https://cdn1.epicgames.com/w.png'}]") =>
        ("{'id':'" + id + "','namespace':'ns','title':'" + title + "','offerType':'" + offerType + "','status':'ACTIVE','isCodeRedemptionOnly':false,"
            + "'items':[{'id':'item1'}],'keyImages':" + images + "," + slugs + ",'promotions':" + promos + "}").Replace('\'', '"');

    [Fact]
    public void TwoWindowsPickTheActiveFreeOne()
    {
        const string promos = """
            {"promotionalOffers":[{"promotionalOffers":[{"startDate":"2026-11-12T16:00:00.000Z","endDate":"2026-11-19T16:00:00.000Z","discountSetting":{"discountPercentage":0}}]}],
             "upcomingPromotionalOffers":[{"promotionalOffers":[{"startDate":"2026-12-01T16:00:00.000Z","endDate":"2026-12-08T16:00:00.000Z","discountSetting":{"discountPercentage":0}}]}]}
            """;
        var p = FreeGamesParser.Parse(Feed(Element(promos: promos)), At("2026-11-14T00:00:00Z"));
        var o = Assert.Single(p.Now);
        Assert.Equal(At("2026-11-19T16:00:00Z"), o.EndsAt);
        Assert.Empty(p.Soon); // one offer, one row: it is listed as "now" until the window ends
    }

    [Fact]
    public void ADiscountWindowNextToAFreeWindowIsIgnored()
    {
        const string promos = """
            {"promotionalOffers":[],
             "upcomingPromotionalOffers":[{"promotionalOffers":[
               {"startDate":"2026-11-20T16:00:00.000Z","endDate":"2026-11-27T16:00:00.000Z","discountSetting":{"discountPercentage":20}},
               {"startDate":"2026-12-01T16:00:00.000Z","endDate":"2026-12-08T16:00:00.000Z","discountSetting":{"discountPercentage":0}}]}]}
            """;
        var p = FreeGamesParser.Parse(Feed(Element(promos: promos)), At("2026-11-14T00:00:00Z"));
        var o = Assert.Single(p.Soon);
        Assert.Equal(At("2026-12-01T16:00:00Z"), o.StartsAt);
    }

    [Fact]
    public void NullPromotionsAndMissingFieldsAreSkippedNotFatal()
    {
        var feed = Feed(Element(id: "a", promos: "null") + ",{'title':'no id'}".Replace('\'', '"') + "," + Element(id: "b", title: "Good") + ",123,null,{}");
        var p = FreeGamesParser.Parse(feed, At("2026-11-14T00:00:00Z"));
        Assert.Equal(["Good"], p.Now.Select(o => o.Title).ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"data":null}""")]
    [InlineData("""{"data":{"Catalog":{"searchStore":{"elements":"x"}}}}""")]
    [InlineData("""{"data":{"Catalog":{"searchStore":{"elements":[1,2,3]}}}}""")]
    public void GarbageNeverThrows(string json)
    {
        var p = FreeGamesParser.Parse(json, DateTimeOffset.UnixEpoch);
        Assert.Empty(p.Now);
        Assert.Empty(p.Soon);
    }

    [Fact]
    public void DuplicateOfferIdsAppearOnce()
    {
        var p = FreeGamesParser.Parse(Feed(Element(id: "x") + "," + Element(id: "x", title: "Again")), At("2026-11-14T00:00:00Z"));
        Assert.True(p.Now.Count == 1, Feed(Element(id: "x")));
    }

    [Fact]
    public void CodeRedemptionOnlyAndInactiveOffersAreSkipped()
    {
        var redemption = Element(id: "r").Replace("\"isCodeRedemptionOnly\":false", "\"isCodeRedemptionOnly\":true");
        var inactive = Element(id: "i").Replace("\"status\":\"ACTIVE\"", "\"status\":\"DRAFT\"");
        var p = FreeGamesParser.Parse(Feed(redemption + "," + inactive), At("2026-11-14T00:00:00Z"));
        Assert.Empty(p.Now);
    }

    [Theory]
    // catalogNs productHome first
    [InlineData("'catalogNs':{'mappings':[{'pageSlug':'base-game','pageType':'productHome'}]},'offerMappings':[{'pageSlug':'offer-slug','pageType':'productHome'}],'productSlug':'p-slug','urlSlug':'u-slug'", "base-game")]
    // offerMappings productHome next (an "offer" page type is not a product page)
    [InlineData("'catalogNs':{'mappings':[]},'offerMappings':[{'pageSlug':'addon-offer','pageType':'offer'},{'pageSlug':'offer-slug','pageType':'productHome'}],'productSlug':'p-slug','urlSlug':'u-slug'", "offer-slug")]
    // productSlug with the feed's trailing /home
    [InlineData("'catalogNs':{'mappings':[]},'offerMappings':[],'productSlug':'thems-fightin-herds/home','urlSlug':'u-slug'", "thems-fightin-herds")]
    // urlSlug only when it is not a raw id
    [InlineData("'catalogNs':{'mappings':[]},'offerMappings':[],'productSlug':null,'urlSlug':'u-slug'", "u-slug")]
    [InlineData("'catalogNs':{'mappings':[]},'offerMappings':[],'productSlug':null,'urlSlug':'a9152a8924cc4e438f0e61106ec02f7d'", null)]
    // hostile slugs never survive
    [InlineData("'catalogNs':{'mappings':[{'pageSlug':'../../evil','pageType':'productHome'}]},'offerMappings':[],'productSlug':'http://evil.test','urlSlug':'Bad Slug'", null)]
    public void SlugResolutionFollowsThePriorityOrder(string slugFields, string? expected)
    {
        var p = FreeGamesParser.Parse(Feed(Element(slugs: slugFields)), At("2026-11-14T00:00:00Z"));
        Assert.Equal(expected, Assert.Single(p.Now).StoreSlug);
    }

    [Fact]
    public void AddOnsLinkToTheBaseGamePageAndBundlesAreFlagged()
    {
        var addOn = Element(id: "add", offerType: "ADD_ON", slugs: "'productSlug':null,'urlSlug':'lost-castle-the-old-ones-awaken','catalogNs':{'mappings':[{'pageSlug':'lost-castle-abb2e2','pageType':'productHome'}]},'offerMappings':[{'pageSlug':'lost-castle-addon','pageType':'offer'}]");
        var bundle = Element(id: "bun", offerType: "BUNDLE", slugs: "'productSlug':'lisa-bundle','urlSlug':'lisa-bundle','catalogNs':{'mappings':[]},'offerMappings':[]");
        var q = FreeGamesParser.Parse(Feed(addOn + "," + bundle), At("2026-11-14T00:00:00Z"));
        Assert.Equal("lost-castle-abb2e2", q.Now.Single(o => o.OfferId == "add").StoreSlug);
        var b = q.Now.Single(o => o.OfferId == "bun");
        Assert.True(b.IsBundle);
        Assert.Equal("lisa-bundle", b.StoreSlug);
    }

    [Fact]
    public void TextIsClippedAndStrippedOfControlCharacters()
    {
        var p = FreeGamesParser.Parse(Feed(Element(title: new string('x', 400) + "\\u0007\\n")), At("2026-11-14T00:00:00Z"));
        var title = Assert.Single(p.Now).Title;
        Assert.Equal(160, title.Length);
    }
}

public class FreeGamesScheduleTests
{
    private static readonly FreeGamesOptions O = new();
    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void NeverFetchedMeansDueNow() => Assert.Equal(DateTimeOffset.MinValue, FreeGamesSchedule.NextDue(null, [], null, 0, O));

    [Fact]
    public void DueAfterTheTtlWhenNoBoundaryIsCloser()
    {
        var fetched = At("2026-11-14T10:00:00Z");
        Assert.Equal(fetched + TimeSpan.FromHours(1), FreeGamesSchedule.NextDue(fetched, [At("2026-11-19T16:00:00Z")], fetched, 0, O));
    }

    [Fact]
    public void ABoundaryInsideTheTtlPullsTheRefreshForwardToThirtySecondsAfterIt()
    {
        var fetched = At("2026-11-19T15:40:00Z");
        var due = FreeGamesSchedule.NextDue(fetched, [At("2026-11-19T16:00:00Z")], fetched, 0, O);
        Assert.Equal(At("2026-11-19T16:00:30Z"), due);
    }

    [Fact]
    public void ABoundaryAlreadyBehindTheFetchIsIgnored()
    {
        var fetched = At("2026-11-19T16:05:00Z");
        Assert.Equal(fetched + TimeSpan.FromHours(1), FreeGamesSchedule.NextDue(fetched, [At("2026-11-19T16:00:00Z")], fetched, 0, O));
    }

    [Fact]
    public void NeverMoreOftenThanEveryFiveMinutes()
    {
        var fetched = At("2026-11-19T15:59:00Z");
        var attempt = At("2026-11-19T15:59:30Z");
        var due = FreeGamesSchedule.NextDue(fetched, [At("2026-11-19T16:00:00Z")], attempt, 0, O);
        Assert.Equal(attempt + TimeSpan.FromMinutes(5), due);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 5)]
    [InlineData(3, 5)]
    [InlineData(4, 8)]
    [InlineData(5, 15)]
    [InlineData(9, 15)]
    public void FailuresBackOffUpToFifteenMinutes(int failures, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), FreeGamesSchedule.Spacing(failures, O));

    [Fact]
    public void NextBoundaryIsTheNearestFutureEdge()
    {
        var edges = new[] { At("2026-11-12T16:00:00Z"), At("2026-11-19T16:00:00Z"), At("2026-11-26T16:00:00Z") };
        Assert.Equal(At("2026-11-19T16:00:00Z"), FreeGamesSchedule.NextBoundary(At("2026-11-14T00:00:00Z"), edges));
        Assert.Null(FreeGamesSchedule.NextBoundary(At("2026-12-01T00:00:00Z"), edges));
    }
}

public sealed class FakeFeedHandler(string body) : HttpMessageHandler
{
    private readonly List<string> _requests = [];
    private readonly Dictionary<string, byte[]> _images = [];

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public bool Throw { get; set; }
    public string Body { get; set; } = body;
    public IReadOnlyList<string> Requests { get { lock (_requests) { return _requests.ToList(); } } }
    public int FeedCalls => Requests.Count(r => r.Contains("freeGamesPromotions", StringComparison.Ordinal));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(request.RequestUri!.ToString());
        }

        if (Throw)
        {
            throw new HttpRequestException("offline");
        }

        if (request.RequestUri!.Host.EndsWith("epicgames.com", StringComparison.Ordinal) && !request.RequestUri.AbsolutePath.Contains("freeGamesPromotions", StringComparison.Ordinal))
        {
            // art
            var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 1, 2, 3 };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) });
        }

        return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body) });
    }
}

public sealed class FreeGamesServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ManualTime _time = new();
    private readonly FakeSink _sink = new();
    private readonly FakeLog _log = new();
    private readonly string _fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "store", "freeGamesPromotions.json"));
    private readonly List<GameSummary> _library = [];
    private FreeGamesService? _service;

    public FreeGamesServiceTests() => _time.Advance(TimeSpan.FromDays(2) + TimeSpan.FromHours(-3)); // 2026-11-14 09:00Z

    private AppPaths Paths => new(_dir.Path, Path.Combine(_dir.Path, "legendary.exe"));

    private FreeGamesService Make(FakeFeedHandler handler, bool art = false, string locale = "it", string country = "IT")
    {
        Paths.EnsureCreated();
        _service?.Dispose();
        _service = new FreeGamesService(Paths, _log, _time, () => _library, _sink, new FreeGamesOptions
        {
            Handler = handler,
            Country = country,
            Locale = locale,
            DownloadArt = art,
            StartupDelay = TimeSpan.FromSeconds(1),
        });
        return _service;
    }

    public void Dispose()
    {
        _service?.Dispose();
        _dir.Dispose();
    }

    [Fact]
    public async Task FetchesWithTheRegionParametersAndNothingAboutTheUser()
    {
        var h = new FakeFeedHandler(_fixture);
        var s = Make(h);
        var r = await s.RefreshAsync(force: true, TestContext.Current.CancellationToken);

        var url = Assert.Single(h.Requests);
        Assert.Equal("https://store-site-backend-static-ipv4.ak.epicgames.com/freeGamesPromotions?locale=it&country=IT&allowCountries=IT", url);
        Assert.Equal(["Knightica", "Alien: Isolation", "Mark of the Deep"], r.Items.Select(i => i.Title).ToArray());
        Assert.False(r.Stale);
        Assert.Equal(_time.GetUtcNow(), r.FetchedAt);
    }

    [Fact]
    public async Task ItemsCarryStatusSlugArtAndPrice()
    {
        var s = Make(new FakeFeedHandler(_fixture));
        var r = await s.RefreshAsync(true, TestContext.Current.CancellationToken);

        var knightica = r.Items[0];
        Assert.Equal(FreeGameStatus.FreeNow, knightica.Status);
        Assert.Equal("knightica-3be45c", knightica.Slug);
        Assert.Equal("$14.99", knightica.RegularPrice);
        Assert.StartsWith("https://cdn1.epicgames.com/", knightica.ImageUrl, StringComparison.Ordinal);
        Assert.EndsWith(FreeGamesParser.WideResize, knightica.ImageUrl, StringComparison.Ordinal);
        Assert.EndsWith(FreeGamesParser.TallResize, knightica.TallImageUrl, StringComparison.Ordinal);
        Assert.All(r.Items.Skip(1), i => Assert.Equal(FreeGameStatus.Upcoming, i.Status));
    }

    [Fact]
    public async Task OwnedComesFromTheLibraryByNamespaceAndCatalogItem()
    {
        var s = Make(new FakeFeedHandler(_fixture));
        Assert.All((await s.RefreshAsync(true, TestContext.Current.CancellationToken)).Items, i => Assert.False(i.Owned));

        _library.Add(LibraryFixtureHelpers.Summary("kn", "02d659f31e1c43a78c43fd9fd9edc089", "abf4eb9c26e24912ae58d2036745d5cf"));
        var r = s.Get();
        Assert.True(r.Items.Single(i => i.Title == "Knightica").Owned);
        Assert.False(r.Items.Single(i => i.Title == "Mark of the Deep").Owned);

        // same item id in another namespace is not the game
        _library.Clear();
        _library.Add(LibraryFixtureHelpers.Summary("x", "other", "abf4eb9c26e24912ae58d2036745d5cf"));
        Assert.False(s.Get().Items.Single(i => i.Title == "Knightica").Owned);
    }

    [Fact]
    public async Task TheFeedIsReRenderedAgainstNowSoTheStripFlipsOfflineToo()
    {
        var h = new FakeFeedHandler(_fixture);
        var s = Make(h);
        await s.RefreshAsync(true, TestContext.Current.CancellationToken);
        h.Throw = true;

        _time.Advance(TimeSpan.FromDays(5) + TimeSpan.FromHours(8)); // 2026-11-19T17:00Z
        var r = s.Get();
        Assert.Equal(["Alien: Isolation", "Mark of the Deep"], r.Items.Select(i => i.Title).ToArray());
        Assert.All(r.Items, i => Assert.Equal(FreeGameStatus.FreeNow, i.Status));
    }

    [Fact]
    public async Task OfflineServesTheDiskCacheAfterARestart()
    {
        var first = Make(new FakeFeedHandler(_fixture));
        await first.RefreshAsync(true, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(Path.Combine(Paths.Cache, "free-games.IT.it.json")));

        var offline = new FakeFeedHandler("") { Throw = true };
        var second = Make(offline);
        var cached = second.Get(); // no fetch needed
        Assert.Equal(3, cached.Items.Count);
        Assert.False(cached.Stale);

        var refreshed = await second.RefreshAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(3, refreshed.Items.Count);
        Assert.True(refreshed.Stale); // the last fetch failed
    }

    [Fact]
    public async Task OldCacheIsStaleAndNoCacheMeansAnEmptyStrip()
    {
        var empty = Make(new FakeFeedHandler("") { Throw = true }, locale: "de", country: "DE");
        var r = await empty.RefreshAsync(true, TestContext.Current.CancellationToken);
        Assert.Empty(r.Items);
        Assert.Null(r.FetchedAt);

        var s = Make(new FakeFeedHandler(_fixture));
        await s.RefreshAsync(true, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromHours(25));
        Assert.True(s.Get().Stale);
    }

    [Fact]
    public async Task CacheIsKeyedByCountryAndLocale()
    {
        await Make(new FakeFeedHandler(_fixture), locale: "it", country: "IT").RefreshAsync(true, TestContext.Current.CancellationToken);
        var other = Make(new FakeFeedHandler("") { Throw = true }, locale: "de", country: "DE");
        Assert.Empty(other.Get().Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Cloudflare</html>")]
    [InlineData("{\"errors\":[]}")]
    public async Task ABadBodyNeverReplacesTheGoodCache(string body)
    {
        var h = new FakeFeedHandler(_fixture);
        var s = Make(h);
        await s.RefreshAsync(true, TestContext.Current.CancellationToken);

        h.Body = body;
        var r = await s.RefreshAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(3, r.Items.Count);
        Assert.True(r.Stale);
    }

    [Fact]
    public async Task HttpErrorsKeepTheCacheAndBackOff()
    {
        var h = new FakeFeedHandler(_fixture);
        var s = Make(h);
        await s.RefreshAsync(true, TestContext.Current.CancellationToken);

        h.Status = HttpStatusCode.ServiceUnavailable;
        _time.Advance(TimeSpan.FromMinutes(6));
        await s.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(2, h.FeedCalls);

        await s.RefreshAsync(false, TestContext.Current.CancellationToken); // too soon after the failure
        Assert.Equal(2, h.FeedCalls);

        _time.Advance(TimeSpan.FromMinutes(6));
        await s.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(3, h.FeedCalls);
    }

    [Fact]
    public async Task UnforcedRefreshesAreSpacedFiveMinutesAndForceBypassesThat()
    {
        var h = new FakeFeedHandler(_fixture);
        var s = Make(h);
        await s.RefreshAsync(false, TestContext.Current.CancellationToken);
        await s.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(1, h.FeedCalls);

        await s.RefreshAsync(true, TestContext.Current.CancellationToken);
        Assert.Equal(2, h.FeedCalls);

        _time.Advance(TimeSpan.FromMinutes(5));
        await s.RefreshAsync(false, TestContext.Current.CancellationToken);
        Assert.Equal(3, h.FeedCalls);
    }

    [Fact]
    public async Task OversizedResponsesAreRejected()
    {
        var s = Make(new FakeFeedHandler(new string(' ', 3 * 1024 * 1024)));
        var r = await s.RefreshAsync(true, TestContext.Current.CancellationToken);
        Assert.Empty(r.Items);
    }

    [Fact]
    public async Task ArtIsDownloadedOnceIntoTheCacheAndThenServedFromThere()
    {
        var h = new FakeFeedHandler(_fixture);
        var s = Make(h, art: true);
        var first = await s.RefreshAsync(true, TestContext.Current.CancellationToken);
        Assert.StartsWith("https://cdn1.epicgames.com/", first.Items[0].ImageUrl, StringComparison.Ordinal); // CDN until cached

        var dir = Path.Combine(Paths.Cache, "free-games");
        await WaitUntil(() => Directory.Exists(dir) && Directory.GetFiles(dir, "*.img").Length >= 2);

        var again = s.Get();
        Assert.StartsWith("https://cache.glider/free-games/", again.Items[0].ImageUrl, StringComparison.Ordinal);
        Assert.StartsWith("https://cache.glider/free-games/", again.Items[0].TallImageUrl, StringComparison.Ordinal);
        var artCalls = h.Requests.Count(r => !r.Contains("freeGamesPromotions", StringComparison.Ordinal));
        s.Get();
        Assert.Equal(artCalls, h.Requests.Count(r => !r.Contains("freeGamesPromotions", StringComparison.Ordinal)));
    }

    [Fact]
    public void ImageMagicNumbersAreChecked()
    {
        Assert.True(FreeGamesService.LooksLikeImage([0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.False(FreeGamesService.LooksLikeImage("<html><body>error page</body></html>"u8));
        Assert.False(FreeGamesService.LooksLikeImage([]));
    }

    [Fact]
    public async Task ChangesArePublishedAsAnEventOnlyWhenTheyChange()
    {
        var s = Make(new FakeFeedHandler(_fixture));
        await s.RefreshAsync(true, TestContext.Current.CancellationToken);
        s.Republish();
        s.Republish();
        var events = _sink.Of<FreeGamesResult>(RpcEvents.FreeGames);
        Assert.Single(events);

        _library.Add(LibraryFixtureHelpers.Summary("kn", "02d659f31e1c43a78c43fd9fd9edc089", "abf4eb9c26e24912ae58d2036745d5cf"));
        s.Republish();
        Assert.Equal(2, _sink.Of<FreeGamesResult>(RpcEvents.FreeGames).Count);
        Assert.True(_sink.Of<FreeGamesResult>(RpcEvents.FreeGames)[1].Items[0].Owned);
    }

    [Fact]
    public async Task TheBackgroundLoopFetchesAfterStartupFlipsAtTheBoundaryAndRefetchesSoonAfter()
    {
        _time.Advance(DateTimeOffset.Parse("2026-11-19T15:59:00Z", System.Globalization.CultureInfo.InvariantCulture) - _time.GetUtcNow());
        var h = new FakeFeedHandler(_fixture);
        var s = Make(h);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var loop = s.RunAsync(cts.Token);

        _time.Advance(TimeSpan.FromSeconds(2)); // the startup delay (the timer is registered synchronously by RunAsync)
        await WaitUntil(() => h.FeedCalls == 1);
        await WaitUntil(() => _sink.Of<FreeGamesResult>(RpcEvents.FreeGames).Count >= 1);
        await Task.Delay(100, TestContext.Current.CancellationToken); // let the loop park on its next timer

        // 16:00:00 UTC: Knightica ends, Alien and Mark start. The strip flips with an event, no fetch needed.
        _time.Advance(TimeSpan.FromSeconds(70));
        await WaitUntil(() => _sink.Of<FreeGamesResult>(RpcEvents.FreeGames).Any(e => e.Items.Any(i => i.Title == "Alien: Isolation" && i.Status == FreeGameStatus.FreeNow)));
        Assert.Equal(1, h.FeedCalls);

        // 30 s after the boundary (and not before the 5 minute spacing) the feed is fetched again.
        await Task.Delay(100, TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromMinutes(6));
        await WaitUntil(() => h.FeedCalls == 2);

        await cts.CancelAsync();
        await loop;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "condition not reached");
    }
}

internal static class LibraryFixtureHelpers
{
    public static GameSummary Summary(string app, string ns, string catalogItemId, InstallStatus status = InstallStatus.NotInstalled, GameKind kind = GameKind.Game) =>
        new(app, app, kind, null, ns, catalogItemId, null, status, null, null, false, null, null, null, null,
            false, SaveSyncStatus.NotSupported, true, ThirdPartyStore.None, null, 0, false, false, null, null, 0,
            new GameArt(null, null, null, null, null), RunState.Idle, null, null, false, false);
}
