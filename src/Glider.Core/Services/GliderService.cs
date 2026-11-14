using Glider.Core.Auth;
using Glider.Core.Contracts;
using Glider.Core.Game;
using Glider.Core.Infrastructure;
using Glider.Core.Jobs;
using Glider.Core.Library;

namespace Glider.Core.Services;

/// <summary>
/// The facade the host's RPC dispatcher calls (contract v2). Every method validates input, never blocks a thread, turns
/// long work into writer-lane jobs, and fails only with <see cref="GliderException"/> (structured errors).
/// Host wiring: construct, set <see cref="DeferredJobHooks.Target"/> = <see cref="Hooks"/>, then <see cref="StartAsync"/>.
/// </summary>
public sealed class GliderService : IGliderRpc, IDisposable
{
    private const string Category = "rpc";
    private static readonly Task<int> Done = Task.FromResult(0);

    private readonly CancellationTokenSource _shutdown = new();
    private readonly ServiceContext _c;
    private readonly AuthService _auth;
    private readonly UpdateService _updates;
    private readonly InstallService _install;
    private readonly LaunchService _launch;
    private readonly EglCoordinator _egl;
    private readonly StorageService _storage;
    private readonly QueuePolicy _policy;
    private readonly Prewarm _prewarm;
    private readonly JobHooks _hooks;
    private readonly CollectionsStore _collections;
    private readonly Saves.SaveService _saves;
    private readonly Overlay.OverlayService _overlay = new();
    private readonly Store.StoreService _store = new();
    private readonly Store.FreeGamesService _freeGames = new();
    private readonly ShortcutService _shortcuts = new();
    private readonly ActivationService _activation = new();
    private readonly PrerequisiteService _prereqs = new();
    private Task _periodic = Task.CompletedTask;
    private int _started;

    public GliderService(
        AppPaths paths,
        IHostPlatform host,
        ILegendary legendary,
        IJobQueue jobs,
        IEglService egl,
        ISettingsStore settings,
        IGamesStore games,
        ILog log,
        TimeProvider time,
        IEventSink events,
        GliderServiceOptions? options = null)
    {
        options ??= new GliderServiceOptions();
        var hub = new StateHub(paths, host, jobs, settings, events, time, options);
        var writer = options.WriterActivity ?? (jobs as JobQueue)?.Activity ?? new WriterActivity();
        _c = new ServiceContext
        {
            Paths = paths,
            Host = host,
            Legendary = legendary,
            Jobs = jobs,
            Egl = egl,
            SettingsStore = settings,
            Games = games,
            Log = log,
            Time = time,
            Hub = hub,
            Options = options,
            Onboarding = options.OnboardingStore ?? FileOnboardingStore.Empty(paths, log),
            Metadata = new MetadataLane(writer),
            Shutdown = _shutdown.Token,
        };
        _collections = options.Collections ?? CollectionsStore.Empty(paths, log);
        _c.Library = new LibraryService(_c);
        _c.Sizes = new InstalledSizeMeter(_c);
        _policy = new QueuePolicy(_c);
        _auth = new AuthService(_c);
        _updates = new UpdateService(_c);
        _install = new InstallService(_c);
        _launch = new LaunchService(_c, new EacService(_c), OnSessionEnded);
        _egl = new EglCoordinator(_c);
        _storage = new StorageService(_c);
        _saves = new Saves.SaveService(_c);
        _saves.Launcher = (r, ct) => _launch.LaunchAsync(r, ct);
        _launch.Saves = _saves;
        _c.WhenSavesIdle = _saves.WhenIdleAsync;
        _prewarm = new Prewarm(_c, _auth, _egl, _updates, _install, _launch);
        _hooks = new JobHooks(_c) { EnqueueVerify = _install.EnqueueVerify };
        if (jobs is JobQueue q)
        {
            _hooks.SetOutcome = q.SetOutcome;
        }

        hub.Sources.Library = _c.Library.Status;
        hub.Sources.Queue = () => _policy.State(_c.Jobs);
        hub.Sources.Storage = _storage.State;
        hub.Sources.Egl = _egl.Build;
        hub.Sources.Overlay = _overlay.State;
        hub.Sources.Collections = () => _collections.Current;
        hub.Sources.Onboarding = () => _c.Onboarding.Current;
    }

    /// <summary>Give this to the job queue (directly or through <see cref="DeferredJobHooks"/>).</summary>
    public IJobHooks Hooks => _hooks;

    /// <summary>Give this to the job queue (<see cref="JobQueueOptions.Policy"/>): holds while a game runs, effective mode.</summary>
    public IQueuePolicy QueuePolicy => _policy;

    public StateHub Hub => _c.Hub;

    /// <summary>The current library (read-only; for the host's tray menu).</summary>
    public IReadOnlyCollection<GameSummary> Library => _c.Library.Games.Values.ToList();

    /// <summary>Completes when background pre-warm has finished (AppInfo.Warm).</summary>
    public Task WarmupTask { get; private set; } = Task.CompletedTask;

    internal LaunchService LaunchServiceForTests => _launch;

    internal LibraryService LibraryForTests => _c.Library;

    internal InstalledSizeMeter SizesForTests => _c.Sizes;

    internal Prewarm PrewarmForTests => _prewarm;

    internal UpdateService UpdatesForTests => _updates;

    internal Saves.SaveService SavesForTests => _saves;

    /// <summary>
    /// Publishes the first snapshot and the cached library immediately (no Legendary process, no parse of its files), then
    /// pre-warms in the background and starts periodic checks. Returns without waiting for any process or network call.
    /// </summary>
    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return Task.CompletedTask;
        }

        try
        {
            _c.Log.MinimumLevel = _c.Settings.LogLevel;
        }
        catch (Exception e)
        {
            _c.Log.Warn("startup", "applying log level failed", e);
        }

        try
        {
            _c.Library.LoadCache();
            _c.Sizes.LoadCached();
        }
        catch (Exception e)
        {
            _c.Log.Warn("startup", "library cache unreadable", e);
        }

        _c.Hub.Attach();
        _c.Hub.StateChanged += _ => _c.Library.Invalidate();
        _c.Jobs.Changed += () => _c.Library.Invalidate();
        _c.Games.Changed += () => _c.Library.Invalidate();
        _c.SettingsStore.Changed += _ => _c.Library.Invalidate();
        _collections.Changed += _c.Hub.Invalidate;
        _c.Library.UpdatesChanged += _updates.Evaluate;
        _c.Hub.Mutate(s => s with { Loaded = true });
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
            await _updates.RunPeriodicAsync(t => _egl.RescanAsync(t), ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            _c.Log.Error("startup", "background loop stopped", e);
        }
    }

    private void OnSessionEnded(string app)
    {
        _updates.TryAutoUpdate(app);
    }

    /// <summary>Marks this run as having migrated older data (AppInfo.MigratedFrom).</summary>
    public void SetMigratedFrom(string? version) => _c.Hub.Mutate(s => s with { MigratedFrom = version });

    public void Dispose()
    {
        _shutdown.Cancel();
        try
        {
            _c.Library.SaveCacheAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception e) when (e is AggregateException or ObjectDisposedException)
        {
        }

        _c.Library.Dispose();
        _c.Sizes.Dispose();
        _c.Hub.Dispose();
        _c.Metadata.Dispose();
        _saves.Dispose();
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
            throw new GliderException(Errors.Unknown(SecretRedactor.Redact(e.Message)), e);
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

    private static string App(AppRef? r) => AppName(Require(r, "request").AppName);

    private static string AppName(string? app) =>
        string.IsNullOrWhiteSpace(app) ? throw new GliderException(Errors.InvalidRequest("appName is required")) : app;

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
        Diagnostics.Header(_c.Hub.BuildSnapshot(), _c.Library.Games, _c.Games.Current)
        + Diagnostics.Redact(_c.Log.Tail(lines) + Diagnostics.HostLogTail(_c.Paths.Logs, Math.Min(lines, 300)));

    public Task<LicensesResult> GetLicenses() => Rpc(nameof(GetLicenses), _ => Task.FromResult(Licenses.Load(_c.Options.LicensesDirectory)));

    public Task Ready() => Rpc(nameof(Ready), _c.Host.RevealWindow);

    public Task Quit() => Rpc(nameof(Quit), _c.Host.Quit);

    // ================================================================== onboarding

    public Task<OnboardingState> SetOnboardingStep(OnboardingStepRequest request) => Rpc(nameof(SetOnboardingStep), async _ =>
    {
        var r = Require(request, "request");
        if (!Enum.IsDefined(r.Step))
        {
            throw new GliderException(Errors.InvalidRequest("Unknown onboarding step"));
        }

        var current = _c.Onboarding.Current;
        var skipped = current.Skipped.ToList();
        if (r.Skipped && current.Step != r.Step && !skipped.Contains(current.Step))
        {
            skipped.Add(current.Step);
        }

        var next = await _c.Onboarding.SetAsync(new OnboardingState(r.Step == OnboardingStep.Done || current.Completed, r.Step, skipped));
        _c.Hub.Invalidate();
        return next;
    });

    public Task<OnboardingState> CompleteOnboarding() => Rpc(nameof(CompleteOnboarding), async _ =>
    {
        var next = await _c.Onboarding.SetAsync(new OnboardingState(true, OnboardingStep.Done, _c.Onboarding.Current.Skipped));
        _c.Hub.Invalidate();
        return next;
    });

    public Task<OnboardingState> RestartOnboarding() => Rpc(nameof(RestartOnboarding), async _ =>
    {
        var next = await _c.Onboarding.SetAsync(new OnboardingState(false, OnboardingStep.Welcome, []));
        _c.Hub.Invalidate();
        return next;
    });

    // ================================================================== window

    public Task Minimize() => Rpc(nameof(Minimize), _c.Host.MinimizeWindow);

    public Task ToggleMaximize() => Rpc(nameof(ToggleMaximize), _c.Host.ToggleMaximizeWindow);

    public Task Close() => Rpc(nameof(Close), () => _c.Host.CloseWindow(_c.Settings.CloseToTray));

    public Task<WindowInfo> GetWindowInfo() => Rpc(nameof(GetWindowInfo), _ => Task.FromResult(_c.Host.GetWindowInfo()));

    // ================================================================== auth

    public Task<AuthState> SignIn() => Rpc(nameof(SignIn), async ct =>
    {
        var state = await _auth.SignInAsync(ct);
        // Fresh session: sync the library in the background (the library and install screens want it).
        _ = _c.Library.RefreshInBackground(userInitiated: false);
        return state;
    });

    public Task<AuthState> SignOut() => Rpc(nameof(SignOut), ct =>
    {
        _launch.InvalidatePrepared();
        return _auth.SignOutAsync(ct);
    });

    // ================================================================== library

    public Task<LibraryDelta> GetLibrary() => Rpc(nameof(GetLibrary), _ => _c.Library.GetAsync());

    public Task RefreshLibrary() => Rpc(nameof(RefreshLibrary), () => { _ = _c.Library.RefreshInBackground(userInitiated: true); });

    public Task CheckForUpdates() => Rpc(nameof(CheckForUpdates), () => { _ = _c.Library.CheckUpdatesInBackground(userInitiated: true); });

    public Task<UpdateAllResult> UpdateAll() => Rpc(nameof(UpdateAll), _updates.UpdateAllAsync);

    public Task SetFavorite(GameFlagRequest request) => Rpc(nameof(SetFavorite), async _ =>
    {
        var r = Require(request, "request");
        await _c.Games.UpdateAsync(AppName(r.AppName), e => e with { Favorite = r.Value });
    });

    public Task SetHidden(GameFlagRequest request) => Rpc(nameof(SetHidden), async _ =>
    {
        var r = Require(request, "request");
        await _c.Games.UpdateAsync(AppName(r.AppName), e => e with { Hidden = r.Value });
    });

    // ================================================================== collections

    public Task<Collection> CreateCollection(CollectionCreateRequest request) =>
        Rpc(nameof(CreateCollection), _ => _collections.CreateAsync(Require(request, "request").Name));

    public Task<Collection> RenameCollection(CollectionRenameRequest request) => Rpc(nameof(RenameCollection), _ =>
    {
        var r = Require(request, "request");
        return _collections.RenameAsync(Require(r.Id, "id"), r.Name);
    });

    public Task DeleteCollection(CollectionRef request) =>
        Rpc(nameof(DeleteCollection), _ => _collections.DeleteAsync(Require(Require(request, "request").Id, "id")));

    public Task<Collection> SetCollectionMembership(CollectionMembershipRequest request) => Rpc(nameof(SetCollectionMembership), _ =>
    {
        var r = Require(request, "request");
        return _collections.SetMembershipAsync(Require(r.Id, "id"), AppName(r.AppName), r.Member);
    });

    // ================================================================== game

    public Task<GameDetail> GetGameDetail(AppRef request) => Rpc(nameof(GetGameDetail), async _ =>
    {
        var app = App(request);
        await _c.Library.FlushAsync();
        return _c.Library.Detail(app, _c.Library.Summary(app) is null ? null : _saves.Get(app));
    });

    public Task<GameSettings> UpdateGameSettings(GameSettingsRequest request) => Rpc(nameof(UpdateGameSettings), async _ =>
    {
        var r = Require(request, "request");
        var app = AppName(r.AppName);
        var settings = GameSettingsValidator.Validate(r.Settings);
        var entry = await _c.Games.UpdateAsync(app, e => e with { Settings = settings });
        _launch.InvalidatePrepared();
        if (entry.Settings.AutoUpdate == AutoUpdatePolicy.Always)
        {
            _updates.TryAutoUpdate(app);
        }

        return entry.Settings;
    });

    public Task<InstallPlan> PlanInstall(InstallPlanRequest request) =>
        Rpc(nameof(PlanInstall), ct => _install.PlanAsync(Require(request, "request"), ct));

    public Task<JobInfo> Install(InstallRequest request) =>
        Rpc(nameof(Install), ct => _install.InstallAsync(Require(request, "request"), ct));

    public Task<JobInfo> Modify(ModifyRequest request) =>
        Rpc(nameof(Modify), ct => _install.ModifyAsync(Require(request, "request"), ct));

    public Task<JobInfo> Update(AppRef request) => Rpc(nameof(Update), ct => _install.UpdateAsync(App(request), ct));

    public Task<JobInfo> Verify(AppRef request) => Rpc(nameof(Verify), _ => Task.FromResult(_install.Verify(App(request))));

    public Task<JobInfo> Move(MoveRequest request) =>
        Rpc(nameof(Move), _ => Task.FromResult(_install.Move(Require(request, "request"))));

    public Task<JobInfo> Uninstall(UninstallRequest request) =>
        Rpc(nameof(Uninstall), _ => Task.FromResult(_install.Uninstall(Require(request, "request"))));

    public Task<LaunchResult> Launch(LaunchRequest request) => Rpc(nameof(Launch), ct =>
    {
        var r = Require(request, "request");
        AppName(r.AppName);
        if (r.Mode is { } m && !Enum.IsDefined(m))
        {
            throw new GliderException(Errors.InvalidRequest("Unknown launch mode"));
        }

        return _launch.LaunchAsync(r, ct);
    });

    public Task PrepareLaunch(AppRef request) => Rpc(nameof(PrepareLaunch), () =>
    {
        try
        {
            _launch.Prepare(App(request));
        }
        catch (Exception e)
        {
            _c.Log.Warn("launch", "prepare failed", e); // a hint only: never an error for the UI
        }
    });

    public Task CloseGame(AppRef request) => Rpc(nameof(CloseGame), _ => _launch.CloseGameAsync(App(request)));

    public Task OpenInstallFolder(AppRef request) => Rpc(nameof(OpenInstallFolder), () =>
    {
        var app = App(request);
        var path = _c.Library.Summary(app)?.InstallPath ?? _c.Games.Get(app).Install?.InstallPath
                   ?? throw new GliderException(Errors.NotInstalled(_c.TitleOf(app), app));
        if (!Directory.Exists(path))
        {
            throw new GliderException(Errors.InvalidFolder("The install folder no longer exists."));
        }

        _c.Host.OpenPath(path);
    });

    public Task ActivateGame(AppRef request) => Rpc(nameof(ActivateGame), _ => _activation.ActivateAsync(App(request)));

    public Task InstallPrerequisites(AppRef request) => Rpc(nameof(InstallPrerequisites), _ => _prereqs.InstallAsync(App(request)));

    public Task<ShortcutState> SetShortcut(ShortcutRequest request) => Rpc(nameof(SetShortcut), _ => _shortcuts.SetAsync(Require(request, "request")));

    // ================================================================== cloud saves (Phase 2)

    public Task<SaveState> GetSaves(AppRef request) => Rpc(nameof(GetSaves), _ => Task.FromResult(_saves.Get(App(request))));

    public Task<SaveState> SyncSaves(SaveSyncRequest request) => Rpc(nameof(SyncSaves), ct => _saves.SyncAsync(Require(request, "request"), ct));

    public Task<LaunchResult?> ResolveSaves(SaveResolveRequest request) => Rpc(nameof(ResolveSaves), ct => _saves.ResolveAsync(Require(request, "request"), ct));

    public Task OpenSaveBackups(AppRef request) => Rpc(nameof(OpenSaveBackups), _ => _saves.OpenBackupsAsync(App(request)));

    // ================================================================== jobs / queue

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

    public Task<QueueState> ReorderJob(JobReorderRequest request) => Rpc(nameof(ReorderJob), async _ =>
    {
        var r = Require(request, "request");
        var id = JobId(new JobRef(r.JobId));
        RequireKnownJob(id);
        await _c.Jobs.ReorderAsync(id, r.ToIndex);
        return _policy.State(_c.Jobs);
    });

    public Task<QueueState> MoveJobToTop(JobRef request) => Rpc(nameof(MoveJobToTop), async _ =>
    {
        var id = JobId(request);
        RequireKnownJob(id);
        await _c.Jobs.MoveToTopAsync(id);
        return _policy.State(_c.Jobs);
    });

    public Task<JobHistory> GetJobHistory(JobRef request) => Rpc(nameof(GetJobHistory), _ =>
    {
        var id = JobId(request);
        RequireKnownJob(id);
        return Task.FromResult(_c.Jobs.History(id));
    });

    public Task<QueueState> PauseQueue() => Rpc(nameof(PauseQueue), async _ =>
    {
        await _c.Jobs.PauseAllAsync();
        return _policy.State(_c.Jobs);
    });

    public Task<QueueState> ResumeQueue() => Rpc(nameof(ResumeQueue), async _ =>
    {
        await _c.Jobs.ResumeAllAsync();
        return _policy.State(_c.Jobs);
    });

    public Task<QueueState> ClearFinishedJobs() => Rpc(nameof(ClearFinishedJobs), _ =>
    {
        _c.Jobs.ClearFinished();
        return Task.FromResult(_policy.State(_c.Jobs));
    });

    // ================================================================== egl

    public Task<EglState> RescanEgl() => Rpc(nameof(RescanEgl), ct => _egl.RescanAsync(ct));

    public Task<EglState> QuickScanEgl() => Rpc(nameof(QuickScanEgl), ct => _egl.QuickScanAsync(ct));

    public Task<FolderValidation> ValidateFolder(ValidateFolderRequest request) =>
        Rpc(nameof(ValidateFolder), _ => Task.FromResult(_install.Validate(Require(request, "request"))));

    public Task<EglImportResult> ImportEgl(EglImportRequest request) =>
        Rpc(nameof(ImportEgl), ct => _install.ImportEglAsync(Require(request, "request"), _egl.RescanAsync, ct));

    public Task<JobInfo> ImportFolder(LocateRequest request) =>
        Rpc(nameof(ImportFolder), ct => _install.ImportFolderAsync(Require(request, "request"), ct));

    public Task<EglReadiness> CheckEglReadiness() => Rpc(nameof(CheckEglReadiness), async ct =>
    {
        await _egl.RescanAsync(ct);
        return _egl.Readiness();
    });

    // ================================================================== EOS overlay (Phase 2)

    public Task<JobInfo> InstallOverlay() => Rpc(nameof(InstallOverlay), _ => _overlay.InstallAsync());

    public Task<OverlayState> SetOverlayEnabled(GameFlagRequest request) => Rpc(nameof(SetOverlayEnabled), _ => _overlay.SetEnabledAsync(Require(request, "request")));

    public Task<OverlayState> RemoveOverlay() => Rpc(nameof(RemoveOverlay), _ => _overlay.RemoveAsync());

    // ================================================================== store / free games (Phase 2)

    public Task<StoreState> ShowStore(StoreShowRequest request) => Rpc(nameof(ShowStore), _ => Task.FromResult(_store.Show(Require(request, "request"))));

    public Task SetStoreBounds(StoreBounds request) => Rpc(nameof(SetStoreBounds), () => _store.SetBounds(Require(request, "request")));

    public Task<StoreState> HideStore() => Rpc(nameof(HideStore), _ => Task.FromResult(_store.Hide()));

    public Task<StoreState> StoreBack() => Rpc(nameof(StoreBack), _ => Task.FromResult(_store.Back()));

    public Task<StoreState> StoreReload() => Rpc(nameof(StoreReload), _ => Task.FromResult(_store.Reload()));

    public Task<FreeGamesResult> GetFreeGames() => Rpc(nameof(GetFreeGames), _ => Task.FromResult(_freeGames.Get()));

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
        if (prev.WhilePlaying != saved.WhilePlaying || prev.DownloadMode != saved.DownloadMode || prev.IdleFullSpeed != saved.IdleFullSpeed)
        {
            _c.Jobs.Reevaluate();
        }

        if (prev.AutoUpdate != AutoUpdatePolicy.Always && saved.AutoUpdate == AutoUpdatePolicy.Always)
        {
            _updates.Evaluate();
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
        Check(s.SpeedLimitBytesPerSecond is null or >= 0, "speedLimitBytesPerSecond must be ≥ 0 or null");
        Check(s.PreferredCdn is null || (s.PreferredCdn.Length is > 0 and <= 253 && char.IsAsciiLetterOrDigit(s.PreferredCdn[0]) &&
                                         s.PreferredCdn.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-')), "preferredCdn must be a host name");
        Check(s.DefaultInstallBase is null || Path.IsPathFullyQualified(s.DefaultInstallBase), "defaultInstallBase must be a full path");
        Check(s.LibraryFolders is null || s.LibraryFolders.All(f => !string.IsNullOrWhiteSpace(f) && Path.IsPathFullyQualified(f)), "libraryFolders must be full paths");
        Check(Enum.IsDefined(s.LogLevel) && Enum.IsDefined(s.Theme) && Enum.IsDefined(s.DownloadMode) && Enum.IsDefined(s.WhilePlaying), "unknown enum value");
        Check(s.AutoUpdate is AutoUpdatePolicy.Always or AutoUpdatePolicy.Ask or AutoUpdatePolicy.Never, "autoUpdate must be always, ask or never");
        return s with { LibraryFolders = s.LibraryFolders ?? [] };
    }
}

internal static class GameSettingsValidator
{
    public static GameSettings Validate(GameSettings? s)
    {
        if (s is null)
        {
            throw new GliderException(Errors.InvalidRequest("settings are required"));
        }

        static void Check(bool ok, string what)
        {
            if (!ok)
            {
                throw new GliderException(Errors.InvalidRequest(what));
            }
        }

        var args = s.LaunchArgs ?? "";
        Check(args.Length <= SettingsStore.MaxLaunchArgsLength && !args.Contains('\n') && !args.Contains('\r') && !args.Contains('\0'),
            $"launchArgs must be one line (≤{SettingsStore.MaxLaunchArgsLength} chars)");
        Check(s.ExecutableOverride is null || (!Path.IsPathRooted(s.ExecutableOverride) && !s.ExecutableOverride.Split('/', '\\').Contains("..")
                                               && s.ExecutableOverride.IndexOfAny(Path.GetInvalidPathChars()) < 0),
            "executableOverride must be a path inside the install folder");
        Check(s.WorkingDirectory is null || Path.IsPathFullyQualified(s.WorkingDirectory), "workingDirectory must be a full path");
        Check(Enum.IsDefined(s.AutoUpdate) && Enum.IsDefined(s.LaunchMode), "unknown enum value");
        Check(s.LaunchOption is null || s.LaunchOption.Length <= 256, "launchOption is too long");
        return s with
        {
            LaunchArgs = SettingsStore.CleanLaunchArgs(args),
            ExecutableOverride = string.IsNullOrWhiteSpace(s.ExecutableOverride) ? null : s.ExecutableOverride.Trim(),
            WorkingDirectory = string.IsNullOrWhiteSpace(s.WorkingDirectory) ? null : s.WorkingDirectory.Trim(),
        };
    }
}

internal static class SharedHttp
{
    public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
}
