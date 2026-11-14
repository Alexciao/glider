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

    /// <summary>Legendary printed "Failed to acquire installed data lock" (exit 0): busy, try again after this long (L§1.8).</summary>
    public TimeSpan LockRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Holds and the effective download mode (null = never hold, Balanced).</summary>
    public IQueuePolicy? Policy { get; init; }

    public IResourcePolicy Resources { get; init; } = DefaultResourcePolicy.Instance;

    /// <summary>Shared with the metadata lane (pre-download phase of the running writer job).</summary>
    public WriterActivity Activity { get; init; } = new();

    /// <summary>Catalog FolderName of an app (Legendary installs into &lt;base&gt;\&lt;FolderName&gt;); null = the app name.</summary>
    public Func<string, string?>? FolderNameFor { get; init; }

    /// <summary>Manifest install size of the installed game (verify byte totals); null = unknown.</summary>
    public Func<string, long?>? InstallSizeFor { get; init; }
}

/// <summary>
/// The writer lane (docs/v0.2-design.md §3, L§1.8): exactly one Legendary job that writes installed.json (or the game's files)
/// at a time, in a user-ordered, persistent queue (state/jobs.json). Jobs are idempotent per (AppName, Kind). A paused job
/// blocks only later jobs of the same game (and its DLC); others run. Pause = kill Legendary (its resume file keeps completed
/// files); resume = run the same command again. After a crash or restart, jobs that were running come back queued and resume
/// automatically; paused ones stay paused. Legendary's "installed data lock" refusal is "busy, retry in 5 s", never a failure.
/// While <see cref="IQueuePolicy.HoldReason"/> is set (a game runs and Settings.WhilePlaying is Pause) a running pausable job
/// is stopped and held, and nothing new starts until the hold lifts. Call <see cref="RestoreAsync"/> once at startup.
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
    private readonly Dictionary<string, JobSamples> _samples = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<Task> _background = [];
    private Task? _loop;
    private Running? _running;
    private bool _restored;
    private bool _disposed;
    private bool _paused;
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

    public WriterActivity Activity => _options.Activity;

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

    public bool Paused
    {
        get
        {
            lock (_lock)
            {
                return _paused;
            }
        }
    }

    public IReadOnlyList<JobInfo> Snapshot()
    {
        lock (_lock)
        {
            var running = _running?.Job;
            var unfinished = _jobs.Where(j => !IsTerminal(j.Info.Status)).OrderBy(j => j == running ? 0 : 1).Select(j => j.Info);
            var finished = _jobs.Where(j => IsTerminal(j.Info.Status)).OrderByDescending(j => j.Info.FinishedAt ?? j.Info.CreatedAt).Select(j => j.Info);
            return [.. unfinished, .. finished];
        }
    }

    public JobHistory History(string jobId)
    {
        lock (_lock)
        {
            return new JobHistory(jobId, _samples.TryGetValue(jobId, out var s) ? s.Snapshot() : []);
        }
    }

    public JobRequest? Request(string jobId)
    {
        lock (_lock)
        {
            return _jobs.FirstOrDefault(j => j.Info.Id == jobId)?.Request;
        }
    }

    public JobInfo Enqueue(JobRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        PersistedJob job;
        lock (_lock)
        {
            var existing = _jobs.FirstOrDefault(j => j.Request.Kind == request.Kind && j.Request.AppName == request.AppName && !IsTerminal(j.Info.Status));
            if (existing is not null)
            {
                return existing.Info; // idempotent per (app, kind): a second click doesn't queue a duplicate
            }

            var id = Guid.NewGuid().ToString("N");
            job = new PersistedJob
            {
                Info = new JobInfo(id, request.AppName, string.IsNullOrWhiteSpace(request.Title) ? request.AppName : request.Title!, request.Kind,
                    JobStatus.Queued, JobPlan.Phases(request.Kind), null, null, null, _time.GetUtcNow(), null, null,
                    request.TargetPath, request.FromVersion, request.ToVersion, null, null, null),
                Request = request,
            };
            if (request.Priority)
            {
                var at = _jobs.FindIndex(j => !IsTerminal(j.Info.Status) && j != _running?.Job);
                _jobs.Insert(at < 0 ? _jobs.Count : at, job);
            }
            else
            {
                _jobs.Add(job);
            }

            _samples[id] = new JobSamples();
        }

        _log.Info("jobs", $"Queued {request.Kind} {request.AppName} {job.Info.Id}{(request.Priority ? " (priority)" : "")}.");
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

            if (!JobPlan.CanPause(job.Request.Kind))
            {
                throw new GliderException(Errors.InvalidRequest($"A {job.Info.Kind} task can't be paused."));
            }

            running = _running?.Job == job ? _running : null;
            if (running is null)
            {
                job.Held = false;
                job.Info = job.Info with { Status = JobStatus.Paused, BlockedReason = null };
            }
            else
            {
                running.Intent = StopIntent.Pause;
            }
        }

        if (running is not null)
        {
            await StopAsync(running);
        }
        else
        {
            Changed?.Invoke();
        }

        await PersistAsync();
        _signal.Release(); // another game's job may run now
        return Find(jobId).Info;
    }

    public async Task<JobInfo> ResumeAsync(string jobId)
    {
        lock (_lock)
        {
            var job = Find(jobId);
            if (job.Info.Status is JobStatus.Queued or JobStatus.Running)
            {
                _paused = false;
                return job.Info;
            }

            if (job.Info.Status is not (JobStatus.Paused or JobStatus.Failed or JobStatus.Waiting))
            {
                throw new GliderException(Errors.InvalidRequest($"A {job.Info.Status} task can't be resumed."));
            }

            if (job.Info.Status == JobStatus.Waiting && _running?.Job == job)
            {
                return job.Info; // waiting for EGL/lock inside its run: it continues by itself
            }

            job.Held = false;
            job.Info = job.Info with { Status = JobStatus.Queued, Error = null, FinishedAt = null, BlockedReason = null, Outcome = null };
            _paused = false; // resuming one job resumes the queue
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
                job.Held = false;
                job.Info = job.Info with { Status = JobStatus.Cancelled, FinishedAt = _time.GetUtcNow(), BlockedReason = null, Outcome = null };
            }
            else
            {
                running.Intent = StopIntent.Cancel;
            }
        }

        if (running is not null)
        {
            await StopAsync(running);
        }
        else
        {
            _log.Info("jobs", $"Cancelled {job.Info.Kind} {job.Info.AppName} {jobId}.");
            StartCleanup(job);
            Changed?.Invoke();
            _signal.Release(); // a paused job no longer blocks its game
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
            _samples.Remove(jobId);
        }

        PersistInBackground();
        Changed?.Invoke();
    }

    /// <summary>
    /// Moves an unfinished job to <paramref name="toIndex"/> in the run order (index into the unfinished jobs as
    /// <see cref="Snapshot"/> lists them). The running job keeps running: nothing is pre-empted.
    /// </summary>
    public async Task ReorderAsync(string jobId, int toIndex)
    {
        lock (_lock)
        {
            var job = Find(jobId);
            if (IsTerminal(job.Info.Status))
            {
                throw new GliderException(Errors.InvalidRequest("Finished tasks can't be reordered."));
            }

            var unfinished = _jobs.Where(j => !IsTerminal(j.Info.Status)).OrderBy(j => j == _running?.Job ? 0 : 1).ToList();
            unfinished.Remove(job);
            var min = _running is not null && _running.Job != job ? 1 : 0;
            unfinished.Insert(Math.Clamp(toIndex, min, unfinished.Count), job);
            var finished = _jobs.Where(j => IsTerminal(j.Info.Status)).ToList();
            _jobs.Clear();
            _jobs.AddRange(unfinished);
            _jobs.AddRange(finished);
        }

        await PersistAsync();
        Changed?.Invoke();
        _signal.Release();
    }

    public Task MoveToTopAsync(string jobId) => ReorderAsync(jobId, 0);

    /// <summary>Pauses the running job (if it can pause) and stops the queue from starting anything until resume-all.</summary>
    public async Task PauseAllAsync()
    {
        Running? running;
        lock (_lock)
        {
            _paused = true;
            running = _running is { } r && JobPlan.CanPause(r.Job.Request.Kind) ? r : null;
            if (running is not null)
            {
                running.Intent = StopIntent.Pause;
            }

            foreach (var j in _jobs.Where(j => j.Info.Status is JobStatus.Queued || (j.Info.Status == JobStatus.Waiting && j.Held)))
            {
                if (JobPlan.CanPause(j.Request.Kind))
                {
                    j.Held = false;
                    j.Info = j.Info with { Status = JobStatus.Paused, BlockedReason = null };
                }
            }
        }

        _log.Info("jobs", "Queue paused.");
        if (running is not null)
        {
            await StopAsync(running);
        }

        await PersistAsync();
        Changed?.Invoke();
    }

    public async Task ResumeAllAsync()
    {
        lock (_lock)
        {
            _paused = false;
            foreach (var j in _jobs.Where(j => j.Info.Status == JobStatus.Paused))
            {
                j.Info = j.Info with { Status = JobStatus.Queued, BlockedReason = null };
            }
        }

        _log.Info("jobs", "Queue resumed.");
        await PersistAsync();
        Changed?.Invoke();
        EnsureLoop();
        _signal.Release();
    }

    public void ClearFinished()
    {
        lock (_lock)
        {
            foreach (var j in _jobs.Where(j => IsTerminal(j.Info.Status)).ToList())
            {
                _jobs.Remove(j);
                _samples.Remove(j.Info.Id);
            }
        }

        PersistInBackground();
        Changed?.Invoke();
    }

    /// <summary>A game started or closed: stop the running job for a hold, or wake the loop when the hold lifted.</summary>
    public void Reevaluate()
    {
        var hold = _options.Policy?.HoldReason;
        Running? stop = null;
        lock (_lock)
        {
            if (hold is not null && _running is { Intent: StopIntent.Shutdown } r && JobPlan.CanPause(r.Job.Request.Kind) && !_disposed)
            {
                r.Intent = StopIntent.Hold;
                r.HoldReason = hold;
                stop = r;
            }
        }

        if (stop is not null)
        {
            _log.Info("jobs", $"Holding {stop.Job.Info.Kind} {stop.Job.Info.AppName}: {hold}");
            _ = Task.Run(() => StopAsync(stop));
        }
        else
        {
            Changed?.Invoke(); // the mode/hold reason shown in the queue changed
            _signal.Release();
        }
    }

    public async Task RestoreAsync(CancellationToken ct)
    {
        PersistedJobFile? file;
        try
        {
            file = await _store.LoadAsync(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Whatever is on disk, the queue must come up: an un-restored queue never runs or saves anything.
            _log.Error("jobs", "jobs.json could not be loaded; starting with an empty queue.", e);
            file = null;
        }

        lock (_lock)
        {
            if (!_restored && file?.Jobs is { } persisted)
            {
                _paused = file.Paused;
                foreach (var job in persisted)
                {
                    // jobs.json is data: drop entries a crash, another build or a hand edit left unusable.
                    if (job?.Info is null || job.Request is null || string.IsNullOrEmpty(job.Info.Id) || string.IsNullOrWhiteSpace(job.Request.AppName) ||
                        !Enum.IsDefined(job.Info.Kind) || !Enum.IsDefined(job.Info.Status) || job.Request.Kind != job.Info.Kind)
                    {
                        _log.Warn("jobs", "Ignoring an unusable entry in jobs.json.");
                        continue;
                    }

                    job.Baseline ??= new ProgressBaseline();
                    if (_jobs.Any(j => j.Info.Id == job.Info.Id))
                    {
                        continue;
                    }

                    job.Info = job.Info with
                    {
                        AppName = job.Request.AppName,
                        Title = string.IsNullOrWhiteSpace(job.Info.Title) ? job.Request.Title ?? job.Request.AppName : job.Info.Title,
                        Phases = job.Info.Phases is { Count: > 0 } ? job.Info.Phases : JobPlan.Phases(job.Info.Kind),
                    };
                    if (job.Info.Status is JobStatus.Running or JobStatus.Waiting)
                    {
                        // Interrupted by a crash, shutdown or restart: Legendary's resume file has the progress.
                        _log.Info("jobs", $"Resuming interrupted {job.Info.Kind} {job.Info.AppName} {job.Info.Id} after restart.");
                        job.Held = false;
                        job.Info = job.Info with { Status = JobStatus.Queued, BlockedReason = null };
                    }

                    _jobs.Add(job);
                    _samples[job.Info.Id] = new JobSamples();
                }

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
            await StopAsync(running);
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

    /// <summary>Completes once nothing is running and nothing claimable is queued (paused/held/finished jobs don't count). For tests and shutdown.</summary>
    internal async Task WhenIdleAsync(CancellationToken ct)
    {
        while (true)
        {
            lock (_lock)
            {
                if (_running is null && (_paused || _options.Policy?.HoldReason is not null || !_jobs.Any(j => Claimable(j))))
                {
                    return;
                }
            }

            await Task.Delay(10, ct);
        }
    }

    /// <summary>Stops a running job and waits until it has fully settled (Legendary dead, state recorded).</summary>
    private static async Task StopAsync(Running running)
    {
        try
        {
            await running.Cts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // it finished on its own a moment ago
        }

        await running.Done.Task;
    }

    // ------------------------------------------------------------------ loop

    /// <summary>Nothing runs (or is written) before <see cref="RestoreAsync"/>: an early save would overwrite the jobs it must load.</summary>
    private void EnsureLoop()
    {
        lock (_lock)
        {
            if (_restored && !_disposed)
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
            while (!ct.IsCancellationRequested && Claim(ct) is { } running)
            {
                await RunAsync(running, ct);
            }
        }
    }

    /// <summary>Queued (or held, once the hold lifted) and not behind a paused job of the same game.</summary>
    private bool Claimable(PersistedJob job)
    {
        if (!(job.Info.Status == JobStatus.Queued || (job.Info.Status == JobStatus.Waiting && job.Held)))
        {
            return false;
        }

        foreach (var j in _jobs)
        {
            if (j == job)
            {
                return true;
            }

            if (j.Group == job.Group && j.Info.Status == JobStatus.Paused)
            {
                return false; // a paused job blocks only its own game
            }
        }

        return true;
    }

    /// <summary>
    /// Picks the next runnable job and marks it running in the same critical section, so a concurrent pause/cancel sees
    /// either a queued job (and changes it directly) or a running one (and stops it) — never a job in between.
    /// </summary>
    private Running? Claim(CancellationToken shutdown)
    {
        var hold = _options.Policy?.HoldReason;
        lock (_lock)
        {
            if (_running is not null || _paused || _disposed)
            {
                return null;
            }

            foreach (var job in _jobs)
            {
                if (!Claimable(job))
                {
                    continue;
                }

                if (hold is not null && JobPlan.CanPause(job.Request.Kind))
                {
                    continue; // held while a game runs; uninstall/move/import still proceed
                }

                var running = new Running(job, CancellationTokenSource.CreateLinkedTokenSource(shutdown));
                _running = running;
                job.Held = false;
                job.Info = job.Info with
                {
                    Status = JobStatus.Running,
                    BlockedReason = null,
                    Error = null,
                    Outcome = null,
                    StartedAt = job.Info.StartedAt ?? _time.GetUtcNow(),
                    Phase = job.Info.Phase ?? JobPhase.Preparing,
                };
                return running;
            }

            return null;
        }
    }

    private async Task RunAsync(Running running, CancellationToken shutdown)
    {
        var job = running.Job;
        using var cts = running.Cts;
        JobSamples samples;
        lock (_lock)
        {
            if (!_samples.TryGetValue(job.Info.Id, out samples!))
            {
                samples = _samples[job.Info.Id] = new JobSamples();
            }
        }

        using var tracker = new JobProgressTracker(job.Info.Id, job.Request.AppName, JobPlan.Segment(job.Request.Kind, job.Step), job.Baseline, _time,
            _options.ProgressInterval, OnProgress, phase => OnPhase(job, phase), () => _options.Policy?.EffectiveMode ?? _settings.Current.DownloadMode,
            samples, () => (job.VerifyBad, job.Info.Verify?.FilesFixed ?? 0), _options.InstallSizeFor?.Invoke(job.Request.AppName));
        lock (_lock)
        {
            running.Tracker = tracker;
        }

        _log.Info("jobs", $"Running {job.Info.Kind} {job.Info.AppName} {job.Info.Id} (step {job.Step}).");
        await PersistAsync();
        Changed?.Invoke();

        GliderError? error = null;
        var result = Result.Failed;
        try
        {
            await _hooks.BeforeRunAsync(job.Info, reason => SetBlocked(job, reason), cts.Token);
            SetBlocked(job, null);
            while (true)
            {
                error = await ExecuteAsync(job, tracker, cts.Token);
                if (error?.Code != ErrorCode.LegendaryLocked)
                {
                    break;
                }

                // Another Legendary (EGL sync, a stray process) holds installed.json.lock: busy, not failed.
                _log.Info("jobs", $"{job.Info.Kind} {job.Info.AppName}: Legendary's install lock is busy; retrying in {_options.LockRetryDelay.TotalSeconds:0} s.");
                SetBlocked(job, Errors.LegendaryLocked().Message);
                await Task.Delay(_options.LockRetryDelay, _time, cts.Token);
                SetBlocked(job, null);
            }

            result = error is null ? Result.Succeeded : Result.Failed;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            result = running.Intent switch
            {
                StopIntent.Pause => Result.Paused,
                StopIntent.Cancel => Result.Cancelled,
                StopIntent.Hold => Result.Held,
                _ => Result.Interrupted,
            };
        }
        catch (GliderException e)
        {
            error = e.Error;
        }
        catch (Exception e)
        {
            _log.Error("jobs", $"{job.Info.Kind} {job.Info.AppName} {job.Info.Id} crashed.", e);
            error = Errors.Unknown(e.Message);
        }
        finally
        {
            _options.Activity.EndPreDownload();
        }

        tracker.Flush();
        var baseline = tracker.Snapshot();
        JobInfo final;
        lock (_lock)
        {
            job.Baseline = baseline;
            var info = job.Info with { BlockedReason = null };
            var progress = info.Progress is { } p ? Idle(p) : null;
            final = result switch
            {
                Result.Succeeded => info with
                {
                    Status = JobStatus.Completed,
                    FinishedAt = _time.GetUtcNow(),
                    Error = null,
                    Phase = JobPhase.Finalizing,
                    Progress = progress is null ? null : progress with { Percent = 100, Phase = JobPhase.Finalizing },
                    Outcome = new JobOutcome(OutcomeKind.Success, DefaultSuccess(job.Request.Kind)),
                },
                Result.Failed => info with
                {
                    Status = JobStatus.Failed,
                    FinishedAt = _time.GetUtcNow(),
                    Error = (error ?? Errors.Unknown()) with { AppName = job.Request.AppName },
                    Progress = progress,
                    Outcome = new JobOutcome(OutcomeKind.Failed, (error ?? Errors.Unknown()).Message),
                },
                Result.Paused => info with { Status = JobStatus.Paused, Progress = progress },
                Result.Held => info with { Status = JobStatus.Waiting, BlockedReason = running.HoldReason, Progress = progress },
                Result.Cancelled => info with { Status = JobStatus.Cancelled, FinishedAt = _time.GetUtcNow(), Progress = progress },
                _ => info with { Progress = progress }, // interrupted by shutdown: keep "running" so the next start resumes it
            };
            job.Held = result == Result.Held;
        }

        _log.Info("jobs", $"{job.Info.Kind} {job.Info.AppName} {job.Info.Id}: {result}{(error is null ? "" : $" ({error.Code}: {error.Detail})")}.");

        // Hooks run before the terminal status is published, so the UI never sees "completed" while the install record
        // or the EGL write-back are still stale.
        if (result is Result.Succeeded or Result.Failed)
        {
            try
            {
                if (result == Result.Succeeded)
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
            // A hook may have set a richer outcome (e.g. "Imported; needs repair") on the job meanwhile.
            final = final with { Outcome = job.PendingOutcome ?? final.Outcome };
            job.PendingOutcome = null;
            job.Info = final;
            _running = null;
            Prune();
        }

        if (result == Result.Cancelled)
        {
            StartCleanup(job);
        }

        if (result != Result.Interrupted)
        {
            await PersistAsync();
        }

        Changed?.Invoke();
        running.Done.TrySetResult();
    }

    /// <summary>Hooks call this (during OnSucceeded/OnFailed) to replace the default outcome of the finishing job.</summary>
    public void SetOutcome(string jobId, JobOutcome outcome)
    {
        lock (_lock)
        {
            if (_jobs.FirstOrDefault(j => j.Info.Id == jobId) is { } job)
            {
                if (IsTerminal(job.Info.Status))
                {
                    job.Info = job.Info with { Outcome = outcome };
                }
                else
                {
                    job.PendingOutcome = outcome;
                }
            }
        }
    }

    private static JobProgress Idle(JobProgress p) => p with
    {
        EtaSeconds = null,
        Network = p.Network is { } n ? n with { BytesPerSecond = 0 } : null,
        Disk = p.Disk is { } d ? d with { BytesPerSecond = 0 } : null,
        Verify = p.Verify is { } v ? v with { ReadBytesPerSecond = 0 } : null,
    };

    private static string DefaultSuccess(JobKind kind) => kind switch
    {
        JobKind.Install => "Installed",
        JobKind.Update => "Updated",
        JobKind.Repair => "Repaired",
        JobKind.Verify => "All files OK",
        JobKind.Move => "Moved",
        JobKind.Uninstall => "Uninstalled",
        JobKind.Import => "Imported",
        _ => "Done",
    };

    // ------------------------------------------------------------------ executors

    private async Task<GliderError?> ExecuteAsync(PersistedJob job, JobProgressTracker tracker, CancellationToken ct)
    {
        var app = job.Request.AppName;
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
                return await VerifyAndRepairAsync(job, tracker, ct);
            case JobKind.Move:
                tracker.SetPhase(JobPhase.Moving);
                return (await _legendary.MoveAsync(app, request.TargetPath!, skipMove: false, tracker, ct)).Error;
            case JobKind.Uninstall:
                tracker.SetPhase(JobPhase.Removing);
                return (await _legendary.UninstallAsync(app, keepFiles: request.UninstallMode != UninstallMode.DeleteFiles, ct)).Error;
            case JobKind.Import:
                tracker.SetPhase(JobPhase.Importing);
                var imported = await WithPreDownloadAsync(tracker, () => _legendary.ImportAsync(app, request.TargetPath!, ct));
                tracker.SetPhase(JobPhase.Finalizing);
                return imported.Error;
            default:
                return Errors.NotAvailableYet($"{request.Kind} jobs");
        }
    }

    /// <summary>Marks the writer's login/metadata phase for the metadata lane until the first progress line or the end of the run.</summary>
    private async Task<LegendaryOutcome> WithPreDownloadAsync(JobProgressTracker tracker, Func<Task<LegendaryOutcome>> run)
    {
        var activity = _options.Activity;
        activity.BeginPreDownload();
        tracker.OnFirstProgress(activity.EndPreDownload);
        try
        {
            return await run();
        }
        finally
        {
            activity.EndPreDownload();
        }
    }

    /// <summary>
    /// "Verify and repair": verify; if files are bad, `repair` them (downloads only those); verify again to confirm.
    /// `verify` alone only reports. Every "repair" button in the UI enqueues this job, so it must do the whole flow.
    /// </summary>
    private async Task<GliderError?> VerifyAndRepairAsync(PersistedJob job, JobProgressTracker tracker, CancellationToken ct)
    {
        var app = job.Request.AppName;
        if (job.Step == JobStep.Main)
        {
            tracker.SetPhase(JobPhase.Verifying);
            var first = await _legendary.VerifyAsync(app, tracker, ct);
            var checkedFiles = first.Counts?.FilesChecked ?? 0;
            var bad = first.Success ? 0 : first.Counts?.Bad ?? 0;
            lock (_lock)
            {
                job.VerifyChecked = checkedFiles;
                job.VerifyBad = bad;
            }

            if (first.Success)
            {
                SetVerifyReport(job, new VerifyReport(checkedFiles, 0, 0, true, 0));
                await ClearVerificationFlagAsync(job, tracker, ct);
                return null;
            }

            if (first.Error?.Code != ErrorCode.Corrupted)
            {
                return first.Error;
            }

            _log.Info("jobs", $"Verify found problems ({first.Error.Detail}); repairing only those files.");
            SetVerifyReport(job, new VerifyReport(checkedFiles, bad, 0, false, 0));
            await AdvanceAsync(job, JobStep.Repair, tracker);
        }

        if (job.Step == JobStep.Repair)
        {
            tracker.SetPhase(JobPhase.Repairing);
            var repaired = await WithPreDownloadAsync(tracker, () => _legendary.InstallAsync(Args(InstallMode.Repair, job), tracker, ct));
            if (!repaired.Success)
            {
                return repaired.Error;
            }

            if (repaired.DownloadSizeBytes is { } toFix && job.Info.Verify is { } r)
            {
                SetVerifyReport(job, r with { BytesToFix = toFix });
            }

            await AdvanceAsync(job, JobStep.Verify, tracker);
        }

        tracker.SetPhase(JobPhase.Verifying);
        var second = await _legendary.VerifyAsync(app, tracker, ct);
        var remaining = second.Success ? 0 : Math.Max(second.Counts?.Bad ?? job.VerifyBad, 1);
        var checkedAfter = second.Counts?.FilesChecked is > 0 and var n ? n : job.VerifyChecked;
        SetVerifyReport(job, new VerifyReport(checkedAfter, job.VerifyBad, Math.Max(0, job.VerifyBad - remaining), second.Success, job.Info.Verify?.BytesToFix ?? 0));
        if (second.Success)
        {
            return null;
        }

        return second.Error?.Code == ErrorCode.Corrupted
            ? Errors.Corrupted($"{remaining} file(s) still damaged or missing after the repair.")
            : second.Error;
    }

    /// <summary>
    /// Legendary only clears <c>needs_verification</c> in installed.json from a <c>repair</c> that finds nothing to download (cli.py:1013-1022,
    /// "Download size is 0"); a clean <c>verify</c> leaves it set. Left set, the next update starts in repair mode and applies nothing
    /// (cli.py:854-856) — the state an adopted install is in right after its first, successful verify. This run reads the .repair file the
    /// verify just wrote and exits at once. Best effort: a failure only means the flag stays.
    /// </summary>
    private async Task ClearVerificationFlagAsync(PersistedJob job, JobProgressTracker tracker, CancellationToken ct)
    {
        try
        {
            if (await _legendary.GetInstalledAsync(job.Request.AppName, ct) is not { NeedsVerification: true })
            {
                return;
            }

            tracker.SetPhase(JobPhase.Finalizing);
            var cleared = await WithPreDownloadAsync(tracker, () => _legendary.InstallAsync(Args(InstallMode.Repair, job), tracker, ct));
            if (!cleared.Success)
            {
                _log.Warn("jobs", $"Verify of {job.Request.AppName} passed, but clearing Legendary's needs-verification flag failed: {cleared.Error?.Code}");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException and not GliderException)
        {
            _log.Warn("jobs", $"Clearing the needs-verification flag of {job.Request.AppName} failed", e);
        }
    }

    private void SetVerifyReport(PersistedJob job, VerifyReport report)
    {
        lock (_lock)
        {
            job.Info = job.Info with { Verify = report };
        }

        Changed?.Invoke();
    }

    /// <summary>install/update/repair, then the verify policy, then at most one automatic repair of what verify found.</summary>
    private async Task<GliderError?> InstallFlowAsync(PersistedJob job, InstallMode mode, VerifyPolicy policy, JobProgressTracker tracker, CancellationToken ct)
    {
        var app = job.Request.AppName;
        if (job.Step == JobStep.Main)
        {
            if (mode == InstallMode.Install && job.GameFolder is null && ExpectedGameFolder(job.Request) is { } folder)
            {
                // Legendary installs into <base>\<catalog FolderName>. Remember whether we create that folder, so cancelling a
                // fresh install may delete it without ever touching pre-existing data.
                lock (_lock)
                {
                    job.GameFolder = folder;
                    job.CreatedGameFolder = !Directory.Exists(folder);
                }

                await PersistAsync();
            }

            if (mode == InstallMode.Repair)
            {
                tracker.SetPhase(JobPhase.Verifying);
            }

            var outcome = await WithPreDownloadAsync(tracker, () => _legendary.InstallAsync(Args(mode, job), tracker, ct));
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

        tracker.SetPhase(JobPhase.Repairing);
        var repaired = await WithPreDownloadAsync(tracker, () => _legendary.InstallAsync(Args(InstallMode.Repair, job), tracker, ct));
        return repaired.Success ? null : repaired.Error;
    }

    private async Task AdvanceAsync(PersistedJob job, JobStep step, JobProgressTracker tracker)
    {
        tracker.BeginStep(JobPlan.Segment(job.Request.Kind, step));
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
        var effective = _options.Policy?.EffectiveMode ?? s.DownloadMode;
        var isDlc = job.Request.Options?.BaseAppName is not null;
        return new InstallArgs(
            job.Request.AppName,
            mode,
            isDlc ? null : job.Request.TargetPath, // a DLC installs into its base game's folder
            s.DownloadWorkers ?? _auto.Workers,
            s.SharedMemoryMb ?? _auto.SharedMemoryMb,
            s.PreferredCdn,
            job.Request.Options?.Components,
            _options.Resources.For(effective, job.Request.Kind));
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
                _log.Info("jobs", $"{job.Info.Kind} {job.Info.AppName} {job.Info.Id} waiting: {reason}");
            }

            PersistInBackground();
            Changed?.Invoke();
        }
    }

    private void OnPhase(PersistedJob job, JobPhase phase)
    {
        lock (_lock)
        {
            job.Info = job.Info with { Phase = phase, Progress = job.Info.Progress is { } p ? p with { Phase = phase } : null };
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

            job.Info = job.Info with { Progress = progress, Phase = progress.Phase };
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

        var app = job.Request.AppName;
        var task = Task.Run(async () =>
        {
            try
            {
                if (await _legendary.GetInstalledAsync(app, CancellationToken.None) is not null)
                {
                    return; // something is registered there: never touch it
                }

                AtomicFile.TryDelete(LegendaryFiles.ResumeFile(_paths.LegendaryConfig, app));
                if (job.CreatedGameFolder && job.GameFolder is { } folder && Directory.Exists(folder))
                {
                    // jobs.json is data, not authority: only ever delete <base>\<FolderName> recomputed from the request.
                    var expected = ExpectedGameFolder(job.Request);
                    if (expected is null || !FolderMover.PathsEqual(folder, expected))
                    {
                        _log.Error("jobs", $"Not removing \"{folder}\": it isn't the folder this install would have created ({expected ?? "no base path"}).");
                        return;
                    }

                    _log.Info("jobs", $"Removing the partial install at \"{folder}\".");
                    FolderMover.TryDeleteTree(folder, _log, _paths.Root, Path.GetDirectoryName(_paths.LegendaryExe), job.Request.TargetPath);
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

    /// <summary><c>&lt;base&gt;\&lt;FolderName&gt;</c> for a (non-DLC) install request with an absolute base path, else null.</summary>
    private string? ExpectedGameFolder(JobRequest request)
    {
        if (request.Options?.BaseAppName is not null || request.TargetPath is not { } basePath || !Path.IsPathFullyQualified(basePath))
        {
            return null;
        }

        var folderName = _options.FolderNameFor?.Invoke(request.AppName) is { Length: > 0 } f ? f : request.AppName;
        if (folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folderName is "." or "..")
        {
            return null;
        }

        return Path.TrimEndingDirectorySeparator(Path.Combine(Path.GetFullPath(basePath), folderName));
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
        var finished = _jobs.Where(j => IsTerminal(j.Info.Status)).OrderBy(j => j.Info.FinishedAt ?? j.Info.CreatedAt).ToList();
        foreach (var old in finished.Take(Math.Max(0, finished.Count - _options.KeepFinishedJobs)))
        {
            _jobs.Remove(old);
            _samples.Remove(old.Info.Id);
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

            snapshot = new PersistedJobFile { Paused = _paused, Jobs = _jobs.Select(j => j.Clone()).ToList() };
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
        if (string.IsNullOrWhiteSpace(r.AppName))
        {
            throw new GliderException(Errors.InvalidRequest("A task needs an app name."));
        }

        if (!Enum.IsDefined(r.Kind))
        {
            throw new GliderException(Errors.InvalidRequest("Unknown task kind."));
        }

        var needsPath = r.Kind is JobKind.Move or JobKind.Import || (r.Kind == JobKind.Install && r.Options?.BaseAppName is null);
        if (needsPath && string.IsNullOrWhiteSpace(r.TargetPath))
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

    private static bool IsTerminal(JobStatus s) => s is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;

    private enum StopIntent
    {
        Shutdown,
        Pause,
        Cancel,
        Hold,
    }

    private enum Result
    {
        Succeeded,
        Failed,
        Paused,
        Cancelled,
        Held,
        Interrupted,
    }

    private sealed class Running(PersistedJob job, CancellationTokenSource cts)
    {
        public PersistedJob Job { get; } = job;

        public CancellationTokenSource Cts { get; } = cts;

        public StopIntent Intent { get; set; } = StopIntent.Shutdown;

        public string? HoldReason { get; set; }

        public JobProgressTracker? Tracker { get; set; }

        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
