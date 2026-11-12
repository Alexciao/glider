using System.Text.Json.Serialization;

// The IPC contract. TypeScript types in frontend/src/ipc/contract.gen.ts are GENERATED from this
// file by tools/Glider.ContractGen (`dotnet run --project tools/Glider.ContractGen`). Never edit
// the generated file by hand; a unit test fails if it is stale.
//
// Conventions:
//  * Properties serialize camelCase; enums serialize as camelCase strings.
//  * Nullable (`?`) properties are always present on the wire, with value null when absent.
//  * Sizes are bytes (long), speeds bytes/second (double), durations seconds (double).
//  * Timestamps are ISO-8601 strings (DateTimeOffset).

namespace Glider.Core.Contracts;

// ===================================== Snapshot =====================================

/// <summary>Everything the UI needs to render, pushed whole on every change (it is small).</summary>
public sealed record AppSnapshot(
    AppInfo App,
    AuthState Auth,
    GameState Game,
    IReadOnlyList<JobInfo> Jobs,
    EglState Egl,
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
    /// <summary>True once background pre-warm (auth refresh, metadata, update + EGL drift check) finished.</summary>
    bool Warm);

[JsonConverter(typeof(CamelEnumConverter<BackdropKind>))]
public enum BackdropKind { Mica, Solid }

[JsonConverter(typeof(CamelEnumConverter<OnboardingStep>))]
public enum OnboardingStep { Welcome, SignIn, ChooseInstall, Done }

public sealed record OnboardingState(bool Completed, OnboardingStep Step);

// ===================================== Auth =====================================

[JsonConverter(typeof(CamelEnumConverter<AuthStatus>))]
public enum AuthStatus { Unknown, SignedOut, SigningIn, SignedIn, Expired }

public sealed record AuthState(AuthStatus Status, Account? Account);

/// <summary>AvatarUrl is null when Epic offers none; the UI then renders initials.</summary>
public sealed record Account(string AccountId, string DisplayName, string? AvatarUrl);

// ===================================== Game =====================================

[JsonConverter(typeof(CamelEnumConverter<InstallStatus>))]
public enum InstallStatus { Unknown, NotInstalled, Installed, Installing, Broken }

[JsonConverter(typeof(CamelEnumConverter<RunState>))]
public enum RunState { Idle, Preparing, InstallingPrerequisites, Launching, Running, Closing }

public sealed record GameState(
    string AppName,
    string Title,
    InstallStatus InstallStatus,
    InstalledGame? Installed,
    /// <summary>Latest version available from Epic (null until known).</summary>
    string? LatestVersion,
    bool UpdateAvailable,
    bool CheckingForUpdates,
    DateTimeOffset? LastUpdateCheck,
    RunState RunState,
    /// <summary>Process start time when Running, for a "playing for 1h 12m" display.</summary>
    DateTimeOffset? RunningSince,
    GameArt? Art,
    /// <summary>Latest manifest sizes when known (used by the install screen before planning).</summary>
    long? DownloadSizeBytes,
    long? InstallSizeBytes);

public sealed record InstalledGame(
    string InstallPath,
    string Version,
    long InstallSizeBytes,
    /// <summary>True if the folder is also registered with the Epic Games Launcher.</summary>
    bool SharedWithEgl,
    /// <summary>Adopted without a full verify; a background verify is recommended.</summary>
    bool NeedsVerification,
    DateTimeOffset? InstalledAt);

/// <summary>Local URLs (https://cache.glider/..., see WebHost.CacheUrl) to art cached on disk; null when not yet fetched.</summary>
public sealed record GameArt(string? HeroUrl, string? TallUrl, string? LogoUrl);

// ===================================== Jobs =====================================

[JsonConverter(typeof(CamelEnumConverter<JobKind>))]
public enum JobKind { Install, Update, Repair, Verify, Move, Uninstall, Import }

[JsonConverter(typeof(CamelEnumConverter<JobStatus>))]
public enum JobStatus
{
    Queued,
    Running,
    Paused,
    /// <summary>Blocked by an external condition (e.g. EGL busy); resumes automatically. See BlockedReason.</summary>
    Waiting,
    Completed,
    Failed,
    Cancelled,
}

[JsonConverter(typeof(CamelEnumConverter<JobPhase>))]
public enum JobPhase { Preparing, Downloading, Verifying, Finalizing, Moving, Removing }

public sealed record JobInfo(
    string Id,
    JobKind Kind,
    JobStatus Status,
    JobProgress? Progress,
    GliderError? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    string? TargetPath,
    string? FromVersion,
    string? ToVersion,
    string? BlockedReason);

/// <summary>Speeds and ETA are EMA-smoothed by the backend; raw values feed the sparkline history.</summary>
public sealed record JobProgress(
    string JobId,
    JobPhase Phase,
    double Percent,
    long DownloadedBytes,
    long TotalDownloadBytes,
    long WrittenBytes,
    long TotalWriteBytes,
    double DownloadBytesPerSecond,
    double DiskBytesPerSecond,
    double RawDownloadBytesPerSecond,
    double? EtaSeconds,
    long ElapsedSeconds);

// ===================================== EGL =====================================

[JsonConverter(typeof(CamelEnumConverter<CandidateSource>))]
public enum CandidateSource { EpicGamesLauncher, Manual }

public sealed record EglState(
    /// <summary>EGL data directory exists (EGL installed on this machine).</summary>
    bool LauncherInstalled,
    bool LauncherRunning,
    /// <summary>EGL is currently installing/updating/verifying the game; Glider blocks writes.</summary>
    bool BusyWithGame,
    IReadOnlyList<InstallCandidate> Candidates,
    /// <summary>Setting "Keep Epic Games Launcher in sync" (mirrors Settings.EglSync for convenience).</summary>
    bool SyncEnabled,
    /// <summary>The current Glider install has an EGL manifest we can keep in sync.</summary>
    bool SyncAvailable,
    string? ManifestPath,
    DateTimeOffset? LastScan);

public sealed record InstallCandidate(
    string Id,
    CandidateSource Source,
    string InstallPath,
    string? Version,
    bool FolderExists,
    /// <summary>FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe exists.</summary>
    bool ExecutableFound,
    /// <summary>Manifest points to a folder that no longer holds the game.</summary>
    bool IsStale,
    long? SizeBytes);

// ===================================== Settings =====================================

[JsonConverter(typeof(CamelEnumConverter<LogLevel>))]
public enum LogLevel { Debug, Info, Warning, Error }

[JsonConverter(typeof(CamelEnumConverter<ThemePreference>))]
public enum ThemePreference { System, Dark, Light }

public sealed record Settings(
    /// <summary>null = auto-tuned (AppInfo.AutoWorkers).</summary>
    int? DownloadWorkers,
    /// <summary>null = auto-tuned (AppInfo.AutoSharedMemoryMb).</summary>
    int? SharedMemoryMb,
    string? PreferredCdn,
    bool AutoUpdate,
    bool AutoUpdateOnlyWhenIdle,
    int UpdateCheckIntervalMinutes,
    bool StartWithWindows,
    bool StartMinimized,
    bool CloseToTray,
    bool MinimizeToTrayWhilePlaying,
    string LaunchArgs,
    LogLevel LogLevel,
    ThemePreference Theme,
    /// <summary>Write version changes back to EGL's manifest so EGL stays happy.</summary>
    bool EglSync,
    bool Notifications,
    string? DefaultInstallBase)
{
    public static Settings Default { get; } = new(
        DownloadWorkers: null,
        SharedMemoryMb: null,
        PreferredCdn: null,
        AutoUpdate: true,
        AutoUpdateOnlyWhenIdle: true,
        UpdateCheckIntervalMinutes: 30,
        StartWithWindows: false,
        StartMinimized: false,
        CloseToTray: true,
        MinimizeToTrayWhilePlaying: true,
        LaunchArgs: "",
        LogLevel: LogLevel.Info,
        Theme: ThemePreference.System,
        EglSync: true,
        Notifications: true,
        DefaultInstallBase: null);
}

// ===================================== Errors =====================================

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
    CdnUnavailable,
    FilesLocked,
    PermissionDenied,
    EglBusy,
    GameRunning,
    NotInstalled,
    AlreadyInstalled,
    InvalidFolder,
    Corrupted,
    Busy,
    LegendaryMissing,
    LegendaryFailed,
    EacSetupFailed,
    LaunchFailed,
    Cancelled,
    Timeout,
}

[JsonConverter(typeof(CamelEnumConverter<FixAction>))]
public enum FixAction { SignIn, FreeSpace, Repair, Retry, ChooseFolder, OpenLogs, CloseGame, WaitForEgl }

/// <summary>A human-readable failure with an optional one-click fix. Detail is technical (for logs/copy).</summary>
public sealed record GliderError(ErrorCode Code, string Title, string Message, FixAction? Fix, string? Detail);

[JsonConverter(typeof(CamelEnumConverter<NoticeLevel>))]
public enum NoticeLevel { Info, Success, Warning, Error }

public sealed record Notice(string Id, NoticeLevel Level, string Title, string? Message, FixAction? Action);

public sealed record WindowInfo(bool Maximized, bool Focused);

[JsonConverter(typeof(CamelEnumConverter<CaptionState>))]
public enum CaptionState { Hover, Leave, Down }

/// <summary>Native caption overlay (Snap Layouts) hover/press on an HTML caption button; Button is "max".</summary>
public sealed record CaptionEvent(string Button, CaptionState State);

// ===================================== Requests / results =====================================

public sealed record InstallPlanRequest(string? BasePath);

public sealed record InstallPlan(
    /// <summary>Parent folder chosen by the user, e.g. D:\Games.</summary>
    string BasePath,
    /// <summary>Folder the game will live in, e.g. D:\Games\Fortnite.</summary>
    string GamePath,
    string Version,
    long DownloadSizeBytes,
    long InstallSizeBytes,
    long FreeBytes,
    bool HasEnoughSpace,
    /// <summary>A valid existing install was found at GamePath; offer adoption instead.</summary>
    bool ExistingInstallFound);

public sealed record InstallRequest(string BasePath);

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

/// <summary>Adopt an existing install: by EGL candidate id, or a user-located folder path.</summary>
public sealed record AdoptRequest(string? CandidateId, string? Path, VerifyMode Verify);

public sealed record AdoptResult(GameState Game, JobInfo? VerifyJob);

public sealed record ValidateFolderRequest(string Path);

public sealed record FolderValidation(bool Valid, string? GamePath, string? Version, string? Reason);

public sealed record MoveRequest(string DestinationBase);

[JsonConverter(typeof(CamelEnumConverter<UninstallMode>))]
public enum UninstallMode
{
    /// <summary>Forget the install in Glider only; files and EGL untouched.</summary>
    GliderOnly,
    DeleteFiles,
}

public sealed record UninstallRequest(UninstallMode Mode);

public sealed record JobRef(string JobId);

public sealed record PathRequest(string Path);

public sealed record UrlRequest(string Url);

public sealed record PickFolderRequest(string? InitialPath, string? Title);

public sealed record PickFolderResult(string? Path);

public sealed record UpdateCheckResult(bool UpdateAvailable, string? InstalledVersion, string? LatestVersion);

public sealed record LogsRequest(int MaxLines);

public sealed record LogsResult(string Text, string LogFile);

public sealed record LaunchResult(bool Started, bool EacInstalled);

public sealed record LicenseInfo(string Name, string Version, string License, string Url, string? Text);

public sealed record LicensesResult(IReadOnlyList<LicenseInfo> Items);
