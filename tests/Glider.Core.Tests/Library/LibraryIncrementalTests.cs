using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;
using Glider.Core.Library;
using Glider.Core.Tests.Services;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Library;

/// <summary>
/// WP4 (docs/review-v0.2-architecture.md H4, M6): library.get at cold start is served from cache/library.json without waiting for
/// Legendary's metadata, and a rebuild recomputes only the games whose inputs changed (and writes the cache only when something did).
/// </summary>
public sealed class LibraryIncrementalTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static List<LibraryDelta> Deltas(Rig rig, long after) =>
        rig.Sink.Of<LibraryDelta>(RpcEvents.Library).Where(d => d.Revision > after).ToList();

    private static GameSummary Cached(string app, string title) => new(
        app, title, GameKind.Game, null, "ns", "id-" + app, null, InstallStatus.NotInstalled, null, "1", false, null, null, null, null, false,
        SaveSyncStatus.NotSupported, true, ThirdPartyStore.None, null, 0, false, false, null, null, 0,
        new GameArt("https://art.glider/x/tall?v=0123456789", null, null, null, "#123456"), RunState.Idle, null, null, false, false);

    private static async Task WriteCacheAsync(Rig rig, params GameSummary[] games)
    {
        Directory.CreateDirectory(rig.Paths.Cache);
        await File.WriteAllBytesAsync(LibraryService.PathFor(rig.Paths),
            JsonSerializer.SerializeToUtf8Bytes(new LibraryCacheFile { Games = [.. games] }, EngineJsonContext.Default.LibraryCacheFile), TestContext.Current.CancellationToken);
    }

    private static async Task WaitForWritesAsync(LibraryService library, long writes)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (library.CacheWrites < writes && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(writes, library.CacheWrites);
    }

    // ------------------------------------------------------------------ H4: cold start

    [Fact]
    public async Task LibraryGet_AtColdStart_ServesTheCache_AndLeavesTheFileParseToTheBackground()
    {
        using var rig = new Rig();
        rig.AddGame("A", "Game A");
        await WriteCacheAsync(rig, Cached("Cached1", "Cached One"), Cached("Cached2", "Cached Two"));
        var gate = rig.Legendary.VersionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); // pre-warm not started

        await rig.Service.StartAsync();
        var get = await rig.Service.GetLibrary(); // the UI's first request
        Assert.True(get.Reset);
        Assert.Equal(["Cached1", "Cached2"], get.Upserts.Select(g => g.AppName));
        Assert.False(rig.Service.LibraryForTests.FilesRead); // nothing parsed Legendary's files on this path

        var detail = await rig.Service.GetGameDetail(new AppRef("Cached1")); // game.getDetail has the same coupling
        Assert.Equal("Cached One", detail.Summary.Title);
        Assert.False(rig.Service.LibraryForTests.FilesRead);

        gate.SetResult(); // the file-based rebuild follows as one delta, which the UI applies in order
        await rig.Service.WarmupTask;
        await rig.Service.LibraryForTests.FlushAsync();
        Assert.True(rig.Service.LibraryForTests.FilesRead);
        var delta = Assert.Single(Deltas(rig, get.Revision));
        Assert.Equal(get.Revision + 1, delta.Revision);
        Assert.False(delta.Reset);
        Assert.Contains(delta.Upserts, g => g.AppName == "A");
        Assert.Equal(["Cached1", "Cached2"], delta.Removes);
    }

    [Fact]
    public async Task LibraryGet_WithoutACache_WaitsForTheFirstRead()
    {
        using var rig = new Rig();
        rig.AddGame("A", "Game A");
        await rig.Service.StartAsync();
        var get = await rig.Service.GetLibrary(); // nothing cached: the files are the only source
        Assert.Contains(get.Upserts, g => g.AppName == "A");
        Assert.True(rig.Service.LibraryForTests.FilesRead);
    }

    [Fact]
    public async Task LibraryGet_IsTheSameInstanceUntilTheLibraryChanges()
    {
        using var rig = new Rig();
        rig.AddGame("A", "Game A");
        await rig.StartWarmAsync();
        var first = await rig.Service.GetLibrary();
        var second = await rig.Service.GetLibrary();
        Assert.Same(first.Upserts, second.Upserts); // the sorted list is built once per revision

        await rig.Service.SetFavorite(new GameFlagRequest("A", true));
        await rig.Service.LibraryForTests.FlushAsync();
        var third = await rig.Service.GetLibrary();
        Assert.Equal(first.Revision + 1, third.Revision);
        Assert.True(third.Upserts.Single(g => g.AppName == "A").Favorite);
        Assert.False(first.Upserts.Single(g => g.AppName == "A").Favorite); // what was handed out earlier is untouched
    }

    // ------------------------------------------------------------------ M6: incremental rebuild

    [Fact]
    public async Task OneGameChanged_RecomputesOneSummary_AndSendsOneUpsert()
    {
        using var rig = new Rig();
        for (var i = 0; i < 20; i++)
        {
            rig.AddGame($"G{i:00}", $"Game {i:00}", installedVersion: i % 4 == 0 ? "1.0" : null);
        }

        await rig.StartWarmAsync();
        var library = rig.Service.LibraryForTests;
        var total = library.Games.Count;
        var r0 = library.Revision;

        await rig.Service.SetFavorite(new GameFlagRequest("G07", true));
        await library.FlushAsync();
        Assert.Equal((1, total - 1), library.LastBuild);
        var delta = Assert.Single(Deltas(rig, r0));
        Assert.Equal("G07", Assert.Single(delta.Upserts).AppName);
        Assert.Empty(delta.Removes);
    }

    [Fact]
    public async Task NothingChanged_RecomputesNothing_PublishesNothing_AndWritesNoCache()
    {
        using var rig = new Rig();
        for (var i = 0; i < 20; i++)
        {
            rig.AddGame($"G{i:00}", $"Game {i:00}");
        }

        await rig.StartWarmAsync();
        var library = rig.Service.LibraryForTests;
        await library.SaveCacheAsync();
        var (revision, writes) = (library.Revision, library.CacheWrites);
        Assert.True(writes >= 1);

        library.Invalidate(); // a job tick, a settings write, an art notification
        await library.FlushAsync();
        await library.ReloadAsync(); // a re-read of files that did not change
        await library.SaveCacheAsync();

        Assert.Equal(0, library.LastBuild.Rebuilt);
        Assert.Equal(revision, library.Revision);
        Assert.Equal(writes, library.CacheWrites);
    }

    [Fact]
    public async Task ARewrittenMetadataFile_RecomputesOnlyThatGame()
    {
        using var rig = new Rig();
        for (var i = 0; i < 10; i++)
        {
            rig.AddGame($"G{i:00}", $"Game {i:00}");
        }

        await rig.StartWarmAsync();
        var library = rig.Service.LibraryForTests;
        var total = library.Games.Count;
        var r0 = library.Revision;

        rig.AddGame("G03", "Game 03 Renamed"); // metadata/G03.json rewritten; assets.json and entitlements.json rewritten with the same content
        await library.ReloadAsync();

        Assert.Equal((1, total - 1), library.LastBuild);
        var delta = Assert.Single(Deltas(rig, r0));
        var upsert = Assert.Single(delta.Upserts);
        Assert.Equal(("G03", "Game 03 Renamed"), (upsert.AppName, upsert.Title));
    }

    [Fact]
    public async Task ANewInstall_RecomputesOnlyThatGame()
    {
        using var rig = new Rig();
        for (var i = 0; i < 10; i++)
        {
            rig.AddGame($"G{i:00}", $"Game {i:00}");
        }

        await rig.StartWarmAsync();
        var library = rig.Service.LibraryForTests;
        var total = library.Games.Count;
        var r0 = library.Revision;

        rig.AddGame("G05", "Game 05", installedVersion: "1.0"); // installed.json (Legendary's) and Glider's record change
        await library.ReloadAsync();

        Assert.Equal((1, total - 1), library.LastBuild);
        var upsert = Assert.Single(Assert.Single(Deltas(rig, r0)).Upserts);
        Assert.Equal(("G05", InstallStatus.Installed), (upsert.AppName, upsert.InstallStatus));
    }

    [Fact]
    public async Task TheCacheIsWrittenOffTheHotPath_Debounced_AndOnlyWhenSomethingChanged()
    {
        using var rig = new Rig { LibraryCacheDelay = TimeSpan.FromSeconds(2) };
        rig.AddGame("A", "Game A");
        await rig.StartWarmAsync();
        var library = rig.Service.LibraryForTests;
        Assert.Equal(0, library.CacheWrites); // the rebuild published a delta, the write waits out its quiet delay

        rig.Time.Advance(TimeSpan.FromSeconds(3));
        await WaitForWritesAsync(library, 1); // the timer's write, off the rebuild's path
        Assert.Contains("Game A", await File.ReadAllTextAsync(LibraryService.PathFor(rig.Paths), TestContext.Current.CancellationToken), StringComparison.Ordinal);

        await rig.Service.SetFavorite(new GameFlagRequest("A", true)); // changes keep coming...
        await library.FlushAsync();
        rig.Time.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, library.CacheWrites); // ...but the file is rewritten at most every 30 s

        rig.Time.Advance(TimeSpan.FromSeconds(30));
        await WaitForWritesAsync(library, 2);
        var cache = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(LibraryService.PathFor(rig.Paths), TestContext.Current.CancellationToken), EngineJsonContext.Default.LibraryCacheFile)!;
        Assert.True(cache.Games.Single(g => g.AppName == "A").Favorite);

        await library.SaveCacheAsync(); // shutdown with nothing new: no write
        Assert.Equal(2, library.CacheWrites);
    }

    // ------------------------------------------------------------------ the pure catalog

    [Fact]
    public async Task Catalog_BuildWithAMemo_EqualsAFullBuild_AndReusesWhatDidNotChange()
    {
        var f = new LibraryFixture(_dir.Path)
            .AddCatalog(FortniteLayout.AppName, LibraryFixture.FortniteCatalog, "v2")
            .AddCatalog(LibraryFixture.AlienApp, LibraryFixture.AlienCatalog, "1.0")
            .AddCatalog(LibraryFixture.AlienDlcApp, LibraryFixture.AlienDlcCatalog, "1.0")
            .AddCatalog(LibraryFixture.CastlevaniaApp, LibraryFixture.CastlevaniaCatalog, "2.0")
            .Entitle("fn", null, "2020-01-01T00:00:00.000Z", "4fe75bbc5a674f4f9b356b5c90567da5");
        f.Save();
        var reader = new LegendaryLibraryReader(_dir.Path, retryDelay: TimeSpan.FromMilliseconds(1));
        var lib = await reader.ReadAsync(null, TestContext.Current.CancellationToken);
        var memo = new CatalogMemo();
        var first = GameCatalog.Build(lib, CatalogContext.Empty, memo);
        Assert.Equal((first.Count, 0), (memo.Rebuilt, memo.Reused));
        Assert.Equal(GameCatalog.Build(lib, CatalogContext.Empty).Values.OrderBy(g => g.AppName), first.Values.OrderBy(g => g.AppName)); // same as without a memo

        var again = GameCatalog.Build(lib, CatalogContext.Empty, memo);
        Assert.Equal((0, first.Count), (memo.Rebuilt, memo.Reused));
        Assert.All(again, kv => Assert.Same(first[kv.Key], kv.Value)); // the very same instances: the diff sees no change at all

        // One game's Glider record changed.
        var ctx = CatalogContext.Empty with { Glider = CatalogContext.Empty.Glider.SetItem(LibraryFixture.CastlevaniaApp, GameEntry.Empty with { Favorite = true }) };
        var changed = GameCatalog.Build(lib, ctx, memo);
        Assert.Equal(1, memo.Rebuilt);
        Assert.True(changed[LibraryFixture.CastlevaniaApp].Favorite);
        Assert.Equal(GameCatalog.Build(lib, ctx).Values.OrderBy(g => g.AppName), changed.Values.OrderBy(g => g.AppName));

        // A DLC arriving or leaving changes its base game's count: base and DLC are recomputed, nobody else.
        var withoutDlc = lib with { Items = lib.Items.Remove(LibraryFixture.AlienDlcApp) };
        var smaller = GameCatalog.Build(withoutDlc, ctx, memo);
        Assert.Equal(GameCatalog.Build(withoutDlc, ctx).Values.OrderBy(g => g.AppName), smaller.Values.OrderBy(g => g.AppName));
        Assert.True(memo.Rebuilt <= 2, $"{memo.Rebuilt} summaries recomputed");

        // The entitlements are global (the added date): when they change, everything is recomputed.
        var newer = lib with { Entitlements = [.. lib.Entitlements!, new EntitlementInfo("fn", null, null, DateTimeOffset.UnixEpoch)] };
        var regrant = GameCatalog.Build(newer, ctx, memo);
        Assert.Equal(0, memo.Reused);
        Assert.Equal(GameCatalog.Build(newer, ctx).Values.OrderBy(g => g.AppName), regrant.Values.OrderBy(g => g.AppName));
    }
}
