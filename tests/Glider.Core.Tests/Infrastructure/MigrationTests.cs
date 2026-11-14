using System.Security.Cryptography;
using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;
using Glider.Core.Infrastructure.Migrations;
using Glider.Core.Jobs;
using Glider.Core.Tests.Jobs;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Infrastructure;

/// <summary>v0.1 → schema 2 (docs/v0.2-design.md §6), from the real v0.1 shapes in Fixtures/v0.1-data.</summary>
[Collection(MigrationCollection.Name)] // MigrationV1ToV2.FailAfter is static
public sealed class MigrationTests : IDisposable
{
    private const string Fortnite = "Fortnite";
    private const string Version = "++Fortnite+Release-43.10-CL-60118918-Windows";

    private readonly TempDir _dir = new();
    private readonly TestLog _log = new();

    public MigrationTests() => Paths = new AppPaths(_dir.Combine("Glider"), _dir.Combine("legendary.exe"));

    private AppPaths Paths { get; }

    private static string FixtureRoot => Path.Combine(AppContext.BaseDirectory, "Fixtures", "v0.1-data");

    private static string Variant(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "v0.1-variants", name);

    /// <summary>Copies the real v0.1 state/ and cache/ (plus optional variant files) into the data root.</summary>
    private void SeedV01(params (string Variant, string Target)[] extra) => SeedV01(Paths, extra);

    private static void SeedV01(AppPaths paths, params (string Variant, string Target)[] extra)
    {
        foreach (var sub in new[] { "state", "cache" })
        {
            var dest = Path.Combine(paths.Root, sub);
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.EnumerateFiles(Path.Combine(FixtureRoot, sub)))
            {
                File.Copy(f, Path.Combine(dest, Path.GetFileName(f)));
            }
        }

        Directory.CreateDirectory(Path.Combine(paths.Cache, "art"));
        File.WriteAllBytes(Path.Combine(paths.Cache, "art", "f8a782b6ac2b3547.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(paths.Cache, "art", "05750b1c893565d2.jpg"), [2]);
        foreach (var (variant, target) in extra)
        {
            File.Copy(Variant(variant), Path.Combine(paths.Root, target), overwrite: true);
        }
    }

    private Dictionary<string, string> Hashes(string dir) => Directory.Exists(dir)
        ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), f => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))))
        : [];

    private Task<MigrationResult> Migrate() => Migrator.RunAsync(Paths, _log, ct: TestContext.Current.CancellationToken);

    [Fact]
    public async Task MigratesTheRealV01Data_AndKeepsByteCopies()
    {
        SeedV01();
        var originalState = Hashes(Path.Combine(FixtureRoot, "state"));
        var originalCache = Hashes(Path.Combine(FixtureRoot, "cache"));

        var result = await Migrate();

        Assert.Equal(MigrationStatus.Migrated, result.Status);
        Assert.Equal("0.1", result.MigratedFrom);
        var schema = Migrator.ReadSchema(Paths)!;
        Assert.Equal(3, schema.SchemaVersion);
        Assert.Equal("0.1", schema.MigratedFrom);
        Assert.Equal([("0.1", 2), ("2", 3)], schema.History!.Select(h => (h.From, h.To)));

        // games.json: Fortnite's install record, playtime and last played.
        using var games = await GamesStore.LoadAsync(Paths, _log, TestContext.Current.CancellationToken);
        var entry = games.Get(Fortnite);
        var install = entry.Install!;
        Assert.Equal(@"D:\Games\Fortnite", install.InstallPath);
        Assert.Equal(Version, install.Version); // the \u002B escapes decoded
        Assert.Equal(160618483626, install.InstallSizeBytes);
        Assert.Equal(Fortnite, install.AppName);
        Assert.False(install.NeedsVerification);
        Assert.Equal(0, entry.PlaytimeSeconds);
        Assert.Equal("", entry.Settings.LaunchArgs); // no v0.1 settings.json on that machine

        // jobs.json v2: same job, now Fortnite's, PascalCase step and the verify report kept.
        var jobs = JsonSerializer.Deserialize(File.ReadAllBytes(JobQueue.PathFor(Paths)), EngineJsonContext.Default.PersistedJobFile)!;
        Assert.Equal(2, jobs.SchemaVersion);
        var job = Assert.Single(jobs.Jobs);
        Assert.Equal("c987321d1b474215ba80fe35900e1201", job.Info.Id);
        Assert.Equal(Fortnite, job.Info.AppName);
        Assert.Equal(Fortnite, job.Request.AppName);
        Assert.Equal(JobKind.Verify, job.Info.Kind);
        Assert.Equal(JobStatus.Completed, job.Info.Status);
        Assert.Equal(JobStep.Repair, job.Step);
        Assert.Equal(new VerifyReport(757, 437, 437, true, 0), job.Info.Verify);
        Assert.Equal(100, job.Info.Progress!.Percent);
        Assert.Equal(1011, job.VerifyChecked);
        Assert.Equal(37206821437, job.Baseline.TotalDownloadBytes);

        // onboarding: completed (an install record existed).
        using var onboarding = await FileOnboardingStore.LoadAsync(Paths, _log, TestContext.Current.CancellationToken);
        Assert.True(onboarding.Current.Completed);

        // caches: per-app art index and installed size.
        var art = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Paths.Cache, "art", "index.json"))).RootElement.GetProperty(Fortnite);
        Assert.Equal("https://cache.glider/art/05750b1c893565d2.jpg", art.GetProperty("tall").GetString());
        Assert.Equal("https://cache.glider/art/f8a782b6ac2b3547.jpg", art.GetProperty("wide").GetString());
        var size = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Paths.Cache, "installed-size.json"))).RootElement;
        Assert.Equal(52376063424, size.GetProperty("games").GetProperty(Fortnite).GetProperty("bytes").GetInt64());

        // v0.1-only files are untouched; the backup holds byte copies of every state\* and cache\*.json.
        foreach (var name in new[] { "game.json", "game.json.bak", "jobs.json.bak", "window.json" })
        {
            Assert.Equal(originalState[name], Hashes(Paths.State)[name]);
        }

        Assert.Equal(originalCache["art.json"], Hashes(Paths.Cache)["art.json"]);
        var backup = result.BackupDirectory!;
        Assert.StartsWith(Paths.MigrationBackups, backup, StringComparison.Ordinal);
        Assert.Equal(originalState, Hashes(Path.Combine(backup, "state")));
        Assert.Equal(originalCache, Hashes(Path.Combine(backup, "cache")));
    }

    [Fact]
    public async Task RunningTwiceChangesNothing()
    {
        SeedV01();
        Assert.Equal(MigrationStatus.Migrated, (await Migrate()).Status);
        var after = Hashes(Paths.Root);

        var second = await Migrate();

        Assert.Equal(MigrationStatus.UpToDate, second.Status);
        Assert.Equal(after, Hashes(Paths.Root));
        Assert.Single(Directory.GetDirectories(Paths.MigrationBackups));
    }

    [Fact]
    public async Task AnUnreadableV01OnboardingFileIsKeptNextToTheNewOne()
    {
        SeedV01();
        var path = Path.Combine(Paths.State, "onboarding.json");
        byte[] torn = [0xEF, 0xBB, 0xBF, (byte)'{', (byte)'"', (byte)'c', (byte)'o'];
        File.WriteAllBytes(path, torn);

        var result = await Migrate();

        Assert.Equal(MigrationStatus.Migrated, result.Status);
        Assert.Equal(torn, File.ReadAllBytes(path + ".v0.1-unreadable"));
        Assert.True(_log.Contains("could not be read"));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.True(doc.RootElement.GetProperty("completed").GetBoolean()); // inferred from the install record
    }

    [Fact]
    public async Task AFailedRunLeavesV01FilesAndIsRetriedToTheSameResult()
    {
        SeedV01((Variant: "settings.json", Target: "state/settings.json"));
        var v01 = Hashes(Paths.State);
        MigrationV1ToV2.FailAfter = "jobs";
        MigrationResult failed;
        try
        {
            failed = await Migrate();
        }
        finally
        {
            MigrationV1ToV2.FailAfter = null;
        }

        Assert.Equal(MigrationStatus.Failed, failed.Status);
        Assert.NotNull(failed.Error);
        Assert.Null(Migrator.ReadSchema(Paths)); // not committed
        Assert.True(_log.Contains("retried at the next start"));
        foreach (var name in new[] { "game.json", "game.json.bak", "jobs.json.bak", "window.json" })
        {
            Assert.Equal(v01[name], Hashes(Paths.State)[name]);
        }

        var retried = await Migrate();
        Assert.Equal(MigrationStatus.Migrated, retried.Status);

        // Same outcome as a clean run.
        using var clean = new TempDir();
        var cleanPaths = new AppPaths(clean.Combine("Glider"), clean.Combine("legendary.exe"));
        SeedV01(cleanPaths, (Variant: "settings.json", Target: "state/settings.json"));
        Assert.Equal(MigrationStatus.Migrated, (await Migrator.RunAsync(cleanPaths, _log, ct: TestContext.Current.CancellationToken)).Status);
        foreach (var file in new[] { "games.json", "jobs.json", "settings.json", "onboarding.json" })
        {
            Assert.Equal(Normalized(Path.Combine(cleanPaths.State, file)), Normalized(Path.Combine(Paths.State, file)));
        }
    }

    [Fact]
    public async Task AnInterruptedInstallKeepsItsResumeState_AndResumesAsFortnite()
    {
        SeedV01((Variant: "jobs-running.json", Target: "state/jobs.json"));
        Assert.Equal(MigrationStatus.Migrated, (await Migrate()).Status);

        var file = JsonSerializer.Deserialize(File.ReadAllBytes(JobQueue.PathFor(Paths)), EngineJsonContext.Default.PersistedJobFile)!;
        var job = Assert.Single(file.Jobs);
        Assert.Equal(JobStep.Verify, job.Step); // resumes the post-install verify, not the 52 GB download
        Assert.True(job.CreatedGameFolder);
        Assert.Equal(@"D:\Games\Fortnite", job.GameFolder);
        Assert.Equal(52000000000, job.Baseline.TotalDownloadBytes);
        Assert.Equal(37, job.Baseline.Percent);
        Assert.Equal(3600.25, job.Baseline.ElapsedSeconds);
        Assert.Equal(@"D:\Games", job.Request.TargetPath);
        Assert.Equal(JobPhase.Verifying, job.Info.Phase);

        await using var h = new QueueHarness(legendary: new StubLegendary(verifySucceeds: true));
        Directory.CreateDirectory(h.Env.Paths.State);
        File.Copy(JobQueue.PathFor(Paths), JobQueue.PathFor(h.Env.Paths));
        var q = await h.StartAsync();
        var restored = Assert.Single(q.Snapshot());
        Assert.Equal(Fortnite, restored.AppName);
        var done = await QueueHarness.WaitForStatus(q, restored.Id, JobStatus.Completed);
        Assert.Equal(["before:Install", "ok:Install"], h.Hooks.Calls);
        Assert.True(done.Progress!.ElapsedSeconds >= 3600); // the elapsed time of the first session carried over
    }

    [Fact]
    public async Task SettingsAreMapped_AndTheGlobalLaunchArgsBecomeFortnites()
    {
        SeedV01((Variant: "settings.json", Target: "state/settings.json"));
        Assert.Equal(MigrationStatus.Migrated, (await Migrate()).Status);

        using var settings = await SettingsStore.LoadAsync(Paths, _log, TestContext.Current.CancellationToken);
        var s = settings.Current;
        Assert.Equal(AutoUpdatePolicy.Never, s.AutoUpdate); // v0.1 autoUpdate: false
        Assert.Equal(12, s.DownloadWorkers);
        Assert.Equal("fastly-download.epicgames.com", s.PreferredCdn);
        Assert.Equal(60, s.UpdateCheckIntervalMinutes);
        Assert.True(s.StartWithWindows);
        Assert.False(s.MinimizeToTrayWhilePlaying);
        Assert.Equal(LogLevel.Debug, s.LogLevel);
        Assert.Equal(ThemePreference.Dark, s.Theme);
        Assert.False(s.EglSync);
        Assert.Equal(@"E:\Games", s.DefaultInstallBase);
        Assert.Equal([@"E:\Games"], s.LibraryFolders); // the default base seeds the library folders
        Assert.Equal(DownloadMode.Balanced, s.DownloadMode); // new settings get their defaults
        Assert.Equal(WhilePlayingPolicy.Pause, s.WhilePlaying);
        Assert.Contains("\"schemaVersion\": 2", File.ReadAllText(SettingsStore.PathFor(Paths)), StringComparison.Ordinal);

        using var games = await GamesStore.LoadAsync(Paths, _log, TestContext.Current.CancellationToken);
        Assert.Equal("-NOTEXTURESTREAMING -USEALLAVAILABLECORES", games.Get(Fortnite).Settings.LaunchArgs);
    }

    [Fact]
    public async Task OnboardingIsInferred()
    {
        // v0.1 said "not completed" (BOM file) and there's no install record: still not done.
        Directory.CreateDirectory(Paths.State);
        File.Copy(Variant("onboarding-false.json"), Path.Combine(Paths.State, "onboarding.json"));
        File.Copy(Variant("settings.json"), SettingsStore.PathFor(Paths));
        Assert.Equal(MigrationStatus.Migrated, (await Migrate()).Status);
        using (var o = await FileOnboardingStore.LoadAsync(Paths, _log, TestContext.Current.CancellationToken))
        {
            Assert.False(o.Current.Completed);
            Assert.Equal(OnboardingStep.Welcome, o.Current.Step);
        }

        // An install record counts as onboarded even when v0.1 said otherwise.
        using var other = new TempDir();
        var p2 = new AppPaths(other.Combine("Glider"), other.Combine("legendary.exe"));
        Directory.CreateDirectory(p2.State);
        File.Copy(Variant("onboarding-false.json"), Path.Combine(p2.State, "onboarding.json"));
        File.Copy(Path.Combine(FixtureRoot, "state", "game.json"), Path.Combine(p2.State, "game.json"));
        Assert.Equal(MigrationStatus.Migrated, (await Migrator.RunAsync(p2, _log, ct: TestContext.Current.CancellationToken)).Status);
        using var o2 = await FileOnboardingStore.LoadAsync(p2, _log, TestContext.Current.CancellationToken);
        Assert.True(o2.Current.Completed);
        Assert.Equal(OnboardingStep.Done, o2.Current.Step);
    }

    [Fact]
    public async Task AFreshInstallOnlyWritesTheMarker()
    {
        var result = await Migrate();
        Assert.Equal(MigrationStatus.Fresh, result.Status);
        Assert.Null(result.MigratedFrom);
        Assert.Equal(3, Migrator.ReadSchema(Paths)!.SchemaVersion);
        Assert.Empty(Migrator.ReadSchema(Paths)!.History!);
        Assert.False(Directory.Exists(Paths.MigrationBackups));
        Assert.Equal(MigrationStatus.UpToDate, (await Migrate()).Status);
    }

    [Fact]
    public async Task TheRuntimeMigratesBeforeAnyStoreLoads_AndReportsIt()
    {
        SeedV01();
        using var env = new FakeLegendaryEnv();
        var paths = new AppPaths(Paths.Root, env.Paths.LegendaryExe);
        var sink = new Services.FakeSink();
        await using var runtime = await Glider.Core.Services.GliderRuntime.StartAsync(paths, new Services.FakeHost(new Services.CallLog()), sink,
            new EglEnvironment(_dir.Combine("ProgramData")), options: new Glider.Core.Services.GliderRuntimeOptions
            {
                ProcessMonitor = new Services.FakeProcesses(),
                ProcessStarter = new Services.FakeProcesses(),
                DiskInfo = new Services.FakeDisk(),
                FreeGamesBackground = false,
                Legendary = new Glider.Core.Legendary.LegendaryOptions { PinnedVersion = "0.21.1" },
                OverlayRegistry = new Services.FakeOverlayRegistry(),
                OverlayEnvironment = new Glider.Core.Overlay.OverlayEnvironment(null),
                ThirdPartyLaunchers = new Services.FakeLaunchers(),
            }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(MigrationStatus.Migrated, runtime.Migration.Status);
        var snap = await runtime.Rpc.GetSnapshot();
        Assert.Equal("0.1", snap.App.MigratedFrom);
        Assert.True(snap.Onboarding.Completed);
    }

    [Fact]
    public async Task ASchema2MarkerIsBumpedTo3_KeepingWhereTheDataCameFrom()
    {
        Directory.CreateDirectory(Paths.State);
        File.WriteAllText(Migrator.SchemaPath(Paths), """{"schemaVersion":2,"migratedFrom":"0.1","migratedAt":"2026-10-01T08:00:00+00:00"}""");
        File.WriteAllText(Path.Combine(Paths.State, "games.json"), """{"schemaVersion":2,"games":{}}""");
        var games = File.ReadAllBytes(Path.Combine(Paths.State, "games.json"));

        var result = await Migrate();

        Assert.Equal(MigrationStatus.Migrated, result.Status);
        Assert.Null(result.MigratedFrom); // nothing was converted this run
        Assert.Null(result.BackupDirectory);
        var schema = Migrator.ReadSchema(Paths)!;
        Assert.Equal(3, schema.SchemaVersion);
        Assert.Equal("0.1", schema.MigratedFrom);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T08:00:00+00:00", System.Globalization.CultureInfo.InvariantCulture), schema.MigratedAt);
        Assert.Equal([("0.1", 2), ("2", 3)], schema.History!.Select(h => (h.From, h.To)));
        Assert.Equal(games, File.ReadAllBytes(Path.Combine(Paths.State, "games.json")));
        Assert.Equal(MigrationStatus.UpToDate, (await Migrate()).Status);
    }

    [Fact]
    public async Task RepeatedFailuresReuseAMatchingBackup()
    {
        SeedV01();
        MigrationV1ToV2.FailAfter = "backup"; // fails before writing anything: every retry sees the same bytes
        try
        {
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(MigrationStatus.Failed, (await Migrate()).Status);
            }
        }
        finally
        {
            MigrationV1ToV2.FailAfter = null;
        }

        var backup = Assert.Single(Directory.GetDirectories(Paths.MigrationBackups));
        var result = await Migrate();
        Assert.Equal(MigrationStatus.Migrated, result.Status);
        Assert.Equal(backup, result.BackupDirectory);
        Assert.Single(Directory.GetDirectories(Paths.MigrationBackups));
    }

    [Fact]
    public async Task AFailureAfterWritingKeepsTheOriginalBackup_AndAddsAtMostOneMore()
    {
        SeedV01((Variant: "settings.json", Target: "state/settings.json"));
        var original = Hashes(Paths.State);
        MigrationV1ToV2.FailAfter = "jobs"; // settings.json and games.json were rewritten before the failure
        try
        {
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(MigrationStatus.Failed, (await Migrate()).Status);
            }
        }
        finally
        {
            MigrationV1ToV2.FailAfter = null;
        }

        var backups = Directory.GetDirectories(Paths.MigrationBackups).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, backups.Count); // the pristine v0.1 bytes, then the half-migrated state that every later retry matches
        Assert.Equal(original, Hashes(Path.Combine(backups[0], "state")));
    }

    /// <summary>
    /// A v0.1 user installing this build: the host moves %LocalAppData%\Glider's data to GliderData first (Velopack's files stay),
    /// then Core converts v0.1 → schema 2 in the new root and records schema 3.
    /// </summary>
    [Fact]
    public async Task AV01RootIsMovedThenMigrated_IntoAWorkingLayout()
    {
        var layout = new MoveLayout(_dir.Combine("LocalAppData"));
        layout.SeedVelopackOnly();
        var old = new AppPaths(layout.Old, _dir.Combine("legendary.exe"));
        SeedV01(old);
        Directory.CreateDirectory(Path.Combine(old.LegendaryConfig, "tmp"));
        File.WriteAllText(Path.Combine(old.LegendaryConfig, "tmp", "Fortnite.resume"), "resume");
        File.WriteAllText(Path.Combine(old.LegendaryConfig, "user.json"), "{}");
        Directory.CreateDirectory(Path.Combine(old.WebView2, "EBWebView"));
        Directory.CreateDirectory(old.Logs);
        File.WriteAllText(Path.Combine(old.Logs, "glider-20261101.log"), "v0.1 log");
        var v01State = Hashes(old.State);
        var velopack = layout.VelopackHashes();

        var moved = DataRoot.Prepare(null, DataRootMoverTests.Options(), layout.LocalAppData);

        Assert.Equal(DataRootMoveStatus.Moved, moved.Status);
        Assert.Equal(layout.New, moved.Root);
        var paths = new AppPaths(moved.Root, old.LegendaryExe);
        Assert.Equal(v01State, Hashes(paths.State)); // moved as is: the conversion happens next, in the new root

        var migrated = await Migrator.RunAsync(paths, _log, ct: TestContext.Current.CancellationToken);

        Assert.Equal(MigrationStatus.Migrated, migrated.Status);
        Assert.Equal("0.1", migrated.MigratedFrom);
        Assert.StartsWith(Path.Combine(layout.New, "backups", "migration"), migrated.BackupDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(v01State, Hashes(Path.Combine(migrated.BackupDirectory!, "state")));
        var schema = Migrator.ReadSchema(paths)!;
        Assert.Equal(3, schema.SchemaVersion);
        Assert.Equal("0.1", schema.MigratedFrom);
        Assert.Equal([("0.1", 2), ("2", 3)], schema.History!.Select(h => (h.From, h.To)));
        Assert.Contains(layout.Old, schema.History![1].Note, StringComparison.Ordinal);
        using (var games = await GamesStore.LoadAsync(paths, _log, TestContext.Current.CancellationToken))
        {
            Assert.Equal(@"D:\Games\Fortnite", games.Get(Fortnite).Install!.InstallPath);
        }

        Assert.Equal("resume", File.ReadAllText(Path.Combine(paths.LegendaryConfig, "tmp", "Fortnite.resume")));
        Assert.Equal("v0.1 log", File.ReadAllText(Path.Combine(paths.Logs, "glider-20261101.log")));
        Assert.Equal(velopack, layout.VelopackHashes());
        Assert.All(DataRoot.Directories, d => Assert.False(Directory.Exists(Path.Combine(layout.Old, d))));
        Assert.True(File.Exists(Path.Combine(layout.Old, DataRoot.PointerFile)));
        Assert.Equal(layout.New, DataRoot.Resolve(layout.Old, layout.New));

        // The next start: nothing moves, nothing migrates.
        Assert.Equal(DataRootMoveStatus.AlreadyMoved, DataRoot.Prepare(null, DataRootMoverTests.Options(), layout.LocalAppData).Status);
        Assert.Equal(MigrationStatus.UpToDate, (await Migrator.RunAsync(paths, _log, ct: TestContext.Current.CancellationToken)).Status);

        // And the runtime starts on it, reporting the v0.1 upgrade and the new data folder.
        using var env = new FakeLegendaryEnv();
        var fresh = new MoveLayout(_dir.Combine("LocalAppData2"));
        fresh.SeedVelopackOnly();
        SeedV01(new AppPaths(fresh.Old, env.Paths.LegendaryExe));
        var root = DataRoot.Prepare(null, DataRootMoverTests.Options(), fresh.LocalAppData).Root;
        await using var runtime = await StartRuntime(new AppPaths(root, env.Paths.LegendaryExe));
        Assert.Equal(MigrationStatus.Migrated, runtime.Migration.Status);
        var snap = await runtime.Rpc.GetSnapshot();
        Assert.Equal("0.1", snap.App.MigratedFrom);
        Assert.Equal(fresh.New, snap.App.DataDirectory);
        Assert.True(snap.Onboarding.Completed);
    }

    private Task<Glider.Core.Services.GliderRuntime> StartRuntime(AppPaths paths) =>
        Glider.Core.Services.GliderRuntime.StartAsync(paths, new Services.FakeHost(new Services.CallLog()), new Services.FakeSink(),
            new EglEnvironment(_dir.Combine("ProgramData")), options: new Glider.Core.Services.GliderRuntimeOptions
            {
                ProcessMonitor = new Services.FakeProcesses(),
                ProcessStarter = new Services.FakeProcesses(),
                DiskInfo = new Services.FakeDisk(),
                FreeGamesBackground = false,
                Legendary = new Glider.Core.Legendary.LegendaryOptions { PinnedVersion = "0.21.1" },
                OverlayRegistry = new Services.FakeOverlayRegistry(),
                OverlayEnvironment = new Glider.Core.Overlay.OverlayEnvironment(null),
                ThirdPartyLaunchers = new Services.FakeLaunchers(),
            }, ct: TestContext.Current.CancellationToken);

    private static string Normalized(string path) =>
        JsonSerializer.Serialize(JsonDocument.Parse(File.ReadAllBytes(path)).RootElement);

    public void Dispose() => _dir.Dispose();
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MigrationCollection
{
    public const string Name = "migration";
}
