using System.Collections.Concurrent;
using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Tests.Services;

/// <summary>Global, ordered call log shared by all fakes of one rig.</summary>
public sealed class CallLog
{
    private readonly ConcurrentQueue<string> _calls = new();

    public void Add(string call) => _calls.Enqueue(call);

    public IReadOnlyList<string> All => _calls.ToArray();

    public bool Has(string call) => _calls.Contains(call);
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
    public WindowInfo GetWindowInfo() => new(false, Focused);
    public void Quit() => log.Add("host.quit");
    public bool IsWindows11 => true;
    public BackdropKind Backdrop => BackdropKind.Mica;
}

public sealed class FakeLegendary(CallLog log) : ILegendary
{
    public static LegendaryAccount Alice { get; } = new("Alice", "acc-1");

    public TaskCompletionSource? VersionGate { get; set; }
    public LegendaryAccount? Account { get; set; } = Alice;
    public Exception? RefreshError { get; set; }
    public Exception? InfoError { get; set; }
    public LegendaryGameInfo Info { get; set; } = MakeInfo("v2");
    public LegendaryInstalledGame? Installed { get; set; }

    /// <summary>What `import` registers (path filled from the argument).</summary>
    public string ImportVersion { get; set; } = "v2";
    public LegendaryOutcome ImportOutcome { get; set; } = new(true, null, null, null);
    public bool? LastSkipVersionCheck { get; private set; }
    public string? LastExtraArgs { get; private set; }

    public static LegendaryGameInfo MakeInfo(string version) => new(
        "Fortnite", "Fortnite", version, 50_000_000_000, 100_000_000_000, "Fortnite", "FortniteGame/Binaries/Win64/FortniteLauncher.exe",
        "cat", "fn", [new KeyImage("DieselGameBox", "https://cdn/x.jpg", 1920, 1080)], null);

    public static LegendaryInstalledGame MakeInstalled(string path, string version) =>
        new("Fortnite", path, version, 100_000_000_000, "FortniteGame/Binaries/Win64/FortniteLauncher.exe", false, null, [""]);

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
        log.Add("legendary.info");
        return InfoError is { } e ? Task.FromException<LegendaryGameInfo>(e) : Task.FromResult(Info);
    }

    public Task<LegendaryInstalledGame?> GetInstalledAsync(string appName, CancellationToken ct) => Task.FromResult(Installed);

    public Task<LegendaryOutcome> InstallAsync(InstallArgs args, IProgress<LegendaryProgress> progress, CancellationToken ct)
    {
        log.Add("legendary.install");
        return Task.FromResult(new LegendaryOutcome(true, null, null, null));
    }

    public Task<LegendaryOutcome> VerifyAsync(string appName, IProgress<LegendaryProgress> progress, CancellationToken ct)
    {
        log.Add("legendary.verify");
        return Task.FromResult(new LegendaryOutcome(true, null, null, null));
    }

    public Task<LegendaryOutcome> ImportAsync(string appName, string gamePath, CancellationToken ct)
    {
        log.Add("legendary.import");
        if (ImportOutcome.Success)
        {
            Installed = MakeInstalled(gamePath, ImportVersion);
        }

        return Task.FromResult(ImportOutcome);
    }

    public Task<LegendaryOutcome> MoveAsync(string appName, string newBasePath, bool skipMove, IProgress<LegendaryProgress>? progress, CancellationToken ct)
    {
        log.Add("legendary.move");
        return Task.FromResult(new LegendaryOutcome(true, null, null, null));
    }

    public Task<LegendaryOutcome> UninstallAsync(string appName, bool keepFiles, CancellationToken ct)
    {
        log.Add($"legendary.uninstall:{keepFiles}");
        return Task.FromResult(new LegendaryOutcome(true, null, null, null));
    }

    public Task<LaunchCommand> GetLaunchCommandAsync(string appName, bool skipVersionCheck, string extraArgs, CancellationToken ct)
    {
        log.Add("legendary.launch");
        LastSkipVersionCheck = skipVersionCheck;
        LastExtraArgs = extraArgs;
        var dir = Installed?.InstallPath ?? "game";
        return Task.FromResult(new LaunchCommand(Path.Combine(dir, "FortniteLauncher.exe"), dir, ["-AUTH_PASSWORD=secret"], new Dictionary<string, string>()));
    }

    public string? GetInstalledManifestPath(string appName, string version) => $"manifests/{version}.manifest";

    /// <summary>What Legendary's `update` dry run reports for the pending update (null = couldn't measure).</summary>
    public LegendarySizes? UpdateSizes { get; set; }

    public Task<LegendarySizes?> GetUpdateSizesAsync(string appName, CancellationToken ct)
    {
        log.Add("legendary.updateSizes");
        return Task.FromResult(UpdateSizes);
    }
}

public sealed class FakeJobQueue(CallLog log) : IJobQueue
{
    private readonly List<JobInfo> _jobs = [];
    private int _next;

    public List<JobRequest> Requests { get; } = [];

    public event Action? Changed;
    public event Action<JobProgress>? ProgressChanged;

    public IReadOnlyList<JobInfo> Snapshot()
    {
        lock (_jobs)
        {
            return _jobs.ToList();
        }
    }

    public bool IsBusy => Snapshot().Any(StateHub.IsActive);

    public JobInfo Enqueue(JobRequest request)
    {
        log.Add($"jobs.enqueue:{request.Kind}");
        var job = new JobInfo($"job-{++_next}", request.Kind, JobStatus.Queued, null, null, DateTimeOffset.UnixEpoch, null,
            request.TargetPath, request.FromVersion, request.ToVersion, null);
        lock (_jobs)
        {
            Requests.Add(request);
            _jobs.Add(job);
        }

        Changed?.Invoke();
        return job;
    }

    public void Set(JobInfo job)
    {
        lock (_jobs)
        {
            var i = _jobs.FindIndex(j => j.Id == job.Id);
            if (i >= 0)
            {
                _jobs[i] = job;
            }
            else
            {
                _jobs.Add(job);
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
            _jobs.RemoveAll(j => j.Id == jobId);
        }
    }

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
        return done;
    }
}

public sealed class FakeEgl(CallLog log) : IEglService
{
    public EglState State { get; set; } = new(true, false, false, [], true, false, null, null);
    public bool Busy { get; set; }
    public string BusyReason { get; set; } = "Epic Games Launcher is updating Fortnite";
    public TaskCompletionSource NotBusy { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public EglInstall? Item { get; set; }
    public DriftResult Drift { get; set; } = new(DriftKind.InSync, null, null, null);
    public List<EglWriteBack> WriteBacks { get; } = [];
    public List<(string Guid, string Path)> LocationUpdates { get; } = [];

    public Task<EglState> ScanAsync(CancellationToken ct)
    {
        log.Add("egl.scan");
        return Task.FromResult(State);
    }

    public bool IsLauncherRunning() => State.LauncherRunning;
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

    public EglInstall? FindByLocation(string installPath) =>
        Item is not null && string.Equals(Path.GetFullPath(Item.InstallLocation), Path.GetFullPath(installPath), StringComparison.OrdinalIgnoreCase) ? Item : null;

    public DriftResult CheckDrift(GameRecord record)
    {
        log.Add("egl.drift");
        return Drift;
    }

    public Task<string> WriteBackAsync(EglWriteBack data, CancellationToken ct)
    {
        log.Add("egl.writeBack");
        WriteBacks.Add(data);
        return Task.FromResult(data.ExistingInstallationGuid ?? "new-guid");
    }

    public Task UpdateLocationAsync(string installationGuid, string newInstallPath, CancellationToken ct)
    {
        log.Add("egl.updateLocation");
        LocationUpdates.Add((installationGuid, newInstallPath));
        return Task.CompletedTask;
    }
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

public sealed class FakeRecords : IGameRecordStore
{
    public GameRecord? Current { get; set; }

    public Task SaveAsync(GameRecord? record)
    {
        Current = record;
        return Task.CompletedTask;
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

public sealed class FakeProcesses : IProcessMonitor, IGameProcessStarter
{
    private readonly ConcurrentDictionary<string, RunningProcess> _running = new(StringComparer.OrdinalIgnoreCase);

    public List<LaunchCommand> Started { get; } = [];
    public Exception? StartError { get; set; }

    public void Add(string name, DateTimeOffset? start = null) => _running[name] = new RunningProcess(1000 + _running.Count, name, start);
    public void Remove(string name) => _running.TryRemove(name, out _);

    public IReadOnlyList<RunningProcess> Find(string processName) =>
        _running.TryGetValue(processName, out var p) ? [p] : [];

    public List<string> Closed { get; } = [];

    public void Close(RunningProcess process)
    {
        Closed.Add(process.Name);
        Remove(process.Name);
    }

    public RunningProcess Start(LaunchCommand command)
    {
        if (StartError is { } e)
        {
            throw e;
        }

        Started.Add(command);
        Add("FortniteLauncher");
        return Find("FortniteLauncher")[0];
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

public sealed class FakeArt : IArtCache
{
    public GameArt? Cached { get; set; }
    public GameArt? LoadCached() => Cached;
    public Task<GameArt?> RefreshAsync(IReadOnlyList<KeyImage> images, CancellationToken ct) =>
        Task.FromResult<GameArt?>(new GameArt("https://app.glider/cache/art/hero.jpg", null, null));
}

public sealed class FakeOnboarding : IOnboardingStore
{
    public bool Completed { get; private set; }

    public Task SetCompletedAsync(bool completed)
    {
        Completed = completed;
        return Task.CompletedTask;
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
