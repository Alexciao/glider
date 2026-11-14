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

    [Fact]
    public async Task ColdStart_ServesFiveHundredCachedGames()
    {
        using var rig = new Rig();
        var games = SyntheticLibrary(Games);
        Directory.CreateDirectory(rig.Paths.Cache);

        var sw = Stopwatch.StartNew();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new LibraryCacheFile { Games = games }, EngineJsonContext.Default.LibraryCacheFile);
        var serializeMs = sw.Elapsed.TotalMilliseconds;
        await File.WriteAllBytesAsync(LibraryService.PathFor(rig.Paths), bytes, TestContext.Current.CancellationToken);
        _ = rig.Service; // writes Legendary's fixture files...
        Directory.Delete(Path.Combine(rig.Paths.LegendaryConfig, "metadata"), recursive: true); // ...an empty config dir keeps the cached library (no metadata yet)
        rig.Legendary.VersionGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); // hold the pre-warm: cold path only

        // Warm the JIT on a throw-away deserialize, like a second run of an R2R image would not need; report both cold and warm.
        sw.Restart();
        await rig.Service.StartAsync();
        var startMs = sw.Elapsed.TotalMilliseconds; // LoadCache + first snapshot + first library publish
        Assert.Equal(Games, rig.Service.LibraryForTests.Games.Count);

        sw.Restart();
        var delta = await rig.Service.GetLibrary();
        var getMs = sw.Elapsed.TotalMilliseconds;
        Assert.Equal(Games, delta.Upserts.Count);

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

        Report($"{Games} games: library.json {bytes.Length / 1024} KiB (write-side serialize {serializeMs:0.0} ms); " +
               $"StartAsync (cache load + first publish) {startMs:0.0} ms; library.get {getMs:0.0} ms; wire JSON {json.Length / 1024} KiB in {wireMs:0.0} ms (first, JIT-cold) / " +
               $"get+serialize again {secondMs:0.0} ms ({wire2.Length / 1024} KiB); snapshot {snapshot.Length / 1024.0:0.0} KiB: build {snapBuildMs:0.0} ms + serialize {snapshotMs:0.0} ms");
        Assert.True(startMs < 400, $"cold start took {startMs} ms");
        Assert.True(getMs < 100, $"library.get took {getMs} ms");
        Assert.True(wireMs < 100, $"serializing library.get took {wireMs} ms");
        Assert.True(snapshot.Length < 16 * 1024, "the snapshot must stay a few KB");

        rig.Legendary.VersionGate.SetResult();
    }

    [Fact]
    public async Task ARebuildOfFiveHundredGames_StaysFast()
    {
        using var rig = new Rig();
        for (var i = 0; i < Games; i++)
        {
            rig.AddGame($"App{i:D4}", $"Game {i}", latest: "2.0", installedVersion: i % 7 == 0 ? "1.0" : null);
        }

        await rig.StartWarmAsync();
        Assert.True(rig.Service.LibraryForTests.Games.Count >= Games);

        var times = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            rig.Service.LibraryForTests.Invalidate();
            var sw = Stopwatch.StartNew();
            await rig.Service.LibraryForTests.FlushAsync();
            times.Add(sw.Elapsed.TotalMilliseconds);
        }

        var median = times.Order().ElementAt(2);
        Report($"rebuild of {rig.Service.LibraryForTests.Games.Count} games without file changes: median {median:0.0} ms (runs {string.Join(", ", times.Select(t => t.ToString("0.0")))})");
        Assert.True(median < 250, $"rebuild took {median} ms");
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
