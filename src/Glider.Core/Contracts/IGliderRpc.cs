namespace Glider.Core.Contracts;

/// <summary>
/// The complete request/response surface between the UI and the host (contract v2, whole library). Each method takes
/// zero or one parameter and returns <see cref="Task"/> or <see cref="Task{T}"/>. Failures throw
/// <see cref="GliderException"/>, which travels to the UI as a <see cref="GliderError"/>.
/// The frontend mock backend implements this same contract. Design: docs/v0.2-design.md.
/// </summary>
[RpcEvent(RpcEvents.State, typeof(AppSnapshot))]
[RpcEvent(RpcEvents.Library, typeof(LibraryDelta))]
[RpcEvent(RpcEvents.Progress, typeof(JobProgress))]
[RpcEvent(RpcEvents.Notice, typeof(Notice))]
[RpcEvent(RpcEvents.Window, typeof(WindowInfo))]
[RpcEvent(RpcEvents.Caption, typeof(CaptionEvent))]
[RpcEvent(RpcEvents.Navigate, typeof(NavigateEvent))]
[RpcEvent(RpcEvents.Store, typeof(StoreState))]
[RpcEvent(RpcEvents.FreeGames, typeof(FreeGamesResult))]
public interface IGliderRpc
{
    // ---- app ----
    [RpcMethod("app.getSnapshot")] Task<AppSnapshot> GetSnapshot();
    [RpcMethod("app.openExternal")] Task OpenExternal(UrlRequest request);
    [RpcMethod("app.openPath")] Task OpenPath(PathRequest request);
    [RpcMethod("app.pickFolder")] Task<PickFolderResult> PickFolder(PickFolderRequest request);
    [RpcMethod("app.getLogs")] Task<LogsResult> GetLogs(LogsRequest request);
    [RpcMethod("app.copyLogs")] Task CopyLogs();
    [RpcMethod("app.getLicenses")] Task<LicensesResult> GetLicenses();
    /// <summary>Called by the UI once it has painted; the host reveals the window then.</summary>
    [RpcMethod("app.ready")] Task Ready();
    [RpcMethod("app.quit")] Task Quit();

    // ---- onboarding ----
    /// <summary>Persist the current step (and whether the previous one was skipped) so the flow resumes after a restart.</summary>
    [RpcMethod("onboarding.setStep")] Task<OnboardingState> SetOnboardingStep(OnboardingStepRequest request);
    [RpcMethod("onboarding.complete")] Task<OnboardingState> CompleteOnboarding();
    /// <summary>Re-run onboarding from Settings.</summary>
    [RpcMethod("onboarding.restart")] Task<OnboardingState> RestartOnboarding();

    // ---- window (frameless chrome) ----
    [RpcMethod("window.minimize")] Task Minimize();
    [RpcMethod("window.toggleMaximize")] Task ToggleMaximize();
    /// <summary>Honors Settings.CloseToTray.</summary>
    [RpcMethod("window.close")] Task Close();
    [RpcMethod("window.getInfo")] Task<WindowInfo> GetWindowInfo();

    // ---- auth ----
    /// <summary>Opens the Epic login window; resolves when signed in, throws AuthCancelled if closed.</summary>
    [RpcMethod("auth.signIn")] Task<AuthState> SignIn();
    [RpcMethod("auth.signOut")] Task<AuthState> SignOut();

    // ---- library ----
    /// <summary>Every owned game and DLC from the on-disk cache (instant; never waits on the network). Reset = true.</summary>
    [RpcMethod("library.get")] Task<LibraryDelta> GetLibrary();
    /// <summary>Start a background sync with Epic (returns at once; changes arrive as library events).</summary>
    [RpcMethod("library.refresh")] Task RefreshLibrary();
    /// <summary>Check every installed game for updates now (background; results arrive as library events).</summary>
    [RpcMethod("library.checkUpdates")] Task CheckForUpdates();
    /// <summary>Queue updates for every installed game with one pending (honors nothing but "Never").</summary>
    [RpcMethod("library.updateAll")] Task<UpdateAllResult> UpdateAll();
    [RpcMethod("library.setFavorite")] Task SetFavorite(GameFlagRequest request);
    [RpcMethod("library.setHidden")] Task SetHidden(GameFlagRequest request);

    // ---- collections ----
    [RpcMethod("collections.create")] Task<Collection> CreateCollection(CollectionCreateRequest request);
    [RpcMethod("collections.rename")] Task<Collection> RenameCollection(CollectionRenameRequest request);
    [RpcMethod("collections.delete")] Task DeleteCollection(CollectionRef request);
    [RpcMethod("collections.setMembership")] Task<Collection> SetCollectionMembership(CollectionMembershipRequest request);

    // ---- game ----
    [RpcMethod("game.getDetail")] Task<GameDetail> GetGameDetail(AppRef request);
    [RpcMethod("game.updateSettings")] Task<GameSettings> UpdateGameSettings(GameSettingsRequest request);
    [RpcMethod("game.planInstall")] Task<InstallPlan> PlanInstall(InstallPlanRequest request);
    [RpcMethod("game.install")] Task<JobInfo> Install(InstallRequest request);
    [RpcMethod("game.modify")] Task<JobInfo> Modify(ModifyRequest request);
    [RpcMethod("game.update")] Task<JobInfo> Update(AppRef request);
    /// <summary>Verify, repair what's bad, verify again.</summary>
    [RpcMethod("game.verify")] Task<JobInfo> Verify(AppRef request);
    [RpcMethod("game.move")] Task<JobInfo> Move(MoveRequest request);
    [RpcMethod("game.uninstall")] Task<JobInfo> Uninstall(UninstallRequest request);
    [RpcMethod("game.launch")] Task<LaunchResult> Launch(LaunchRequest request);
    /// <summary>
    /// Hint that the user is about to press Play (hover/focus): resolves the launch command in the background so
    /// <c>game.launch</c> starts the game without waiting for Legendary. Returns at once, never fails; safe to call repeatedly.
    /// </summary>
    [RpcMethod("game.prepareLaunch")] Task PrepareLaunch(AppRef request);
    /// <summary>Closes the running game (graceful first, then kill). No-op when it is not running.</summary>
    [RpcMethod("game.close")] Task CloseGame(AppRef request);
    [RpcMethod("game.openFolder")] Task OpenInstallFolder(AppRef request);
    /// <summary>Link/activate a third-party-store game (Ubisoft Connect / EA app) via Legendary's activate flow.</summary>
    [RpcMethod("game.activate")] Task ActivateGame(AppRef request);
    /// <summary>Install the game's declared prerequisites (one UAC prompt) and remember them.</summary>
    [RpcMethod("game.installPrerequisites")] Task InstallPrerequisites(AppRef request);
    /// <summary>"Play anyway" after <see cref="ErrorCode.PrerequisiteFailed"/>: stop asking for this game's prerequisites until the user installs them on purpose. Launch again afterwards.</summary>
    [RpcMethod("game.skipPrerequisites")] Task SkipPrerequisites(AppRef request);
    [RpcMethod("game.setShortcut")] Task<ShortcutState> SetShortcut(ShortcutRequest request);

    // ---- cloud saves ----
    [RpcMethod("saves.get")] Task<SaveState> GetSaves(AppRef request);
    [RpcMethod("saves.sync")] Task<SaveState> SyncSaves(SaveSyncRequest request);
    /// <summary>Resolve a conflict by keeping one side; the other side is backed up first.</summary>
    [RpcMethod("saves.resolve")] Task<LaunchResult?> ResolveSaves(SaveResolveRequest request);
    [RpcMethod("saves.openBackups")] Task OpenSaveBackups(AppRef request);

    // ---- jobs / queue ----
    [RpcMethod("jobs.pause")] Task<JobInfo> PauseJob(JobRef request);
    [RpcMethod("jobs.resume")] Task<JobInfo> ResumeJob(JobRef request);
    [RpcMethod("jobs.cancel")] Task<JobInfo> CancelJob(JobRef request);
    [RpcMethod("jobs.dismiss")] Task DismissJob(JobRef request);
    [RpcMethod("jobs.reorder")] Task<QueueState> ReorderJob(JobReorderRequest request);
    [RpcMethod("jobs.moveToTop")] Task<QueueState> MoveJobToTop(JobRef request);
    [RpcMethod("jobs.getHistory")] Task<JobHistory> GetJobHistory(JobRef request);
    [RpcMethod("queue.pauseAll")] Task<QueueState> PauseQueue();
    [RpcMethod("queue.resumeAll")] Task<QueueState> ResumeQueue();
    [RpcMethod("queue.clearFinished")] Task<QueueState> ClearFinishedJobs();

    // ---- egl ----
    [RpcMethod("egl.rescan")] Task<EglState> RescanEgl();
    /// <summary>Rescan plus a quick look for installs EGL doesn't know about on every fixed drive (known folders only).</summary>
    [RpcMethod("egl.quickScan")] Task<EglState> QuickScanEgl();
    [RpcMethod("egl.validateFolder")] Task<FolderValidation> ValidateFolder(ValidateFolderRequest request);
    /// <summary>Import the selected installs (one Import job each; zero re-download).</summary>
    [RpcMethod("egl.import")] Task<EglImportResult> ImportEgl(EglImportRequest request);
    /// <summary>Import a user-located folder.</summary>
    [RpcMethod("egl.importFolder")] Task<JobInfo> ImportFolder(LocateRequest request);
    [RpcMethod("egl.checkReadiness")] Task<EglReadiness> CheckEglReadiness();

    // ---- EOS overlay ----
    [RpcMethod("overlay.install")] Task<JobInfo> InstallOverlay();
    [RpcMethod("overlay.setEnabled")] Task<OverlayState> SetOverlayEnabled(GameFlagRequest request);
    [RpcMethod("overlay.remove")] Task<OverlayState> RemoveOverlay();

    // ---- store / free games ----
    /// <summary>Create (lazily) and show the native Store view over the placeholder; optionally navigate to a target.</summary>
    [RpcMethod("store.show")] Task<StoreState> ShowStore(StoreShowRequest request);
    [RpcMethod("store.setBounds")] Task SetStoreBounds(StoreBounds request);
    /// <summary>Hide (and later suspend) the Store view.</summary>
    [RpcMethod("store.hide")] Task<StoreState> HideStore();
    [RpcMethod("store.back")] Task<StoreState> StoreBack();
    [RpcMethod("store.reload")] Task<StoreState> StoreReload();
    /// <summary>Hide the native view under an HTML modal (snapshot placeholder) / restore it.</summary>
    [RpcMethod("store.setCovered")] Task<StoreState> SetStoreCovered(StoreCoverRequest request);
    /// <summary>Open the current Store page (sanitized URL) in the default browser.</summary>
    [RpcMethod("store.openInBrowser")] Task OpenStoreInBrowser();
    /// <summary>The current Store state, for late subscribers.</summary>
    [RpcMethod("store.getState")] Task<StoreState> GetStoreState();
    /// <summary>Epic's free games (cache first; never waits on the network when a cache exists).</summary>
    [RpcMethod("freeGames.get")] Task<FreeGamesResult> GetFreeGames();
    /// <summary>Fetch the feed now (rate-limited), then return the result.</summary>
    [RpcMethod("freeGames.refresh")] Task<FreeGamesResult> RefreshFreeGames();

    // ---- settings ----
    [RpcMethod("settings.update")] Task<Settings> UpdateSettings(Settings settings);
    [RpcMethod("settings.reset")] Task<Settings> ResetSettings();
}
