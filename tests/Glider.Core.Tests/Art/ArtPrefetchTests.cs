using Glider.Core.Art;
using Glider.Core.Tests.Services;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Art;

public sealed class ArtPrefetchTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeArtSource _source = new();
    private readonly FakeFetcher _fetcher = new();
    private readonly List<ArtCache> _caches = [];
    private bool _offline;

    public void Dispose()
    {
        foreach (var c in _caches)
        {
            c.Dispose();
        }

        _dir.Dispose();
    }

    private ArtCache Cache(ArtCacheOptions? options = null)
    {
        var c = new ArtCache(_dir.Path, _source, _fetcher, new FakeLog(), TimeProvider.System, (options ?? new ArtCacheOptions()) with { PlaceholderPublishDelay = TimeSpan.Zero, IsOffline = () => _offline });
        _caches.Add(c);
        return c;
    }

    private void Games(int n)
    {
        for (var i = 0; i < n; i++)
        {
            _source.Set($"app{i:D3}", ArtKind.TallThumb, $"https://cdn1.epicgames.com/item/app{i:D3}/tall", 1200);
            _source.Set($"app{i:D3}", ArtKind.Wide, $"https://cdn1.epicgames.com/item/app{i:D3}/wide", 2560);
        }
    }

    private static async Task WaitUntil(Func<bool> c)
    {
        for (var i = 0; i < 500 && !c(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(c(), "condition not reached");
    }

    [Fact]
    public async Task EveryCoverIsDownloadedOnceInTheOrderGiven()
    {
        Games(20);
        var cache = Cache(new ArtCacheOptions { PrefetchParallel = 1 });
        cache.Prefetch(Enumerable.Range(0, 20).Select(i => ($"app{i:D3}", ArtKind.TallThumb)));
        await WaitUntil(() => cache.Prefetched == 20);
        Assert.Equal(20, _fetcher.Calls);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"https://cdn1.epicgames.com/item/app{i:D3}/tall"), _fetcher.Requested.Select(u => u[..u.IndexOf('?')]));
    }

    [Fact]
    public async Task PrefetchLeavesMostSlotsToThePage()
    {
        Games(30);
        _fetcher.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = Cache(new ArtCacheOptions { MaxParallel = 8, PrefetchParallel = 3 });
        cache.Prefetch(Enumerable.Range(0, 30).Select(i => ($"app{i:D3}", ArtKind.TallThumb)));
        await _fetcher.WaitForCallsAsync(3);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(3, _fetcher.MaxConcurrent); // never more than its share

        // The page asks for a cover prefetch has not reached: it is fetched at once, not behind the other 27.
        var page = cache.HandleAsync(ArtUrls.Build("app029", ArtKind.TallThumb, "https://cdn1.epicgames.com/item/app029/tall"), CancellationToken.None);
        await _fetcher.WaitForCallsAsync(4);
        _fetcher.Gate.SetResult();
        Assert.Equal(200, (await page.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Status);
        await WaitUntil(() => cache.Prefetched == 30);
        Assert.Equal(30, _fetcher.Calls); // the page's request joined the prefetch's download or was already cached, never a second one
    }

    [Fact]
    public async Task CoversAlreadyOnDiskAreNotDownloadedAgainAndNothingIsQueuedTwice()
    {
        Games(5);
        var cache = Cache();
        var plan = Enumerable.Range(0, 5).Select(i => ($"app{i:D3}", ArtKind.TallThumb)).ToList();
        cache.Prefetch(plan);
        await WaitUntil(() => cache.Prefetched == 5);
        cache.Prefetch(plan);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(5, _fetcher.Calls);

        // A new session over the same folder finds them on disk.
        var second = Cache();
        second.Prefetch(plan);
        await WaitUntil(() => second.Prefetched == 5);
        Assert.Equal(5, _fetcher.Calls);
    }

    [Fact]
    public async Task ChangedArtIsPrefetchedAgain()
    {
        Games(1);
        var cache = Cache();
        cache.Prefetch([("app000", ArtKind.TallThumb)]);
        await WaitUntil(() => cache.Prefetched == 1);
        _source.Set("app000", ArtKind.TallThumb, "https://cdn1.epicgames.com/item/app000/tall-v2", 1200);
        cache.Prefetch([("app000", ArtKind.TallThumb)]);
        await WaitUntil(() => cache.Prefetched == 2);
        Assert.Equal(2, _fetcher.Calls);
    }

    [Fact]
    public async Task OfflineItDoesNothingAndTheNextCallStartsOver()
    {
        Games(5);
        _offline = true;
        var cache = Cache();
        var plan = Enumerable.Range(0, 5).Select(i => ($"app{i:D3}", ArtKind.TallThumb)).ToList();
        cache.Prefetch(plan);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(0, _fetcher.Calls);
        _offline = false;
        cache.Prefetch(plan);
        await WaitUntil(() => cache.Prefetched == 5);
        Assert.Equal(5, _fetcher.Calls);
    }

    [Fact]
    public async Task ARunOfFailuresStopsItForTheRun()
    {
        Games(40);
        _fetcher.Fail = true;
        var cache = Cache(new ArtCacheOptions { PrefetchParallel = 1, PrefetchGiveUpAfter = 4 });
        cache.Prefetch(Enumerable.Range(0, 40).Select(i => ($"app{i:D3}", ArtKind.TallThumb)));
        await WaitUntil(() => _fetcher.Calls >= 4);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.True(_fetcher.Calls <= 6, $"kept going after the failures: {_fetcher.Calls} downloads");
        Assert.Equal(0, cache.Prefetched);
    }

    [Fact]
    public void UnknownAndUnsafeAppsAreSkipped()
    {
        Games(1);
        var cache = Cache();
        cache.Prefetch([("../evil", ArtKind.TallThumb), ("nosuchapp", ArtKind.TallThumb)]);
        Assert.Equal(0, _fetcher.Calls);
    }
}
