using System.Collections.Immutable;
using Glider.Core.Contracts;

// Module seams. Phase-2 workstreams implement one side of these and code against the others.
// Change a signature only additively (it is shared by parallel branches). Design: docs/v0.2-design.md.

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
    public string Backups => Path.Combine(Root, "backups");
    public string EglBackups => Path.Combine(Backups, "egl");
    public string MigrationBackups => Path.Combine(Backups, "migration");
    public string WebView2 => Path.Combine(Root, "webview2");

    /// <summary>Where Glider's own copy of the EOS overlay lives (passed to <c>eos-overlay install --path</c>).</summary>
    public string Overlay => Path.Combine(Root, "overlay");

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
    /// <summary>Shows the Epic login window; returns the exchange code Epic's page hands to the launcher bridge, or null if the user closed it.</summary>
    Task<string?> AcquireEpicExchangeCodeAsync(CancellationToken ct);

    /// <summary>Clears the login webview's cookies (sign-out).</summary>
    Task ClearLoginSessionAsync();

    Task<string?> PickFolderAsync(string? initialPath, string? title);
    void OpenUrl(string url);
    void OpenPath(string path);
    void SetClipboardText(string text);
    void ShowToast(string title, string message);

    /// <summary>Runs an exe elevated (UAC) and waits; returns exit code, or null if the user declined UAC.</summary>
    Task<int?> RunElevatedAsync(string exe, string arguments, CancellationToken ct);

    /// <summary>
    /// Hands a launch URI to another store's launcher (<c>uplay://</c> or <c>link2ea://</c> only; anything else must be refused).
    /// The URI carries a live Epic exchange code: never log it.
    /// </summary>
    void OpenThirdPartyUri(string uri) => throw new NotSupportedException("This host cannot open third-party launcher links.");

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

    /// <summary>The native Store view (a second WebView2 over the Store page's placeholder). Null on hosts without one.</summary>
    Store.IStoreHost? StoreHost => null;
}

// ============================== Legendary ==============================

public sealed record LegendaryAccount(string DisplayName, string AccountId);

/// <summary>Parsed from `info &lt;app&gt; --json` (and the metadata cache).</summary>
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
    IReadOnlyList<string> InstallTags,
    bool IsDlc = false,
    string? Title = null,
    bool CanRunOffline = true,
    bool RequiresOwnershipToken = false,
    string? Platform = null,
    /// <summary>installed.json <c>prereq_info</c>: the redistributables the manifest asks to run once. Null when the game declares none.</summary>
    PrerequisiteInfo? Prerequisites = null);

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

/// <summary>Process priorities for one Legendary run of a writer-lane job (see <see cref="Jobs.IResourcePolicy"/>).</summary>
public sealed record ResourceProfile(bool LowPriority)
{
    /// <summary>v0.1 behaviour: the whole tree below normal CPU priority.</summary>
    public static ResourceProfile Default { get; } = new(LowPriority: true);
}

public sealed record InstallArgs(
    string AppName,
    InstallMode Mode,
    string? BasePath,
    int MaxWorkers,
    int MaxSharedMemoryMb,
    string? PreferredCdn,
    /// <summary>Explicit install tags (components); null = Legendary's defaults via <c>--skip-sdl</c>.</summary>
    IReadOnlyList<string>? InstallTags = null,
    ResourceProfile? Resources = null);

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

/// <summary>
/// Typed wrapper over the bundled legendary.exe. All methods are cancellable and never block the caller's thread.
/// Which lane a call belongs to is the caller's business (docs/v0.2-design.md §3); the auth gate is applied inside.
/// </summary>
public interface ILegendary
{
    Task<string> GetVersionAsync(CancellationToken ct);

    /// <summary>`status --offline --json`; null when signed out. Never touches the network.</summary>
    Task<LegendaryAccount?> GetAccountOfflineAsync(CancellationToken ct);

    /// <summary>Online `status --json`: refreshes tokens. Throws AuthExpired / Network GliderExceptions.</summary>
    Task<LegendaryAccount?> RefreshSessionAsync(CancellationToken ct);

    /// <summary>`auth --token`: the sign-in the login window produces (an exchange code).</summary>
    Task<LegendaryAccount> SignInWithExchangeTokenAsync(string exchangeToken, CancellationToken ct);
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

    /// <summary>`launch --json` (+ `--skip-version-check` when we already checked, `--offline` to skip Epic).</summary>
    Task<LaunchCommand> GetLaunchCommandAsync(string appName, bool skipVersionCheck, string extraArgs, bool offline, CancellationToken ct);

    /// <summary>Path of Legendary's current manifest file for the installed version (for EGL write-back).</summary>
    string? GetInstalledManifestPath(string appName, string version);

    /// <summary>
    /// Dry run of `update`: Legendary's own analysis of what the pending update would download and write, or null when it
    /// can't be measured (nothing to update, needs a verify first, offline, Legendary busy). Changes nothing on disk.
    /// </summary>
    Task<LegendarySizes?> GetUpdateSizesAsync(string appName, CancellationToken ct) => Task.FromResult<LegendarySizes?>(null);

    /// <summary>`list -T --json`: Legendary refreshes metadata/assets/entitlements on disk; Glider then re-reads the files.</summary>
    Task<LegendaryOutcome> RefreshLibraryAsync(CancellationToken ct) => Task.FromResult(new LegendaryOutcome(true, null, null, null));

    /// <summary>`list-installed --check-updates --json`: one assets request per platform (also warms the session).</summary>
    Task<LegendaryOutcome> CheckUpdatesAsync(CancellationToken ct) => Task.FromResult(new LegendaryOutcome(true, null, null, null));

    /// <summary>
    /// `eos-overlay install --path` (or `update` when Legendary already manages one): downloads Glider's copy of the EOS overlay with
    /// live progress. Anonymous session, no installed.json lock. Legendary also points HKCU at it. Success also covers "already up to date".
    /// </summary>
    Task<LegendaryOutcome> InstallOverlayAsync(string? path, IProgress<LegendaryProgress> progress, CancellationToken ct) =>
        Task.FromResult(new LegendaryOutcome(false, Errors.NotAvailableYet("the EOS overlay"), null, null));

    /// <summary>`eos-overlay remove`: deletes Glider's overlay copy and its HKCU entries. Succeeds when nothing is left installed.</summary>
    Task<LegendaryOutcome> RemoveOverlayAsync(CancellationToken ct) =>
        Task.FromResult(new LegendaryOutcome(false, Errors.NotAvailableYet("the EOS overlay"), null, null));

    /// <summary>`activate --uplay`: claims and redeems the account's Ubisoft keys. Fails with ActivationRequired when no Ubisoft account is linked.</summary>
    Task<LegendaryOutcome> ActivateUplayAsync(CancellationToken ct) =>
        Task.FromResult(new LegendaryOutcome(false, Errors.NotAvailableYet("third-party activation"), null, null));

    /// <summary>
    /// `launch &lt;app&gt; --ubisoft|--origin --json [--offline]`: the <c>uplay://</c> / <c>link2ea://</c> URI that starts the game in the other
    /// store's launcher. It embeds a live exchange code: callers open it at once and never log it.
    /// </summary>
    Task<string> GetThirdPartyUriAsync(string appName, ThirdPartyStore store, bool offline, CancellationToken ct) =>
        throw new GliderException(Errors.NotAvailableYet("third-party launch"));

    /// <summary>
    /// `get-token --json`: a fresh one-time web exchange code (32 hex) for the signed-in account, used for the Store's web sign-in.
    /// A credential: never logged, never stored, valid ~5 minutes. Null when signed out or Epic refuses.
    /// </summary>
    Task<string?> GetWebExchangeCodeAsync(CancellationToken ct) => Task.FromResult<string?>(null);
}

// ============================== Glider's per-game records (state/games.json) ==============================

/// <summary>Glider's own record of one install: the v0.1 GameRecord plus the app it belongs to.</summary>
public sealed record InstallRecord(
    string AppName,
    string InstallPath,
    string Version,
    long InstallSizeBytes,
    DateTimeOffset InstalledAt,
    bool AdoptedFromEgl,
    /// <summary>EGL InstallationGuid when the folder is shared with EGL.</summary>
    string? EglInstallationGuid,
    bool NeedsVerification);

/// <summary>Everything Glider stores about one game (installed or not).</summary>
public sealed record GameEntry(
    InstallRecord? Install,
    bool Favorite,
    bool Hidden,
    /// <summary>Total time the game has run under Glider, in seconds. Flushed periodically and on exit.</summary>
    long PlaytimeSeconds,
    DateTimeOffset? LastPlayedAt,
    GameSettings Settings,
    ShortcutState Shortcuts)
{
    public static GameEntry Empty { get; } = new(null, false, false, 0, null, GameSettings.Default, new ShortcutState(false, false));

    /// <summary>Nothing worth persisting (lets the store drop default entries).</summary>
    public bool IsEmpty => this == Empty;
}

public interface IGamesStore
{
    ImmutableDictionary<string, GameEntry> Current { get; }

    event Action? Changed;

    GameEntry Get(string appName) => Current.TryGetValue(appName, out var e) ? e : GameEntry.Empty;

    /// <summary>Atomically transforms one entry and persists the file. Returns the new entry.</summary>
    Task<GameEntry> UpdateAsync(string appName, Func<GameEntry, GameEntry> change);
}

// ============================== EGL ==============================

/// <summary>Where EGL keeps its data. Tests point this at a temp tree.</summary>
public sealed record EglEnvironment(string ProgramData)
{
    public static EglEnvironment ForMachine() =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    public string ManifestsDir => Path.Combine(ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
    public string LauncherInstalledDat => Path.Combine(ProgramData, "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
}

/// <summary>A parsed EGL install manifest (.item) plus where it came from.</summary>
public sealed record EglItem(
    string ItemPath,
    string InstallationGuid,
    string InstallLocation,
    string Version,
    long InstallSizeBytes,
    bool IsIncomplete,
    bool NeedsValidation,
    string AppName = "",
    string? DisplayName = null);

/// <summary>One install EGL (or a quick scan) knows about, before Glider joins it with its library.</summary>
public sealed record EglFoundInstall(
    string Id,
    CandidateSource Source,
    string AppName,
    string? DisplayName,
    string InstallPath,
    string? Version,
    long? SizeBytes,
    bool FolderExists,
    /// <summary>The folder holds the game (profile probe, or EGL metadata + content).</summary>
    bool LooksInstalled,
    /// <summary>EGL is working on it right now.</summary>
    bool Busy,
    string? InstallationGuid);

public sealed record EglScanResult(
    bool LauncherInstalled,
    bool LauncherRunning,
    IReadOnlyList<EglFoundInstall> Installs,
    DateTimeOffset? At)
{
    public static EglScanResult Empty { get; } = new(false, false, [], null);
}

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

public sealed record DriftResult(DriftKind Kind, string? EglVersion, string? GliderVersion, EglItem? Egl);

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
    string? ExistingInstallationGuid,
    /// <summary>EGL DisplayName for a new .item (an existing one keeps EGL's).</summary>
    string? Title = null);

public interface IEglService
{
    /// <summary>Fast scan (target &lt;20 ms): EGL presence, every game .item (DLC excluded, stale ones included), running/busy flags.</summary>
    Task<EglScanResult> ScanAsync(CancellationToken ct);

    /// <summary>Folders under well-known paths on the given drives that hold a game EGL doesn't list (no recursive walks).</summary>
    IReadOnlyList<EglFoundInstall> QuickScan(IReadOnlyList<string> driveRoots, CancellationToken ct) => [];

    bool IsLauncherRunning();

    /// <summary>True if EGL is installing/updating/verifying the game at this path right now.</summary>
    bool IsBusyWith(string installPath);

    /// <summary>Completes when EGL is not busy with the path; reports the blocking reason while waiting.</summary>
    Task WaitUntilNotBusyAsync(string installPath, Action<string?> onBlocked, CancellationToken ct);

    /// <summary>EGL's manifest for an install location (case/separator-insensitive); any app when <paramref name="appName"/> is null.</summary>
    EglItem? FindByLocation(string installPath, string? appName = null);

    DriftResult CheckDrift(InstallRecord record);

    /// <summary>Back up then atomically write the .item (+ .egstore manifest/.mancpn) like Legendary's egl_export.</summary>
    Task<string> WriteBackAsync(EglWriteBack data, CancellationToken ct);

    /// <summary>Update EGL's manifest after a move (sync on) — same backup/atomic guarantees.</summary>
    Task UpdateLocationAsync(string appName, string installationGuid, string newInstallPath, CancellationToken ct);

    /// <summary>The AppName EGL's <c>.egstore\*.mancpn</c> in the folder declares, or null.</summary>
    string? DetectAppName(string folder) => null;
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

// ============================== Settings ==============================

public interface ISettingsStore
{
    Settings Current { get; }
    event Action<Settings>? Changed;
    Task<Settings> UpdateAsync(Settings settings);
}

// ============================== Jobs (writer lane) ==============================

/// <summary>Per-job choices that change what Legendary is asked to do.</summary>
public sealed record JobOptions(
    /// <summary>Install tags; null = defaults (<c>--skip-sdl</c>).</summary>
    IReadOnlyList<string>? Components = null,
    /// <summary>Import: verify afterwards (Auto decides from EGL's manifest).</summary>
    VerifyMode? Verify = null,
    /// <summary>DLC jobs: the base game they install into.</summary>
    string? BaseAppName = null);

public sealed record JobRequest(
    string AppName,
    JobKind Kind,
    /// <summary>Install: base path. Move: destination base. Import: game folder.</summary>
    string? TargetPath,
    UninstallMode? UninstallMode,
    string? FromVersion,
    string? ToVersion,
    string? Title = null,
    JobOptions? Options = null,
    /// <summary>Put the job at the front of the queue (behind the running one).</summary>
    bool Priority = false);

/// <summary>Writer lane: serial, user-ordered, persistent job queue (state/jobs.json). Survives restarts and crashes.</summary>
public interface IJobQueue
{
    /// <summary>Jobs in run order (unfinished first, in queue order), then finished ones (newest first).</summary>
    IReadOnlyList<JobInfo> Snapshot();
    bool IsBusy { get; }
    /// <summary>The whole queue was paused by the user (pause-all).</summary>
    bool Paused => false;
    event Action? Changed;
    /// <summary>Smoothed progress, throttled to ~10 Hz.</summary>
    event Action<JobProgress>? ProgressChanged;

    /// <summary>Idempotent per (AppName, Kind): a second request returns the unfinished job.</summary>
    JobInfo Enqueue(JobRequest request);
    Task<JobInfo> PauseAsync(string jobId);
    Task<JobInfo> ResumeAsync(string jobId);
    Task<JobInfo> CancelAsync(string jobId);
    void Dismiss(string jobId);
    Task ReorderAsync(string jobId, int toIndex) => Task.CompletedTask;
    Task MoveToTopAsync(string jobId) => ReorderAsync(jobId, 0);
    Task PauseAllAsync() => Task.CompletedTask;
    Task ResumeAllAsync() => Task.CompletedTask;
    void ClearFinished() { }
    JobHistory History(string jobId) => new(jobId, []);

    /// <summary>The request a job was created from (options the JobInfo doesn't carry).</summary>
    JobRequest? Request(string jobId) => null;

    /// <summary>A hold/mode condition changed (a game started or closed): re-evaluate, stopping or restarting the running job.</summary>
    void Reevaluate() { }

    /// <summary>Load persisted jobs; interrupted ones resume automatically, paused ones stay paused.</summary>
    Task RestoreAsync(CancellationToken ct);
}

/// <summary>Callbacks the queue invokes around every job; implemented by the service layer.</summary>
public interface IJobHooks
{
    /// <summary>Gate before (and while) a job runs: EGL busy, game running, auth. Report waits via setBlockedReason.</summary>
    Task BeforeRunAsync(JobInfo job, Action<string?> setBlockedReason, CancellationToken ct);

    /// <summary>After success: update the install record, EGL write-back, notifications. May return the job's outcome.</summary>
    Task OnSucceededAsync(JobInfo job, CancellationToken ct);

    Task OnFailedAsync(JobInfo job, CancellationToken ct);
}
