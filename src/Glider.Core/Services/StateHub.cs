using System.Collections.Immutable;
using Glider.Core.Contracts;

namespace Glider.Core.Services;

/// <summary>Bytes measured in <paramref name="Path"/> while it held <paramref name="Version"/>; stale as soon as either changes.</summary>
public sealed record SizeMeasure(string Path, string Version, long Bytes, DateTimeOffset MeasuredAt);

/// <summary>Legendary's dry-run download size for updating <paramref name="Installed"/> to <paramref name="Latest"/>.</summary>
public sealed record UpdateSize(string Installed, string Latest, long DownloadBytes);

/// <summary>
/// Service-layer state that isn't owned by a store: auth, connectivity, the EGL scan, library sync status and the per-game
/// runtime maps (sessions, measured sizes, update sizes, broken installs). Immutable; replaced atomically by <see cref="StateHub.Mutate"/>.
/// </summary>
public sealed record HubState(
    AuthState Auth,
    Connectivity Connectivity,
    string LegendaryVersion,
    bool Warm,
    /// <summary>Stores loaded and the cached library is up (before that, nothing is known).</summary>
    bool Loaded,
    string? MigratedFrom,
    EglScanResult? Egl,
    bool EglScanning,
    /// <summary>Installs found by the drive quick scan that EGL doesn't list.</summary>
    ImmutableList<EglFoundInstall> EglExtras,
    /// <summary>Drift per imported EGL-shared app, from the last scan.</summary>
    ImmutableDictionary<string, EglDrift> EglDrift,
    bool Syncing,
    DateTimeOffset? LastSyncedAt,
    GliderError? SyncError,
    DateTimeOffset? LastUpdateCheck,
    ImmutableDictionary<string, RunningGame> Sessions,
    ImmutableDictionary<string, SizeMeasure> Sizes,
    ImmutableDictionary<string, UpdateSize> UpdateSizes,
    ImmutableHashSet<string> Broken)
{
    public static HubState Initial { get; } = new(
        new AuthState(AuthStatus.Unknown, null), Connectivity.Unknown, "", false, false, null, null, false,
        ImmutableList<EglFoundInstall>.Empty, ImmutableDictionary<string, EglDrift>.Empty, false, null, null, null,
        ImmutableDictionary<string, RunningGame>.Empty, ImmutableDictionary<string, SizeMeasure>.Empty,
        ImmutableDictionary<string, UpdateSize>.Empty, ImmutableHashSet<string>.Empty);

    public RunState RunStateOf(string app) => Sessions.TryGetValue(app, out var s) ? s.State : RunState.Idle;

    public bool AnySessionActive => Sessions.Values.Any(s => s.State != RunState.Idle);
}

/// <summary>The parts of the snapshot other services own; wired by <see cref="GliderService"/>.</summary>
public sealed class SnapshotSources
{
    public Func<LibraryStatus> Library { get; set; } = () => new LibraryStatus(0, 0, false, null, null, 0);
    public Func<QueueState> Queue { get; set; } = () => new QueueState([], false, DownloadMode.Balanced, null);
    public Func<StorageState> Storage { get; set; } = () => new StorageState([], []);
    public Func<EglState> Egl { get; set; } = () => new EglState(false, false, false, [], [], true, null);
    public Func<OverlayState> Overlay { get; set; } = () => new OverlayState(false, false, null, null, false, null, false);
    public Func<IReadOnlyList<Collection>> Collections { get; set; } = () => [];
    public Func<OnboardingState> Onboarding { get; set; } = () => new OnboardingState(false, OnboardingStep.Welcome, []);
}

/// <summary>
/// Builds the small app-wide <see cref="AppSnapshot"/> and publishes it as a coalesced `state` event (trailing edge, at most
/// one per <see cref="GliderServiceOptions.StateCoalesceInterval"/>); forwards job progress. Per-game data never goes in here
/// (that's the library delta). Building a snapshot only reads cached values — it never spawns processes or touches the network.
/// </summary>
public sealed class StateHub : IDisposable
{
    private static readonly JobStatus[] ActiveStatuses = [JobStatus.Queued, JobStatus.Running, JobStatus.Paused, JobStatus.Waiting];

    private readonly AppPaths _paths;
    private readonly IHostPlatform _host;
    private readonly IJobQueue _jobs;
    private readonly ISettingsStore _settings;
    private readonly IEventSink _sink;
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
        IEventSink sink,
        TimeProvider time,
        GliderServiceOptions options)
    {
        _paths = paths;
        _host = host;
        _jobs = jobs;
        _settings = settings;
        _sink = sink;
        _options = options;
        _timer = time.CreateTimer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public SnapshotSources Sources { get; } = new();

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

    public IEventSink Sink => _sink;

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
            changed = !ReferenceEquals(next, _state) && !Equals(next, _state);
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

    /// <summary>Unfinished writer-lane jobs of one app.</summary>
    public IReadOnlyList<JobInfo> ActiveJobs(string appName) => _jobs.Snapshot().Where(j => IsActive(j) && j.AppName == appName).ToList();

    public AppSnapshot BuildSnapshot()
    {
        var s = State;
        var settings = _settings.Current;
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
                s.Warm,
                s.MigratedFrom),
            s.Auth,
            s.Connectivity,
            Safe(Sources.Library, new LibraryStatus(0, 0, s.Syncing, s.LastSyncedAt, s.SyncError, 0)),
            Safe(Sources.Queue, new QueueState([], false, settings.DownloadMode, null)),
            s.Sessions.Values.Where(r => r.State != RunState.Idle).OrderBy(r => r.Since ?? DateTimeOffset.MaxValue).ThenBy(r => r.AppName, StringComparer.Ordinal).ToList(),
            Safe(Sources.Storage, new StorageState([], [])),
            Safe(Sources.Egl, new EglState(false, false, false, [], [], settings.EglSync, null)),
            Safe(Sources.Overlay, new OverlayState(false, false, null, null, false, null, false)),
            Safe(Sources.Collections, []),
            settings,
            Safe(Sources.Onboarding, new OnboardingState(false, OnboardingStep.Welcome, [])));
    }

    /// <summary>A failing source must never stop the snapshot (it is the UI's only view of the app).</summary>
    private static T Safe<T>(Func<T> source, T fallback)
    {
        try
        {
            return source();
        }
        catch (Exception)
        {
            return fallback;
        }
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
