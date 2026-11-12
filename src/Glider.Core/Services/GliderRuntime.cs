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
        CancellationToken ct = default)
    {
        time ??= TimeProvider.System;
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
            var legendary = new LegendaryCli(paths, log, new LegendaryOptions { Runner = new LegendaryRunnerOptions { ExpectedSha256 = LegendaryCli.PinnedSha256 } }, time);
            var hooks = new DeferredJobHooks();
            var jobs = new JobQueue(paths, legendary, settings, hooks, log, time, new JobQueueOptions { AutoTune = tune });
            var egl = new EglService(eglEnvironment ?? EglEnvironment.ForMachine(), paths, log, settings: settings, records: records);
            var service = new GliderService(paths, host, legendary, jobs, egl, settings, records, log, time, events, new GliderServiceOptions
            {
                AutoWorkers = tune.Workers,
                AutoSharedMemoryMb = tune.SharedMemoryMb,
                IdleSignal = idleSignal,
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
