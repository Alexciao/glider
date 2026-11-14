using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Glider.Core.Art;
using Glider.Core.Tests.Services;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Art;

public sealed class ArtCacheTests : IDisposable
{
    private const string TallUrl = "https://cdn1.epicgames.com/item/fn/tall_1200x1600-aaaa";
    private const string WideUrl = "https://cdn1.epicgames.com/item/fn/wide_2560x1440-bbbb";

    private readonly TempDir _dir = new();
    private readonly FakeArtSource _source = new();
    private readonly FakeFetcher _fetcher = new();
    private readonly FakeLog _log = new();
    private readonly ManualTime _time = new();
    private readonly List<ArtCache> _caches = [];

    public ArtCacheTests()
    {
        _source.Set("Fortnite", ArtKind.Tall, TallUrl, 1200);
        _source.Set("Fortnite", ArtKind.TallThumb, TallUrl, 1200);
        _source.Set("Fortnite", ArtKind.Wide, WideUrl, 2560);
    }

    public void Dispose()
    {
        foreach (var c in _caches)
        {
            c.Dispose();
        }

        _dir.Dispose();
    }

    private ArtCache Cache(ArtCacheOptions? options = null, IImageProcessor? images = null)
    {
        var c = new ArtCache(_dir.Path, _source, _fetcher, _log, _time, options ?? new ArtCacheOptions { PlaceholderPublishDelay = TimeSpan.Zero }, images);
        _caches.Add(c);
        return c;
    }

    private static string Url(string app, ArtKind kind, string source) => ArtUrls.Build(app, kind, source);

    // ------------------------------------------------------------------ URLs

    [Fact]
    public void Urls_RoundTrip_AndAreStablePerSource()
    {
        var url = ArtUrls.Build("Fortnite", ArtKind.TallThumb, TallUrl);
        Assert.Matches("^https://art\\.glider/Fortnite/tall-thumb\\?v=[0-9a-f]{10}$", url);
        Assert.Equal(url, ArtUrls.Build("Fortnite", ArtKind.TallThumb, TallUrl));
        Assert.NotEqual(url, ArtUrls.Build("Fortnite", ArtKind.TallThumb, TallUrl + "x"));

        Assert.True(ArtUrls.TryParse(url, out var app, out var kind, out var v));
        Assert.Equal(("Fortnite", ArtKind.TallThumb, ArtUrls.HashOf(TallUrl)), (app, kind, v));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(TallUrl)))[..10], v);
        Assert.True(ArtUrls.TryParse("https://art.glider/9d2d0eb64d5c44529cece33fe2a46482/logo", out _, out var logo, out var none));
        Assert.Equal((ArtKind.Logo, (string?)null), (logo, none));
    }

    [Theory]
    [InlineData("http://art.glider/Fortnite/tall")]
    [InlineData("https://art.glider.evil.com/Fortnite/tall")]
    [InlineData("https://cache.glider/Fortnite/tall")]
    [InlineData("https://art.glider:8443/Fortnite/tall")]
    [InlineData("https://user@art.glider/Fortnite/tall")]
    [InlineData("https://art.glider/%2e%2e/tall")]
    [InlineData("https://art.glider/Fortnite/huge")]
    [InlineData("https://art.glider/Fortnite/tall/extra")]
    [InlineData("https://art.glider/Fortnite")]
    [InlineData("https://art.glider/.hidden/tall")]
    [InlineData("https://art.glider/Fortnite/tall?v=zzzzzzzzzz")]
    [InlineData("https://art.glider/Fortnite/tall?v=ABCDEF0123")]
    [InlineData("https://art.glider/Fort\\nite/tall")]
    [InlineData("not a url")]
    public void Urls_RejectAnythingButAWellFormedArtUrl(string url) => Assert.False(ArtUrls.TryParse(url, out _, out _, out _));

    [Theory]
    [InlineData("https://art.glider/../state/tall")]
    [InlineData("https://art.glider/Fortnite/../tall")]
    [InlineData("https://art.glider/Fort%6Eite/tall")]
    [InlineData("https://art.glider/a/../../tall")]
    public void Urls_DotSegmentsAndEscapes_NeverLeaveTheArtFolder(string url)
    {
        // The Uri class resolves dot segments and unreserved escapes before we look: what is left is either rejected or a plain app token.
        if (ArtUrls.TryParse(url, out var app, out _, out _))
        {
            Assert.True(ArtUrls.IsValidApp(app));
            Assert.DoesNotContain("..", app, StringComparison.Ordinal);
            Assert.DoesNotContain('/', app);
            Assert.DoesNotContain('\\', app);
        }
    }

    [Fact]
    public void SourcePolicy_OnlyPublicHttps_AndEpicCdnGetsResizeParams()
    {
        Assert.True(ArtSourcePolicy.IsAllowed(new Uri("https://cdn1.epicgames.com/a.jpg")));
        foreach (var bad in new[] { "http://cdn1.epicgames.com/a.jpg", "https://127.0.0.1/a.jpg", "https://[::1]/a.jpg", "https://localhost/a.jpg", "https://intranet/a.jpg", "https://printer.local/a.jpg", "https://u:p@cdn1.epicgames.com/a.jpg", "https://cdn1.epicgames.com:444/a.jpg", "file:///c:/a.jpg" })
        {
            Assert.False(ArtSourcePolicy.IsAllowed(new Uri(bad)), bad);
        }

        Assert.Equal("https://cdn1.epicgames.com/a?w=300&resize=1&quality=medium", ArtSourcePolicy.RequestFor(new Uri("https://cdn1.epicgames.com/a"), 300, 1200).AbsoluteUri);
        Assert.Equal("https://cdn1.epicgames.com/a?w=200&resize=1&quality=medium", ArtSourcePolicy.RequestFor(new Uri("https://cdn1.epicgames.com/a"), 300, 200).AbsoluteUri); // never upscale
        Assert.Equal("https://cdn2.unrealengine.com/a?w=300&resize=1&quality=medium", ArtSourcePolicy.RequestFor(new Uri("https://cdn2.unrealengine.com/a"), 300, null).AbsoluteUri);
        Assert.Equal("https://example.com/a.png", ArtSourcePolicy.RequestFor(new Uri("https://example.com/a.png"), 300, 1200).AbsoluteUri); // only Epic's CDN knows the params
        Assert.Equal("https://cdn1.epicgames.com/a", ArtSourcePolicy.RequestFor(new Uri("https://cdn1.epicgames.com/a"), 0, 1200).AbsoluteUri); // logo: as delivered
        Assert.Equal("https://cdn1.epicgames.com/a?sig=1", ArtSourcePolicy.RequestFor(new Uri("https://cdn1.epicgames.com/a?sig=1"), 300, 1200).AbsoluteUri); // a signed URL is left alone
    }

    // ------------------------------------------------------------------ fetch + store

    [Fact]
    public async Task FirstRequestFetchesResizedPicture_ThenServesFromDisk_AcrossSessions()
    {
        var cache = Cache();
        var url = Url("Fortnite", ArtKind.TallThumb, TallUrl);

        var r = await cache.HandleAsync(url, CancellationToken.None);

        Assert.Equal(200, r.Status);
        Assert.Equal("public, max-age=31536000, immutable", r.CacheControl);
        Assert.Equal("image/jpeg", r.File!.ContentType);
        Assert.Equal(Path.Combine(_dir.Path, "art", "Fortnite", $"tall-thumb-{ArtUrls.HashOf(TallUrl)}.jpg"), r.File.Path);
        Assert.Equal(FakeFetcher.Jpeg(TallUrl), await File.ReadAllBytesAsync(r.File.Path));
        Assert.Equal([$"{TallUrl}?w=300&resize=1&quality=medium"], _fetcher.Requested);

        // A new session: the picture is a disk hit, no network.
        var next = Cache();
        var again = await next.HandleAsync(url, CancellationToken.None);
        Assert.Equal(200, again.Status);
        Assert.Single(_fetcher.Requested);
        Assert.Equal(0, next.Fetched);
    }

    [Fact]
    public async Task EachVariantUsesItsOwnSizeAndSource()
    {
        var cache = Cache();
        await cache.GetAsync("Fortnite", ArtKind.Tall, null, CancellationToken.None);
        await cache.GetAsync("Fortnite", ArtKind.Wide, null, CancellationToken.None);

        Assert.Contains($"{TallUrl}?w=600&resize=1&quality=medium", _fetcher.Requested);
        Assert.Contains($"{WideUrl}?w=1280&resize=1&quality=medium", _fetcher.Requested);
        Assert.True(File.Exists(Path.Combine(_dir.Path, "art", "Fortnite", $"tall-{ArtUrls.HashOf(TallUrl)}.jpg")));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "art", "Fortnite", $"wide-{ArtUrls.HashOf(WideUrl)}.jpg")));
    }

    [Fact]
    public async Task LogoIsStoredAsDelivered_WithItsOwnType()
    {
        const string logo = "https://cdn1.epicgames.com/item/fn/logo.png";
        _source.Set("Fortnite", ArtKind.Logo, logo, 800);
        _fetcher.Bytes[logo] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
        var cache = Cache();

        var file = await cache.GetAsync("Fortnite", ArtKind.Logo, null, CancellationToken.None);

        Assert.Equal("image/png", file!.ContentType);
        Assert.EndsWith($"logo-{ArtUrls.HashOf(logo)}.png", file.Path, StringComparison.Ordinal);
        Assert.Equal([logo], _fetcher.Requested); // no resize query
        var hit = await Cache().GetAsync("Fortnite", ArtKind.Logo, ArtUrls.HashOf(logo), CancellationToken.None);
        Assert.Equal("image/png", hit!.ContentType);
    }

    [Fact]
    public async Task ConcurrentRequestsForOneFile_ShareOneDownload()
    {
        _fetcher.Gate = new TaskCompletionSource();
        var cache = Cache();

        var requests = Enumerable.Range(0, 25).Select(_ => cache.GetAsync("Fortnite", ArtKind.TallThumb, ArtUrls.HashOf(TallUrl), CancellationToken.None)).ToList();
        await _fetcher.WaitForCallsAsync(1);
        await Task.Delay(50);
        Assert.Equal(1, _fetcher.Calls);
        _fetcher.Gate.SetResult();

        var files = await Task.WhenAll(requests);
        Assert.Equal(1, _fetcher.Calls);
        Assert.All(files, f => Assert.Equal(files[0]!.Path, f!.Path));
    }

    [Fact]
    public async Task Downloads_AreBoundedToSixAtATime()
    {
        for (var i = 0; i < 30; i++)
        {
            _source.Set($"App{i}", ArtKind.TallThumb, $"https://cdn1.epicgames.com/item/{i}/t", 1200);
        }

        _fetcher.Gate = new TaskCompletionSource();
        var cache = Cache();
        var requests = Enumerable.Range(0, 30).Select(i => cache.GetAsync($"App{i}", ArtKind.TallThumb, null, CancellationToken.None)).ToList();

        await _fetcher.WaitForCallsAsync(6);
        await Task.Delay(100);
        Assert.Equal(6, _fetcher.Calls);
        Assert.Equal(6, _fetcher.MaxConcurrent);

        _fetcher.Gate.SetResult();
        Assert.All(await Task.WhenAll(requests), f => Assert.NotNull(f));
        Assert.Equal(30, _fetcher.Calls);
        Assert.Equal(6, _fetcher.MaxConcurrent);
    }

    // ------------------------------------------------------------------ offline / failures / hostile input

    [Fact]
    public async Task Offline_ServesCached_ElseNotFoundWithoutTouchingTheNetwork()
    {
        var offline = false;
        var cache = Cache(new ArtCacheOptions { IsOffline = () => offline, PlaceholderPublishDelay = TimeSpan.Zero });
        Assert.Equal(200, (await cache.HandleAsync(Url("Fortnite", ArtKind.TallThumb, TallUrl), CancellationToken.None)).Status);

        offline = true;
        Assert.Equal(200, (await cache.HandleAsync(Url("Fortnite", ArtKind.TallThumb, TallUrl), CancellationToken.None)).Status); // cached
        var before = _fetcher.Calls;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var miss = await cache.HandleAsync(Url("Fortnite", ArtKind.Wide, WideUrl), CancellationToken.None);
        Assert.Equal(404, miss.Status);
        Assert.Null(miss.File);
        Assert.True(sw.ElapsedMilliseconds < 500);
        Assert.Equal(before, _fetcher.Calls);
    }

    [Fact]
    public async Task AFailedDownload_IsNotRetriedForAWhile_ThenIs()
    {
        _fetcher.Fail = true;
        var cache = Cache(new ArtCacheOptions { FailureTtl = TimeSpan.FromSeconds(30), PlaceholderPublishDelay = TimeSpan.Zero });
        var url = Url("Fortnite", ArtKind.TallThumb, TallUrl);

        Assert.Equal(404, (await cache.HandleAsync(url, CancellationToken.None)).Status);
        Assert.Equal(404, (await cache.HandleAsync(url, CancellationToken.None)).Status);
        Assert.Equal(1, _fetcher.Calls);

        _fetcher.Fail = false;
        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(200, (await cache.HandleAsync(url, CancellationToken.None)).Status);
        Assert.Equal(2, _fetcher.Calls);
    }

    [Fact]
    public async Task ABodyThatIsNotAnImage_IsNeverStored()
    {
        _fetcher.Bytes[TallUrl] = Encoding.UTF8.GetBytes("<html>Access denied</html>");
        var cache = Cache();

        Assert.Equal(404, (await cache.HandleAsync(Url("Fortnite", ArtKind.TallThumb, TallUrl), CancellationToken.None)).Status);
        Assert.False(Directory.Exists(Path.Combine(_dir.Path, "art", "Fortnite")) && Directory.EnumerateFiles(Path.Combine(_dir.Path, "art", "Fortnite")).Any());
    }

    [Fact]
    public async Task ASourceOnALocalOrPlainHttpHost_IsNeverFetched()
    {
        _source.Set("Evil", ArtKind.TallThumb, "http://127.0.0.1:9/x.jpg", 100);
        _source.Set("Evil2", ArtKind.TallThumb, "https://localhost/x.jpg", 100);
        var cache = Cache();

        Assert.Null(await cache.GetAsync("Evil", ArtKind.TallThumb, null, CancellationToken.None));
        Assert.Null(await cache.GetAsync("Evil2", ArtKind.TallThumb, null, CancellationToken.None));
        Assert.Equal(0, _fetcher.Calls);
    }

    [Fact]
    public async Task BadRequests_AreAnswered400_AndUnknownGamesAndImages404()
    {
        var cache = Cache();
        Assert.Equal(400, (await cache.HandleAsync("https://art.glider/../tall", CancellationToken.None)).Status);
        Assert.Equal(400, (await cache.HandleAsync("https://elsewhere.example/Fortnite/tall", CancellationToken.None)).Status);
        Assert.Equal(404, (await cache.HandleAsync("https://art.glider/NoSuchGame/tall", CancellationToken.None)).Status);
        Assert.Equal(404, (await cache.HandleAsync("https://art.glider/Fortnite/logo", CancellationToken.None)).Status); // no logo in the catalog
        Assert.Equal(0, _fetcher.Calls);
    }

    [Fact]
    public async Task ACacheMiss_WaitsForTheCatalog_ButOnlyBriefly()
    {
        var late = new FakeArtSource { Ready = new TaskCompletionSource() };
        var cache = new ArtCache(_dir.Path, late, _fetcher, _log, _time, new ArtCacheOptions { SourceWait = TimeSpan.FromMilliseconds(80) });
        _caches.Add(cache);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await cache.GetAsync("Fortnite", ArtKind.TallThumb, null, CancellationToken.None)); // catalog never ready
        Assert.InRange(sw.ElapsedMilliseconds, 60, 2000);

        var waiting = cache.GetAsync("Fortnite", ArtKind.TallThumb, null, CancellationToken.None);
        late.Set("Fortnite", ArtKind.TallThumb, TallUrl, 1200);
        late.Ready.SetResult();
        Assert.NotNull(await waiting);
    }

    // ------------------------------------------------------------------ changed art, LRU, migration

    [Fact]
    public async Task ChangedArt_ServesTheNewPicture_AndDropsTheOldFile()
    {
        var cache = Cache();
        var oldV = ArtUrls.HashOf(TallUrl);
        var first = await cache.GetAsync("Fortnite", ArtKind.TallThumb, oldV, CancellationToken.None);

        const string newTall = "https://cdn1.epicgames.com/item/fn/tall_v2-cccc";
        _source.Set("Fortnite", ArtKind.TallThumb, newTall, 1200);
        _source.Set("Fortnite", ArtKind.Tall, newTall, 1200);

        // The old URL is immutable: until the new picture is fetched it still answers with the bytes it always had.
        var served = await cache.GetAsync("Fortnite", ArtKind.TallThumb, oldV, CancellationToken.None);
        Assert.Equal(first!.Path, served!.Path);

        var fresh = await cache.GetAsync("Fortnite", ArtKind.TallThumb, ArtUrls.HashOf(newTall), CancellationToken.None);
        Assert.NotEqual(first.Path, fresh!.Path);
        Assert.Equal(FakeFetcher.Jpeg(newTall), await File.ReadAllBytesAsync(fresh.Path));
        Assert.False(File.Exists(first.Path)); // obsolete version removed
        Assert.True(File.Exists(Path.Combine(_dir.Path, "art", "Fortnite", $"tall-thumb-{ArtUrls.HashOf(newTall)}.jpg")));

        // A page still holding the old URL now gets the current picture.
        var stale = await cache.GetAsync("Fortnite", ArtKind.TallThumb, oldV, CancellationToken.None);
        Assert.Equal(fresh.Path, stale!.Path);
    }

    [Fact]
    public async Task Prune_RemovesLeastRecentlyUsedFirst_DownToNinetyPercent()
    {
        var cache = Cache(new ArtCacheOptions { MaxBytes = 1000, PlaceholderPublishDelay = TimeSpan.Zero });
        var dir = Path.Combine(_dir.Path, "art", "G");
        Directory.CreateDirectory(dir);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 10; i++)
        {
            var p = Path.Combine(dir, $"wide-{i:D10}.jpg");
            await File.WriteAllBytesAsync(p, new byte[150]);
            File.SetLastWriteTimeUtc(p, now.AddHours(-10 + i)); // i = 0 is the oldest
        }

        await File.WriteAllTextAsync(Path.Combine(_dir.Path, "art", "placeholders.json"), "{}"); // never counted or pruned
        Assert.Equal(1500, cache.SizeOnDisk());

        var freed = cache.Prune();

        Assert.Equal(600, freed); // four of the oldest: 1500 -> 900 = 90 % of the cap
        Assert.True(cache.SizeOnDisk() <= 900);
        Assert.False(File.Exists(Path.Combine(dir, "wide-0000000000.jpg")));
        Assert.False(File.Exists(Path.Combine(dir, "wide-0000000003.jpg")));
        Assert.True(File.Exists(Path.Combine(dir, "wide-0000000009.jpg")));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "art", "placeholders.json")));
        Assert.Equal(0, cache.Prune()); // under the cap now
    }

    [Fact]
    public async Task ACacheHit_RefreshesTheTimestamp_SoRecentlyShownArtSurvivesTrimming()
    {
        var cache = Cache(new ArtCacheOptions { TouchAfter = TimeSpan.FromHours(1), PlaceholderPublishDelay = TimeSpan.Zero });
        var file = (await cache.GetAsync("Fortnite", ArtKind.TallThumb, null, CancellationToken.None))!;
        File.SetLastWriteTimeUtc(file.Path, _time.GetUtcNow().UtcDateTime.AddDays(-30));

        await cache.GetAsync("Fortnite", ArtKind.TallThumb, ArtUrls.HashOf(TallUrl), CancellationToken.None);

        Assert.True(File.GetLastWriteTimeUtc(file.Path) > _time.GetUtcNow().UtcDateTime.AddMinutes(-1));
    }

    [Fact]
    public async Task V01FullSizeFile_IsAdopted_WithoutDownloading()
    {
        // v0.1 stored cache/art/<first 16 hex of SHA-256(url)>.jpg (the full-size original).
        var name = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(TallUrl)))[..16];
        Directory.CreateDirectory(Path.Combine(_dir.Path, "art"));
        var legacy = FakeFetcher.Jpeg("v0.1 original");
        await File.WriteAllBytesAsync(Path.Combine(_dir.Path, "art", $"{name}.jpg"), legacy);
        var cache = Cache(new ArtCacheOptions { IsOffline = () => true, PlaceholderPublishDelay = TimeSpan.Zero }); // even offline

        var thumb = await cache.GetAsync("Fortnite", ArtKind.TallThumb, null, CancellationToken.None);
        var tall = await cache.GetAsync("Fortnite", ArtKind.Tall, null, CancellationToken.None);

        Assert.Equal(0, _fetcher.Calls);
        Assert.Equal(legacy, await File.ReadAllBytesAsync(thumb!.Path));
        Assert.Equal(legacy, await File.ReadAllBytesAsync(tall!.Path));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "art", $"{name}.jpg"))); // v0.1 files are never deleted (downgrade safety)
        Assert.Null(await cache.GetAsync("Fortnite", ArtKind.Wide, null, CancellationToken.None)); // no legacy wide, and offline
    }

    // ------------------------------------------------------------------ image processor: shrinking + placeholder colours

    [Fact]
    public async Task AnImageLargerThanTheTarget_IsShrunkByTheHostProcessor()
    {
        var images = new FakeImages { Facts = new ImageFacts(1200, 1600, "#336699"), Shrunk = FakeFetcher.Jpeg("small") };
        var cache = Cache(images: images);

        var file = await cache.GetAsync("Fortnite", ArtKind.TallThumb, null, CancellationToken.None);

        Assert.Equal([300], images.DownscaledTo);
        Assert.Equal(images.Shrunk, await File.ReadAllBytesAsync(file!.Path));

        images.DownscaledTo.Clear();
        images.Facts = new ImageFacts(300, 400, "#336699"); // the CDN already resized it
        await cache.GetAsync("Fortnite", ArtKind.Tall, null, CancellationToken.None);
        Assert.Empty(images.DownscaledTo);
    }

    [Fact]
    public async Task TheCoverColour_IsLearnedOnce_PersistedAndOnlyValidForTheSameSource()
    {
        var images = new FakeImages { Facts = new ImageFacts(300, 400, "#336699") };
        var cache = Cache(images: images);
        var changed = 0;
        cache.PlaceholdersChanged += () => Interlocked.Increment(ref changed);

        await cache.GetAsync("Fortnite", ArtKind.TallThumb, null, CancellationToken.None);
        await Eventually(() => changed > 0);

        Assert.Equal("#336699", cache.PlaceholderFor("Fortnite", ArtUrls.HashOf(TallUrl)));
        Assert.Null(cache.PlaceholderFor("Fortnite", ArtUrls.HashOf(TallUrl + "other"))); // art changed: colour no longer applies
        Assert.Null(cache.PlaceholderFor("Nobody", ArtUrls.HashOf(TallUrl)));
        await cache.GetAsync("Fortnite", ArtKind.Wide, null, CancellationToken.None); // the wide image doesn't replace the tile colour
        Assert.Equal("#336699", cache.PlaceholderFor("Fortnite", ArtUrls.HashOf(TallUrl)));

        var next = Cache(images: images);
        next.LoadPlaceholders();
        Assert.Equal("#336699", next.PlaceholderFor("Fortnite", ArtUrls.HashOf(TallUrl)));
    }

    [Fact]
    public async Task WithoutAnImageProcessor_NoColourIsInvented()
    {
        var cache = Cache();
        await cache.GetAsync("Fortnite", ArtKind.TallThumb, null, CancellationToken.None);
        Assert.Empty(cache.Placeholders);
        Assert.False(cache.CanAnalyze);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}

public sealed class PixelMathTests
{
    private static byte[] Solid(int r, int g, int b, int count = 16, byte a = 255) =>
        Enumerable.Range(0, count).SelectMany(_ => new[] { (byte)b, (byte)g, (byte)r, a }).ToArray();

    [Fact]
    public void DominantColour_IsTheOpaqueAverage_PulledIntoACalmRange()
    {
        Assert.Matches("^#[0-9a-f]{6}$", PixelMath.DominantColor(Solid(200, 30, 30)));
        var red = PixelMath.DominantColor(Solid(200, 30, 30));
        var r = Convert.ToInt32(red[1..3], 16);
        var g = Convert.ToInt32(red[3..5], 16);
        Assert.True(r > g + 30, red); // still reads as red
        Assert.True(r <= 200); // but not brighter than the cover

        // White and black covers are pulled to mid-dark greys, never to #ffffff / #000000.
        Assert.NotEqual("#ffffff", PixelMath.DominantColor(Solid(255, 255, 255)));
        Assert.NotEqual("#000000", PixelMath.DominantColor(Solid(0, 0, 0)));
    }

    [Fact]
    public void TransparentPixelsAreIgnored_AndAnEmptyImageHasANeutralColour()
    {
        var mixed = Solid(10, 120, 200, 4).Concat(Solid(255, 0, 0, 40, a: 0)).ToArray();
        Assert.Equal(PixelMath.DominantColor(Solid(10, 120, 200, 4)), PixelMath.DominantColor(mixed));
        Assert.Equal("#1c1c22", PixelMath.DominantColor([]));
        Assert.Equal("#1c1c22", PixelMath.DominantColor(Solid(5, 5, 5, 8, a: 0)));
    }

    [Fact]
    public void SniffsOnlyRealImages()
    {
        Assert.True(ImageSniffer.TryGetKind([0xFF, 0xD8, 0xFF, 0xE0], out var jpeg, out var jpg));
        Assert.Equal(("image/jpeg", "jpg"), (jpeg, jpg));
        Assert.True(ImageSniffer.TryGetKind("RIFF\0\0\0\0WEBPVP8 "u8, out var webp, out _));
        Assert.Equal("image/webp", webp);
        Assert.False(ImageSniffer.TryGetKind("<html>"u8, out _, out _));
        Assert.False(ImageSniffer.TryGetKind([], out _, out _));
        Assert.Null(ImageSniffer.ContentTypeOfExtension("exe"));
    }
}

internal sealed class FakeArtSource : IArtSource
{
    private readonly ConcurrentDictionary<(string, ArtKind), ArtSource> _items = new();

    public TaskCompletionSource? Ready { get; set; }

    public void Set(string app, ArtKind kind, string url, int? width) => _items[(app, kind)] = new ArtSource(url, width);

    public ArtSource? Resolve(string app, ArtKind kind) => _items.GetValueOrDefault((app, kind));

    public Task WhenReadyAsync(CancellationToken ct) => Ready?.Task.WaitAsync(ct) ?? Task.CompletedTask;
}

internal sealed class FakeFetcher : IArtFetcher
{
    private int _calls;
    private int _active;
    private int _max;

    public ConcurrentQueue<string> RequestedQueue { get; } = new();
    public List<string> Requested => [.. RequestedQueue];
    public ConcurrentDictionary<string, byte[]> Bytes { get; } = new();
    public TaskCompletionSource? Gate { get; set; }
    public bool Fail { get; set; }
    public int Calls => Volatile.Read(ref _calls);
    public int MaxConcurrent => Volatile.Read(ref _max);

    /// <summary>A minimal JPEG-looking blob that is unique per key.</summary>
    public static byte[] Jpeg(string key) => [0xFF, 0xD8, 0xFF, 0xE0, .. Encoding.UTF8.GetBytes(key)];

    public async Task<byte[]?> FetchAsync(Uri url, int maxBytes, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        var now = Interlocked.Increment(ref _active);
        int seen;
        while (now > (seen = Volatile.Read(ref _max)) && Interlocked.CompareExchange(ref _max, now, seen) != seen)
        {
        }

        RequestedQueue.Enqueue(url.AbsoluteUri);
        try
        {
            if (Gate is { } g)
            {
                await g.Task.WaitAsync(ct);
            }

            if (Fail)
            {
                throw new HttpRequestException("offline");
            }

            var bare = new UriBuilder(url) { Query = "" }.Uri.AbsoluteUri;
            return Bytes.TryGetValue(bare, out var b) ? b : Jpeg(bare);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    public async Task WaitForCallsAsync(int n)
    {
        for (var i = 0; i < 300 && Calls < n; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(Calls >= n, $"expected {n} fetches, saw {Calls}");
    }
}

internal sealed class FakeImages : IImageProcessor
{
    public ImageFacts? Facts { get; set; }
    public byte[]? Shrunk { get; set; }
    public List<int> DownscaledTo { get; } = [];

    public ImageFacts? Analyze(ReadOnlySpan<byte> image) => Facts;

    public byte[]? Downscale(ReadOnlySpan<byte> image, int maxWidth)
    {
        DownscaledTo.Add(maxWidth);
        return Shrunk;
    }
}
