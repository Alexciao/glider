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
        "Sign-in was cancelled", "The sign-in window was closed before you finished. If Epic showed an error, copy the diagnostic logs so it can be looked into.", null, null);

    public static GliderError InsufficientSpace(long requiredBytes, long freeBytes, string? detail = null) => new(ErrorCode.InsufficientSpace,
        "Not enough disk space",
        $"This needs {Format.Bytes(requiredBytes)} but only {Format.Bytes(freeBytes)} is free. Free up space or choose another drive.",
        FixAction.FreeSpace, detail);

    public static GliderError Network(string? detail = null) => new(ErrorCode.Network,
        "Can't reach Epic", "Check your internet connection, then retry. Progress is kept.", FixAction.Retry, detail);

    public static GliderError CdnUnavailable(string? detail = null) => new(ErrorCode.CdnUnavailable,
        "Download server unavailable", "Epic's download servers didn't respond. Retry, or pick another CDN in Settings.", FixAction.Retry, detail);

    public static GliderError FilesLocked(string? detail = null) => new(ErrorCode.FilesLocked,
        "Game files are in use", "Close Fortnite and any program using its files, then retry.", FixAction.Retry, detail);

    public static GliderError PermissionDenied(string? detail = null) => new(ErrorCode.PermissionDenied,
        "Access denied", "Glider can't write to this folder. Choose a different location.", FixAction.ChooseFolder, detail);

    public static GliderError EglBusy(string? detail = null) => new(ErrorCode.EglBusy,
        "Epic Games Launcher is busy", "Epic Games Launcher is updating Fortnite, so Glider will wait and continue automatically.", FixAction.WaitForEgl, detail);

    public static GliderError EglFileLocked(string? detail = null) => new(ErrorCode.FilesLocked,
        "Epic Games Launcher files are in use",
        "Glider couldn't update Epic Games Launcher's record of Fortnite because the file is in use. Close Epic Games Launcher, then retry.",
        FixAction.Retry, detail);

    public static GliderError EglAccessDenied(string? detail = null) => new(ErrorCode.PermissionDenied,
        "Can't update Epic Games Launcher",
        "Windows didn't let Glider update Epic Games Launcher's record of Fortnite. The game is fine; Epic Games Launcher may ask to verify it.",
        FixAction.OpenLogs, detail);

    public static GliderError EglWriteFailed(string? detail = null) => new(ErrorCode.Unknown,
        "Couldn't update Epic Games Launcher",
        "Glider couldn't update Epic Games Launcher's record of Fortnite. The game is fine; Epic Games Launcher may ask to verify it.",
        FixAction.OpenLogs, detail);

    public static GliderError GameRunning() => new(ErrorCode.GameRunning,
        "Fortnite is running", "Close the game first, then try again.", FixAction.CloseGame, null);

    public static GliderError NotInstalled() => new(ErrorCode.NotInstalled,
        "Fortnite isn't installed", "Install Fortnite or locate an existing install first.", null, null);

    public static GliderError AlreadyInstalled() => new(ErrorCode.AlreadyInstalled,
        "Already installed", "Fortnite is already installed in Glider.", null, null);

    public static GliderError InvalidFolder(string reason) => new(ErrorCode.InvalidFolder,
        "Not a Fortnite folder", reason, FixAction.ChooseFolder, null);

    public static GliderError Corrupted(string? detail = null) => new(ErrorCode.Corrupted,
        "Game files need repair", "Some files are missing or damaged. Repair downloads only what's broken.", FixAction.Repair, detail);

    public static GliderError Busy(string what) => new(ErrorCode.Busy,
        "Another task is running", $"Wait for {what} to finish, or cancel it first.", null, null);

    public static GliderError LegendaryMissing(string? detail = null) => new(ErrorCode.LegendaryMissing,
        "Glider is damaged", "A required component is missing. Reinstall Glider to fix it.", FixAction.OpenLogs, detail);

    public static GliderError LegendaryFailed(string? detail = null) => new(ErrorCode.LegendaryFailed,
        "The task failed", "The operation stopped unexpectedly. Retry; progress is kept.", FixAction.Retry, detail);

    public static GliderError EacSetupFailed(string? detail = null) => new(ErrorCode.EacSetupFailed,
        "Anti-cheat setup failed", "Easy Anti-Cheat must be installed to play. Retry and accept the Windows prompt.", FixAction.Retry, detail);

    public static GliderError LaunchFailed(string? detail = null) => new(ErrorCode.LaunchFailed,
        "Couldn't start Fortnite", "The game didn't start. Retry, or repair the install if it keeps happening.", FixAction.Repair, detail);

    public static GliderError Cancelled() => new(ErrorCode.Cancelled, "Cancelled", "The task was cancelled.", null, null);

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
