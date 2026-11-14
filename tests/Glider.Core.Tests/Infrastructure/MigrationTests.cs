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
        Assert.Equal(2, schema.SchemaVersion);
        Assert.Equal("0.1", schema.MigratedFrom);

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
        Assert.Equal(2, Migrator.ReadSchema(Paths)!.SchemaVersion);
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

    private static string Normalized(string path) =>
        JsonSerializer.Serialize(JsonDocument.Parse(File.ReadAllBytes(path)).RootElement);

    public void Dispose() => _dir.Dispose();
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MigrationCollection
{
    public const string Name = "migration";
}
