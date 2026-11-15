using System.Diagnostics;
using Glider.Core.Art;
using Glider.Core.Tests.Services;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Art;

/// <summary>
/// Numbers behind the "art appears at once" work (DECISIONS: art speed). The CDN is modelled, not measured: a fixed first-byte latency
/// per request, no bandwidth limit (a 300 px cover is ~25 KB), and unlimited server-side concurrency. They print their numbers and
/// assert generous relative floors (a loaded CI machine must stay green; losing the parallelism or the single request per tile must not).
/// </summary>
public sealed class ArtBenchmarks : IDisposable
{
    private const int Games = 300;
    private const int VisibleTiles = 30; // six columns, five rows: what a 1280×800 Library shows
    private static readonly TimeSpan CdnLatency = TimeSpan.FromMilliseconds(150);

    private readonly TempDir _dir = new();
    private readonly List<ArtCache> _caches = [];

    public void Dispose()
    {
        foreach (var c in _caches)
        {
            c.Dispose();
        }

        _dir.Dispose();
    }

    private static void Report(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private sealed class SlowFetcher(TimeSpan latency) : IArtFetcher
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public async Task<byte[]?> FetchAsync(Uri url, int maxBytes, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            await Task.Delay(latency, ct).ConfigureAwait(false);
            return FakeFetcher.Jpeg(url.AbsoluteUri);
        }
    }

    private (ArtCache Cache, SlowFetcher Fetcher, FakeArtSource Source) Make(int maxParallel, string? sub = null)
    {
        var source = new FakeArtSource();
        for (var i = 0; i < Games; i++)
        {
            var url = $"https://cdn1.epicgames.com/item/app{i:D4}/tall_1200x1600-{i:D4}";
            source.Set($"app{i:D4}", ArtKind.Tall, url, 1200);
            source.Set($"app{i:D4}", ArtKind.TallThumb, url, 1200);
        }

        var fetcher = new SlowFetcher(CdnLatency);
        var root = sub is null ? _dir.Path : Path.Combine(_dir.Path, sub);
        Directory.CreateDirectory(root);
        var cache = new ArtCache(root, source, fetcher, new FakeLog(), TimeProvider.System, new ArtCacheOptions { MaxParallel = maxParallel, PlaceholderPublishDelay = TimeSpan.Zero });
        _caches.Add(cache);
        return (cache, fetcher, source);
    }

    private static string UrlOf(int i, ArtKind kind) => ArtUrls.Build($"app{i:D4}", kind, $"https://cdn1.epicgames.com/item/app{i:D4}/tall_1200x1600-{i:D4}");

    private static async Task<double> TimeAllAsync(ArtCache cache, IEnumerable<string> urls)
    {
        var sw = Stopwatch.StartNew();
        var responses = await Task.WhenAll(urls.Select(u => cache.HandleAsync(u, CancellationToken.None)));
        var ms = sw.Elapsed.TotalMilliseconds;
        Assert.All(responses, r => Assert.Equal(200, r.Status));
        return ms;
    }

    /// <summary>
    /// Cold art cache, first screen of the Library. Before: every tile asked for the 600 px cover and the 300 px thumbnail (blur-up) and
    /// six downloads ran at once. After: one thumbnail per tile and sixteen at once.
    /// </summary>
    [Fact]
    public async Task ColdFirstScreen_OfTheLibrary()
    {
        var (before, beforeFetcher, _) = Make(maxParallel: 6, "before");
        var beforeMs = await TimeAllAsync(before, Enumerable.Range(0, VisibleTiles).SelectMany(i => new[] { UrlOf(i, ArtKind.Tall), UrlOf(i, ArtKind.TallThumb) }));

        var (after, afterFetcher, _) = Make(maxParallel: new ArtCacheOptions().MaxParallel, "after");
        var afterMs = await TimeAllAsync(after, Enumerable.Range(0, VisibleTiles).Select(i => UrlOf(i, ArtKind.TallThumb)));

        Report($"cold first screen, {VisibleTiles} tiles, {CdnLatency.TotalMilliseconds:0} ms CDN latency per request: " +
               $"old pattern (cover + thumbnail per tile, 6 parallel) {beforeFetcher.Calls} downloads in {beforeMs:0} ms; " +
               $"new pattern (thumbnail per tile, {new ArtCacheOptions().MaxParallel} parallel) {afterFetcher.Calls} downloads in {afterMs:0} ms");
        Assert.Equal(VisibleTiles, afterFetcher.Calls);
        Assert.True(afterMs * 3 < beforeMs, $"expected at least 3x faster: {beforeMs:0} ms → {afterMs:0} ms");
    }

    /// <summary>Warm art cache: every request is a disk hit and must cost next to nothing, also for a whole library at once.</summary>
    [Fact]
    public async Task WarmCache_AnswersAWholeLibraryFromDisk()
    {
        var (cache, fetcher, _) = Make(maxParallel: 6, "warm");
        await TimeAllAsync(cache, Enumerable.Range(0, Games).Select(i => UrlOf(i, ArtKind.TallThumb)));
        Assert.Equal(Games, fetcher.Calls);

        // A new session: a new cache object over the same folder, every picture already on disk.
        var (second, secondFetcher, _) = Make(maxParallel: 6, "warm");
        var first = await TimeAllAsync(second, Enumerable.Range(0, Games).Select(i => UrlOf(i, ArtKind.TallThumb)));
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < Games; i++)
        {
            await second.HandleAsync(UrlOf(i, ArtKind.TallThumb), CancellationToken.None);
        }

        var repeatedMs = sw.Elapsed.TotalMilliseconds;
        Report($"warm start, {Games} covers: all requests at once {first:0.0} ms ({first / Games * 1000:0} µs each); one by one again {repeatedMs:0.0} ms ({repeatedMs / Games * 1000:0} µs each); network calls {secondFetcher.Calls}");
        Assert.Equal(0, secondFetcher.Calls);
        Assert.True(first / Games < 5, $"a disk hit took {first / Games:0.00} ms");
    }
}
