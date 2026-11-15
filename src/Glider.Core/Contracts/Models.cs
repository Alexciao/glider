using System.Text.Json.Serialization;

// The IPC contract, v2 (whole Epic library). TypeScript types in frontend/src/ipc/contract.gen.ts are GENERATED
// from this file by tools/Glider.ContractGen (`dotnet run --project tools/Glider.ContractGen`). Never edit the
// generated file by hand; a unit test fails if it is stale. Design notes: docs/v0.2-design.md.
//
// Conventions:
//  * Properties serialize camelCase; enums serialize as camelCase strings.
//  * Nullable (`?`) properties are always present on the wire, with value null when absent.
//  * Sizes are bytes (long), speeds bytes/second (double), durations seconds (double unless noted).
//  * Timestamps are ISO-8601 strings (DateTimeOffset).
//  * Every per-game request carries AppName (Legendary's app_name, the stable key everywhere).

namespace Glider.Core.Contracts;

// ===================================== Snapshot =====================================

/// <summary>
/// Small, app-wide state pushed whole (coalesced) on every change. Per-game data is NOT in here: it travels as
/// <see cref="LibraryDelta"/> events and <c>library.get</c>, so the snapshot stays a few KB with 500+ games.
/// </summary>
public sealed record AppSnapshot(
    AppInfo App,
    AuthState Auth,
    Connectivity Connectivity,
    LibraryStatus Library,
    QueueState Queue,
    IReadOnlyList<RunningGame> Sessions,
    StorageState Storage,
    EglState Egl,
    OverlayState Overlay,
    IReadOnlyList<Collection> Collections,
    Settings Settings,
    OnboardingState Onboarding);

public sealed record AppInfo(
    string Version,
    string LegendaryVersion,
    bool IsWindows11,
    BackdropKind Backdrop,
    string DataDirectory,
    string LogDirectory,
    int CpuCount,
    long TotalMemoryBytes,
    /// <summary>Auto-tuned defaults used when Settings.DownloadWorkers / SharedMemoryMb are null.</summary>
    int AutoWorkers,
    int AutoSharedMemoryMb,
    /// <summary>True once background pre-warm (auth refresh, library sync, update + EGL drift checks) finished.</summary>
    bool Warm,
    /// <summary>Set once when this run migrated older Glider data (e.g. "0.1"); null otherwise.</summary>
    string? MigratedFrom);

[JsonConverter(typeof(CamelEnumConverter<BackdropKind>))]
public enum BackdropKind { Mica, Solid }

[JsonConverter(typeof(CamelEnumConverter<Connectivity>))]
public enum Connectivity { Unknown, Online, Offline }

// ===================================== Onboarding =====================================

[JsonConverter(typeof(CamelEnumConverter<OnboardingStep>))]
public enum OnboardingStep { Welcome, SignIn, Scan, Folders, Mode, Done }

/// <summary>Persisted so the flow resumes where it was if the app closes midway; re-runnable from Settings.</summary>
public sealed record OnboardingState(bool Completed, OnboardingStep Step, IReadOnlyList<OnboardingStep> Skipped);

public sealed record OnboardingStepRequest(OnboardingStep Step, bool Skipped);

// ===================================== Auth =====================================

[JsonConverter(typeof(CamelEnumConverter<AuthStatus>))]
public enum AuthStatus { Unknown, SignedOut, SigningIn, SignedIn, Expired }

public sealed record AuthState(AuthStatus Status, Account? Account);

/// <summary>AvatarUrl is null when Epic offers none; the UI then renders initials.</summary>
public sealed record Account(string AccountId, string DisplayName, string? AvatarUrl);

// ===================================== Library =====================================

public sealed record LibraryStatus(
    /// <summary>Monotonic revision of the game list; every <see cref="LibraryDelta"/> bumps it by exactly one.</summary>
    long Revision,
    int GameCount,
    /// <summary>A background sync with Epic is running (the UI never waits for it).</summary>
    bool Syncing,
    DateTimeOffset? LastSyncedAt,
    /// <summary>Last sync failure, shown quietly; the cached library stays usable.</summary>
    GliderError? SyncError,
    /// <summary>Installed games with an update available (badge on Library/Home).</summary>
    int UpdatesAvailable);

/// <summary>
/// Pushed as the <c>library</c> event and returned by <c>library.get</c> (with Reset = true and every game).
/// Apply in order; if Revision != previous + 1, call <c>library.get</c> again.
/// </summary>
public sealed record LibraryDelta(long Revision, bool Reset, IReadOnlyList<GameSummary> Upserts, IReadOnlyList<string> Removes);

[JsonConverter(typeof(CamelEnumConverter<GameKind>))]
public enum GameKind
{
    Game,
    /// <summary>Content DLC of a base game (installable or entitlement-only).</summary>
    Dlc,
    /// <summary>A DLC that is launched on its own (Legendary category addons/launchable).</summary>
    LaunchableAddon,
}

[JsonConverter(typeof(CamelEnumConverter<InstallStatus>))]
public enum InstallStatus
{
    NotInstalled,
    Installed,
    /// <summary>Installed but files are missing or damaged (needs repair).</summary>
    Broken,
    /// <summary>Owned but has nothing to install through Epic (third-party managed, or entitlement-only DLC).</summary>
    NotInstallable,
}

[JsonConverter(typeof(CamelEnumConverter<ThirdPartyStore>))]
public enum ThirdPartyStore { None, Ubisoft, Ea, Other }

[JsonConverter(typeof(CamelEnumConverter<SaveSyncStatus>))]
public enum SaveSyncStatus
{
    /// <summary>The game has no Epic cloud saves.</summary>
    NotSupported,
    /// <summary>Supported but switched off for this game (or globally).</summary>
    Disabled,
    /// <summary>Supported; not compared yet (e.g. not installed, or never synced).</summary>
    Unknown,
    InSync,
    LocalNewer,
    CloudNewer,
    /// <summary>Both sides changed since the last sync: the user must choose (never resolved silently).</summary>
    Conflict,
    Syncing,
    Error,
}

[JsonConverter(typeof(CamelEnumConverter<AutoUpdatePolicy>))]
public enum AutoUpdatePolicy
{
    /// <summary>Per-game only: follow Settings.AutoUpdate.</summary>
    Default,
    Always,
    Ask,
    Never,
}

/// <summary>
/// Everything the library grid, list, filters, sort, search, tray and command palette need for one game or DLC.
/// Kept compact (it is sent for every owned item). Detail-page data comes from <c>game.getDetail</c>.
/// </summary>
public sealed record GameSummary(
    string AppName,
    string Title,
    GameKind Kind,
    /// <summary>For DLC and add-ons: the base game's AppName.</summary>
    string? BaseAppName,
    string Namespace,
    string CatalogItemId,
    string? Developer,
    InstallStatus InstallStatus,
    string? InstalledVersion,
    /// <summary>Latest version available from Epic (null until known / not installable).</summary>
    string? LatestVersion,
    bool UpdateAvailable,
    string? InstallPath,
    /// <summary>Manifest install size (installed: of the installed tags; otherwise the full game) when known.</summary>
    long? InstallSizeBytes,
    /// <summary>Measured bytes on disk for an installed game; null while unmeasured.</summary>
    long? InstalledSizeBytes,
    /// <summary>Bytes to download next: update delta when an update is pending, full download when not installed.</summary>
    long? DownloadSizeBytes,
    bool CloudSaves,
    SaveSyncStatus SaveStatus,
    bool CanRunOffline,
    ThirdPartyStore ThirdParty,
    /// <summary>Display name of the third-party launcher ("Ubisoft Connect", "EA app") when ThirdParty != None.</summary>
    string? ThirdPartyName,
    /// <summary>Owned DLC count (base games only).</summary>
    int DlcCount,
    bool Favorite,
    bool Hidden,
    /// <summary>Purchase/claim date (Epic grant date), else catalog creation date.</summary>
    DateTimeOffset? AddedAt,
    DateTimeOffset? LastPlayedAt,
    /// <summary>Local playtime under Glider, seconds.</summary>
    long PlaytimeSeconds,
    GameArt Art,
    RunState RunState,
    /// <summary>The queued/running/paused job for this game, if any.</summary>
    string? ActiveJobId,
    JobKind? ActiveJobKind,
    /// <summary>The install folder is also registered with the Epic Games Launcher.</summary>
    bool SharedWithEgl,
    /// <summary>Adopted without a full verify; a background verify is recommended.</summary>
    bool NeedsVerification);

/// <summary>
/// Art URLs served lazily by the host (https://art.glider/...): fetched from Epic on first request, resized and cached
/// on disk. Null when the catalog has no such image. Placeholder is a tiny inline image (data: URI) or a CSS color for
/// blur-up loading.
/// </summary>
public sealed record GameArt(string? Tall, string? TallThumb, string? Wide, string? Logo, string? Placeholder);

public sealed record GameDetail(
    GameSummary Summary,
    string? Description,
    string? Publisher,
    DateTimeOffset? ReleaseDate,
    /// <summary>AppNames of owned DLC (their summaries are in the library).</summary>
    IReadOnlyList<string> Dlc,
    /// <summary>Selectable install components (Legendary install tags / selective downloads); empty when not supported.</summary>
    IReadOnlyList<InstallComponent> Components,
    IReadOnlyList<Prerequisite> Prerequisites,
    SaveState? Saves,
    GameSettings Settings,
    /// <summary>Launch choices declared by the game (extraLaunchOption_* in Epic metadata).</summary>
    IReadOnlyList<LaunchOption> LaunchOptions,
    ShortcutState Shortcuts,
    /// <summary>Per-game quirks applied by Glider (e.g. "Easy Anti-Cheat", "Launches through Ubisoft Connect").</summary>
    IReadOnlyList<string> Quirks,
    AntiCheat AntiCheat,
    /// <summary>Executable relative to the install folder (from installed.json), when installed.</summary>
    string? Executable,
    DateTimeOffset? InstalledAt,
    /// <summary>The EGL manifest kept in sync for this install, when there is one.</summary>
    string? EglManifestPath,
    /// <summary>Third-party store state (launcher present, account linked) for Ubisoft Connect / EA app games; null for ordinary Epic games.</summary>
    ThirdPartyStatus? ThirdPartyInfo = null);

/// <summary>
/// What a game that runs through another store's launcher needs. <see cref="ManagedExternally"/> games have no Epic files
/// (the other launcher installs, updates and starts them); the others install from Epic and only need the account linked.
/// </summary>
public sealed record ThirdPartyStatus(
    ThirdPartyStore Store,
    string Name,
    /// <summary>Ubisoft Connect / EA app is installed on this PC (its URL handler is registered).</summary>
    bool LauncherInstalled,
    /// <summary>Glider linked/activated this Epic account with the store (Ubisoft), or the store was opened for the game (EA).</summary>
    bool Activated,
    /// <summary>Play will ask for activation first (<see cref="LaunchOutcome.NeedsActivation"/>).</summary>
    bool NeedsActivation,
    bool ManagedExternally);

[JsonConverter(typeof(CamelEnumConverter<AntiCheat>))]
public enum AntiCheat { None, EasyAntiCheat, BattlEye }

public sealed record InstallComponent(
    /// <summary>Stable id passed back in install requests (an SDL pack id or a raw install tag).</summary>
    string Id,
    string Name,
    string? Description,
    long? SizeBytes,
    /// <summary>Always installed (cannot be deselected).</summary>
    bool Required,
    /// <summary>Selected by default for a new install; for an installed game, currently installed.</summary>
    bool Selected);

public sealed record Prerequisite(string Id, string Name, bool Installed);

public sealed record LaunchOption(string Name, string Args);

public sealed record ShortcutState(bool Desktop, bool StartMenu);

/// <summary>Per-game settings (stored by Glider, never in Legendary's config.ini).</summary>
public sealed record GameSettings(
    string LaunchArgs,
    /// <summary>Executable relative to the install folder, replacing the manifest's launch exe. Null = default.</summary>
    string? ExecutableOverride,
    /// <summary>Absolute working directory override. Null = the executable's folder (Legendary's default).</summary>
    string? WorkingDirectory,
    AutoUpdatePolicy AutoUpdate,
    /// <summary>Per-game cloud-save switch (effective only when Settings.CloudSaves is on).</summary>
    bool CloudSaves,
    LaunchMode LaunchMode,
    /// <summary>Name of the chosen <see cref="LaunchOption"/>, or null for the default.</summary>
    string? LaunchOption)
{
    public static GameSettings Default { get; } = new("", null, null, AutoUpdatePolicy.Default, true, LaunchMode.Auto, null);
}

public sealed record GameSettingsRequest(string AppName, GameSettings Settings);

public sealed record AppRef(string AppName);

public sealed record GameFlagRequest(string AppName, bool Value);

// ===================================== Collections =====================================

public sealed record Collection(string Id, string Name, IReadOnlyList<string> AppNames);

public sealed record CollectionCreateRequest(string Name);

public sealed record CollectionRenameRequest(string Id, string Name);

public sealed record CollectionRef(string Id);

public sealed record CollectionMembershipRequest(string Id, string AppName, bool Member);

// ===================================== Runtime / launch =====================================

[JsonConverter(typeof(CamelEnumConverter<RunState>))]
public enum RunState { Idle, Preparing, SyncingSaves, InstallingPrerequisites, Launching, Running, Closing }

public sealed record RunningGame(
    string AppName,
    string Title,
    RunState State,
    /// <summary>Process start time when Running.</summary>
    DateTimeOffset? Since,
    bool Offline,
    /// <summary>Launched through a third-party launcher: Glider can't see when it exits.</summary>
    bool Untracked);

[JsonConverter(typeof(CamelEnumConverter<LaunchMode>))]
public enum LaunchMode
{
    /// <summary>Online when Epic is reachable; offline automatically when not (if the game supports it).</summary>
    Auto,
    Online,
    Offline,
}

public sealed record LaunchRequest(
    string AppName,
    /// <summary>Null = the game's setting.</summary>
    LaunchMode? Mode,
    /// <summary>Skip the pre-launch cloud-save sync once (e.g. after the user chose "Play without syncing").</summary>
    bool SkipSaveSync);

[JsonConverter(typeof(CamelEnumConverter<LaunchOutcome>))]
public enum LaunchOutcome
{
    Started,
    /// <summary>Cloud and local saves both changed; nothing was launched. Resolve with <c>saves.resolve</c>.</summary>
    SaveConflict,
    /// <summary>Opened the game's third-party launcher (Ubisoft Connect / EA app), which starts the game.</summary>
    ThirdPartyLauncher,
    /// <summary>The game must be linked/activated with its third-party store first (<c>game.activate</c>).</summary>
    NeedsActivation,
}

public sealed record LaunchResult(LaunchOutcome Outcome, SaveConflict? Conflict, bool Offline, string? Message);

// ===================================== Cloud saves =====================================

public sealed record SaveState(
    bool Supported,
    bool Enabled,
    SaveSyncStatus Status,
    /// <summary>Resolved local save folder (null until Legendary has resolved it).</summary>
    string? SavePath,
    DateTimeOffset? LocalModifiedAt,
    DateTimeOffset? CloudModifiedAt,
    DateTimeOffset? LastSyncedAt,
    GliderError? LastError,
    /// <summary>Number of backups kept for this game (taken before every overwrite).</summary>
    int Backups,
    /// <summary>Both sides changed since the last sync (Status = Conflict): what the user must choose between. Null otherwise.</summary>
    SaveConflict? Conflict = null,
    /// <summary>A quiet one-line explanation of the current state ("Offline: your saves will sync the next time you're online."). Null when there is nothing to add.</summary>
    string? Note = null);

public sealed record SaveConflict(
    string AppName,
    string Title,
    DateTimeOffset? LocalModifiedAt,
    DateTimeOffset? CloudModifiedAt,
    long? LocalSizeBytes,
    long? CloudSizeBytes,
    /// <summary>When the two sides last agreed (null if never synced through Glider).</summary>
    DateTimeOffset? LastSyncedAt);

[JsonConverter(typeof(CamelEnumConverter<SyncDirection>))]
public enum SyncDirection { Auto, Upload, Download }

[JsonConverter(typeof(CamelEnumConverter<SaveSide>))]
public enum SaveSide { Local, Cloud }

public sealed record SaveSyncRequest(string AppName, SyncDirection Direction);

public sealed record SaveResolveRequest(string AppName, SaveSide Keep, bool ThenLaunch);

// ===================================== Jobs / queue =====================================

[JsonConverter(typeof(CamelEnumConverter<JobKind>))]
public enum JobKind
{
    Install,
    Update,
    /// <summary>Repair only (re-download bad files from a fresh verify).</summary>
    Repair,
    /// <summary>Verify, then repair what's bad, then verify again.</summary>
    Verify,
    Move,
    Uninstall,
    Import,
    /// <summary>Change installed components / DLC selection of an installed game.</summary>
    Modify,
    /// <summary>Install or update the EOS overlay (AppName = <see cref="JobInfo.OverlayAppName"/>).</summary>
    Overlay,
}

[JsonConverter(typeof(CamelEnumConverter<JobStatus>))]
public enum JobStatus
{
    Queued,
    Running,
    Paused,
    /// <summary>Blocked by an external condition (EGL busy, game running, offline); resumes automatically. See BlockedReason.</summary>
    Waiting,
    Completed,
    Failed,
    Cancelled,
}

/// <summary>Distinct, honest phases (see docs/v0.2-design.md): verifying only reads, downloading is network, installing is disk.</summary>
[JsonConverter(typeof(CamelEnumConverter<JobPhase>))]
public enum JobPhase { Preparing, Verifying, Repairing, Downloading, Installing, Finalizing, Moving, Removing, Importing }

[JsonConverter(typeof(CamelEnumConverter<OutcomeKind>))]
public enum OutcomeKind { Success, NeedsRepair, Failed }

/// <summary>Per-job result, e.g. for each game of a bulk EGL import: imported / needs repair / failed (+ why).</summary>
public sealed record JobOutcome(OutcomeKind Kind, string Message);

public sealed record JobInfo(
    string Id,
    string AppName,
    string Title,
    JobKind Kind,
    JobStatus Status,
    /// <summary>Planned phases in order, for the segmented stepper; the current one is <see cref="Phase"/>.</summary>
    IReadOnlyList<JobPhase> Phases,
    JobPhase? Phase,
    JobProgress? Progress,
    GliderError? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? TargetPath,
    string? FromVersion,
    string? ToVersion,
    string? BlockedReason,
    /// <summary>Verify/repair jobs: what was found and fixed. Null until the first verify finishes.</summary>
    VerifyReport? Verify,
    JobOutcome? Outcome,
    /// <summary>
    /// <c>jobs.cancel</c> is accepted now. False while a step runs that must not be interrupted: a running uninstall
    /// (killing Legendary mid-delete leaves a half-deleted, still-registered game) and a move outside its copy phase.
    /// </summary>
    bool Cancellable = true)
{
    public const string OverlayAppName = "eos-overlay";
}

/// <summary>Files checked, files bad (damaged or missing) at the first verify, files fixed by the repair, and whether the final verify passed.</summary>
public sealed record VerifyReport(int FilesChecked, int FilesBad, int FilesFixed, bool Verified, long BytesToFix);

[JsonConverter(typeof(CamelEnumConverter<DownloadMode>))]
public enum DownloadMode
{
    /// <summary>Lowest CPU/IO/memory priority and fewer workers; the PC stays fully responsive.</summary>
    Background,
    /// <summary>Network near full speed, low disk/CPU priority. Default.</summary>
    Balanced,
    /// <summary>Everything uncapped.</summary>
    Full,
}

[JsonConverter(typeof(CamelEnumConverter<Limiter>))]
public enum Limiter { Unknown, Network, Cdn, Cpu, Disk, SpeedLimit, Mode, Paused }

[JsonConverter(typeof(CamelEnumConverter<WhilePlayingPolicy>))]
public enum WhilePlayingPolicy
{
    /// <summary>Pause the queue while any game runs; resume when the last one closes.</summary>
    Pause,
    /// <summary>Drop to Background mode while any game runs.</summary>
    Throttle,
    Continue,
}

public sealed record QueueState(
    /// <summary>Unfinished jobs in run order (running first), then up to 20 recent finished jobs (newest first).</summary>
    IReadOnlyList<JobInfo> Jobs,
    /// <summary>The whole queue is paused by the user.</summary>
    bool Paused,
    /// <summary>Mode currently in effect (Settings.DownloadMode, or Background while a game runs, or Full while idle).</summary>
    DownloadMode EffectiveMode,
    /// <summary>Why the effective mode differs from the setting, e.g. "Paused while Fortnite is running".</summary>
    string? ModeReason,
    /// <summary>
    /// Why queued downloads aren't starting although nothing is paused by the user: "Paused while Fortnite is running"
    /// (Settings.WhilePlaying = Pause) or "Waiting for an internet connection". Null when nothing holds the queue.
    /// </summary>
    string? HoldReason = null,
    /// <summary>
    /// The same hold as <see cref="HoldReason"/>, structured, so the download card composes its own words ("Waiting for Fortnite to
    /// close") instead of parsing a sentence. Also set for a queue the user paused and for the head job waiting on another task
    /// (EGL busy, saves, a game of the same title). Null when nothing holds the queue.
    /// </summary>
    HoldInfo? Hold = null);

[JsonConverter(typeof(CamelEnumConverter<HoldKind>))]
public enum HoldKind
{
    /// <summary>A game is running and Settings.WhilePlaying is Pause (or the same game blocks this job). <see cref="HoldInfo.Subject"/> is its title.</summary>
    Game,
    /// <summary>No connection; continues on its own when it is back.</summary>
    Offline,
    /// <summary>The Epic Games Launcher is installing or updating the game.</summary>
    Egl,
    /// <summary>The user paused the whole queue.</summary>
    Paused,
    /// <summary>Another task holds the game's files (cloud saves, the overlay…); <see cref="HoldInfo.Subject"/> carries Core's sentence.</summary>
    Other,
}

/// <summary>Why queued work isn't starting: the kind picks the copy, <see cref="Subject"/> names the game where one is involved.</summary>
public sealed record HoldInfo(HoldKind Kind, string? Subject);

/// <summary>
/// Live job progress (event <c>progress</c>, ≤10 Hz). Groups are null when they don't apply to the current phase:
/// verifying fills Verify only (never network figures), downloading fills Network + Disk, installing fills Disk.
/// </summary>
public sealed record JobProgress(
    string JobId,
    string AppName,
    JobPhase Phase,
    /// <summary>Whole-job percent 0..100, monotonic across phases.</summary>
    double Percent,
    /// <summary>Current phase percent 0..100.</summary>
    double PhasePercent,
    double ElapsedSeconds,
    /// <summary>
    /// Seconds until the whole job is done (downloading: the slower of the download and the writes still owed; installing: the
    /// writes still owed; verifying/moving: that step's own estimate). Null while there is no estimate yet.
    /// </summary>
    double? EtaSeconds,
    NetworkStats? Network,
    DiskStats? Disk,
    VerifyStats? Verify,
    int? FilesDone,
    int? FilesTotal,
    DownloadMode Mode,
    /// <summary>What is limiting speed right now.</summary>
    Limiter Limiter,
    /// <summary>One plain-language sentence explaining <see cref="Limiter"/> ("Limited by drive D: (≈8 MB/s). Your connection can do ≈40 MB/s."); null when there is nothing to say.</summary>
    string? LimiterReason = null);

public sealed record NetworkStats(
    long DownloadedBytes,
    long TotalBytes,
    double BytesPerSecond,
    double AverageBytesPerSecond,
    double PeakBytesPerSecond,
    /// <summary>CDN host in use, e.g. "fastly-download.epicgames.com".</summary>
    string? Cdn,
    int? Connections,
    /// <summary>
    /// The most this connection has shown it can deliver: the larger of this job's peak and the CDN probe's rate (the number behind
    /// "Your connection can do ≈40 MB/s"). Null until either is known.
    /// </summary>
    double? CeilingBytesPerSecond = null);

public sealed record DiskStats(
    long WrittenBytes,
    long TotalBytes,
    double BytesPerSecond,
    double AverageBytesPerSecond,
    double PeakBytesPerSecond,
    /// <summary>Final installed size of the game after this job.</summary>
    long? InstallSizeBytes);

public sealed record VerifyStats(
    int FilesChecked,
    int FilesTotal,
    long BytesChecked,
    long BytesTotal,
    double ReadBytesPerSecond,
    int BadFiles,
    int FixedFiles);

/// <summary>One graph sample (≈1/s), seconds since the job started.</summary>
public sealed record JobSample(double T, double NetworkBytesPerSecond, double DiskBytesPerSecond);

public sealed record JobHistory(string JobId, IReadOnlyList<JobSample> Samples);

public sealed record JobRef(string JobId);

public sealed record JobReorderRequest(string JobId, int ToIndex);

// ===================================== Install planning =====================================

public sealed record InstallPlanRequest(
    string AppName,
    string? BasePath,
    /// <summary>Component ids; null = defaults.</summary>
    IReadOnlyList<string>? Components,
    /// <summary>DLC AppNames to include; null = all owned installable DLC.</summary>
    IReadOnlyList<string>? Dlc);

public sealed record InstallPlan(
    string AppName,
    string Title,
    /// <summary>Parent folder, e.g. D:\Games.</summary>
    string BasePath,
    /// <summary>Folder the game will live in, e.g. D:\Games\AlienIsolation (catalog FolderName).</summary>
    string GamePath,
    string Version,
    long DownloadSizeBytes,
    long InstallSizeBytes,
    long FreeBytes,
    bool HasEnoughSpace,
    /// <summary>A valid existing install was found at GamePath; offer importing it instead.</summary>
    bool ExistingInstallFound,
    IReadOnlyList<InstallComponent> Components,
    IReadOnlyList<DlcOption> Dlc,
    IReadOnlyList<Prerequisite> Prerequisites,
    ThirdPartyStore ThirdParty,
    string? ThirdPartyName);

public sealed record DlcOption(string AppName, string Title, long? DownloadSizeBytes, long? InstallSizeBytes, bool Installable, bool Selected);

public sealed record InstallRequest(
    string AppName,
    string BasePath,
    IReadOnlyList<string>? Components,
    IReadOnlyList<string>? Dlc,
    /// <summary>Put the job at the front of the queue.</summary>
    bool Priority);

public sealed record ModifyRequest(string AppName, IReadOnlyList<string> Components, IReadOnlyList<string> Dlc);

public sealed record MoveRequest(string AppName, string DestinationBase);

[JsonConverter(typeof(CamelEnumConverter<UninstallMode>))]
public enum UninstallMode
{
    /// <summary>Forget the install in Glider only; files and EGL untouched.</summary>
    GliderOnly,
    DeleteFiles,
}

public sealed record UninstallRequest(string AppName, UninstallMode Mode);

public sealed record UpdateAllResult(IReadOnlyList<JobInfo> Jobs);

// ===================================== Storage =====================================

public sealed record StorageState(IReadOnlyList<LibraryFolder> Folders, IReadOnlyList<DriveInfoDto> Drives);

public sealed record LibraryFolder(string Path, bool IsDefault, bool Exists, string Drive, long FreeBytes, int GameCount);

public sealed record DriveInfoDto(
    string Root,
    string? Label,
    long FreeBytes,
    long TotalBytes,
    bool IsSsd,
    /// <summary>A plain-language warning for the install picker (hard disk, nearly full, or slow in practice); null when the drive looks fine.</summary>
    string? Hint = null);

// ===================================== EGL =====================================

[JsonConverter(typeof(CamelEnumConverter<CandidateSource>))]
public enum CandidateSource { EpicGamesLauncher, GliderV1, Manual }

[JsonConverter(typeof(CamelEnumConverter<EglInstallStatus>))]
public enum EglInstallStatus
{
    /// <summary>Can be imported with zero re-download.</summary>
    Importable,
    /// <summary>Already in Glider.</summary>
    Imported,
    /// <summary>Manifest points to a folder that no longer holds the game.</summary>
    Stale,
    /// <summary>EGL is installing/updating/verifying it right now; Glider won't touch it.</summary>
    Busy,
    /// <summary>Not owned by the signed-in account (or unknown app).</summary>
    NotOwned,
}

[JsonConverter(typeof(CamelEnumConverter<EglDrift>))]
public enum EglDrift { None, EglNewer, GliderNewer, Unknown }

public sealed record EglState(
    /// <summary>EGL data directory exists (EGL installed on this machine).</summary>
    bool LauncherInstalled,
    bool LauncherRunning,
    bool Scanning,
    /// <summary>Every install found (EGL manifests on every drive, Glider v0.1's record, located folders).</summary>
    IReadOnlyList<EglInstall> Installs,
    /// <summary>AppNames EGL is busy with; Glider blocks writes to those.</summary>
    IReadOnlyList<string> BusyApps,
    /// <summary>Setting "Keep Epic Games Launcher in sync" (mirrors Settings.EglSync).</summary>
    bool SyncEnabled,
    DateTimeOffset? LastScan);

public sealed record EglInstall(
    string Id,
    CandidateSource Source,
    string AppName,
    string Title,
    string InstallPath,
    /// <summary>Drive root, e.g. "D:\".</summary>
    string Drive,
    string? Version,
    long? SizeBytes,
    EglInstallStatus Status,
    EglDrift Drift,
    /// <summary>Why it can't be imported (null when it can).</summary>
    string? Reason,
    /// <summary>Cover art (art.glider URL) when the game is in the library.</summary>
    string? ArtUrl);

public sealed record EglImportRequest(IReadOnlyList<string> Ids, VerifyMode Verify);

public sealed record EglImportResult(IReadOnlyList<JobInfo> Jobs);

[JsonConverter(typeof(CamelEnumConverter<VerifyMode>))]
public enum VerifyMode
{
    /// <summary>Verify only if versions can't be reconciled (default).</summary>
    Auto,
    /// <summary>Import now, verify in the background afterwards.</summary>
    Background,
    /// <summary>Full verify before the install is usable.</summary>
    Full,
}

public sealed record ValidateFolderRequest(string Path);

/// <summary>Result of "Locate another folder…": which game (if any) lives there.</summary>
public sealed record FolderValidation(bool Valid, string? AppName, string? Title, string? GamePath, string? Version, string? Reason);

public sealed record LocateRequest(string Path, VerifyMode Verify);

[JsonConverter(typeof(CamelEnumConverter<ReadinessSeverity>))]
public enum ReadinessSeverity { Ok, Warning, Blocker }

public sealed record ReadinessItem(string? AppName, string Title, ReadinessSeverity Severity, string Message);

/// <summary>"Ready to uninstall EGL?": anything that would still depend on the Epic Games Launcher.</summary>
public sealed record EglReadiness(bool Ready, IReadOnlyList<ReadinessItem> Items);

// ===================================== EOS overlay =====================================

public sealed record OverlayState(
    bool Installed,
    bool Enabled,
    string? Version,
    string? LatestVersion,
    bool UpdateAvailable,
    string? InstallPath,
    /// <summary>An overlay job is queued or running.</summary>
    bool Busy,
    /// <summary>Glider downloaded this overlay itself, so update and remove apply. False when only an overlay installed by Epic's own software exists.</summary>
    bool Managed = false,
    /// <summary>An overlay installed by Epic's launcher / EOS service that can be switched on without downloading anything.</summary>
    string? ExternalPath = null,
    /// <summary>One calm sentence worth showing under the switch (what Glider left alone), else null.</summary>
    string? Note = null);

// ===================================== Store / free games =====================================

[JsonConverter(typeof(CamelEnumConverter<StoreTargetKind>))]
public enum StoreTargetKind
{
    Home,
    Product,
    FreeGames,
    Wishlist,
    /// <summary>Epic's own "library" has no web page; the host opens the Store home.</summary>
    Library,
    /// <summary>A bundle page; <see cref="StoreTarget.Slug"/> is the bundle slug.</summary>
    Bundle,
    Browse,
    /// <summary>Resume where the Store view was last (path only, kept in memory); the Store home when unknown.</summary>
    LastVisited,
}

/// <summary>A typed Store destination; the UI never passes raw URLs to the Store view.</summary>
public sealed record StoreTarget(StoreTargetKind Kind, string? Slug);

/// <summary>Placeholder rectangle (CSS px relative to the WebView2 client area) the native Store view must cover.</summary>
public sealed record StoreBounds(double X, double Y, double Width, double Height);

public sealed record StoreShowRequest(StoreTarget? Target, StoreBounds Bounds);

[JsonConverter(typeof(CamelEnumConverter<StorePhase>))]
public enum StorePhase
{
    /// <summary>No native view exists (never shown, closed after idling, or the host has no Store).</summary>
    Idle,
    Creating,
    Loading,
    Ready,
    /// <summary>Created but not on screen (another tab is open).</summary>
    Hidden,
    /// <summary>Hidden for a while: the page is suspended to save memory (resumes on show).</summary>
    Suspended,
    /// <summary>The web session is missing and could not be restored; offer "Sign in again" / "Open in browser".</summary>
    SignedOut,
    /// <summary>The page could not be loaded (see <see cref="StoreState.Error"/>).</summary>
    Error,
}

/// <summary>
/// The native Store view as the UI should draw around it. <see cref="Url"/> is origin + path only (no query or fragment).
/// <see cref="Created"/>, <see cref="Visible"/>, <see cref="Loading"/>, <see cref="CanGoBack"/>, <see cref="Title"/> are the v2 fields;
/// the rest are additive (defaults make older payloads valid).
/// </summary>
public sealed record StoreState(
    bool Created,
    bool Visible,
    bool Loading,
    bool CanGoBack,
    string? Title,
    StorePhase Phase = StorePhase.Idle,
    string? Url = null,
    /// <summary>Host of <see cref="Url"/>, shown next to a padlock.</summary>
    string? Host = null,
    bool Secure = false,
    bool CanGoForward = false,
    /// <summary>A checkout page was seen since the last library refresh (new purchases/claims will be picked up).</summary>
    bool CheckoutActive = false,
    /// <summary>The native view is hidden under a modal; the placeholder paints <see cref="CoverUrl"/> instead.</summary>
    bool Covered = false,
    string? CoverUrl = null,
    GliderError? Error = null);

/// <summary>Hide the native Store view while an HTML modal is open (a snapshot stands in for it), and bring it back.</summary>
public sealed record StoreCoverRequest(bool Covered);

[JsonConverter(typeof(CamelEnumConverter<FreeGameStatus>))]
public enum FreeGameStatus { FreeNow, Upcoming }

public sealed record FreeGame(
    string Id,
    string Title,
    /// <summary>Landscape art (https://cache.glider/... once cached, else Epic's CDN), sized for a card.</summary>
    string? ImageUrl,
    /// <summary>Product (or bundle) slug for <see cref="StoreTargetKind.Product"/>/<see cref="StoreTargetKind.Bundle"/>; null = open the free games page.</summary>
    string? Slug,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    FreeGameStatus Status,
    /// <summary>Already in the library.</summary>
    bool Owned,
    /// <summary>Portrait art, same sourcing as <see cref="ImageUrl"/>.</summary>
    string? TallImageUrl = null,
    /// <summary>Regular price as Epic formats it for the region ("$13.99").</summary>
    string? RegularPrice = null,
    string? Seller = null,
    /// <summary>The slug is a bundle page (<see cref="StoreTargetKind.Bundle"/>), not a product page.</summary>
    bool Bundle = false);

public sealed record FreeGamesResult(
    IReadOnlyList<FreeGame> Items,
    DateTimeOffset? FetchedAt,
    /// <summary>The data is from the cache and older than a day, or the last fetch failed.</summary>
    bool Stale = false);

// ===================================== Shortcuts / links =====================================

[JsonConverter(typeof(CamelEnumConverter<ShortcutLocation>))]
public enum ShortcutLocation { Desktop, StartMenu }

public sealed record ShortcutRequest(string AppName, ShortcutLocation Location, bool Create);

/// <summary>Host → UI request to show a route (from glider:// links, tray, toasts), e.g. "game/Fortnite", "downloads".</summary>
public sealed record NavigateEvent(string Route);

// ===================================== Settings =====================================

[JsonConverter(typeof(CamelEnumConverter<LogLevel>))]
public enum LogLevel { Debug, Info, Warning, Error }

[JsonConverter(typeof(CamelEnumConverter<ThemePreference>))]
public enum ThemePreference { System, Dark, Light }

public sealed record Settings(
    DownloadMode DownloadMode,
    /// <summary>Speed limit for the whole queue, bytes/s; null = none.</summary>
    long? SpeedLimitBytesPerSecond,
    /// <summary>Go to Full speed while the PC is idle (no input for a few minutes, no game running).</summary>
    bool IdleFullSpeed,
    WhilePlayingPolicy WhilePlaying,
    /// <summary>null = auto-tuned (AppInfo.AutoWorkers).</summary>
    int? DownloadWorkers,
    /// <summary>null = auto-tuned (AppInfo.AutoSharedMemoryMb).</summary>
    int? SharedMemoryMb,
    /// <summary>null = pick the fastest CDN automatically per job.</summary>
    string? PreferredCdn,
    /// <summary>Global auto-update default (Always / Ask / Never; never Default).</summary>
    AutoUpdatePolicy AutoUpdate,
    bool AutoUpdateOnlyWhenIdle,
    int UpdateCheckIntervalMinutes,
    bool StartWithWindows,
    bool StartMinimized,
    bool CloseToTray,
    bool MinimizeToTrayWhilePlaying,
    LogLevel LogLevel,
    ThemePreference Theme,
    /// <summary>Write version changes back to EGL's manifests so EGL stays happy.</summary>
    bool EglSync,
    bool Notifications,
    /// <summary>Default library folder for new installs.</summary>
    string? DefaultInstallBase,
    /// <summary>All library folders (the default one included).</summary>
    IReadOnlyList<string> LibraryFolders,
    /// <summary>Global cloud-save switch.</summary>
    bool CloudSaves,
    /// <summary>Handle com.epicgames.launcher:// links (Store buttons, old EGL shortcuts) in Glider. Off by default.</summary>
    bool HandleEpicLinks)
{
    public static Settings Default { get; } = new(
        DownloadMode: DownloadMode.Balanced,
        SpeedLimitBytesPerSecond: null,
        IdleFullSpeed: true,
        WhilePlaying: WhilePlayingPolicy.Pause,
        DownloadWorkers: null,
        SharedMemoryMb: null,
        PreferredCdn: null,
        AutoUpdate: AutoUpdatePolicy.Always,
        AutoUpdateOnlyWhenIdle: true,
        UpdateCheckIntervalMinutes: 30,
        StartWithWindows: false,
        StartMinimized: false,
        CloseToTray: true,
        MinimizeToTrayWhilePlaying: true,
        LogLevel: LogLevel.Info,
        Theme: ThemePreference.System,
        EglSync: true,
        Notifications: true,
        DefaultInstallBase: null,
        LibraryFolders: [],
        CloudSaves: true,
        HandleEpicLinks: false);
}

// ===================================== Errors / notices / window =====================================

[JsonConverter(typeof(CamelEnumConverter<ErrorCode>))]
public enum ErrorCode
{
    Unknown,
    InvalidRequest,
    NotSignedIn,
    AuthExpired,
    AuthFailed,
    AuthCancelled,
    InsufficientSpace,
    Network,
    Offline,
    CdnUnavailable,
    FilesLocked,
    PermissionDenied,
    EglBusy,
    GameRunning,
    GameBusy,
    NotFound,
    NotOwned,
    NotInstalled,
    AlreadyInstalled,
    InvalidFolder,
    Corrupted,
    Busy,
    LegendaryMissing,
    LegendaryFailed,
    LegendaryLocked,
    EacSetupFailed,
    /// <summary>A prerequisite installer failed or was declined. The UI offers Retry (launch again) and "Play anyway" (<c>game.skipPrerequisites</c>, then launch).</summary>
    PrerequisiteFailed,
    LaunchFailed,
    ThirdPartyRequired,
    ActivationRequired,
    SaveConflict,
    SaveSyncFailed,
    OverlayFailed,
    Cancelled,
    Timeout,
    /// <summary>Windows Security (Defender) blocked or stopped legendary.exe, typically a false positive on the PyInstaller build.</summary>
    BlockedByAntivirus,
}

[JsonConverter(typeof(CamelEnumConverter<FixAction>))]
public enum FixAction { SignIn, FreeSpace, Repair, Retry, ChooseFolder, OpenLogs, CloseGame, WaitForEgl, Activate, ResolveSaves, InstallPrerequisites, PlayOffline, Update, OpenSecurity }

/// <summary>A human-readable failure with an optional one-click fix. Detail is technical (for logs/copy).</summary>
public sealed record GliderError(
    ErrorCode Code,
    string Title,
    string Message,
    FixAction? Fix,
    string? Detail,
    string? AppName = null,
    /// <summary>Not enough disk space only: the bytes the task needed, so the UI can compare them with the drive's free space as it is now.</summary>
    long? RequiredBytes = null,
    /// <summary>Not enough disk space only: the drive the task writes to, as "D:".</summary>
    string? Drive = null);

[JsonConverter(typeof(CamelEnumConverter<NoticeLevel>))]
public enum NoticeLevel { Info, Success, Warning, Error }

/// <summary>Transient toast. AppName/JobId let the UI suppress a toast whose subject is already on screen.</summary>
public sealed record Notice(string Id, NoticeLevel Level, string Title, string? Message, FixAction? Action, string? AppName, string? JobId);

public sealed record WindowInfo(bool Maximized, bool Focused, bool Visible);

[JsonConverter(typeof(CamelEnumConverter<CaptionState>))]
public enum CaptionState { Hover, Leave, Down }

/// <summary>Native caption overlay (Snap Layouts) hover/press on an HTML caption button; Button is "max".</summary>
public sealed record CaptionEvent(string Button, CaptionState State);

// ===================================== Misc requests / results =====================================

public sealed record PathRequest(string Path);

public sealed record UrlRequest(string Url);

public sealed record PickFolderRequest(string? InitialPath, string? Title);

public sealed record PickFolderResult(string? Path);

public sealed record LogsRequest(int MaxLines);

public sealed record LogsResult(string Text, string LogFile);

public sealed record LicenseInfo(string Name, string Version, string License, string Url, string? Text);

public sealed record LicensesResult(IReadOnlyList<LicenseInfo> Items);
