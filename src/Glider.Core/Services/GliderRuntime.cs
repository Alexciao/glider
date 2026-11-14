using Glider.Core.Contracts;
using Glider.Core.Egl;
using Glider.Core.Infrastructure;
using Glider.Core.Infrastructure.Migrations;
using Glider.Core.Jobs;
using Glider.Core.Legendary;

namespace Glider.Core.Services;

/// <summary>
/// Composition root: migrates older data, then builds the real object graph (log, stores, Legendary, writer lane, EGL,
/// service) and starts it. The host calls <see cref="StartAsync"/> once on a background thread and exposes
/// <see cref="Rpc"/> to the UI. Kept in Core so the full graph is testable end-to-end with FakeLegendary and a temp EGL tree.
/// </summary>
public sealed class GliderRuntime : IAsyncDisposable
{
    private readonly FileLog _log;
    private readonly LegendaryCli _legendary;
    private readonly JobQueue _jobs;
    private readonly EglService _egl;
    private readonly GliderService _service;
    private readonly IDisposable[] _stores;

    private GliderRuntime(FileLog log, LegendaryCli legendary, JobQueue jobs, EglService egl, GliderService service, MigrationResult migration, IDisposable[] stores)
    {
        _log = log;
        _legendary = legendary;
        _jobs = jobs;
        _egl = egl;
        _service = service;
        Migration = migration;
        _stores = stores;
    }

    public IGliderRpc Rpc => _service;

    public GliderService Service => _service;

    public ILog Log => _log;

    /// <summary>What the startup migration did (diagnostics/tests).</summary>
    public MigrationResult Migration { get; }

    internal JobQueue Jobs => _jobs;

    public static async Task<GliderRuntime> StartAsync(
        AppPaths paths,
        IHostPlatform host,
        IEventSink events,
        EglEnvironment? eglEnvironment = null,
        IIdleSignal? idleSignal = null,
        TimeProvider? time = null,
        GliderRuntimeOptions? options = null,
        CancellationToken ct = default)
    {
        time ??= TimeProvider.System;
        options ??= new GliderRuntimeOptions();
        paths.EnsureCreated();
        var log = new FileLog(paths.Logs, time);
        try
        {
            log.Info("startup", $"Glider starting; data={paths.Root}");
            var migration = await Migrator.RunAsync(paths, log, time, ct);

            var settingsTask = SettingsStore.LoadAsync(paths, log, ct);
            var gamesTask = GamesStore.LoadAsync(paths, log, ct);
            var collectionsTask = CollectionsStore.LoadAsync(paths, log, ct);
            var onboardingTask = FileOnboardingStore.LoadAsync(paths, log, ct);
            var settings = await settingsTask;
            var games = await gamesTask;
            var collections = await collectionsTask;
            var onboarding = await onboardingTask;
            log.MinimumLevel = settings.Current.LogLevel;

            var tune = AutoTune.ForCurrentMachine();
            // Production verifies the bundled exe against the pinned hash; tests inject FakeLegendary via options.
            var legendary = new LegendaryCli(
                paths,
                log,
                options.Legendary ?? new LegendaryOptions { Runner = new LegendaryRunnerOptions { ExpectedSha256 = LegendaryCli.PinnedSha256 } },
                time);
            var hooks = new DeferredJobHooks();
            var policy = new DeferredQueuePolicy();
            var activity = new WriterActivity();
            GliderService? service = null;
            var jobs = new JobQueue(paths, legendary, settings, hooks, log, time, new JobQueueOptions
            {
                AutoTune = tune,
                Policy = policy,
                Activity = activity,
                LockRetryDelay = options.LockRetryDelay ?? TimeSpan.FromSeconds(5),
                FolderNameFor = app => service?.LibraryForTests.FolderName(app),
                InstallSizeFor = app => service?.LibraryForTests.Installed(app)?.InstallSizeBytes,
            });
            var egl = new EglService(eglEnvironment ?? EglEnvironment.ForMachine(), paths, log, options.EglProcesses, options.Egl);
            var defaults = new GliderServiceOptions();
            service = new GliderService(paths, host, legendary, jobs, egl, settings, games, log, time, events, new GliderServiceOptions
            {
                AutoWorkers = tune.Workers,
                AutoSharedMemoryMb = tune.SharedMemoryMb,
                IdleSignal = idleSignal,
                ProcessMonitor = options.ProcessMonitor ?? defaults.ProcessMonitor,
                ProcessStarter = options.ProcessStarter ?? defaults.ProcessStarter,
                ServiceQuery = options.ServiceQuery ?? defaults.ServiceQuery,
                DiskInfo = options.DiskInfo ?? defaults.DiskInfo,
                ProcessPollInterval = options.ProcessPollInterval ?? defaults.ProcessPollInterval,
                SessionEndGrace = options.SessionEndGrace ?? defaults.SessionEndGrace,
                LibraryCoalesceInterval = options.LibraryCoalesceInterval ?? defaults.LibraryCoalesceInterval,
                LibraryCacheDelay = options.LibraryCacheDelay ?? defaults.LibraryCacheDelay,
                OnboardingStore = onboarding,
                Collections = collections,
                WriterActivity = activity,
                OverlayRegistry = options.OverlayRegistry ?? defaults.OverlayRegistry,
                OverlayEnvironment = options.OverlayEnvironment ?? defaults.OverlayEnvironment,
                ThirdPartyLaunchers = options.ThirdPartyLaunchers ?? defaults.ThirdPartyLaunchers,
            });
            hooks.Target = service.Hooks;
            policy.Target = service.QueuePolicy;
            service.SetMigratedFrom(migration.MigratedFrom);
            await service.StartAsync();
            return new GliderRuntime(log, legendary, jobs, egl, service, migration, [settings, games, collections, onboarding]);
        }
        catch (Exception e)
        {
            log.Error("startup", "composition failed", e);
            await log.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _service.Dispose();
        await _jobs.DisposeAsync();
        _egl.Dispose();
        _legendary.Dispose();
        foreach (var s in _stores)
        {
            s.Dispose();
        }

        await _log.DisposeAsync();
    }
}

/// <summary>Optional seams for <see cref="GliderRuntime.StartAsync"/>. Null = the real implementation; end-to-end tests swap a few.</summary>
public sealed class GliderRuntimeOptions
{
    public LegendaryOptions? Legendary { get; init; }
    public EglServiceOptions? Egl { get; init; }
    public IProcessProbe? EglProcesses { get; init; }
    public IProcessMonitor? ProcessMonitor { get; init; }
    public IGameProcessStarter? ProcessStarter { get; init; }
    public IServiceQuery? ServiceQuery { get; init; }
    public IDiskInfo? DiskInfo { get; init; }
    public TimeSpan? ProcessPollInterval { get; init; }
    public TimeSpan? SessionEndGrace { get; init; }
    public TimeSpan? LockRetryDelay { get; init; }
    public TimeSpan? LibraryCoalesceInterval { get; init; }
    public TimeSpan? LibraryCacheDelay { get; init; }
    public Overlay.IOverlayRegistry? OverlayRegistry { get; init; }
    public Overlay.OverlayEnvironment? OverlayEnvironment { get; init; }
    public Game.IThirdPartyLaunchers? ThirdPartyLaunchers { get; init; }
}

/// <summary>Bridges construction cycles: the queue needs the policy, the policy comes from GliderService (which needs the queue).</summary>
public sealed class DeferredQueuePolicy : IQueuePolicy
{
    public IQueuePolicy? Target { get; set; }

    public string? HoldReason => Target?.HoldReason;

    public DownloadMode EffectiveMode => Target?.EffectiveMode ?? DownloadMode.Balanced;

    public string? ModeReason => Target?.ModeReason;
}
