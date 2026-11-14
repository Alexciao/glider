using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;
using Glider.Core.Services;

namespace Glider.Core.Tests.Services;

/// <summary>Global, ordered call log shared by all fakes of one rig.</summary>
public sealed class CallLog
{
    private readonly ConcurrentQueue<string> _calls = new();

    public void Add(string call) => _calls.Enqueue(call);

    public IReadOnlyList<string> All => _calls.ToArray();

    public bool Has(string call) => _calls.Contains(call);

    public int Count(string call) => _calls.Count(c => c == call);
}

/// <summary>Deterministic TimeProvider: timers fire only inside <see cref="Advance"/>.</summary>
public sealed class ManualTime : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(t);
        }

        t.Change(dueTime, period);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + by;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due is { } d && d <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value;
                next.Due = next.Period is { } p && p > TimeSpan.Zero ? _now + p : null;
            }

            next.Fire();
        }
    }

    internal void Remove(ManualTimer t)
    {
        lock (_gate)
        {
            _timers.Remove(t);
        }
    }

    internal sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? Due { get; set; }
        public TimeSpan? Period { get; private set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? null : period;
            }

            return true;
        }

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class FakeHost(CallLog log) : IHostPlatform
{
    public string? AuthCode { get; set; } = "exchange-code";
    public int? ElevatedResult { get; set; } = 0;
    public bool Focused { get; set; }
    public List<(string Exe, string Args)> Elevated { get; } = [];
    public List<(string Title, string Message)> Toasts { get; } = [];
    public List<bool> StartWithWindows { get; } = [];
    public int HideCount { get; private set; }
    public int RestoreCount { get; private set; }
    public string? Clipboard { get; private set; }
    public List<string> Opened { get; } = [];

    public Task<string?> AcquireEpicExchangeCodeAsync(CancellationToken ct)
    {
        log.Add("host.login");
        return Task.FromResult(AuthCode);
    }

    public Task ClearLoginSessionAsync()
    {
        log.Add("host.clearLogin");
        return Task.CompletedTask;
    }

    public Task<string?> PickFolderAsync(string? initialPath, string? title) => Task.FromResult<string?>(null);
    public void OpenUrl(string url) => Opened.Add(url);
    public void OpenPath(string path) => Opened.Add(path);
    public void SetClipboardText(string text) => Clipboard = text;
    public void ShowToast(string title, string message) => Toasts.Add((title, message));

    public Task<int?> RunElevatedAsync(string exe, string arguments, CancellationToken ct)
    {
        Elevated.Add((exe, arguments));
        return Task.FromResult(ElevatedResult);
    }

    public void SetStartWithWindows(bool enabled) => StartWithWindows.Add(enabled);
    public void MinimizeWindow() => log.Add("host.minimize");
    public void ToggleMaximizeWindow() => log.Add("host.maximize");
    public void CloseWindow(bool toTray) => log.Add($"host.close:{toTray}");
    public void HideToTray() => HideCount++;
    public void RestoreWindow() => RestoreCount++;
    public void RevealWindow() => log.Add("host.reveal");
    public WindowInfo GetWindowInfo() => new(false, Focused, Focused);
    public void Quit() => log.Add("host.quit");
    public bool IsWindows11 => true;
    public BackdropKind Backdrop => BackdropKind.Mica;
}

/// <summary>
/// In-memory ILegendary that keeps Legendary's installed.json on disk in step (the library reads that file directly), so
/// the service sees installs/imports/uninstalls exactly as with the real exe. Calls are logged as "legendary.&lt;verb&gt;:&lt;app&gt;".
/// </summary>
public sealed class FakeLegendary(CallLog log, string config) : ILegendary
{
    public static LegendaryAccount Alice { get; } = new("Alice", "acc-1");

    private readonly ConcurrentDictionary<string, LegendaryInstalledGame> _installed = new(StringComparer.Ordinal);

    public TaskCompletionSource? VersionGate { get; set; }
    public LegendaryAccount? Account { get; set; } = Alice;
    public Exception? RefreshError { get; set; }
    public Exception? InfoError { get; set; }
    public LegendaryGameInfo Info { get; set; } = MakeInfo("v2");

    /// <summary>Outcome of `list-installed --check-updates` / `list -T` (null = success).</summary>
    public GliderError? SyncError { get; set; }

    /// <summary>Runs when `list` / `list-installed --check-updates` "refresh" Legendary's files (tests edit assets.json here).</summary>
    public Action? OnSync { get; set; }

    /// <summary>What `import` registers (path filled from the argument).</summary>
    public string ImportVersion { get; set; } = "v2";
    public LegendaryOutcome ImportOutcome { get; set; } = new(true, null, null, null);
    public bool? LastSkipVersionCheck { get; private set; }
    public bool? LastOffline { get; private set; }
    public string? LastExtraArgs { get; private set; }
    public string? LastLaunchedApp { get; private set; }

    /// <summary>Completes the next InstallAsync only when set (to hold a download "running").</summary>
    public TaskCompletionSource? InstallGate { get; set; }

    public static LegendaryGameInfo MakeInfo(string version, string app = "Fortnite") => new(
        app, app, version, 50_000_000_000, 100_000_000_000, app == "Fortnite" ? "Fortnite" : null, "FortniteGame/Binaries/Win64/FortniteLauncher.exe",
        "cat", "fn", [new KeyImage("DieselGameBox", "https://cdn/x.jpg", 1920, 1080)], null);

    public static LegendaryInstalledGame MakeInstalled(string path, string version, string app = "Fortnite", string? executable = null) =>
        new(app, path, version, 100_000_000_000, executable ?? (app == "Fortnite" ? "FortniteGame/Binaries/Win64/FortniteLauncher.exe" : "Game.exe"),
            false, null, [""]);

    /// <summary>The Fortnite entry of installed.json.</summary>
    public LegendaryInstalledGame? Installed
    {
        get => Get("Fortnite");
        set => Set("Fortnite", value);
    }

    public LegendaryInstalledGame? Get(string app) => _installed.TryGetValue(app, out var g) ? g : null;

    public void Set(string app, LegendaryInstalledGame? game)
    {
        if (game is null)
        {
            _installed.TryRemove(app, out _);
        }
        else
        {
            _installed[app] = game with { AppName = app };
        }

        WriteInstalledJson();
    }

    public void WriteInstalledJson()
    {
        var o = new JsonObject();
        foreach (var (app, g) in _installed.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var tags = new JsonArray();
            foreach (var t in g.InstallTags)
            {
                tags.Add(t);
            }

            o[app] = new JsonObject
            {
                ["app_name"] = app,
                ["install_path"] = g.InstallPath,
                ["title"] = g.Title ?? app,
                ["version"] = g.Version,
                ["install_size"] = g.InstallSizeBytes,
                ["executable"] = g.Executable ?? "",
                ["is_dlc"] = g.IsDlc,
                ["can_run_offline"] = g.CanRunOffline,
                ["needs_verification"] = g.NeedsVerification,
                ["egl_guid"] = g.EglGuid ?? "",
                ["install_tags"] = tags,
                ["platform"] = "Windows",
            };
        }

        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "installed.json"), o.ToJsonString());
    }

    public async Task<string> GetVersionAsync(CancellationToken ct)
    {
        log.Add("legendary.version");
        if (VersionGate is { } g)
        {
            await g.Task;
        }

        return "0.21.1";
    }

    public Task<LegendaryAccount?> GetAccountOfflineAsync(CancellationToken ct)
    {
        log.Add("legendary.offline");
        return Task.FromResult(Account);
    }

    public Task<LegendaryAccount?> RefreshSessionAsync(CancellationToken ct)
    {
        log.Add("legendary.refresh");
        return RefreshError is { } e ? Task.FromException<LegendaryAccount?>(e) : Task.FromResult(Account);
    }

    public Task<LegendaryAccount> SignInWithExchangeTokenAsync(string exchangeToken, CancellationToken ct)
    {
        log.Add($"legendary.signInToken:{exchangeToken}");
        Account = Alice;
        return Task.FromResult(Alice);
    }

    public Task SignOutAsync(CancellationToken ct)
    {
        log.Add("legendary.signOut");
        Account = null;
        return Task.CompletedTask;
    }

    public Task<LegendaryGameInfo> GetGameInfoAsync(string appName, CancellationToken ct)
    {
        log.Add($"legendary.info:{appName}");
        return InfoError is { } e ? Task.FromException<LegendaryGameInfo>(e)
            : Task.FromResult(Info with { AppName = appName, FolderName = appName == Info.AppName ? Info.FolderName : null });
    }

    public Task<LegendaryInstalledGame?> GetInstalledAsync(string appName, CancellationToken ct) => Task.FromResult(Get(appName));

    public async Task<LegendaryOutcome> InstallAsync(InstallArgs args, IProgress<LegendaryProgress> progress, CancellationToken ct)
    {
        log.Add($"legendary.install:{args.AppName}");
        if (InstallGate is { } gate)
        {
            await gate.Task.WaitAsync(ct);
        }

        return new LegendaryOutcome(true, null, null, null);
    }

    public Task<LegendaryOutcome> VerifyAsync(string appName, IProgress<LegendaryProgress> progress, CancellationToken ct)
    {
        log.Add($"legendary.verify:{appName}");
        return Task.FromResult(new LegendaryOutcome(true, null, null, null));
    }

    public Task<LegendaryOutcome> ImportAsync(string appName, string gamePath, CancellationToken ct)
    {
        log.Add($"legendary.import:{appName}");
        if (ImportOutcome.Success)
        {
            Set(appName, MakeInstalled(gamePath, ImportVersion, appName));
        }

        return Task.FromResult(ImportOutcome);
    }

    public Task<LegendaryOutcome> MoveAsync(string appName, string newBasePath, bool skipMove, IProgress<LegendaryProgress>? progress, CancellationToken ct)
    {
        log.Add($"legendary.move:{appName}");
        if (Get(appName) is { } g)
        {
            Set(appName, g with { InstallPath = Path.Combine(newBasePath, Path.GetFileName(Path.TrimEndingDirectorySeparator(g.InstallPath))) });
        }

        return Task.FromResult(new LegendaryOutcome(true, null, null, null));
    }

    public Task<LegendaryOutcome> UninstallAsync(string appName, bool keepFiles, CancellationToken ct)
    {
        log.Add($"legendary.uninstall:{appName}:{keepFiles}");
        Set(appName, null);
        return Task.FromResult(new LegendaryOutcome(true, null, null, null));
    }

    public Task<LaunchCommand> GetLaunchCommandAsync(string appName, bool skipVersionCheck, string extraArgs, bool offline, CancellationToken ct)
    {
        log.Add($"legendary.launch:{appName}");
        LastSkipVersionCheck = skipVersionCheck;
        LastOffline = offline;
        LastExtraArgs = extraArgs;
        LastLaunchedApp = appName;
        var dir = Get(appName)?.InstallPath ?? "game";
        return Task.FromResult(new LaunchCommand(Path.Combine(dir, appName == "Fortnite" ? "FortniteLauncher.exe" : "Game.exe"), dir, ["-AUTH_PASSWORD=secret"], new Dictionary<string, string>()));
    }

    public string? GetInstalledManifestPath(string appName, string version) => $"manifests/{version}.manifest";

    /// <summary>What Legendary's `update` dry run reports for the pending update (null = couldn't measure).</summary>
    public LegendarySizes? UpdateSizes { get; set; }

    public Task<LegendarySizes?> GetUpdateSizesAsync(string appName, CancellationToken ct)
    {
        log.Add($"legendary.updateSizes:{appName}");
        return Task.FromResult(UpdateSizes);
    }

    public Task<LegendaryOutcome> RefreshLibraryAsync(CancellationToken ct)
    {
        log.Add("legendary.list");
        if (SyncError is null)
        {
            OnSync?.Invoke();
        }

        return Task.FromResult(new LegendaryOutcome(SyncError is null, SyncError, null, null));
    }

    public Task<LegendaryOutcome> CheckUpdatesAsync(CancellationToken ct)
    {
        log.Add("legendary.checkUpdates");
        if (SyncError is null)
        {
            OnSync?.Invoke();
        }

        return Task.FromResult(new LegendaryOutcome(SyncError is null, SyncError, null, null));
    }
}

public sealed class FakeJobQueue(CallLog log) : IJobQueue
{
    private readonly List<(JobInfo Info, JobRequest Request)> _jobs = [];
    private int _next;

    public List<JobRequest> Requests { get; } = [];

    public event Action? Changed;
    public event Action<JobProgress>? ProgressChanged;

    public int ReevaluateCount { get; private set; }

    public IReadOnlyList<JobInfo> Snapshot()
    {
        lock (_jobs)
        {
            return _jobs.Select(j => j.Info).ToList();
        }
    }

    public bool IsBusy => Snapshot().Any(j => j.Status == JobStatus.Running);

    public JobInfo Enqueue(JobRequest request)
    {
        log.Add($"jobs.enqueue:{request.Kind}:{request.AppName}");
        var job = new JobInfo($"job-{++_next}", request.AppName, request.Title ?? request.AppName, request.Kind, JobStatus.Queued, [], null, null, null,
            DateTimeOffset.UnixEpoch, null, null, request.TargetPath, request.FromVersion, request.ToVersion, null, null, null);
        lock (_jobs)
        {
            Requests.Add(request);
            _jobs.Add((job, request));
        }

        Changed?.Invoke();
        return job;
    }

    public JobRequest? Request(string jobId)
    {
        lock (_jobs)
        {
            return _jobs.FirstOrDefault(j => j.Info.Id == jobId).Request;
        }
    }

    public void Set(JobInfo job)
    {
        lock (_jobs)
        {
            var i = _jobs.FindIndex(j => j.Info.Id == job.Id);
            if (i >= 0)
            {
                _jobs[i] = (job, _jobs[i].Request);
            }
            else
            {
                _jobs.Add((job, new JobRequest(job.AppName, job.Kind, job.TargetPath, null, job.FromVersion, job.ToVersion)));
            }
        }

        Changed?.Invoke();
    }

    public JobInfo Get(string id) => Snapshot().First(j => j.Id == id);

    public Task<JobInfo> PauseAsync(string jobId) => Task.FromResult(Update(jobId, JobStatus.Paused));
    public Task<JobInfo> ResumeAsync(string jobId) => Task.FromResult(Update(jobId, JobStatus.Queued));
    public Task<JobInfo> CancelAsync(string jobId) => Task.FromResult(Update(jobId, JobStatus.Cancelled));

    public void Dismiss(string jobId)
    {
        lock (_jobs)
        {
            _jobs.RemoveAll(j => j.Info.Id == jobId);
        }
    }

    public void Reevaluate() => ReevaluateCount++;

    private JobInfo Update(string id, JobStatus s)
    {
        var j = Get(id) with { Status = s };
        Set(j);
        return j;
    }

    public Task RestoreAsync(CancellationToken ct)
    {
        log.Add("jobs.restore");
        return Task.CompletedTask;
    }

    public void RaiseProgress(JobProgress p) => ProgressChanged?.Invoke(p);

    /// <summary>Minimal engine: gate → Waiting/Running → Completed (+hooks) or Failed.</summary>
    public async Task<JobInfo> RunAsync(string jobId, IJobHooks hooks, CancellationToken ct = default)
    {
        try
        {
            await hooks.BeforeRunAsync(Get(jobId), reason =>
                Set(Get(jobId) with { Status = reason is null ? JobStatus.Running : JobStatus.Waiting, BlockedReason = reason }), ct);
        }
        catch (GliderException e)
        {
            var failed = Get(jobId) with { Status = JobStatus.Failed, Error = e.Error };
            Set(failed);
            await hooks.OnFailedAsync(failed, ct);
            return failed;
        }

        Set(Get(jobId) with { Status = JobStatus.Running, BlockedReason = null });
        var done = Get(jobId) with { Status = JobStatus.Completed };
        Set(done);
        await hooks.OnSucceededAsync(done, ct);
        return Get(jobId);
    }
}

public sealed class FakeEgl(CallLog log) : IEglService
{
    public EglScanResult Scan { get; set; } = new(true, false, [], null);
    public bool Busy { get; set; }
    public string BusyReason { get; set; } = "Epic Games Launcher is updating this game";
    public TaskCompletionSource NotBusy { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public EglItem? Item { get; set; }
    public DriftResult Drift { get; set; } = new(DriftKind.InSync, null, null, null);
    public List<EglWriteBack> WriteBacks { get; } = [];
    public List<(string App, string Guid, string Path)> LocationUpdates { get; } = [];
    public List<EglFoundInstall> QuickScanResult { get; } = [];

    /// <summary>AppName per folder declared by .egstore (DetectAppName).</summary>
    public Dictionary<string, string> Declared { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<EglScanResult> ScanAsync(CancellationToken ct)
    {
        log.Add("egl.scan");
        return Task.FromResult(Scan);
    }

    public IReadOnlyList<EglFoundInstall> QuickScan(IReadOnlyList<string> driveRoots, CancellationToken ct) => QuickScanResult;

    public bool IsLauncherRunning() => Scan.LauncherRunning;
    public bool IsBusyWith(string installPath) => Busy;

    public async Task WaitUntilNotBusyAsync(string installPath, Action<string?> onBlocked, CancellationToken ct)
    {
        if (!Busy)
        {
            return;
        }

        onBlocked(BusyReason);
        await NotBusy.Task.WaitAsync(ct);
        Busy = false;
    }

    public EglItem? FindByLocation(string installPath, string? appName = null) =>
        Item is not null && string.Equals(Path.GetFullPath(Item.InstallLocation), Path.GetFullPath(installPath), StringComparison.OrdinalIgnoreCase)
            && (appName is null || Item.AppName.Length == 0 || Item.AppName == appName) ? Item : null;

    public DriftResult CheckDrift(InstallRecord record)
    {
        log.Add($"egl.drift:{record.AppName}");
        return Drift;
    }

    public Task<string> WriteBackAsync(EglWriteBack data, CancellationToken ct)
    {
        log.Add($"egl.writeBack:{data.AppName}");
        WriteBacks.Add(data);
        return Task.FromResult(data.ExistingInstallationGuid ?? "new-guid");
    }

    public Task UpdateLocationAsync(string appName, string installationGuid, string newInstallPath, CancellationToken ct)
    {
        log.Add($"egl.updateLocation:{appName}");
        LocationUpdates.Add((appName, installationGuid, newInstallPath));
        return Task.CompletedTask;
    }

    public string? DetectAppName(string folder) => Declared.TryGetValue(Path.TrimEndingDirectorySeparator(folder), out var app) ? app : null;
}

public sealed class FakeSettings : ISettingsStore
{
    public Settings Current { get; private set; } = Settings.Default;
    public event Action<Settings>? Changed;

    public Task<Settings> UpdateAsync(Settings settings)
    {
        Current = settings;
        Changed?.Invoke(settings);
        return Task.FromResult(settings);
    }

    public void Set(Settings s) => Current = s;
}

/// <summary>In-memory games.json.</summary>
public sealed class FakeGames : IGamesStore
{
    private ImmutableDictionary<string, GameEntry> _current = ImmutableDictionary<string, GameEntry>.Empty;

    public ImmutableDictionary<string, GameEntry> Current => Volatile.Read(ref _current);

    public event Action? Changed;

    public GameEntry Get(string appName) => Current.TryGetValue(appName, out var e) ? e : GameEntry.Empty;

    public InstallRecord? Record(string app = "Fortnite") => Get(app).Install;

    public void Put(string app, GameEntry entry)
    {
        ImmutableInterlocked.Update(ref _current, m => m.SetItem(app, entry));
        Changed?.Invoke();
    }

    public Task<GameEntry> UpdateAsync(string appName, Func<GameEntry, GameEntry> change)
    {
        GameEntry next = GameEntry.Empty;
        ImmutableInterlocked.Update(ref _current, m =>
        {
            next = change(m.TryGetValue(appName, out var e) ? e : GameEntry.Empty);
            return next.IsEmpty ? m.Remove(appName) : m.SetItem(appName, next);
        });
        Changed?.Invoke();
        return Task.FromResult(next);
    }
}

public sealed class FakeLog : ILog
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public LogLevel MinimumLevel { get; set; } = LogLevel.Info;
    public string CurrentFile => "glider.log";

    public void Write(LogLevel level, string category, string message, Exception? exception = null) =>
        Lines.Enqueue($"{level} [{category}] {message} {exception?.Message}");

    public string Tail(int maxLines) => string.Join('\n', Lines.TakeLast(maxLines));
}

public sealed class FakeSink : IEventSink
{
    public ConcurrentQueue<(string Event, object? Data)> Events { get; } = new();

    public void Publish<T>(string eventName, T data) => Events.Enqueue((eventName, data));

    public IReadOnlyList<T> Of<T>(string name) => Events.Where(e => e.Event == name).Select(e => (T)e.Data!).ToList();
}

/// <summary>
/// A fake process table with parent ids. <see cref="Start"/> spawns the exe Legendary resolved as a new process; <see cref="OnStart"/>
/// lets a test play the launcher → bootstrapper → game chain.
/// </summary>
public sealed class FakeProcesses : IProcessMonitor, IGameProcessStarter
{
    private readonly ConcurrentDictionary<int, ProcessEntry> _table = new();
    private int _nextPid = 1000;

    public List<LaunchCommand> Started { get; } = [];
    public Exception? StartError { get; set; }
    public List<string> Closed { get; } = [];

    /// <summary>Called with the spawned process after <see cref="Start"/>.</summary>
    public Action<FakeProcesses, ProcessEntry>? OnStart { get; set; }

    /// <summary>Clock used for process start times (the session ignores processes older than the launch).</summary>
    public Func<DateTimeOffset>? Clock { get; set; }

    public ProcessEntry Spawn(string name, int parentId = 4, DateTimeOffset? start = null)
    {
        var pid = Interlocked.Increment(ref _nextPid);
        var e = new ProcessEntry(pid, parentId, name, start ?? Clock?.Invoke());
        _table[pid] = e;
        return e;
    }

    /// <summary>v0.1-style helper: one process with this name (start time = now).</summary>
    public void Add(string name, DateTimeOffset? start = null) => Spawn(name, 4, start);

    public void Remove(string name)
    {
        foreach (var e in _table.Values.Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            _table.TryRemove(e.Id, out _);
        }
    }

    public void Exit(int pid) => _table.TryRemove(pid, out _);

    public IReadOnlyList<RunningProcess> Find(string processName) =>
        [.. _table.Values.Where(e => string.Equals(e.Name, processName, StringComparison.OrdinalIgnoreCase)).Select(e => new RunningProcess(e.Id, e.Name, e.StartTime))];

    public IReadOnlyList<ProcessEntry> Snapshot() => [.. _table.Values];

    public DateTimeOffset? StartTimeOf(int pid) => _table.TryGetValue(pid, out var e) ? e.StartTime : null;

    public void Close(RunningProcess process)
    {
        Closed.Add(process.Name);
        _table.TryRemove(process.Id, out _);
    }

    public RunningProcess Start(LaunchCommand command)
    {
        if (StartError is { } e)
        {
            throw e;
        }

        Started.Add(command);
        var p = Spawn(Path.GetFileNameWithoutExtension(command.Executable));
        OnStart?.Invoke(this, p);
        return new RunningProcess(p.Id, p.Name, p.StartTime);
    }
}

public sealed class FakeServices : IServiceQuery
{
    public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsInstalled(string serviceName) => Installed.Contains(serviceName);
}

public sealed class FakeDisk : IDiskInfo
{
    public List<FixedDrive> Drives { get; } = [];
    public long Free { get; set; } = 500_000_000_000;
    public IReadOnlyList<FixedDrive> FixedDrives() => Drives;
    public long? FreeBytes(string path) => Free;
}

public sealed class FakeOnboarding : IOnboardingStore
{
    public OnboardingState Current { get; private set; } = new(false, OnboardingStep.Welcome, []);

    public Task<OnboardingState> SetAsync(OnboardingState state)
    {
        Current = state;
        return Task.FromResult(state);
    }
}

public sealed class FakeIdle : IIdleSignal
{
    public TimeSpan IdleTime { get; set; }
}

public sealed class FakePublisherCheck : IPublisherCheck
{
    /// <summary>Null = trusted.</summary>
    public string? Reason { get; set; }

    public List<string> Checked { get; } = [];

    public string? WhyUntrusted(string path)
    {
        Checked.Add(path);
        return Reason;
    }
}
