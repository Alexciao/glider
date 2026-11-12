using Glider.Core;
using Glider.Core.Contracts;

namespace Glider.App;

/// <summary>
/// TEMPORARY stand-in for Core's GliderService so the host runs on its own. Returns a static, plausible snapshot, forwards the
/// window/app calls to the host platform (the real service does the same), and fails everything else with Errors.Unknown().
/// DELETE when Composition.Build returns the real service.
/// </summary>
internal sealed class PlaceholderRpc(AppPaths paths, IHostPlatform host, IEventSink events) : IGliderRpc
{
    private Settings _settings = Settings.Default;

    private static Task<T> NotImplemented<T>() => Task.FromException<T>(new GliderException(Errors.Unknown("PlaceholderRpc: not implemented in the host-only build")));

    private static Task NotImplemented() => NotImplemented<object>();

    public Task<AppSnapshot> GetSnapshot() => Task.FromResult(new AppSnapshot(
        new AppInfo("0.0.0-placeholder", "n/a", host.IsWindows11, host.Backdrop, paths.Root, paths.Logs, Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, 8, 256, true),
        new AuthState(AuthStatus.SignedOut, null),
        new GameState(FortniteLayout.AppName, FortniteLayout.Title, InstallStatus.NotInstalled, null, "34.20", false, false, null, RunState.Idle, null, null, 48_000_000_000, 95_000_000_000),
        [],
        new EglState(false, false, false, [], true, false, null, null),
        _settings,
        new OnboardingState(false, OnboardingStep.Welcome)));

    public Task OpenExternal(UrlRequest request)
    {
        host.OpenUrl(request.Url);
        return Task.CompletedTask;
    }

    public Task OpenPath(PathRequest request)
    {
        host.OpenPath(request.Path);
        return Task.CompletedTask;
    }

    public async Task<PickFolderResult> PickFolder(PickFolderRequest request) =>
        new(await host.PickFolderAsync(request.InitialPath, request.Title));

    public Task<LogsResult> GetLogs(LogsRequest request) => NotImplemented<LogsResult>();

    public Task CopyLogs() => NotImplemented();

    public Task<LicensesResult> GetLicenses() => Task.FromResult(new LicensesResult([]));

    public Task CompleteOnboarding() => Task.CompletedTask;

    public Task Ready()
    {
        host.RevealWindow();
        return Task.CompletedTask;
    }

    public Task Quit()
    {
        host.Quit();
        return Task.CompletedTask;
    }

    public Task Minimize()
    {
        host.MinimizeWindow();
        return Task.CompletedTask;
    }

    public Task ToggleMaximize()
    {
        host.ToggleMaximizeWindow();
        return Task.CompletedTask;
    }

    public Task Close()
    {
        host.CloseWindow(_settings.CloseToTray);
        return Task.CompletedTask;
    }

    public Task<WindowInfo> GetWindowInfo() => Task.FromResult(host.GetWindowInfo());

    public Task<AuthState> SignIn() => NotImplemented<AuthState>();

    public Task<AuthState> SignOut() => NotImplemented<AuthState>();

    public Task<InstallPlan> PlanInstall(InstallPlanRequest request) => NotImplemented<InstallPlan>();

    public Task<JobInfo> Install(InstallRequest request) => NotImplemented<JobInfo>();

    public Task<FolderValidation> ValidateFolder(ValidateFolderRequest request) => NotImplemented<FolderValidation>();

    public Task<AdoptResult> Adopt(AdoptRequest request) => NotImplemented<AdoptResult>();

    public Task<UpdateCheckResult> CheckForUpdates() => NotImplemented<UpdateCheckResult>();

    public Task<JobInfo> Update() => NotImplemented<JobInfo>();

    public Task<JobInfo> Verify() => NotImplemented<JobInfo>();

    public Task<JobInfo> Move(MoveRequest request) => NotImplemented<JobInfo>();

    public Task<JobInfo> Uninstall(UninstallRequest request) => NotImplemented<JobInfo>();

    public Task<LaunchResult> Launch() => NotImplemented<LaunchResult>();

    public Task OpenInstallFolder() => NotImplemented();

    public Task<JobInfo> PauseJob(JobRef request) => NotImplemented<JobInfo>();

    public Task<JobInfo> ResumeJob(JobRef request) => NotImplemented<JobInfo>();

    public Task<JobInfo> CancelJob(JobRef request) => NotImplemented<JobInfo>();

    public Task DismissJob(JobRef request) => NotImplemented();

    public Task<EglState> RescanEgl() => Task.FromResult(new EglState(false, false, false, [], true, false, null, null));

    public Task<Settings> UpdateSettings(Settings settings)
    {
        _settings = settings;
        host.SetStartWithWindows(settings.StartWithWindows);
        events.Publish(RpcEvents.Notice, new Notice("settings", NoticeLevel.Info, "Settings updated (placeholder backend)", null, null));
        return Task.FromResult(_settings);
    }

    public Task<Settings> ResetSettings() => Task.FromResult(_settings = Settings.Default);
}
