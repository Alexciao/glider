using Glider.Core.Auth;
using Glider.Core.Contracts;
using Glider.Core.Game;

namespace Glider.Core.Services;

/// <summary>
/// The facade the host's RPC dispatcher calls. Every method validates input, never blocks a thread,
/// turns long work into jobs, and fails only with <see cref="GliderException"/> (structured errors).
/// Host wiring: construct, set <see cref="DeferredJobHooks.Target"/> = <see cref="Hooks"/>, then <see cref="StartAsync"/>.
/// </summary>
public sealed class GliderService : IGliderRpc, IDisposable
{
    private const string Category = "rpc";
    private static readonly Task<int> Done = Task.FromResult(0);

    private readonly CancellationTokenSource _shutdown = new();
    private readonly ServiceContext _c;
    private readonly AuthService _auth;
    private readonly UpdateChecker _updates;
    private readonly InstallService _install;
    private readonly LaunchService _launch;
    private readonly Prewarm _prewarm;
    private readonly JobHooks _hooks;
    private Task _periodic = Task.CompletedTask;
    private int _started;

    public GliderService(
        AppPaths paths,
        IHostPlatform host,
        ILegendary legendary,
        IJobQueue jobs,
        IEglService egl,
        ISettingsStore settings,
        IGameRecordStore records,
        ILog log,
        TimeProvider time,
        IEventSink events,
        GliderServiceOptions? options = null)
    {
        options ??= new GliderServiceOptions();
        var onboarding = options.OnboardingStore ?? new FileOnboardingStore(paths);
        var hub = new StateHub(paths, host, jobs, settings, records, events, time, onboarding, options);
        _c = new ServiceContext
        {
            Paths = paths,
            Host = host,
            Legendary = legendary,
            Jobs = jobs,
            Egl = egl,
            SettingsStore = settings,
            Records = records,
            Log = log,
            Time = time,
            Hub = hub,
            Options = options,
            Art = options.ArtCache ?? new HttpArtCache(paths, SharedHttp.Client),
            Onboarding = onboarding,
            Shutdown = _shutdown.Token,
        };
        _auth = new AuthService(_c);
        _updates = new UpdateChecker(_c);
        _install = new InstallService(_c, _updates);
        _launch = new LaunchService(_c, new EacService(_c), _updates);
        _prewarm = new Prewarm(_c, _auth, _updates);
        _hooks = new JobHooks(_c);
    }

    /// <summary>Give this to the job queue (directly or through <see cref="DeferredJobHooks"/>).</summary>
    public IJobHooks Hooks => _hooks;

    public StateHub Hub => _c.Hub;

    /// <summary>Completes when background pre-warm has finished (AppInfo.Warm).</summary>
    public Task WarmupTask { get; private set; } = Task.CompletedTask;

    internal LaunchService LaunchServiceForTests => _launch;

    /// <summary>
    /// Publishes the first snapshot from cached state immediately, then pre-warms in the background and starts
    /// periodic update checks. Returns without waiting for any process or network call.
    /// </summary>
    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        var rec = _c.Records.Current;
        try
        {
            _c.Log.MinimumLevel = _c.Settings.LogLevel;
        }
        catch (Exception e)
        {
            _c.Log.Warn("startup", "applying log level failed", e);
        }

        GameArt? art = null;
        try
        {
            art = _c.Art.LoadCached();
        }
        catch (Exception e)
        {
            _c.Log.Warn("startup", "cached art unreadable", e);
        }

        _c.Hub.Attach();
        _c.Hub.Mutate(s => s with
        {
            Loaded = true,
            Art = art ?? s.Art,
            InstallBroken = rec is not null && !FortniteLayout.LooksLikeInstall(rec.InstallPath),
        });
        try
        {
            _launch.DetectExisting();
        }
        catch (Exception e)
        {
            _c.Log.Warn("startup", "running-game detection failed", e);
        }

        _c.Hub.PublishNow();

        var ct = _shutdown.Token;
        WarmupTask = Task.Run(() => _prewarm.RunAsync(ct), ct);
        _periodic = RunPeriodicAfterWarmupAsync(ct);
        return Task.CompletedTask;
    }

    private async Task RunPeriodicAfterWarmupAsync(CancellationToken ct)
    {
        try
        {
            await WarmupTask;
            await _updates.RunPeriodicAsync(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _c.Log.Error("startup", "background loop stopped", e);
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _c.Hub.Dispose();
        _shutdown.Dispose();
    }

    // ================================================================== RPC plumbing

    private async Task<T> Rpc<T>(string method, Func<CancellationToken, Task<T>> body)
    {
        try
        {
            return await body(_shutdown.Token);
        }
        catch (GliderException)
        {
            throw;
        }
        catch (OperationCanceledException e)
        {
            throw new GliderException(Errors.Cancelled(), e);
        }
        catch (Exception e) when (e is ArgumentException or KeyNotFoundException or FormatException)
        {
            throw new GliderException(Errors.InvalidRequest(e.Message), e);
        }
        catch (Exception e)
        {
            _c.Log.Error(Category, $"{method} failed", e);
            throw new GliderException(Errors.Unknown(e.Message), e);
        }
    }

    private Task<int> Rpc(string method, Func<CancellationToken, Task> body) => Rpc(method, async ct =>
    {
        await body(ct);
        return 0;
    });

    private Task<int> Rpc(string method, Action body) => Rpc<int>(method, _ =>
    {
        body();
        return Done;
    });

    private static T Require<T>(T? value, string name)
        where T : class => value ?? throw new GliderException(Errors.InvalidRequest($"{name} is required"));

    // ================================================================== app

    public Task<AppSnapshot> GetSnapshot() => Rpc(nameof(GetSnapshot), _ => Task.FromResult(_c.Hub.BuildSnapshot()));

    public Task OpenExternal(UrlRequest request) => Rpc(nameof(OpenExternal), () =>
    {
        var url = Require(Require(request, "request").Url, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new GliderException(Errors.InvalidRequest("Only http(s) links can be opened"));
        }

        _c.Host.OpenUrl(uri.AbsoluteUri);
    });

    /// <summary>Folders anywhere; files only inside Glider's data directory (opening an arbitrary file could run it).</summary>
    public Task OpenPath(PathRequest request) => Rpc(nameof(OpenPath), () =>
    {
        var path = Require(Require(request, "request").Path, "path");
        InstallService.RequireFullPath(path, "path");
        var full = Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            _c.Host.OpenPath(full);
            return;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_c.Paths.Root)) + Path.DirectorySeparatorChar;
        if (File.Exists(full) && full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            _c.Host.OpenPath(full);
            return;
        }

        throw new GliderException(Errors.InvalidRequest("Path not found or not allowed"));
    });

    public Task<PickFolderResult> PickFolder(PickFolderRequest request) => Rpc(nameof(PickFolder), async _ =>
    {
        var r = Require(request, "request");
        var initial = r.InitialPath is { Length: > 0 } p && Path.IsPathFullyQualified(p) ? p : null;
        return new PickFolderResult(await _c.Host.PickFolderAsync(initial, r.Title));
    });

    public Task<LogsResult> GetLogs(LogsRequest request) => Rpc(nameof(GetLogs), _ =>
    {
        var lines = Math.Clamp(Require(request, "request").MaxLines, 1, 10_000);
        return Task.FromResult(new LogsResult(LogText(lines), _c.Log.CurrentFile));
    });

    public Task CopyLogs() => Rpc(nameof(CopyLogs), () => _c.Host.SetClipboardText(LogText(2000)));

    private string LogText(int lines) =>
        Diagnostics.Header(_c.Hub.BuildSnapshot(), _c.Records.Current) + Diagnostics.Redact(_c.Log.Tail(lines));

    public Task<LicensesResult> GetLicenses() => Rpc(nameof(GetLicenses), _ => Task.FromResult(Licenses.Load(_c.Options.LicensesDirectory)));

    public Task CompleteOnboarding() => Rpc(nameof(CompleteOnboarding), async _ =>
    {
        await _c.Onboarding.SetCompletedAsync(true);
        _c.Hub.Invalidate();
    });

    public Task Ready() => Rpc(nameof(Ready), _c.Host.RevealWindow);

    public Task Quit() => Rpc(nameof(Quit), _c.Host.Quit);

    // ================================================================== window

    public Task Minimize() => Rpc(nameof(Minimize), _c.Host.MinimizeWindow);

    public Task ToggleMaximize() => Rpc(nameof(ToggleMaximize), _c.Host.ToggleMaximizeWindow);

    public Task Close() => Rpc(nameof(Close), () => _c.Host.CloseWindow(_c.Settings.CloseToTray));

    public Task<WindowInfo> GetWindowInfo() => Rpc(nameof(GetWindowInfo), _ => Task.FromResult(_c.Host.GetWindowInfo()));

    // ================================================================== auth

    public Task<AuthState> SignIn() => Rpc(nameof(SignIn), async ct =>
    {
        var state = await _auth.SignInAsync(ct);
        // Fresh session: fetch latest version/sizes/art in the background (the install screen wants them).
        _ = Task.Run(() => _updates.CheckAsync(userInitiated: false, ct), ct);
        return state;
    });

    public Task<AuthState> SignOut() => Rpc(nameof(SignOut), ct =>
    {
        _launch.InvalidatePrepared();
        return _auth.SignOutAsync(ct);
    });

    // ================================================================== game

    public Task<InstallPlan> PlanInstall(InstallPlanRequest request) =>
        Rpc(nameof(PlanInstall), ct => _install.PlanAsync(Require(request, "request"), ct));

    public Task<JobInfo> Install(InstallRequest request) =>
        Rpc(nameof(Install), ct => _install.InstallAsync(Require(request, "request"), ct));

    public Task<FolderValidation> ValidateFolder(ValidateFolderRequest request) =>
        Rpc(nameof(ValidateFolder), _ => Task.FromResult(_install.Validate(Require(request, "request"))));

    public Task<AdoptResult> Adopt(AdoptRequest request) =>
        Rpc(nameof(Adopt), ct => _install.AdoptAsync(Require(request, "request"), ct));

    public Task<UpdateCheckResult> CheckForUpdates() =>
        Rpc(nameof(CheckForUpdates), ct => _updates.CheckAsync(userInitiated: true, ct));

    public Task<JobInfo> Update() => Rpc(nameof(Update), _install.UpdateAsync);

    public Task<JobInfo> Verify() => Rpc(nameof(Verify), _ => Task.FromResult(_install.Verify()));

    public Task<JobInfo> Move(MoveRequest request) =>
        Rpc(nameof(Move), _ => Task.FromResult(_install.Move(Require(request, "request"))));

    public Task<JobInfo> Uninstall(UninstallRequest request) =>
        Rpc(nameof(Uninstall), _ => Task.FromResult(_install.Uninstall(Require(request, "request"))));

    public Task<LaunchResult> Launch() => Rpc(nameof(Launch), _launch.LaunchAsync);

    public Task PrepareLaunch() => Rpc(nameof(PrepareLaunch), () =>
    {
        try
        {
            _launch.Prepare();
        }
        catch (Exception e)
        {
            _c.Log.Warn("launch", "prepare failed", e); // a hint only: never an error for the UI
        }
    });

    public Task OpenInstallFolder() => Rpc(nameof(OpenInstallFolder), () =>
    {
        var rec = _c.Records.Current ?? throw new GliderException(Errors.NotInstalled());
        if (!Directory.Exists(rec.InstallPath))
        {
            throw new GliderException(Errors.InvalidFolder("The install folder no longer exists."));
        }

        _c.Host.OpenPath(rec.InstallPath);
    });

    // ================================================================== jobs

    private static string JobId(JobRef? r)
    {
        var id = Require(r, "request").JobId;
        return string.IsNullOrWhiteSpace(id) ? throw new GliderException(Errors.InvalidRequest("jobId is required")) : id;
    }

    private void RequireKnownJob(string id)
    {
        if (!_c.Jobs.Snapshot().Any(j => j.Id == id))
        {
            throw new GliderException(Errors.InvalidRequest($"Unknown job '{id}'"));
        }
    }

    public Task<JobInfo> PauseJob(JobRef request) => Rpc(nameof(PauseJob), _ =>
    {
        var id = JobId(request);
        RequireKnownJob(id);
        return _c.Jobs.PauseAsync(id);
    });

    public Task<JobInfo> ResumeJob(JobRef request) => Rpc(nameof(ResumeJob), _ =>
    {
        var id = JobId(request);
        RequireKnownJob(id);
        return _c.Jobs.ResumeAsync(id);
    });

    public Task<JobInfo> CancelJob(JobRef request) => Rpc(nameof(CancelJob), _ =>
    {
        var id = JobId(request);
        RequireKnownJob(id);
        return _c.Jobs.CancelAsync(id);
    });

    public Task DismissJob(JobRef request) => Rpc(nameof(DismissJob), () =>
    {
        var id = JobId(request);
        RequireKnownJob(id);
        _c.Jobs.Dismiss(id);
    });

    // ================================================================== egl

    public Task<EglState> RescanEgl() => Rpc(nameof(RescanEgl), async ct =>
    {
        var egl = await _c.Egl.ScanAsync(ct);
        _c.Hub.Mutate(s => s with { Egl = egl });
        return _c.Hub.BuildSnapshot().Egl;
    });

    // ================================================================== settings

    public Task<Settings> UpdateSettings(Settings settings) => Rpc(nameof(UpdateSettings), _ => ApplySettingsAsync(SettingsValidator.Validate(settings)));

    public Task<Settings> ResetSettings() => Rpc(nameof(ResetSettings), _ => ApplySettingsAsync(Settings.Default));

    private async Task<Settings> ApplySettingsAsync(Settings next)
    {
        var prev = _c.Settings;
        if (prev.StartWithWindows != next.StartWithWindows)
        {
            _c.Host.SetStartWithWindows(next.StartWithWindows);
        }

        _c.Log.MinimumLevel = next.LogLevel;
        var saved = await _c.SettingsStore.UpdateAsync(next);
        _c.Hub.Invalidate();
        if (!prev.AutoUpdate && saved.AutoUpdate)
        {
            _updates.TryAutoUpdate();
        }

        return saved;
    }
}

internal static class SettingsValidator
{
    public static Settings Validate(Settings? s)
    {
        if (s is null)
        {
            throw new GliderException(Errors.InvalidRequest("settings are required"));
        }

        if (s.DefaultInstallBase is { } b && string.IsNullOrWhiteSpace(b))
        {
            s = s with { DefaultInstallBase = null };
        }

        static void Check(bool ok, string what)
        {
            if (!ok)
            {
                throw new GliderException(Errors.InvalidRequest(what));
            }
        }

        Check(s.DownloadWorkers is null or >= 1 and <= 64, "downloadWorkers must be 1–64 or null");
        Check(s.SharedMemoryMb is null or >= 256 and <= 16384, "sharedMemoryMb must be 256–16384 or null");
        Check(s.UpdateCheckIntervalMinutes is >= 5 and <= 1440, "updateCheckIntervalMinutes must be 5–1440");
        Check(s.LaunchArgs is not null && s.LaunchArgs.Length <= 2048 && !s.LaunchArgs.Contains('\n') && !s.LaunchArgs.Contains('\r'), "launchArgs must be one line (≤2048 chars)");
        Check(s.PreferredCdn is null || (s.PreferredCdn.Length is > 0 and <= 253 && !s.PreferredCdn.Any(char.IsWhiteSpace)), "preferredCdn must be a host name");
        Check(s.DefaultInstallBase is null || Path.IsPathFullyQualified(s.DefaultInstallBase), "defaultInstallBase must be a full path");
        Check(Enum.IsDefined(s.LogLevel) && Enum.IsDefined(s.Theme), "unknown enum value");
        return s;
    }
}

internal static class SharedHttp
{
    public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
}
