using Glider.Core.Contracts;
using Glider.Core.Egl;
using Glider.Core.Infrastructure;
using Glider.Core.Jobs;
using Glider.Core.Legendary;

namespace Glider.Core.Services;

/// <summary>
/// Composition root: builds the real object graph (log, stores, Legendary, job queue, EGL, service) and starts it.
/// The host calls <see cref="StartAsync"/> once on a background thread and exposes <see cref="Rpc"/> to the UI.
/// Kept in Core so the full graph is testable end-to-end with FakeLegendary and a temp EGL tree.
/// </summary>
public sealed class GliderRuntime : IAsyncDisposable
{
    private readonly FileLog _log;
    private readonly LegendaryCli _legendary;
    private readonly JobQueue _jobs;
    private readonly EglService _egl;
    private readonly GliderService _service;

    private GliderRuntime(FileLog log, LegendaryCli legendary, JobQueue jobs, EglService egl, GliderService service)
    {
        _log = log;
        _legendary = legendary;
        _jobs = jobs;
        _egl = egl;
        _service = service;
    }

    public IGliderRpc Rpc => _service;

    public GliderService Service => _service;

    public ILog Log => _log;

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
            var settingsTask = SettingsStore.LoadAsync(paths, log, ct);
            var recordsTask = GameRecordStore.LoadAsync(paths, log, ct);
            var settings = await settingsTask;
            var records = await recordsTask;
            log.MinimumLevel = settings.Current.LogLevel;

            var tune = AutoTune.ForCurrentMachine();
            // Production verifies the bundled exe against the pinned hash; tests inject FakeLegendary via options.
            var legendary = new LegendaryCli(
                paths,
                log,
                options.Legendary ?? new LegendaryOptions { Runner = new LegendaryRunnerOptions { ExpectedSha256 = LegendaryCli.PinnedSha256 } },
                time);
            var hooks = new DeferredJobHooks();
            var jobs = new JobQueue(paths, legendary, settings, hooks, log, time, new JobQueueOptions { AutoTune = tune });
            var egl = new EglService(eglEnvironment ?? EglEnvironment.ForMachine(), paths, log, options.EglProcesses, settings, records, options.Egl);
            var defaults = new GliderServiceOptions();
            var service = new GliderService(paths, host, legendary, jobs, egl, settings, records, log, time, events, new GliderServiceOptions
            {
                AutoWorkers = tune.Workers,
                AutoSharedMemoryMb = tune.SharedMemoryMb,
                IdleSignal = idleSignal,
                ProcessMonitor = options.ProcessMonitor ?? defaults.ProcessMonitor,
                ProcessStarter = options.ProcessStarter ?? defaults.ProcessStarter,
                ServiceQuery = options.ServiceQuery ?? defaults.ServiceQuery,
                DiskInfo = options.DiskInfo ?? defaults.DiskInfo,
                ArtCache = options.ArtCache,
                ProcessPollInterval = options.ProcessPollInterval ?? defaults.ProcessPollInterval,
            });
            hooks.Target = service.Hooks;
            await service.StartAsync();
            return new GliderRuntime(log, legendary, jobs, egl, service);
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
    public IArtCache? ArtCache { get; init; }
    public TimeSpan? ProcessPollInterval { get; init; }
}
