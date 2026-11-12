using Glider.Core.Contracts;

// Module seams. Each Phase-2 workstream implements one side of these and codes against the others.
// Change a signature only with the orchestrator's agreement (it is shared by parallel branches).

namespace Glider.Core;

// ============================== Infrastructure ==============================

/// <summary>All per-user locations. Tests construct it over a temp root.</summary>
public sealed class AppPaths
{
    public AppPaths(string root, string legendaryExe)
    {
        Root = root;
        LegendaryExe = legendaryExe;
    }

    /// <summary>Default: %LocalAppData%\Glider, legendary.exe next to the host executable.</summary>
    public static AppPaths ForCurrentUser() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glider"),
        Path.Combine(AppContext.BaseDirectory, "legendary", "legendary.exe"));

    public string Root { get; }
    public string LegendaryExe { get; }
    public string LegendaryConfig => Path.Combine(Root, "legendary");
    public string State => Path.Combine(Root, "state");
    public string Cache => Path.Combine(Root, "cache");
    public string Logs => Path.Combine(Root, "logs");
    public string EglBackups => Path.Combine(Root, "backups", "egl");
    public string WebView2 => Path.Combine(Root, "webview2");

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, LegendaryConfig, State, Cache, Logs, EglBackups })
        {
            Directory.CreateDirectory(d);
        }
    }
}

/// <summary>Host services Core needs but cannot implement cross-platform. Implemented by Glider.App.</summary>
public interface IHostPlatform
{
    /// <summary>Shows the Epic login window; returns the authorizationCode, or null if the user closed it.</summary>
    Task<string?> AcquireEpicAuthorizationCodeAsync(CancellationToken ct);

    /// <summary>Clears the login webview's cookies (sign-out).</summary>
    Task ClearLoginSessionAsync();

    Task<string?> PickFolderAsync(string? initialPath, string? title);
    void OpenUrl(string url);
    void OpenPath(string path);
    void SetClipboardText(string text);
    void ShowToast(string title, string message);

    /// <summary>Runs an exe elevated (UAC) and waits; returns exit code, or null if the user declined UAC.</summary>
    Task<int?> RunElevatedAsync(string exe, string arguments, CancellationToken ct);

    void SetStartWithWindows(bool enabled);
    void MinimizeWindow();
    void ToggleMaximizeWindow();
    /// <summary>Close button: hide to tray or quit, per settings (host decides with the bool).</summary>
    void CloseWindow(bool toTray);
    void HideToTray();
    void RestoreWindow();
    void RevealWindow();
    WindowInfo GetWindowInfo();
    void Quit();

    bool IsWindows11 { get; }
    BackdropKind Backdrop { get; }
}

// ============================== Legendary ==============================

public sealed record LegendaryAccount(string DisplayName, string AccountId);

/// <summary>Parsed from `info Fortnite --json` (and metadata cache).</summary>
public sealed record LegendaryGameInfo(
    string AppName,
    string Title,
    string Version,
    long DownloadSizeBytes,
    long InstallSizeBytes,
    string? FolderName,
    string? LaunchExecutable,
    string? CatalogItemId,
    string? Namespace,
    IReadOnlyList<KeyImage> KeyImages,
    PrerequisiteInfo? Prerequisites);

public sealed record KeyImage(string Type, string Url, int? Width, int? Height);

public sealed record PrerequisiteInfo(string? Name, string? Path, string? Args, IReadOnlyList<string> Ids);

/// <summary>Entry from Legendary's installed.json (read directly, no process).</summary>
public sealed record LegendaryInstalledGame(
    string AppName,
    string InstallPath,
    string Version,
    long InstallSizeBytes,
    string? Executable,
    bool NeedsVerification,
    string? EglGuid,
    IReadOnlyList<string> InstallTags);

/// <summary>Raw (unsmoothed) progress parsed from Legendary's DLManager/verify output.</summary>
public readonly record struct LegendaryProgress(
    double Percent,
    long ProcessedChunks,
    long TotalChunks,
    long ElapsedSeconds,
    long? EtaSeconds,
    long DownloadedBytes,
    long WrittenBytes,
    double DownloadBytesPerSecond,
    double DecompressedBytesPerSecond,
    double DiskWriteBytesPerSecond,
    double DiskReadBytesPerSecond);

[System.Text.Json.Serialization.JsonConverter(typeof(CamelEnumConverter<InstallMode>))]
public enum InstallMode { Install, Update, Repair }

public sealed record InstallArgs(
    string AppName,
    InstallMode Mode,
    string? BasePath,
    int MaxWorkers,
    int MaxSharedMemoryMb,
    string? PreferredCdn);

/// <summary>Sizes from Legendary's install/update analysis: compressed bytes to download, and the resulting install size.</summary>
public sealed record LegendarySizes(long DownloadBytes, long InstallBytes);

/// <summary>What one `verify` run found: files checked, and how many failed their hash or were missing.</summary>
public sealed record VerifyCounts(int FilesChecked, int Corrupted, int Missing)
{
    public int Bad => Corrupted + Missing;
}

/// <summary>Outcome of a long-running Legendary operation. Error is non-null on failure. Counts is set by `verify` only.</summary>
public sealed record LegendaryOutcome(bool Success, GliderError? Error, long? DownloadSizeBytes, long? InstallSizeBytes, VerifyCounts? Counts = null);

/// <summary>The command `launch --json` resolved (Glider spawns and owns the game process).</summary>
public sealed record LaunchCommand(string Executable, string WorkingDirectory, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment);

/// <summary>Typed wrapper over the bundled legendary.exe. All methods are cancellable and never block the caller's thread.</summary>
public interface ILegendary
{
    Task<string> GetVersionAsync(CancellationToken ct);

    /// <summary>`status --offline --json`; null when signed out. Never touches the network.</summary>
    Task<LegendaryAccount?> GetAccountOfflineAsync(CancellationToken ct);

    /// <summary>Online `status --json`: refreshes tokens. Throws AuthExpired / Network GliderExceptions.</summary>
    Task<LegendaryAccount?> RefreshSessionAsync(CancellationToken ct);

    Task<LegendaryAccount> SignInWithCodeAsync(string authorizationCode, CancellationToken ct);
    Task SignOutAsync(CancellationToken ct);

    /// <summary>`info &lt;app&gt; --json` (online; refreshes metadata + manifest).</summary>
    Task<LegendaryGameInfo> GetGameInfoAsync(string appName, CancellationToken ct);

    /// <summary>Reads installed.json directly (fast). Null if not installed in Legendary's data.</summary>
    Task<LegendaryInstalledGame?> GetInstalledAsync(string appName, CancellationToken ct);

    /// <summary>install / update / repair with live progress. Pause = cancel ct (resume file keeps progress).</summary>
    Task<LegendaryOutcome> InstallAsync(InstallArgs args, IProgress<LegendaryProgress> progress, CancellationToken ct);

    Task<LegendaryOutcome> VerifyAsync(string appName, IProgress<LegendaryProgress> progress, CancellationToken ct);

    /// <summary>`import`: registers an existing folder without downloading.</summary>
    Task<LegendaryOutcome> ImportAsync(string appName, string gamePath, CancellationToken ct);

    Task<LegendaryOutcome> MoveAsync(string appName, string newBasePath, bool skipMove, IProgress<LegendaryProgress>? progress, CancellationToken ct);

    Task<LegendaryOutcome> UninstallAsync(string appName, bool keepFiles, CancellationToken ct);

    /// <summary>`launch --json` (+ `--skip-version-check` when we already checked).</summary>
    Task<LaunchCommand> GetLaunchCommandAsync(string appName, bool skipVersionCheck, string extraArgs, CancellationToken ct);

    /// <summary>Path of Legendary's current manifest file for the installed version (for EGL write-back).</summary>
    string? GetInstalledManifestPath(string appName, string version);

    /// <summary>
    /// Dry run of `update`: Legendary's own analysis of what the pending update would download and write, or null when it
    /// can't be measured (nothing to update, needs a verify first, offline). Changes nothing on disk.
    /// </summary>
    Task<LegendarySizes?> GetUpdateSizesAsync(string appName, CancellationToken ct) => Task.FromResult<LegendarySizes?>(null);
}

// ============================== Game record ==============================

/// <summary>Glider's own persisted record of the install (state/game.json).</summary>
public sealed record GameRecord(
    string InstallPath,
    string Version,
    long InstallSizeBytes,
    DateTimeOffset InstalledAt,
    bool AdoptedFromEgl,
    /// <summary>EGL InstallationGuid when the folder is shared with EGL.</summary>
    string? EglInstallationGuid,
    bool NeedsVerification);

// ============================== EGL ==============================

/// <summary>Where EGL keeps its data. Tests point this at a temp tree.</summary>
public sealed record EglEnvironment(string ProgramData)
{
    public static EglEnvironment ForMachine() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    public string ManifestsDir => Path.Combine(ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
    public string LauncherInstalledDat => Path.Combine(ProgramData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
}

/// <summary>A parsed EGL install manifest (.item) for our app, plus where it came from.</summary>
public sealed record EglInstall(
    string ItemPath,
    string InstallationGuid,
    string InstallLocation,
    string Version,
    long InstallSizeBytes,
    bool IsIncomplete,
    bool NeedsValidation);

[System.Text.Json.Serialization.JsonConverter(typeof(CamelEnumConverter<DriftKind>))]
public enum DriftKind
{
    InSync,
    /// <summary>EGL updated/repaired the game: refresh our record (re-import), never re-download.</summary>
    EglNewer,
    /// <summary>Glider updated the game: write back to EGL if sync is on.</summary>
    GliderNewer,
    /// <summary>EGL's manifest vanished or points elsewhere.</summary>
    EglMissing,
    /// <summary>Can't reconcile safely: run a quick verify (never a full re-download).</summary>
    Unreconcilable,
}

public sealed record DriftResult(DriftKind Kind, string? EglVersion, string? GliderVersion, EglInstall? Egl);

/// <summary>Data written back to EGL after Glider changes the install (mirrors Legendary's egl_export).</summary>
public sealed record EglWriteBack(
    string AppName,
    string InstallPath,
    string Version,
    long InstallSizeBytes,
    IReadOnlyList<string> InstallTags,
    string? LegendaryManifestPath,
    string? CatalogItemId,
    string? Namespace,
    string? LaunchExecutable,
    string? ExistingInstallationGuid);

public interface IEglService
{
    /// <summary>Fast scan (target &lt;20 ms): EGL presence, candidates (incl. stale), running/busy flags.</summary>
    Task<EglState> ScanAsync(CancellationToken ct);

    bool IsLauncherRunning();

    /// <summary>True if EGL is installing/updating/verifying the game at this path right now.</summary>
    bool IsBusyWith(string installPath);

    /// <summary>Completes when EGL is not busy with the path; reports the blocking reason while waiting.</summary>
    Task WaitUntilNotBusyAsync(string installPath, Action<string?> onBlocked, CancellationToken ct);

    /// <summary>Find EGL's manifest for an install location (case/separator-insensitive).</summary>
    EglInstall? FindByLocation(string installPath);

    DriftResult CheckDrift(GameRecord record);

    /// <summary>Back up then atomically write the .item (+ .egstore manifest/.mancpn) like Legendary's egl_export.</summary>
    Task<string> WriteBackAsync(EglWriteBack data, CancellationToken ct);

    /// <summary>Update EGL's manifest after a move (sync on) — same backup/atomic guarantees.</summary>
    Task UpdateLocationAsync(string installationGuid, string newInstallPath, CancellationToken ct);
}

// ============================== Folder validation ==============================

public static class FortniteLayout
{
    public const string AppName = "Fortnite";
    public const string Title = "Fortnite";
    public const string ShippingExeRelative = @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe";
    public const string ShippingProcessName = "FortniteClient-Win64-Shipping";

    public static string ShippingExe(string gamePath) =>
        Path.Combine(gamePath, ShippingExeRelative.Replace('\\', Path.DirectorySeparatorChar));

    public static bool LooksLikeInstall(string gamePath) => File.Exists(ShippingExe(gamePath));
}

// ============================== Logging ==============================

public interface ILog
{
    LogLevel MinimumLevel { get; set; }
    void Write(LogLevel level, string category, string message, Exception? exception = null);

    /// <summary>Last N lines (for the in-app viewer / copy-to-clipboard).</summary>
    string Tail(int maxLines);
    string CurrentFile { get; }
}

public static class LogExtensions
{
    public static void Debug(this ILog log, string category, string message) => log.Write(LogLevel.Debug, category, message);
    public static void Info(this ILog log, string category, string message) => log.Write(LogLevel.Info, category, message);
    public static void Warn(this ILog log, string category, string message, Exception? ex = null) => log.Write(LogLevel.Warning, category, message, ex);
    public static void Error(this ILog log, string category, string message, Exception? ex = null) => log.Write(LogLevel.Error, category, message, ex);
}

// ============================== Settings & records ==============================

public interface ISettingsStore
{
    Settings Current { get; }
    event Action<Settings>? Changed;
    Task<Settings> UpdateAsync(Settings settings);
}

public interface IGameRecordStore
{
    GameRecord? Current { get; }
    Task SaveAsync(GameRecord? record);
}

// ============================== Jobs ==============================

public sealed record JobRequest(
    JobKind Kind,
    /// <summary>Install: base path. Move: destination base. Import: game folder.</summary>
    string? TargetPath,
    UninstallMode? UninstallMode,
    string? FromVersion,
    string? ToVersion);

/// <summary>Serial, persistent job queue (state/jobs.json). Survives restarts and crashes.</summary>
public interface IJobQueue
{
    IReadOnlyList<JobInfo> Snapshot();
    bool IsBusy { get; }
    event Action? Changed;
    /// <summary>Smoothed progress, throttled to ~10 Hz.</summary>
    event Action<JobProgress>? ProgressChanged;

    JobInfo Enqueue(JobRequest request);
    Task<JobInfo> PauseAsync(string jobId);
    Task<JobInfo> ResumeAsync(string jobId);
    Task<JobInfo> CancelAsync(string jobId);
    void Dismiss(string jobId);

    /// <summary>Load persisted jobs; interrupted ones come back Paused (user resumes) or auto-resume per policy.</summary>
    Task RestoreAsync(CancellationToken ct);
}

/// <summary>Callbacks the queue invokes around every job; implemented by the service layer.</summary>
public interface IJobHooks
{
    /// <summary>Gate before (and while) a job runs: EGL busy, game running, auth. Report waits via setBlockedReason.</summary>
    Task BeforeRunAsync(JobInfo job, Action<string?> setBlockedReason, CancellationToken ct);

    /// <summary>After success: update GameRecord, EGL write-back, notifications.</summary>
    Task OnSucceededAsync(JobInfo job, CancellationToken ct);

    Task OnFailedAsync(JobInfo job, CancellationToken ct);
}
