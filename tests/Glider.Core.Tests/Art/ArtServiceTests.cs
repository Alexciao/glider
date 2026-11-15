using Glider.Core.Infrastructure;
using System.Text.Json;
using Glider.Core.Art;
using Glider.Core.Contracts;
using Glider.Core.Library;
using Glider.Core.Services;
using Glider.Core.Tests.Services;

namespace Glider.Core.Tests.Art;

/// <summary>The art cache inside the real service graph: summaries carry art.glider URLs, the catalog resolves them on demand.</summary>
public sealed class ArtServiceTests
{
    private const string Fn = FortniteLayout.AppName;

    [Fact]
    public async Task SummaryUrls_AreServedFromTheCatalogSource_ThenFromDisk()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        var art = (await rig.SummaryAsync())!.Art;
        Assert.StartsWith("https://art.glider/Fortnite/tall?v=", art.Tall, StringComparison.Ordinal);
        Assert.StartsWith("https://art.glider/Fortnite/tall-thumb?v=", art.TallThumb, StringComparison.Ordinal);
        Assert.StartsWith("https://art.glider/Fortnite/wide?v=", art.Wide, StringComparison.Ordinal);

        var thumb = await rig.Service.Art.HandleAsync(art.TallThumb!, CancellationToken.None);
        var wide = await rig.Service.Art.HandleAsync(art.Wide!, CancellationToken.None);

        Assert.Equal((200, 200), (thumb.Status, wide.Status));
        var source = GameCatalog.ArtSourceOf(rig.Service.LibraryForTests.CatalogItem(Fn)!, ArtKind.TallThumb)!;
        Assert.Contains($"{source.Url}?w=300&resize=1&quality=medium", rig.ArtFetcher.Requested);
        Assert.Equal(2, rig.ArtFetcher.Calls);

        await rig.Service.Art.HandleAsync(art.TallThumb!, CancellationToken.None);
        Assert.Equal(2, rig.ArtFetcher.Calls); // disk hit
    }

    [Fact]
    public async Task TheLibraryFillsTheArtCacheAheadOfTheScreen()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        for (var i = 0; i < 500 && rig.Service.Art.Prefetched == 0; i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        // Nobody asked for a picture: the cover of every game is downloaded anyway, so the Library paints from disk.
        var source = GameCatalog.ArtSourceOf(rig.Service.LibraryForTests.CatalogItem(Fn)!, ArtKind.TallThumb)!;
        Assert.Contains($"{source.Url}?w=300&resize=1&quality=medium", rig.ArtFetcher.Requested);
        var art = (await rig.SummaryAsync())!.Art;
        var before = rig.ArtFetcher.Calls;
        Assert.Equal(200, (await rig.Service.Art.HandleAsync(art.TallThumb!, CancellationToken.None)).Status);
        Assert.Equal(before, rig.ArtFetcher.Calls); // a disk hit
    }

    [Fact]
    public async Task AColdStart_ServesTheCachedUrls_EvenBeforeLegendarysFilesAreRead()
    {
        string url;
        byte[] libraryJson;
        using (var first = new Rig())
        {
            await first.StartWarmAsync();
            url = (await first.SummaryAsync())!.Art.TallThumb!;
            await first.Service.LibraryForTests.SaveCacheAsync();
            libraryJson = (await AtomicFile.TryReadAllBytesAsync(LibraryService.PathFor(first.Paths), TestContext.Current.CancellationToken))!;
        }

        // Second run: library.json holds the URLs, the pre-warm is held back, and the picture is wanted at once.
        using var again = new Rig();
        Directory.CreateDirectory(again.Paths.Cache);
        await File.WriteAllBytesAsync(LibraryService.PathFor(again.Paths), libraryJson);
        again.Legendary.VersionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await again.Service.StartAsync();
        Assert.False(again.Service.LibraryForTests.FilesRead);
        Assert.Equal(url, again.Service.LibraryForTests.Games[Fn].Art.TallThumb);

        // A miss right after start: the cached URL is resolved against the catalog as soon as it is readable (never a hang, never a 404).
        // The request parks until the library files are read (the pre-warm's first step after the held-back version check).
        var pending = again.Service.Art.HandleAsync(url, CancellationToken.None);
        Assert.False(pending.IsCompleted);
        again.Legendary.VersionGate.SetResult();
        var response = await pending;

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public async Task ALearnedColour_ReachesTheLibraryAndSurvivesARebuild()
    {
        using var rig = new Rig();
        var service = rig.Service;
        await rig.StartWarmAsync();
        Assert.Equal(GameCatalog.Placeholder(Fn), (await rig.SummaryAsync())!.Art.Placeholder);

        // The service's own cache has already prefetched the cover; start from an empty folder so this cache downloads it itself.
        for (var i = 0; i < 500 && !service.Art.PrefetchIdle; i++)
        {
            await Task.Delay(10);
        }

        Directory.Delete(Path.Combine(rig.Paths.Cache, "art"), recursive: true);

        // The host's decoder reports a colour for the tile picture; the library publishes it as a normal delta.
        var processor = new FakeImages { Facts = new ImageFacts(300, 400, "#2a1d5e") };
        using var withColours = new ArtCache(
            rig.Paths.Cache, service.LibraryForTests, rig.ArtFetcher, rig.Log, rig.Time,
            new ArtCacheOptions { PlaceholderPublishDelay = TimeSpan.Zero }, processor);
        withColours.LoadPlaceholders();
        await withColours.GetAsync(Fn, ArtKind.TallThumb, null, CancellationToken.None);
        for (var i = 0; i < 100 && withColours.Placeholders.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal("#2a1d5e", withColours.Placeholders[Fn].Color);
        var file = Path.Combine(rig.Paths.Cache, "art", "placeholders.json");
        for (var i = 0; i < 6000 && !File.Exists(file); i++) // a safety net: the debounced write lands as soon as it can
        {
            await Task.Delay(10);
        }

        using var doc = JsonDocument.Parse((await AtomicFile.TryReadAllBytesAsync(file, TestContext.Current.CancellationToken))!);
        Assert.Equal("#2a1d5e", doc.RootElement.GetProperty(Fn).GetProperty("c").GetString());
    }
}
