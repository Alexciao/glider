namespace Glider.Core.Contracts;

/// <summary>
/// The complete request/response surface between the UI and the host. Each method takes zero or one
/// parameter and returns <see cref="Task"/> or <see cref="Task{T}"/>. Failures throw
/// <see cref="GliderException"/>, which travels to the UI as a <see cref="GliderError"/>.
/// The frontend mock backend implements this same contract.
/// </summary>
[RpcEvent(RpcEvents.State, typeof(AppSnapshot))]
[RpcEvent(RpcEvents.Progress, typeof(JobProgress))]
[RpcEvent(RpcEvents.Notice, typeof(Notice))]
[RpcEvent(RpcEvents.Window, typeof(WindowInfo))]
[RpcEvent(RpcEvents.Caption, typeof(CaptionEvent))]
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
    [RpcMethod("app.completeOnboarding")] Task CompleteOnboarding();
    /// <summary>Called by the UI once it has painted; the host reveals the window then.</summary>
    [RpcMethod("app.ready")] Task Ready();
    [RpcMethod("app.quit")] Task Quit();

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

    // ---- game ----
    [RpcMethod("game.planInstall")] Task<InstallPlan> PlanInstall(InstallPlanRequest request);
    [RpcMethod("game.install")] Task<JobInfo> Install(InstallRequest request);
    [RpcMethod("game.validateFolder")] Task<FolderValidation> ValidateFolder(ValidateFolderRequest request);
    [RpcMethod("game.adopt")] Task<AdoptResult> Adopt(AdoptRequest request);
    [RpcMethod("game.checkForUpdates")] Task<UpdateCheckResult> CheckForUpdates();
    [RpcMethod("game.update")] Task<JobInfo> Update();
    [RpcMethod("game.verify")] Task<JobInfo> Verify();
    [RpcMethod("game.move")] Task<JobInfo> Move(MoveRequest request);
    [RpcMethod("game.uninstall")] Task<JobInfo> Uninstall(UninstallRequest request);
    [RpcMethod("game.launch")] Task<LaunchResult> Launch();
    /// <summary>
    /// Hint that the user is about to press Play (hover/focus): resolves the launch command in the background so
    /// <c>game.launch</c> starts the game without waiting for Legendary. Returns at once, never fails; safe to call repeatedly.
    /// </summary>
    [RpcMethod("game.prepareLaunch")] Task PrepareLaunch();
    [RpcMethod("game.openInstallFolder")] Task OpenInstallFolder();

    // ---- jobs ----
    [RpcMethod("jobs.pause")] Task<JobInfo> PauseJob(JobRef request);
    [RpcMethod("jobs.resume")] Task<JobInfo> ResumeJob(JobRef request);
    [RpcMethod("jobs.cancel")] Task<JobInfo> CancelJob(JobRef request);
    [RpcMethod("jobs.dismiss")] Task DismissJob(JobRef request);

    // ---- egl ----
    [RpcMethod("egl.rescan")] Task<EglState> RescanEgl();

    /// <summary>Rescan plus a quick look for installs EGL doesn't know about on every fixed drive (known folders only).</summary>
    [RpcMethod("egl.quickScan")] Task<EglState> QuickScanEgl();

    // ---- settings ----
    [RpcMethod("settings.update")] Task<Settings> UpdateSettings(Settings settings);
    [RpcMethod("settings.reset")] Task<Settings> ResetSettings();
}
