using Glider.Core.Contracts;
using Glider.Core.Infrastructure;
using Glider.Core.Legendary;

namespace Glider.Core.Jobs;

/// <summary>When to run the post-download verify pass (Legendary doesn't abort on write failures, manager.py:588-590).</summary>
public enum VerifyPolicy
{
    Never,
    /// <summary>Only when the run logged write failures of unknown cause.</summary>
    OnWriteFailure,
    Always,
}

public sealed class JobQueueOptions
{
    public string AppName { get; init; } = FortniteLayout.AppName;

    /// <summary>Workers / cache used when the settings say "auto". Null = <see cref="Infrastructure.AutoTune.ForCurrentMachine"/>.</summary>
    public AutoTuneResult? AutoTune { get; init; }

    /// <summary>Fresh installs: one full SHA-1 pass catches silent write problems before the user plays.</summary>
    public VerifyPolicy VerifyAfterInstall { get; init; } = VerifyPolicy.Always;

    /// <summary>Weekly updates: a full verify costs minutes of disk I/O; only when writes failed.</summary>
    public VerifyPolicy VerifyAfterUpdate { get; init; } = VerifyPolicy.OnWriteFailure;

    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Progress is persisted at most this often while a job runs (state changes are persisted immediately).</summary>
    public TimeSpan ProgressPersistInterval { get; init; } = TimeSpan.FromSeconds(10);

    public int KeepFinishedJobs { get; init; } = 20;
}

/// <summary>
/// Serial, persistent job queue. Every state change is written atomically to state/jobs.json. Jobs run strictly in
/// order; a paused job blocks the ones behind it (an update or verify must not run under a half-finished install).
/// Pause = kill Legendary (its resume file keeps completed files); resume = run the same command again. After a crash
/// or restart, jobs that were running come back queued and resume automatically; paused ones stay paused.
/// Call <see cref="RestoreAsync"/> once at startup; jobs only start running after it.
/// </summary>
public sealed class JobQueue : IJobQueue, IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly ILegendary _legendary;
    private readonly ISettingsStore _settings;
    private readonly IJobHooks _hooks;
    private readonly ILog _log;
    private readonly TimeProvider _time;
    private readonly JobQueueOptions _options;
    private readonly AutoTuneResult _auto;
    private readonly JsonFileStore<PersistedJobFile> _store;
    private readonly Lock _lock = new();
    private readonly List<PersistedJob> _jobs = [];
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _background = [];
    private Task? _loop;
    private Running? _running;
    private bool _restored;
    private bool _disposed;
    private long _lastProgressPersist;

    public JobQueue(AppPaths paths, ILegendary legendary, ISettingsStore settings, IJobHooks hooks, ILog log, TimeProvider? time = null, JobQueueOptions? options = null)
    {
        _paths = paths;
        _legendary = legendary;
        _settings = settings;
        _hooks = hooks;
        _log = log;
        _time = time ?? TimeProvider.System;
        _options = options ?? new JobQueueOptions();
        _auto = _options.AutoTune ?? AutoTune.ForCurrentMachine();
        _store = new JsonFileStore<PersistedJobFile>(PathFor(paths), EngineJsonContext.Default.PersistedJobFile, log);
    }

    public event Action? Changed;

    public event Action<JobProgress>? ProgressChanged;

    public static string PathFor(AppPaths paths) => Path.Combine(paths.State, "jobs.json");

    public bool IsBusy
    {
        get
        {
            lock (_lock)
            {
                return _running is not null;
            }
        }
    }

    public IReadOnlyList<JobInfo> Snapshot()
    {
        lock (_lock)
        {
            return _jobs.Select(j => j.Info).ToList();
        }
    }

    public JobInfo Enqueue(JobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        PersistedJob job;
        lock (_lock)
        {
            var existing = _jobs.FirstOrDefault(j => j.Request.Kind == request.Kind && !IsTerminal(j.Info.Status));
            if (existing is not null)
            {
                return existing.Info; // idempotent: a second click doesn't queue a duplicate
            }

            job = new PersistedJob
            {
                Info = new JobInfo(Guid.NewGuid().ToString("N"), request.Kind, JobStatus.Queued, null, null, _time.GetUtcNow(), null,
                    request.TargetPath, request.FromVersion, request.ToVersion, null),
                Request = request,
            };
            _jobs.Add(job);
        }

        _log.Info("jobs", $"Queued {request.Kind} {job.Info.Id}.");
        PersistInBackground();
        Changed?.Invoke();
        EnsureLoop();
        _signal.Release();
        return job.Info;
    }

    public async Task<JobInfo> PauseAsync(string jobId)
    {
        Running? running;
        lock (_lock)
        {
            var job = Find(jobId);
            if (job.Info.Status == JobStatus.Paused)
            {
                return job.Info;
            }

            if (IsTerminal(job.Info.Status))
            {
                throw new GliderException(Errors.InvalidRequest($"The {job.Info.Kind} task has already finished."));
            }

            if (!CanPause(job.Request.Kind))
            {
                throw new GliderException(Errors.InvalidRequest($"A {job.Info.Kind} task can't be paused."));
            }

            running = _running?.Job == job ? _running : null;
            if (running is null)
            {
                job.Info = job.Info with { Status = JobStatus.Paused, BlockedReason = null };
            }
            else
            {
                running.Intent = StopIntent.Pause;
            }
        }

        if (running is not null)
        {
            await running.Cts.CancelAsync();
            await running.Done.Task;
        }
        else
        {
            Changed?.Invoke();
        }

        await PersistAsync();
        return Find(jobId).Info;
    }

    public async Task<JobInfo> ResumeAsync(string jobId)
    {
        lock (_lock)
        {
            var job = Find(jobId);
            if (job.Info.Status is JobStatus.Queued or JobStatus.Running or JobStatus.Waiting)
            {
                return job.Info;
            }

            if (job.Info.Status is not (JobStatus.Paused or JobStatus.Failed))
            {
                throw new GliderException(Errors.InvalidRequest($"A {job.Info.Status} task can't be resumed."));
            }

            job.Info = job.Info with { Status = JobStatus.Queued, Error = null, FinishedAt = null, BlockedReason = null };
        }

        _log.Info("jobs", $"Resuming {jobId}.");
        await PersistAsync();
        Changed?.Invoke();
        EnsureLoop();
        _signal.Release();
        return Find(jobId).Info;
    }

    public async Task<JobInfo> CancelAsync(string jobId)
    {
        Running? running;
        PersistedJob job;
        lock (_lock)
        {
            job = Find(jobId);
            if (IsTerminal(job.Info.Status) && job.Info.Status != JobStatus.Failed)
            {
                return job.Info;
            }

            running = _running?.Job == job ? _running : null;
            if (running is null)
            {
                job.Info = job.Info with { Status = JobStatus.Cancelled, FinishedAt = _time.GetUtcNow(), BlockedReason = null };
            }
            else
            {
                running.Intent = StopIntent.Cancel;
            }
        }

        if (running is not null)
        {
            await running.Cts.CancelAsync();
            await running.Done.Task;
        }
        else
        {
            _log.Info("jobs", $"Cancelled {job.Info.Kind} {jobId}.");
            StartCleanup(job);
            Changed?.Invoke();
            _signal.Release(); // a paused head no longer blocks the queue
        }

        await PersistAsync();
        return Find(jobId).Info;
    }

    public void Dismiss(string jobId)
    {
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.Info.Id == jobId);
            if (job is null || !IsTerminal(job.Info.Status))
            {
                return;
            }

            _jobs.Remove(job);
        }

        PersistInBackground();
        Changed?.Invoke();
    }

    public async Task RestoreAsync(CancellationToken ct)
    {
        var file = await _store.LoadAsync(ct);
        lock (_lock)
        {
            if (!_restored && file is not null)
            {
                foreach (var job in file.Jobs.Where(j => j.Info is not null && j.Request is not null))
                {
                    if (_jobs.Any(j => j.Info.Id == job.Info.Id))
                    {
                        continue;
                    }

                    if (job.Info.Status is JobStatus.Running or JobStatus.Waiting)
                    {
                        // Interrupted by a crash, shutdown or restart: Legendary's resume file has the progress.
                        _log.Info("jobs", $"Resuming interrupted {job.Info.Kind} {job.Info.Id} after restart.");
                        job.Info = job.Info with { Status = JobStatus.Queued, BlockedReason = null };
                    }

                    _jobs.Add(job);
                }

                _jobs.Sort((a, b) => a.Info.CreatedAt.CompareTo(b.Info.CreatedAt));
                Prune();
            }

            _restored = true;
        }

        await PersistAsync();
        Changed?.Invoke();
        EnsureLoop();
        _signal.Release();
    }

    /// <summary>Stops the loop. A running job is interrupted, not paused: it stays "running" on disk and resumes on next start.</summary>
    public async ValueTask DisposeAsync()
    {
        Running? running;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            running = _running;
            if (running is not null)
            {
                running.Intent = StopIntent.Shutdown;
            }
        }

        await _shutdown.CancelAsync();
        if (running is not null)
        {
            await running.Done.Task;
        }

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        Task[] background;
        lock (_lock)
        {
            background = [.. _background];
        }

        await Task.WhenAll(background);
        await PersistAsync();
        _store.Dispose();
        _signal.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>Completes once no job is queued or running (paused/finished jobs don't count). For tests and shutdown.</summary>
    internal async Task WhenIdleAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_running is null && !_jobs.Any(j => j.Info.Status == JobStatus.Queued && !BlockedByPause(j)))
                {
                    return;
                }
            }

            await Task.Delay(10, ct);
        }
    }

    // ------------------------------------------------------------------ loop

    /// <summary>Nothing runs (or is written) before <see cref="RestoreAsync"/>: an early save would overwrite the jobs it must load.</summary>
    private void EnsureLoop()
    {
        lock (_lock)
        {
            if (_restored)
            {
                _loop ??= Task.Run(LoopAsync);
            }
        }
    }

    private async Task LoopAsync()
    {
        var ct = _shutdown.Token;
        while (!ct.IsCancellationRequested)
        {
            await _signal.WaitAsync(ct);
            while (!ct.IsCancellationRequested && Next() is { } job)
            {
                await RunAsync(job, ct);
            }
        }
    }

    private PersistedJob? Next()
    {
        lock (_lock)
        {
            foreach (var job in _jobs)
            {
                switch (job.Info.Status)
                {
                    case JobStatus.Paused:
                        return null; // strictly serial: nothing overtakes a paused job
                    case JobStatus.Queued:
                        return job;
                }
            }

            return null;
        }
    }

    private bool BlockedByPause(PersistedJob job)
    {
        foreach (var j in _jobs)
        {
            if (j == job)
            {
                return false;
            }

            if (j.Info.Status == JobStatus.Paused)
            {
                return true;
            }
        }

        return false;
    }

    private async Task RunAsync(PersistedJob job, CancellationToken shutdown)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        var running = new Running(job, cts);
        using var tracker = new JobProgressTracker(job.Info.Id, job.Baseline, _time, _options.ProgressInterval, OnProgress, phase => OnPhase(job, phase));
        running.Tracker = tracker;
        lock (_lock)
        {
            _running = running;
            job.Info = job.Info with
            {
                Status = JobStatus.Running,
                BlockedReason = null,
                Error = null,
                Progress = job.Info.Progress is { } p ? p with { Phase = JobPhase.Preparing, DownloadBytesPerSecond = 0, RawDownloadBytesPerSecond = 0, DiskBytesPerSecond = 0, EtaSeconds = null } : null,
            };
        }

        _log.Info("jobs", $"Running {job.Info.Kind} {job.Info.Id} (step {job.Step}).");
        await PersistAsync();
        Changed?.Invoke();

        GliderError? error = null;
        var outcome = Outcome.Failed;
        try
        {
            await _hooks.BeforeRunAsync(job.Info, reason => SetBlocked(job, reason), cts.Token);
            SetBlocked(job, null);
            error = await ExecuteAsync(job, tracker, cts.Token);
            outcome = error is null ? Outcome.Succeeded : Outcome.Failed;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            outcome = running.Intent switch
            {
                StopIntent.Pause => Outcome.Paused,
                StopIntent.Cancel => Outcome.Cancelled,
                _ => Outcome.Interrupted,
            };
        }
        catch (GliderException e)
        {
            error = e.Error;
        }
        catch (Exception e)
        {
            _log.Error("jobs", $"{job.Info.Kind} {job.Info.Id} crashed.", e);
            error = Errors.Unknown(e.Message);
        }

        tracker.Flush();
        var baseline = tracker.Snapshot();
        JobInfo final;
        lock (_lock)
        {
            job.Baseline = baseline;
            var info = job.Info with { BlockedReason = null };
            var progress = info.Progress is { } p ? p with { DownloadBytesPerSecond = 0, RawDownloadBytesPerSecond = 0, DiskBytesPerSecond = 0, EtaSeconds = null } : null;
            final = outcome switch
            {
                Outcome.Succeeded => info with { Status = JobStatus.Completed, FinishedAt = _time.GetUtcNow(), Error = null, Progress = progress is null ? null : progress with { Percent = 100 } },
                Outcome.Failed => info with { Status = JobStatus.Failed, FinishedAt = _time.GetUtcNow(), Error = error ?? Errors.Unknown(), Progress = progress },
                Outcome.Paused => info with { Status = JobStatus.Paused, Progress = progress },
                Outcome.Cancelled => info with { Status = JobStatus.Cancelled, FinishedAt = _time.GetUtcNow(), Progress = progress },
                _ => info with { Progress = progress }, // interrupted by shutdown: keep "running" so the next start resumes it
            };
        }

        _log.Info("jobs", $"{job.Info.Kind} {job.Info.Id}: {outcome}{(error is null ? "" : $" ({error.Code}: {error.Detail})")}.");

        // Hooks run before the terminal status is published, so the UI never sees "completed" while the game record
        // or the EGL write-back are still stale.
        if (outcome is Outcome.Succeeded or Outcome.Failed)
        {
            try
            {
                if (outcome == Outcome.Succeeded)
                {
                    await _hooks.OnSucceededAsync(final, shutdown);
                }
                else
                {
                    await _hooks.OnFailedAsync(final, shutdown);
                }
            }
            catch (Exception e)
            {
                _log.Error("jobs", $"Job hook failed for {job.Info.Id}.", e);
            }
        }

        lock (_lock)
        {
            job.Info = final;
            _running = null;
            Prune();
        }

        if (outcome == Outcome.Cancelled)
        {
            StartCleanup(job);
        }

        if (outcome != Outcome.Interrupted)
        {
            await PersistAsync();
        }

        Changed?.Invoke();
        running.Done.TrySetResult();
    }

    // ------------------------------------------------------------------ executors

    private async Task<GliderError?> ExecuteAsync(PersistedJob job, JobProgressTracker tracker, CancellationToken ct)
    {
        var app = _options.AppName;
        var request = job.Request;
        switch (request.Kind)
        {
            case JobKind.Install:
                return await InstallFlowAsync(job, InstallMode.Install, _options.VerifyAfterInstall, tracker, ct);
            case JobKind.Update:
                return await InstallFlowAsync(job, InstallMode.Update, _options.VerifyAfterUpdate, tracker, ct);
            case JobKind.Repair:
                return await InstallFlowAsync(job, InstallMode.Repair, VerifyPolicy.Never, tracker, ct);
            case JobKind.Verify:
                tracker.SetPhase(JobPhase.Verifying);
                return (await _legendary.VerifyAsync(app, tracker, ct)).Error;
            case JobKind.Move:
                tracker.SetPhase(JobPhase.Moving);
                return (await _legendary.MoveAsync(app, request.TargetPath!, skipMove: false, tracker, ct)).Error;
            case JobKind.Uninstall:
                tracker.SetPhase(JobPhase.Removing);
                return (await _legendary.UninstallAsync(app, keepFiles: request.UninstallMode != UninstallMode.DeleteFiles, ct)).Error;
            case JobKind.Import:
                tracker.SetPhase(JobPhase.Preparing);
                var imported = await _legendary.ImportAsync(app, request.TargetPath!, ct);
                tracker.SetPhase(JobPhase.Finalizing);
                return imported.Error;
            default:
                return Errors.InvalidRequest($"Unknown job kind {request.Kind}.");
        }
    }

    /// <summary>install/update/repair, then the verify policy, then at most one automatic repair of what verify found.</summary>
    private async Task<GliderError?> InstallFlowAsync(PersistedJob job, InstallMode mode, VerifyPolicy policy, JobProgressTracker tracker, CancellationToken ct)
    {
        var app = _options.AppName;
        if (job.Step == JobStep.Main)
        {
            if (mode == InstallMode.Install && job.GameFolder is null && job.Request.TargetPath is { } basePath)
            {
                // Legendary installs into <base>\<catalog FolderName> ("Fortnite"). Remember whether we create that folder,
                // so cancelling a fresh install may delete it without ever touching pre-existing data.
                var folder = Path.Combine(Path.GetFullPath(basePath), app);
                lock (_lock)
                {
                    job.GameFolder = folder;
                    job.CreatedGameFolder = !Directory.Exists(folder);
                }

                await PersistAsync();
            }

            var outcome = await _legendary.InstallAsync(Args(mode, job), tracker, ct);
            var writeFailures = !outcome.Success && outcome.Error?.Code == ErrorCode.Corrupted && mode != InstallMode.Repair;
            if (!outcome.Success && !writeFailures)
            {
                return outcome.Error;
            }

            var verify = policy == VerifyPolicy.Always || writeFailures;
            if (!verify)
            {
                return null;
            }

            _log.Info("jobs", writeFailures ? "Writes failed during the download; verifying the install." : "Verifying the fresh install.");
            await AdvanceAsync(job, JobStep.Verify, tracker);
        }

        if (job.Step == JobStep.Verify)
        {
            tracker.SetPhase(JobPhase.Verifying);
            var verified = await _legendary.VerifyAsync(app, tracker, ct);
            if (verified.Success)
            {
                return null;
            }

            if (verified.Error?.Code != ErrorCode.Corrupted || job.AutoRepairAttempted)
            {
                return verified.Error;
            }

            _log.Info("jobs", $"Verify found problems ({verified.Error.Detail}); repairing only those files.");
            lock (_lock)
            {
                job.AutoRepairAttempted = true;
            }

            await AdvanceAsync(job, JobStep.Repair, tracker);
        }

        tracker.SetPhase(JobPhase.Preparing);
        var repaired = await _legendary.InstallAsync(Args(InstallMode.Repair, job), tracker, ct);
        return repaired.Success ? null : repaired.Error;
    }

    private async Task AdvanceAsync(PersistedJob job, JobStep step, JobProgressTracker tracker)
    {
        tracker.BeginStep();
        lock (_lock)
        {
            job.Step = step;
            job.Baseline = tracker.Snapshot();
        }

        await PersistAsync();
    }

    private InstallArgs Args(InstallMode mode, PersistedJob job)
    {
        var s = _settings.Current;
        return new InstallArgs(
            _options.AppName,
            mode,
            job.Request.TargetPath,
            s.DownloadWorkers ?? _auto.Workers,
            s.SharedMemoryMb ?? _auto.SharedMemoryMb,
            s.PreferredCdn);
    }

    // ------------------------------------------------------------------ state helpers

    private void SetBlocked(PersistedJob job, string? reason)
    {
        bool changed;
        lock (_lock)
        {
            var status = reason is null ? JobStatus.Running : JobStatus.Waiting;
            changed = job.Info.Status != status || job.Info.BlockedReason != reason;
            if (changed && !IsTerminal(job.Info.Status))
            {
                job.Info = job.Info with { Status = status, BlockedReason = reason };
            }
        }

        if (changed)
        {
            if (reason is not null)
            {
                _log.Info("jobs", $"{job.Info.Kind} {job.Info.Id} waiting: {reason}");
            }

            PersistInBackground();
            Changed?.Invoke();
        }
    }

    private void OnPhase(PersistedJob job, JobPhase phase)
    {
        lock (_lock)
        {
            var p = job.Info.Progress ?? new JobProgress(job.Info.Id, phase, 0, 0, 0, 0, 0, 0, 0, 0, null, 0);
            job.Info = job.Info with { Progress = p with { Phase = phase } };
        }

        PersistInBackground();
        Changed?.Invoke();
    }

    private void OnProgress(JobProgress progress)
    {
        var persist = false;
        lock (_lock)
        {
            var job = _jobs.FirstOrDefault(j => j.Info.Id == progress.JobId);
            if (job is null || IsTerminal(job.Info.Status))
            {
                return;
            }

            job.Info = job.Info with { Progress = progress };
            if (_running?.Job == job && _running.Tracker is { } t)
            {
                job.Baseline = t.Snapshot();
            }

            var now = _time.GetTimestamp();
            if (_time.GetElapsedTime(_lastProgressPersist, now) >= _options.ProgressPersistInterval)
            {
                _lastProgressPersist = now;
                persist = true;
            }
        }

        if (persist)
        {
            PersistInBackground();
        }

        ProgressChanged?.Invoke(progress);
    }

    /// <summary>
    /// Cancelled fresh install: delete Legendary's resume file so a later install starts clean, and the game folder only
    /// if this job created it and Legendary never registered the game. Cancelled update/repair: keep the resume file —
    /// without it Legendary would treat already-updated files as old and patch from them, corrupting the game.
    /// </summary>
    private void StartCleanup(PersistedJob job)
    {
        if (job.Request.Kind != JobKind.Install)
        {
            return;
        }

        var task = Task.Run(async () =>
        {
            try
            {
                if (await _legendary.GetInstalledAsync(_options.AppName, CancellationToken.None) is not null)
                {
                    return; // something is registered there: never touch it
                }

                AtomicFile.TryDelete(LegendaryFiles.ResumeFile(_paths.LegendaryConfig, _options.AppName));
                if (job.CreatedGameFolder && job.GameFolder is { } folder && Directory.Exists(folder))
                {
                    _log.Info("jobs", $"Removing the partial install at \"{folder}\".");
                    FolderMover.TryDeleteTree(folder, _log);
                }
            }
            catch (Exception e)
            {
                _log.Warn("jobs", "Cleanup after cancel failed.", e);
            }
        });
        lock (_lock)
        {
            _background.RemoveAll(t => t.IsCompleted);
            _background.Add(task);
        }
    }

    internal Task WhenCleanupDoneAsync()
    {
        lock (_lock)
        {
            return Task.WhenAll(_background);
        }
    }

    private PersistedJob Find(string jobId)
    {
        lock (_lock)
        {
            return _jobs.FirstOrDefault(j => j.Info.Id == jobId)
                   ?? throw new GliderException(Errors.InvalidRequest($"No task with id {jobId}."));
        }
    }

    private void Prune()
    {
        var finished = _jobs.Where(j => IsTerminal(j.Info.Status)).ToList();
        foreach (var old in finished.Take(Math.Max(0, finished.Count - _options.KeepFinishedJobs)))
        {
            _jobs.Remove(old);
        }
    }

    private void PersistInBackground() => _ = PersistAsync();

    private async Task PersistAsync()
    {
        PersistedJobFile snapshot;
        lock (_lock)
        {
            if (!_restored)
            {
                return;
            }

            snapshot = new PersistedJobFile { Jobs = _jobs.Select(j => j.Clone()).ToList() };
        }

        try
        {
            await _store.SaveAsync(snapshot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            _log.Warn("jobs", "Could not save jobs.json.", e);
        }
    }

    private static void Validate(JobRequest r)
    {
        if (r.Kind is JobKind.Install or JobKind.Move or JobKind.Import && string.IsNullOrWhiteSpace(r.TargetPath))
        {
            throw new GliderException(Errors.InvalidRequest($"{r.Kind} needs a target path."));
        }

        if (r.TargetPath is { } p && r.Kind is JobKind.Install or JobKind.Move or JobKind.Import && !Path.IsPathFullyQualified(p))
        {
            throw new GliderException(Errors.InvalidRequest($"\"{p}\" is not an absolute path."));
        }

        if (r.Kind == JobKind.Uninstall && r.UninstallMode is null)
        {
            throw new GliderException(Errors.InvalidRequest("Uninstall needs a mode."));
        }
    }

    private static bool CanPause(JobKind kind) => kind is JobKind.Install or JobKind.Update or JobKind.Repair or JobKind.Verify;

    private static bool IsTerminal(JobStatus s) => s is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;

    private enum StopIntent
    {
        Shutdown,
        Pause,
        Cancel,
    }

    private enum Outcome
    {
        Succeeded,
        Failed,
        Paused,
        Cancelled,
        Interrupted,
    }

    private sealed class Running(PersistedJob job, CancellationTokenSource cts)
    {
        public PersistedJob Job { get; } = job;

        public CancellationTokenSource Cts { get; } = cts;

        public StopIntent Intent { get; set; } = StopIntent.Shutdown;

        public JobProgressTracker? Tracker { get; set; }

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
