using System.Text;
using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Infrastructure;

public class AtomicFileTests
{
    [Fact]
    public async Task WritesReplacesAndKeepsBackup()
    {
        using var dir = new TempDir();
        var path = dir.Combine("a.json");
        await AtomicFile.WriteAllTextAsync(path, "one", keepBackup: true, TestContext.Current.CancellationToken);
        await AtomicFile.WriteAllTextAsync(path, "two", keepBackup: true, TestContext.Current.CancellationToken);

        Assert.Equal("two", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal("one", await File.ReadAllTextAsync(AtomicFile.BackupPathFor(path), TestContext.Current.CancellationToken));
        Assert.Equal(2, Directory.GetFiles(dir.Path).Length); // no temp files left behind
    }

    [Fact]
    public async Task ReadersNeverSeeAPartialFile()
    {
        using var dir = new TempDir();
        var path = dir.Combine("big.json");
        var ct = TestContext.Current.CancellationToken;
        string Doc(int i) => JsonSerializer.Serialize(new { i, pad = new string('x', 200_000 + i) });
        await AtomicFile.WriteAllTextAsync(path, Doc(0), ct: ct);

        using var stop = new CancellationTokenSource();
        var reads = 0;
        var reader = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var bytes = await AtomicFile.TryReadAllBytesAsync(path, CancellationToken.None);
                Assert.NotNull(bytes);
                using var _ = JsonDocument.Parse(bytes); // throws on a torn file
                reads++;
                await Task.Delay(2, CancellationToken.None);
            }
        }, CancellationToken.None);

        for (var i = 1; i <= 40; i++)
        {
            await AtomicFile.WriteAllTextAsync(path, Doc(i), ct: ct);
        }

        await stop.CancelAsync();
        await reader;
        Assert.True(reads > 0);
        Assert.Single(Directory.GetFiles(dir.Path));
    }
}

public class JsonFileStoreTests
{
    private static GameRecord Record(string version) =>
        new(@"D:\Games\Fortnite", version, 42, new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), false, null, false);

    [Fact]
    public async Task RoundTripsAndFallsBackToBackupWhenCorrupt()
    {
        using var dir = new TempDir();
        var ct = TestContext.Current.CancellationToken;
        using var store = new JsonFileStore<GameRecord>(dir.Combine("game.json"), EngineJsonContext.Default.GameRecord);
        await store.SaveAsync(Record("1"), ct);
        await store.SaveAsync(Record("2"), ct);
        Assert.Equal("2", (await store.LoadAsync(ct))!.Version);

        await File.WriteAllTextAsync(store.FilePath, "{ torn", ct);
        Assert.Equal("1", (await store.LoadAsync(ct))!.Version);
    }

    [Fact]
    public async Task ConcurrentSavesAreOrderedAndCoalesced()
    {
        using var dir = new TempDir();
        using var store = new JsonFileStore<GameRecord>(dir.Combine("game.json"), EngineJsonContext.Default.GameRecord);
        var saves = Enumerable.Range(0, 50).Select(i => store.SaveAsync(Record(i.ToString()))).ToList();
        await Task.WhenAll(saves);
        Assert.Equal("49", (await store.LoadAsync(TestContext.Current.CancellationToken))!.Version);
    }

    [Fact]
    public async Task SavingNullDeletesTheFile()
    {
        using var dir = new TempDir();
        using var store = new JsonFileStore<GameRecord>(dir.Combine("game.json"), EngineJsonContext.Default.GameRecord);
        await store.SaveAsync(Record("1"), TestContext.Current.CancellationToken);
        await store.SaveAsync(null, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(store.FilePath));
        Assert.Null(await store.LoadAsync(TestContext.Current.CancellationToken));
    }
}

public class FileLogTests
{
    [Fact]
    public async Task WritesFiltersAndTails()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        await using var log = new FileLog(dir.Path, time, LogLevel.Info);
        log.Debug("t", "hidden");
        log.Info("t", "first");
        log.Error("t", "boom", new InvalidOperationException("why"));
        await log.FlushAsync();

        Assert.EndsWith("glider-20261112.log", log.CurrentFile, StringComparison.Ordinal);
        var text = Encoding.UTF8.GetString((await AtomicFile.TryReadAllBytesAsync(log.CurrentFile, TestContext.Current.CancellationToken))!);
        Assert.Contains("INF [t] first", text);
        Assert.Contains("ERR [t] boom", text);
        Assert.Contains("why", text);
        Assert.DoesNotContain("hidden", text);
        Assert.StartsWith("2026-11-12 10:00:00.000 ERR [t] boom", log.Tail(10).Split('\n')[1]);
        Assert.Single(log.Tail(1).TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public async Task FlushedLinesAreVisibleOnDiskWhileLogIsOpen()
    {
        using var dir = new TempDir();
        await using var log = new FileLog(dir.Path);
        log.Info("t", "hello");
        await log.FlushAsync();

        Assert.True(new FileInfo(log.CurrentFile).Length > 0);
    }

    [Fact]
    public async Task RollsDailyAndDeletesOldFiles()
    {
        using var dir = new TempDir();
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "glider-20260101.log"), "old", TestContext.Current.CancellationToken);
        var time = new ManualTimeProvider();
        await using var log = new FileLog(dir.Path, time, retentionDays: 14);
        log.Info("t", "day one");
        await log.FlushAsync();
        time.Advance(TimeSpan.FromDays(1));
        log.Info("t", "day two");
        await log.FlushAsync();

        Assert.True(File.Exists(Path.Combine(dir.Path, "glider-20261112.log")));
        Assert.True(File.Exists(Path.Combine(dir.Path, "glider-20261113.log")));
        Assert.False(File.Exists(Path.Combine(dir.Path, "glider-20260101.log")));
    }

    [Fact]
    public async Task WriteNeverBlocksEvenUnderLoad()
    {
        using var dir = new TempDir();
        await using var log = new FileLog(dir.Path);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Parallel.For(0, 20_000, i => log.Info("load", $"line {i}"));
        Assert.True(sw.ElapsedMilliseconds < 5000);
        Assert.Contains("line", log.Tail(1));
    }
}

public class SettingsStoreTests
{
    [Fact]
    public async Task MissingFileGivesDefaults()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path, "legendary.exe");
        using var store = await SettingsStore.LoadAsync(paths, new TestLog(), TestContext.Current.CancellationToken);
        Assert.Equal(Settings.Default, store.Current);
    }

    [Fact]
    public async Task PartialAndPartlyInvalidFilesMergeOverDefaults()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path, "legendary.exe");
        Directory.CreateDirectory(paths.State);
        // eglSync missing (must stay true), theme invalid (falls back), workers out of range (clamped), unknown key ignored
        await File.WriteAllTextAsync(SettingsStore.PathFor(paths),
            """{ "downloadWorkers": 500, "theme": "purple", "closeToTray": false, "futureSetting": 1, "preferredCdn": " fastly ", }""",
            TestContext.Current.CancellationToken);
        var log = new TestLog();
        using var store = await SettingsStore.LoadAsync(paths, log, TestContext.Current.CancellationToken);

        Assert.True(store.Current.EglSync);
        Assert.False(store.Current.CloseToTray);
        Assert.Equal(ThemePreference.System, store.Current.Theme);
        Assert.Equal(AutoTune.MaxWorkers, store.Current.DownloadWorkers);
        Assert.Equal("fastly", store.Current.PreferredCdn);
        Assert.True(log.Contains("theme"));
    }

    [Fact]
    public async Task UpdatePersistsNormalizesAndNotifies()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path, "legendary.exe");
        using var store = await SettingsStore.LoadAsync(paths, new TestLog(), TestContext.Current.CancellationToken);
        Settings? seen = null;
        store.Changed += s => seen = s;

        var result = await store.UpdateAsync(Settings.Default with
        {
            SharedMemoryMb = 1,
            UpdateCheckIntervalMinutes = 0,
            LaunchArgs = "-a\r\n-b",
            PreferredCdn = "bad host!",
            DefaultInstallBase = "relative\\path",
        });

        Assert.Equal(AutoTune.MinSharedMemoryMb, result.SharedMemoryMb);
        Assert.Equal(SettingsStore.MinUpdateIntervalMinutes, result.UpdateCheckIntervalMinutes);
        Assert.Equal("-a  -b", result.LaunchArgs);
        Assert.Null(result.PreferredCdn);
        Assert.Null(result.DefaultInstallBase);
        Assert.Equal(result, seen);

        using var reloaded = await SettingsStore.LoadAsync(paths, new TestLog(), TestContext.Current.CancellationToken);
        Assert.Equal(result, reloaded.Current);
    }
}

public class GameRecordStoreTests
{
    [Fact]
    public async Task RoundTripsAndClears()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path, "legendary.exe");
        var record = new GameRecord(@"D:\Games\Fortnite", "++Fortnite+Release-33.00", 123, DateTimeOffset.UnixEpoch, true, "ABCDEF", false);
        using (var store = await GameRecordStore.LoadAsync(paths, new TestLog(), TestContext.Current.CancellationToken))
        {
            Assert.Null(store.Current);
            await store.SaveAsync(record);
        }

        using (var store = await GameRecordStore.LoadAsync(paths, new TestLog(), TestContext.Current.CancellationToken))
        {
            Assert.Equal(record, store.Current);
            await store.SaveAsync(null);
            Assert.False(File.Exists(GameRecordStore.PathFor(paths)));
        }
    }
}

public class AutoTuneTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(2, 3, 4, 1024)]     // tiny laptop: RAM caps workers
    [InlineData(4, 8, 8, 2048)]
    [InlineData(6, 16, 12, 3072)]
    [InlineData(12, 32, 16, 4096)]  // Legendary's own min(2*cpu, 16)
    [InlineData(64, 128, 16, 4096)] // writer-bound beyond 16
    [InlineData(1, 6, 6, 2048)]     // floor keeps a fast pipe full on 1-2 cores
    public void PicksSaneValues(int cpus, long ramGiB, int workers, int shm)
    {
        var r = AutoTune.Compute(cpus, ramGiB * GiB);
        Assert.Equal(workers, r.Workers);
        Assert.Equal(shm, r.SharedMemoryMb);
    }

    [Fact]
    public void RetryCeilingIsBounded()
    {
        Assert.Equal(1024, AutoTune.MaxSharedMemoryMb(1 * GiB));
        Assert.Equal(8192, AutoTune.MaxSharedMemoryMb(16 * GiB));
        Assert.Equal(12288, AutoTune.MaxSharedMemoryMb(256 * GiB));
    }

    [Fact]
    public void CurrentMachineIsWithinLimits()
    {
        var r = AutoTune.ForCurrentMachine();
        Assert.InRange(r.Workers, 4, 16);
        Assert.InRange(r.SharedMemoryMb, 1024, 4096);
        Assert.True(Encoding.UTF8.GetByteCount(r.ToString()) > 0);
    }
}
