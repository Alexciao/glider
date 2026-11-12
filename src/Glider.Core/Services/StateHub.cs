using Glider.Core.Contracts;

namespace Glider.Core.Services;

/// <summary>Service-layer state that isn't owned by a store (auth, EGL scan, run state, update info…).</summary>
public sealed record HubState(
    AuthState Auth,
    EglState? Egl,
    string LegendaryVersion,
    bool Warm,
    string? LatestVersion,
    long? DownloadSizeBytes,
    long? InstallSizeBytes,
    bool CheckingForUpdates,
    DateTimeOffset? LastUpdateCheck,
    RunState RunState,
    DateTimeOffset? RunningSince,
    GameArt? Art,
    /// <summary>Recorded install whose folder no longer holds the game.</summary>
    bool InstallBroken,
    /// <summary>Stores loaded (before that the install status is Unknown).</summary>
    bool Loaded,
    /// <summary>Last measured size of the install folder (see <see cref="InstalledSizeMeter"/>).</summary>
    SizeMeasure? InstalledSize = null)
{
    public static HubState Initial { get; } = new(
        new AuthState(AuthStatus.Unknown, null), null, "", false, null, null, null, false, null, RunState.Idle, null, null, false, false);
}

/// <summary>Bytes measured in <paramref name="Path"/> while it held <paramref name="Version"/>; stale as soon as either changes.</summary>
public sealed record SizeMeasure(string Path, string Version, long Bytes, DateTimeOffset MeasuredAt);

/// <summary>
/// Builds <see cref="AppSnapshot"/> from every source and publishes it as a coalesced `state` event
/// (trailing edge, at most one per <see cref="GliderServiceOptions.StateCoalesceInterval"/>). Forwards job progress.
/// Building a snapshot only reads cached values — it never spawns processes or touches the network.
/// </summary>
public sealed class StateHub : IDisposable
{
    private static readonly JobStatus[] ActiveStatuses = [JobStatus.Queued, JobStatus.Running, JobStatus.Paused, JobStatus.Waiting];

    private readonly AppPaths _paths;
    private readonly IHostPlatform _host;
    private readonly IJobQueue _jobs;
    private readonly ISettingsStore _settings;
    private readonly IGameRecordStore _records;
    private readonly IEventSink _sink;
    private readonly IOnboardingStore _onboarding;
    private readonly GliderServiceOptions _options;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private readonly Lock _publishGate = new();
    private HubState _state = HubState.Initial;
    private bool _pending;
    private bool _attached;
    private bool _disposed;

    public StateHub(
        AppPaths paths,
        IHostPlatform host,
        IJobQueue jobs,
        ISettingsStore settings,
        IGameRecordStore records,
        IEventSink sink,
        TimeProvider time,
        IOnboardingStore onboarding,
        GliderServiceOptions options)
    {
        _paths = paths;
        _host = host;
        _jobs = jobs;
        _settings = settings;
        _records = records;
        _sink = sink;
        _onboarding = onboarding;
        _options = options;
        _timer = time.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public HubState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Raised synchronously after every effective <see cref="Mutate"/> (before coalescing).</summary>
    public event Action<HubState>? StateChanged;

    /// <summary>Number of `state` events published (diagnostics/tests).</summary>
    public int PublishCount { get; private set; }

    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _jobs.Changed += Invalidate;
        _jobs.ProgressChanged += OnProgress;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>Atomically transforms the hub state and schedules a publish if it changed.</summary>
    public HubState Mutate(Func<HubState, HubState> change)
    {
        HubState next;
        bool changed;
        lock (_gate)
        {
            next = change(_state);
            changed = !Equals(next, _state);
            _state = next;
        }

        if (changed)
        {
            StateChanged?.Invoke(next);
            Invalidate();
        }

        return next;
    }

    /// <summary>Some source changed: publish a fresh snapshot at the end of the current coalescing window.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            if (_pending || _disposed)
            {
                return;
            }

            _pending = true;
        }

        _timer.Change(_options.StateCoalesceInterval, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Publish immediately (startup first paint), cancelling a pending coalesced publish.</summary>
    public void PublishNow()
    {
        lock (_gate)
        {
            _pending = false;
        }

        _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Publish();
    }

    private void Flush()
    {
        lock (_gate)
        {
            if (!_pending)
            {
                return;
            }

            _pending = false;
        }

        Publish();
    }

    private void Publish()
    {
        lock (_publishGate)
        {
            var snap = BuildSnapshot();
            PublishCount++;
            _sink.Publish(RpcEvents.State, snap);
        }
    }

    private void OnProgress(JobProgress p) => _sink.Publish(RpcEvents.Progress, p);

    private void OnSettingsChanged(Settings _) => Invalidate();

    public void PublishNotice(Notice notice) => _sink.Publish(RpcEvents.Notice, notice);

    // ------------------------------------------------------------------ snapshot building

    public static bool IsActive(JobInfo j) => ActiveStatuses.Contains(j.Status);

    public IReadOnlyList<JobInfo> ActiveJobs() => _jobs.Snapshot().Where(IsActive).ToList();

    public JobInfo? FirstActiveJob() => ActiveJobs() is { Count: > 0 } a ? a[0] : null;

    public AppSnapshot BuildSnapshot()
    {
        var s = State;
        var settings = _settings.Current;
        var jobs = _jobs.Snapshot();
        var game = BuildGame(s, jobs);
        return new AppSnapshot(
            new AppInfo(
                _options.AppVersion,
                s.LegendaryVersion,
                _host.IsWindows11,
                _host.Backdrop,
                _paths.Root,
                _paths.Logs,
                Environment.ProcessorCount,
                TotalMemory(),
                _options.AutoWorkers,
                _options.AutoSharedMemoryMb,
                s.Warm),
            s.Auth,
            game,
            jobs,
            BuildEgl(s, settings),
            settings,
            BuildOnboarding(s, game));
    }

    public GameState BuildGame() => BuildGame(State, _jobs.Snapshot());

    private GameState BuildGame(HubState s, IReadOnlyList<JobInfo> jobs)
    {
        var record = _records.Current;
        var installing = jobs.Any(j => j.Kind == JobKind.Install && IsActive(j));
        var status = !s.Loaded ? InstallStatus.Unknown
            : installing && record is null ? InstallStatus.Installing
            : record is null ? InstallStatus.NotInstalled
            : s.InstallBroken ? InstallStatus.Broken
            : InstallStatus.Installed;

        InstalledGame? installed = record is null ? null : new InstalledGame(
            record.InstallPath,
            record.Version,
            record.InstallSizeBytes,
            record.EglInstallationGuid is not null,
            record.NeedsVerification,
            record.InstalledAt);

        var updateAvailable = record is not null && s.LatestVersion is not null
            && !string.Equals(s.LatestVersion, record.Version, StringComparison.Ordinal);

        return new GameState(
            FortniteLayout.AppName,
            FortniteLayout.Title,
            status,
            installed,
            s.LatestVersion,
            updateAvailable,
            s.CheckingForUpdates,
            s.LastUpdateCheck,
            s.RunState,
            s.RunningSince,
            s.Art,
            s.DownloadSizeBytes,
            s.InstallSizeBytes,
            record is not null && s.InstalledSize is { } m
                && string.Equals(m.Path, record.InstallPath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(m.Version, record.Version, StringComparison.Ordinal)
                ? m.Bytes
                : null,
            record?.PlaytimeSeconds ?? 0,
            record?.LastPlayedAt);
    }

    private EglState BuildEgl(HubState s, Settings settings)
    {
        var record = _records.Current;
        var syncAvailable = record?.EglInstallationGuid is not null;
        var e = s.Egl;
        return e is null
            ? new EglState(false, false, false, [], settings.EglSync, syncAvailable, null, null)
            : e with { SyncEnabled = settings.EglSync, SyncAvailable = syncAvailable && e.LauncherInstalled };
    }

    private OnboardingState BuildOnboarding(HubState s, GameState game)
    {
        // Existing installs (EGL adoption or an earlier Glider) count as onboarded.
        if (_onboarding.Completed || game.Installed is not null)
        {
            return new OnboardingState(true, OnboardingStep.Done);
        }

        var step = s.Auth.Status == AuthStatus.SignedIn ? OnboardingStep.ChooseInstall : OnboardingStep.Welcome;
        return new OnboardingState(false, step);
    }

    private static long TotalMemory() => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        if (_attached)
        {
            _jobs.Changed -= Invalidate;
            _jobs.ProgressChanged -= OnProgress;
            _settings.Changed -= OnSettingsChanged;
        }

        _timer.Dispose();
    }
}
