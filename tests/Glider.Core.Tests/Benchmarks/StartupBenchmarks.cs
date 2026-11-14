using System.Diagnostics;
using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;
using Glider.Core.Library;
using Glider.Core.Tests.Services;

namespace Glider.Core.Tests.Benchmarks;

/// <summary>
/// Cold-start numbers behind docs/perf.md ("interactive library with 500 cached games under 1.5 s"): the Core's share of that is
/// reading cache/library.json, publishing the first snapshot and answering library.get. They print their numbers and assert generous
/// floors (a loaded CI machine must stay green; an order-of-magnitude regression must not).
/// </summary>
public sealed class StartupBenchmarks
{
    /// <summary>Slack on the wall-clock budgets of the Core-only numbers (a loaded machine must stay green; a regression must not).</summary>
    internal const int LoadSlack = 3;

    /// <summary>The v0.2 target for `library.get` at cold start, asserted without any slack (docs/perf.md).</summary>
    private const double LibraryGetBudgetMs = 50;

    private const int Games = 500;

    private static void Report(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    /// <summary>A library shaped like a real one: 32-hex ids, art URLs, ~15 % installed, some DLC.</summary>
    internal static List<GameSummary> SyntheticLibrary(int count)
    {
        var rng = new Random(42);
        string Hex() { var b = new byte[16]; rng.NextBytes(b); return Convert.ToHexStringLower(b); }
        var list = new List<GameSummary>(count);
        for (var i = 0; i < count; i++)
        {
            var app = Hex();
            var installed = i % 7 == 0;
            var art = new GameArt(
                $"https://art.glider/{app}/tall?v={Hex()[..10]}", $"https://art.glider/{app}/tall-thumb?v={Hex()[..10]}",
                $"https://art.glider/{app}/wide?v={Hex()[..10]}", i % 3 == 0 ? $"https://art.glider/{app}/logo?v={Hex()[..10]}" : null, "#2a1d5e");
            list.Add(new GameSummary(
                app, $"Synthetic Game Number {i} of the Library", i % 25 == 0 ? GameKind.Dlc : GameKind.Game, null, Hex(), Hex(), "Some Studio Games Ltd",
                installed ? InstallStatus.Installed : InstallStatus.NotInstalled, installed ? "1.2.3-12345+Release" : null, "1.2.4-12399+Release", installed && i % 14 == 0,
                installed ? $@"D:\Games\Epic\Synthetic Game Number {i}" : null, 40_000_000_000, installed ? 41_000_000_000 : null, null, i % 2 == 0,
                SaveSyncStatus.Unknown, true, ThirdPartyStore.None, null, i % 25 == 0 ? 0 : 1, i % 11 == 0, false,
                DateTimeOffset.UnixEpoch.AddDays(i), installed ? DateTimeOffset.UnixEpoch.AddDays(i + 3) : null, installed ? i * 600L : 0,
                art, RunState.Idle, null, null, false, false));
        }

        return list;
    }

    /// <summary>
    /// Legendary's metadata folder as a real account has it: one <c>metadata/&lt;app&gt;.json</c> per game, sized like the public
    /// catalog responses in docs/research/fixtures/legendary/catalog-v0.21.1 (75 % a 27 KB AAA item, 20 % a 2 KB one, 5 % a 77 KB
    /// one: about 24 KB on average, 12 MB for 500 games), plus one entitlement per game. Call before <c>rig.Service</c> is first used.
    /// </summary>
    internal static long WriteRealShapedMetadata(Rig rig, IReadOnlyList<string> apps)
    {
        var big = LibraryFixture.CatalogItem(LibraryFixture.AlienCatalog);
        var small = LibraryFixture.CatalogItem(LibraryFixture.CastlevaniaCatalog);
        var huge = LibraryFixture.CatalogItem(LibraryFixture.FortniteCatalog);
        for (var i = 0; i < apps.Count; i++)
        {
            var template = i % 20 == 0 ? huge : i % 5 == 1 ? small : big;
            var n = i;
            rig.Library.Add(apps[i], template, build: "1.2.4-12399+Release", edit: item =>
            {
                item["title"] = $"Synthetic Game Number {n} of the Library";
                item["id"] = $"item{n:D5}";
                item["namespace"] = $"ns{n:D5}";
                item.Remove("mainGameItem");
            });
            rig.Library.Entitle($"ns{n:D5}", null, "2024-05-06T07:08:09.000Z", $"item{n:D5}");
        }

        return Directory.EnumerateFiles(Path.Combine(rig.Paths.LegendaryConfig, "metadata"), "*.json").Sum(f => new FileInfo(f).Length);
    }

    [Fact]
    public async Task ColdStart_ServesFiveHundredCachedGames_WithoutWaitingForLegendarysMetadata()
    {
        using var rig = new Rig();
        var games = SyntheticLibrary(Games);
        var metadataBytes = WriteRealShapedMetadata(rig, [.. games.Select(g => g.AppName)]); // real-shaped metadata stays on disk: the parse is what H4 was about
        Directory.CreateDirectory(rig.Paths.Cache);

        var sw = Stopwatch.StartNew();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new LibraryCacheFile { Games = games }, EngineJsonContext.Default.LibraryCacheFile);
        var serializeMs = sw.Elapsed.TotalMilliseconds;
        await File.WriteAllBytesAsync(LibraryService.PathFor(rig.Paths), bytes, TestContext.Current.CancellationToken);
        var gate = rig.Legendary.VersionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        sw.Restart();
        await rig.Service.StartAsync();
        var startMs = sw.Elapsed.TotalMilliseconds; // LoadCache + first snapshot + first library publish
        Assert.Equal(Games, rig.Service.LibraryForTests.Games.Count);

        // Pre-warm runs now, in parallel with the UI's first request: `library files` parses every metadata/*.json while library.get is served.
        gate.SetResult();
        sw.Restart();
        var delta = await rig.Service.GetLibrary();
        var getMs = sw.Elapsed.TotalMilliseconds;
        var parseStillRunning = !rig.Service.LibraryForTests.FilesRead;
        Assert.True(delta.Upserts.Count >= Games); // the 500 cached games (plus Fortnite once the parse has landed)

        sw.Restart();
        var json = JsonSerializer.Serialize(delta, GliderJsonContext.Default.LibraryDelta);
        var wireMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var snap = await rig.Service.GetSnapshot();
        var snapBuildMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var snapshot = JsonSerializer.Serialize(snap, GliderJsonContext.Default.AppSnapshot);
        var snapshotMs = sw.Elapsed.TotalMilliseconds;

        // Second pass = what a warm (already JIT-ed / R2R) process pays on library.get for a reload of the UI.
        sw.Restart();
        var again = await rig.Service.GetLibrary();
        var wire2 = JsonSerializer.Serialize(again, GliderJsonContext.Default.LibraryDelta);
        var secondMs = sw.Elapsed.TotalMilliseconds;

        // The background parse finishes later and publishes its delta; the UI never waited for it.
        sw.Restart();
        await rig.Service.WarmupTask;
        await rig.Service.LibraryForTests.FlushAsync();
        var parseWaitMs = sw.Elapsed.TotalMilliseconds;
        Assert.True(rig.Service.LibraryForTests.FilesRead);

        Report($"{Games} games, {metadataBytes / 1024 / 1024.0:0.0} MiB of metadata on disk, library.json {bytes.Length / 1024} KiB (write-side serialize {serializeMs:0.0} ms); " +
               $"StartAsync (cache load + first publish) {startMs:0.0} ms; library.get {getMs:0.0} ms (Legendary's files {(parseStillRunning ? "still being parsed" : "already parsed")}); " +
               $"wire JSON {json.Length / 1024} KiB in {wireMs:0.0} ms (first, JIT-cold) / get+serialize again {secondMs:0.0} ms ({wire2.Length / 1024} KiB); " +
               $"snapshot {snapshot.Length / 1024.0:0.0} KiB: build {snapBuildMs:0.0} ms + serialize {snapshotMs:0.0} ms; background parse + delta finished {parseWaitMs:0.0} ms later");
        Assert.True(getMs < LibraryGetBudgetMs, $"library.get took {getMs:0.0} ms (budget {LibraryGetBudgetMs} ms, Legendary's metadata on disk)");
        Assert.True(startMs < 400 * LoadSlack, $"cold start took {startMs} ms");
        Assert.True(wireMs < 100 * LoadSlack, $"serializing library.get took {wireMs} ms");
        Assert.True(snapshot.Length < 16 * 1024, "the snapshot must stay a few KB");
    }

    [Fact]
    public async Task ARebuildOfFiveHundredGames_StaysFast()
    {
        using var rig = new Rig();
        var apps = Enumerable.Range(0, Games).Select(i => $"App{i:D4}").ToList();
        WriteRealShapedMetadata(rig, apps);
        await rig.StartWarmAsync();
        var library = rig.Service.LibraryForTests;
        Assert.True(library.Games.Count >= Games);

        async Task<double> Timed(Func<Task> change)
        {
            await change();
            var sw = Stopwatch.StartNew();
            await library.FlushAsync();
            return sw.Elapsed.TotalMilliseconds;
        }

        static double Median(List<double> t) => t.Order().ElementAt(t.Count / 2);
        static string Runs(List<double> t) => string.Join(", ", t.Select(x => x.ToString("0.0")));

        // Nothing changed (a job tick, a settings write, an art notification): the rebuild must find that out cheaply.
        var unchanged = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            unchanged.Add(await Timed(() => { library.Invalidate(); return Task.CompletedTask; }));
        }

        // One game's Glider record changed (a favorite, playtime): one summary is rebuilt, one upsert goes out.
        var oneGame = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var before = rig.Sink.Of<LibraryDelta>(RpcEvents.Library).Count;
            var fav = i % 2 == 0;
            oneGame.Add(await Timed(() =>
            {
                rig.Games.Put("App0003", rig.Games.Get("App0003") with { Favorite = fav, PlaytimeSeconds = 100 + i });
                return Task.CompletedTask;
            }));
            var deltas = rig.Sink.Of<LibraryDelta>(RpcEvents.Library).Skip(before).ToList();
            Assert.Equal("App0003", Assert.Single(Assert.Single(deltas).Upserts).AppName);
        }

        // A re-read of Legendary's files when none changed (every sync and job end), then with one metadata file rewritten.
        var reread = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            await library.ReloadAsync();
            reread.Add(sw.Elapsed.TotalMilliseconds);
        }

        var metadata = Path.Combine(rig.Paths.LegendaryConfig, "metadata", "App0002.json");
        var rewritten = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            File.AppendAllText(metadata, new string(' ', i + 1));
            var sw = Stopwatch.StartNew();
            await library.ReloadAsync();
            rewritten.Add(sw.Elapsed.TotalMilliseconds);
        }

        var median = Median(unchanged);
        Report($"{library.Games.Count} real-shaped games: rebuild without any change median {median:0.0} ms (runs {Runs(unchanged)}); one game's record changed median {Median(oneGame):0.0} ms ({Runs(oneGame)}); " +
               $"re-read of unchanged files median {Median(reread):0.0} ms ({Runs(reread)}); one metadata file rewritten median {Median(rewritten):0.0} ms ({Runs(rewritten)})");
        Assert.True(median < 50 * LoadSlack, $"rebuild took {median} ms");
    }

    /// <summary>
    /// Not a test: with GLIDER_BENCH_SEED=&lt;dir&gt; it writes a data dir holding 500 cached games (cache/library.json), a finished
    /// onboarding and schema 2, so the real host can be measured with <c>Glider.exe --measure-startup --data-dir=&lt;dir&gt;</c> (docs/perf.md).
    /// </summary>
    [Fact]
    public async Task SeedDataDirForHostMeasurement()
    {
        if (Environment.GetEnvironmentVariable("GLIDER_BENCH_SEED") is not { Length: > 0 } dir)
        {
            return;
        }

        Directory.CreateDirectory(Path.Combine(dir, "cache"));
        Directory.CreateDirectory(Path.Combine(dir, "state"));
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(Path.Combine(dir, "cache", "library.json"),
            JsonSerializer.SerializeToUtf8Bytes(new LibraryCacheFile { Games = SyntheticLibrary(Games) }, EngineJsonContext.Default.LibraryCacheFile), ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "state", "onboarding.json"), """{"schemaVersion":2,"completed":true,"step":"done","skipped":[]}""", ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "state", "schema.json"), """{"schemaVersion":2,"migratedFrom":null,"migratedAt":"2026-11-14T00:00:00+00:00"}""", ct);
    }
}
