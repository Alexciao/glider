namespace Glider.Core.Contracts;

/// <summary>Canonical, human-readable errors. All user-facing copy for failures lives here.</summary>
public static class Errors
{
    public static GliderError Unknown(string? detail = null) => new(ErrorCode.Unknown,
        "Something went wrong", "An unexpected error occurred. Try again, or copy the logs if it keeps happening.", FixAction.Retry, detail);

    public static GliderError InvalidRequest(string detail) => new(ErrorCode.InvalidRequest,
        "Invalid request", "The app sent a request the backend did not understand.", null, detail);

    public static GliderError NotSignedIn(string? detail = null) => new(ErrorCode.NotSignedIn,
        "Sign in required", "Sign in with your Epic Games account to continue.", FixAction.SignIn, detail);

    public static GliderError AuthExpired(string? detail = null) => new(ErrorCode.AuthExpired,
        "Your session expired", "Sign in again to keep downloading and playing.", FixAction.SignIn, detail);

    public static GliderError AuthFailed(string? detail = null) => new(ErrorCode.AuthFailed,
        "Sign-in failed", "Epic didn't accept the sign-in. Try again.", FixAction.SignIn, detail);

    public static GliderError AuthCancelled() => new(ErrorCode.AuthCancelled,
        "Sign-in was canceled", "The sign-in window was closed before you finished. If Epic showed an error, copy the diagnostic logs so it can be looked into.", null, null);

    public static GliderError InsufficientSpace(long requiredBytes, long freeBytes, string? detail = null) => new(ErrorCode.InsufficientSpace,
        "Not enough disk space",
        $"This needs {Format.Bytes(requiredBytes)} but only {Format.Bytes(freeBytes)} is free. Free up space or choose another drive.",
        FixAction.FreeSpace, detail, RequiredBytes: requiredBytes);

    public static GliderError Network(string? detail = null) => new(ErrorCode.Network,
        "Can't reach Epic", "Check your internet connection. Progress is kept.", FixAction.Retry, detail);

    public static GliderError CdnUnavailable(string? detail = null) => new(ErrorCode.CdnUnavailable,
        "Download server unavailable", "Epic's download servers didn't respond. You can choose another download server in Settings.", FixAction.Retry, detail);

    public static GliderError FilesLocked(string? detail = null) => new(ErrorCode.FilesLocked,
        "Game files are in use", "Close the game and any program using its files.", FixAction.Retry, detail);

    public static GliderError PermissionDenied(string? detail = null) => new(ErrorCode.PermissionDenied,
        "Access denied", "Glider can't write to this folder. Choose a different location.", FixAction.ChooseFolder, detail);

    public static GliderError EglBusy(string? detail = null) => new(ErrorCode.EglBusy,
        "Epic Games Launcher is busy", "Epic Games Launcher is working on this game, so Glider will wait and continue automatically.", FixAction.WaitForEgl, detail);

    public static GliderError EglFileLocked(string? detail = null) => new(ErrorCode.FilesLocked,
        "Epic Games Launcher files are in use",
        "Glider couldn't update Epic Games Launcher's record of the game because the file is in use. Close Epic Games Launcher, then try again.",
        FixAction.Retry, detail);

    public static GliderError EglAccessDenied(string? detail = null) => new(ErrorCode.PermissionDenied,
        "Can't update Epic Games Launcher",
        "Windows didn't let Glider update Epic Games Launcher's record of the game. The game is fine; Epic Games Launcher may ask to verify it.",
        FixAction.OpenLogs, detail);

    public static GliderError EglWriteFailed(string? detail = null) => new(ErrorCode.Unknown,
        "Couldn't update Epic Games Launcher",
        "Glider couldn't update Epic Games Launcher's record of the game. The game is fine; Epic Games Launcher may ask to verify it.",
        FixAction.OpenLogs, detail);

    public static GliderError GameRunning(string? title = null, string? appName = null) => new(ErrorCode.GameRunning,
        $"{Name(title)} is running", "Close the game first, then try again.", FixAction.CloseGame, null, appName);

    public static GliderError NotInstalled(string? title = null, string? appName = null) => new(ErrorCode.NotInstalled,
        $"{Name(title)} isn't installed", $"Install {NameLower(title)} or locate an existing install first.", null, null, appName);

    public static GliderError AlreadyInstalled(string? title = null, string? appName = null) => new(ErrorCode.AlreadyInstalled,
        "Already installed", $"{Name(title)} is already installed in Glider.", null, null, appName);

    public static GliderError InvalidFolder(string reason) => new(ErrorCode.InvalidFolder,
        "Not a game folder", reason, FixAction.ChooseFolder, null);

    public static GliderError NotFound(string what) => new(ErrorCode.NotFound,
        "Not found", $"{what} isn't in your library.", null, null);

    public static GliderError NotOwned(string? title = null, string? appName = null) => new(ErrorCode.NotOwned,
        "Not on this account", $"{Name(title)} isn't available on this Epic account.", null, null, appName);

    public static GliderError GameBusy(string what, string? title = null, string? appName = null) => new(ErrorCode.GameBusy,
        $"{Name(title)} is busy", $"Wait for {what} to finish, or cancel it first.", null, null, appName);

    public static GliderError LegendaryLocked(string? detail = null) => new(ErrorCode.LegendaryLocked,
        "Waiting for another install", "Another program is changing installed games right now. Glider retries automatically.", null, detail);

    public static GliderError ThirdPartyRequired(string store, string? title = null, string? appName = null) => new(ErrorCode.ThirdPartyRequired,
        $"Install through {store}", $"{Name(title)} is installed and launched by {store}, not by Epic.", null, null, appName);

    /// <summary>The other store's launcher isn't on this PC, so a game that only it can start (or needs it to run) can't be opened.</summary>
    public static GliderError ThirdPartyLauncherMissing(string store, string? title = null, string? appName = null) => new(ErrorCode.ThirdPartyRequired,
        $"{store} isn't installed",
        $"{Name(title)} is started by {store}, which isn't installed on this PC. Install {store} (and sign in with the account that owns the game), then press Play again.",
        null, null, appName);

    /// <summary>The game is linked to another store's account, and that link hasn't been made (or confirmed) through Glider yet.</summary>
    public static GliderError ActivationRequired(string store, string? title = null, string? appName = null) => new(ErrorCode.ActivationRequired,
        $"Link your {store} account",
        $"{Name(title)} needs your Epic account linked with {store} before it can start. Link it once; Glider asks Epic to unlock the game on {store}.",
        FixAction.Activate, null, appName);

    /// <summary>Epic reports no linked account for the store: the activation page was opened in the browser.</summary>
    public static GliderError AccountNotLinked(string store, string? title = null, string? appName = null) => new(ErrorCode.ActivationRequired,
        $"No {store} account is linked",
        $"Epic doesn't know your {store} account yet. Link it in the page that just opened in your browser, then try again.",
        FixAction.Activate, null, appName);

    public static GliderError ActivationFailed(string store, string? detail = null, string? title = null, string? appName = null) => new(ErrorCode.ActivationRequired,
        $"Couldn't link {store}",
        $"Epic didn't unlock {NameLower(title)} on {store}. Try again in a moment.",
        FixAction.Activate, detail, appName);

    /// <summary>A prerequisite (redistributable) installer failed or was declined; the game may still run, so the user can go on.</summary>
    public static GliderError PrerequisiteFailed(string what, string? detail = null, string? title = null, string? appName = null) => new(ErrorCode.PrerequisiteFailed,
        "Game components didn't install",
        $"{Name(title)} needs {what}, but its installer didn't finish. Try again and accept the Windows prompt, or play anyway if the game starts without it.",
        FixAction.Retry, detail, appName);

    public static GliderError OverlayNotInstalled() => new(ErrorCode.OverlayFailed,
        "No overlay to switch on", "Install the Epic overlay first, then turn it on.", null, null);

    public static GliderError OverlayFailed(string? detail = null) => new(ErrorCode.OverlayFailed,
        "Epic overlay didn't install",
        "The overlay download or setup stopped. Try again; nothing else is affected.",
        FixAction.Retry, detail);

    /// <summary>A contract method whose feature lands in a later phase.</summary>
    public static GliderError NotAvailableYet(string? detail = null) => new(ErrorCode.Unknown,
        "Not available yet", "Not available yet", null, detail);

    public static GliderError Corrupted(string? detail = null) => new(ErrorCode.Corrupted,
        "Game files need repair", "Some files are missing or damaged. Repair downloads only what's broken.", FixAction.Repair, detail);

    public static GliderError Busy(string what) => new(ErrorCode.Busy,
        "Another task is running", $"Wait for {what} to finish, or cancel it first.", null, null);

    public static GliderError LegendaryMissing(string? detail = null) => new(ErrorCode.LegendaryMissing,
        "Glider is damaged", "A required component is missing. Reinstall Glider to fix it.", FixAction.OpenLogs, detail);

    public static GliderError LegendaryFailed(string? detail = null) => new(ErrorCode.LegendaryFailed,
        "The task failed", "The operation stopped unexpectedly. Progress is kept.", FixAction.Retry, detail);

    public static GliderError EacSetupFailed(string? detail = null) => new(ErrorCode.EacSetupFailed,
        "Anti-cheat setup failed", "Easy Anti-Cheat must be installed to play. Try again and accept the Windows prompt.", FixAction.Retry, detail);

    public static GliderError LaunchFailed(string? detail = null, string? title = null, string? appName = null) => new(ErrorCode.LaunchFailed,
        $"Couldn't start {NameLower(title)}", "The game didn't start. Try again, or repair the install if it keeps happening.", FixAction.Repair, detail, appName);

    public static GliderError Cancelled() => new(ErrorCode.Cancelled, "Canceled", "The task was canceled.", null, null);

    private static string Name(string? title) => string.IsNullOrWhiteSpace(title) ? "The game" : title;

    private static string NameLower(string? title) => string.IsNullOrWhiteSpace(title) ? "the game" : title;

    public static GliderError Timeout(string what) => new(ErrorCode.Timeout,
        "Timed out", $"{what} took too long to respond. Check your connection and retry.", FixAction.Retry, null);
}

/// <summary>Formatting helpers shared by error copy and logs (UI does its own formatting).</summary>
public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Bytes(long bytes)
    {
        double v = bytes;
        var u = 0;
        while (Math.Abs(v) >= 1024 && u < Units.Length - 1)
        {
            v /= 1024;
            u++;
        }

        return u == 0
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes} B")
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{v:0.#} {Units[u]}");
    }
}
