using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;
using Glider.Core.Library;
using Glider.Core.Tests.Services;

namespace Glider.Core.Tests.Library;

public class LibraryServiceTests
{
    private static List<LibraryDelta> Deltas(Rig rig, long after) =>
        rig.Sink.Of<LibraryDelta>(RpcEvents.Library).Where(d => d.Revision > after).ToList();

    [Fact]
    public async Task DeltasCarryOnlyWhatChanged_WithConsecutiveRevisions()
    {
        using var rig = new Rig();
        rig.AddGame("A", "Game A");
        rig.AddGame("B", "Game B");
        await rig.StartWarmAsync();
        var full = await rig.Service.GetLibrary();
        Assert.True(full.Reset);
        Assert.Equal(["A", "B", FortniteLayout.AppName], full.Upserts.Select(g => g.AppName));
        var r0 = full.Revision;

        await rig.Service.SetFavorite(new GameFlagRequest("A", true));
        await rig.Service.LibraryForTests.FlushAsync();
        File.Delete(Path.Combine(rig.Paths.LegendaryConfig, "metadata", "B.json"));
        await rig.Service.LibraryForTests.ReloadAsync();
        rig.AddGame("C", "Game C");
        await rig.Service.LibraryForTests.ReloadAsync();

        var deltas = Deltas(rig, r0);
        Assert.Equal(Enumerable.Range(1, deltas.Count).Select(i => r0 + i), deltas.Select(d => d.Revision)); // exactly +1 each
        Assert.Contains(deltas, d => d.Upserts.Count == 1 && d.Upserts[0].AppName == "A" && d.Upserts[0].Favorite && d.Removes.Count == 0);
        Assert.Contains(deltas, d => d.Removes.SequenceEqual(["B"]));
        Assert.Contains(deltas, d => d.Upserts.Any(u => u.AppName == "C"));
        Assert.All(deltas, d => Assert.False(d.Reset));
        Assert.DoesNotContain(deltas.SelectMany(d => d.Upserts), u => u.AppName == FortniteLayout.AppName); // untouched games are never resent

        var again = await rig.Service.GetLibrary();
        Assert.Equal(deltas[^1].Revision, again.Revision);
        Assert.Equal(["A", "C", FortniteLayout.AppName], again.Upserts.Select(g => g.AppName));
        Assert.Equal(again.Revision, (await rig.Service.GetSnapshot()).Library.Revision);
    }

    [Fact]
    public async Task ChangesWithinTheWindowCoalesceIntoOneDelta()
    {
        using var rig = new Rig { LibraryCoalesce = TimeSpan.FromMilliseconds(50) };
        rig.AddGame("A", "Game A");
        rig.AddGame("B", "Game B");
        await rig.StartWarmAsync();
        var r0 = (await rig.Service.GetLibrary()).Revision;

        await rig.Service.SetFavorite(new GameFlagRequest("A", true));
        await rig.Service.SetHidden(new GameFlagRequest("B", true));
        await rig.Service.SetFavorite(new GameFlagRequest(FortniteLayout.AppName, true));
        Assert.Empty(Deltas(rig, r0)); // nothing until the 50 ms window closes

        rig.Time.Advance(TimeSpan.FromMilliseconds(60));
        await rig.Service.LibraryForTests.FlushAsync(); // the timer's flush runs in the background
        var d = Assert.Single(Deltas(rig, r0));
        Assert.Equal(r0 + 1, d.Revision);
        Assert.Equal(["A", "B", FortniteLayout.AppName], d.Upserts.Select(u => u.AppName));
    }

    [Fact]
    public async Task TheLibraryIsCachedForTheNextColdStart()
    {
        using var rig = new Rig();
        rig.AddGame("A", "Game A");
        await rig.StartWarmAsync();
        await rig.Service.SetFavorite(new GameFlagRequest("A", true));
        await rig.Service.LibraryForTests.FlushAsync();
        await rig.Service.LibraryForTests.SaveCacheAsync();

        var cache = JsonSerializer.Deserialize(File.ReadAllBytes(LibraryService.PathFor(rig.Paths)), EngineJsonContext.Default.LibraryCacheFile)!;
        Assert.Equal(2, cache.SchemaVersion);
        Assert.True(cache.Games.Single(g => g.AppName == "A").Favorite);
        Assert.Contains(cache.Games, g => g.AppName == FortniteLayout.AppName);
    }

    [Fact]
    public async Task FiveHundredGames_LoadAndRebuildQuickly()
    {
        using var rig = new Rig();
        for (var i = 0; i < 500; i++)
        {
            rig.AddGame($"g{i:000}", $"Game {i:000}");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await rig.StartWarmAsync();
        var lib = await rig.Service.GetLibrary();
        sw.Stop();
        Assert.Equal(501, lib.Upserts.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");

        sw.Restart();
        await rig.Service.SetFavorite(new GameFlagRequest("g250", true));
        await rig.Service.LibraryForTests.FlushAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"rebuild took {sw.Elapsed}");
    }
}
