using System.Globalization;
using Glider.Core.Contracts;

namespace Glider.Core.Legendary;

internal enum LineClass
{
    /// <summary>Retry spam / idle chatter: kept out of liveness tracking and rate-limited in the log.</summary>
    Noise,
    Normal,
}

/// <summary>
/// Everything worth knowing about one Legendary run, accumulated line by line (span matching, allocating only for the
/// details it keeps). Facts survive arbitrarily long runs, unlike a bounded tail, so a <c>Writing for … failed!</c> from
/// minute 3 still counts after an hour of retry spam. Patterns are the verbatim messages listed in legendary.md §7.
/// </summary>
internal sealed class LegendaryOutputFacts
{
    private const double MiB = 1024 * 1024;
    private const double GiB = 1024 * 1024 * 1024;
    private const int MaxDetail = 400;
    private const string SignedInMarker = "Successfully logged in as \"";

    // ---- auth ----
    public bool NoSavedCredentials { get; private set; }
    public bool CredentialsInvalid { get; private set; }
    public bool LoginHttpFailed { get; private set; }
    public bool LoginFailed { get; private set; }
    public bool CredentialsStillValid { get; private set; }
    public bool UserDataDeleted { get; private set; }
    public string? CorrectiveAction { get; private set; }
    public string? SignedInAs { get; private set; }

    // ---- generic ----
    public bool LockFailed { get; private set; }
    public bool NotInstalled { get; private set; }
    public bool InstallDirMissing { get; private set; }
    public bool NotOwned { get; private set; }
    public bool OutOfDate { get; private set; }
    public bool Aborted { get; private set; }
    public bool PromptHitEof { get; private set; }
    public bool Traceback { get; private set; }
    public bool PyInstallerCrash { get; private set; }
    public bool VersionCheckCrash { get; private set; }
    public bool NetworkException { get; private set; }
    public string? ExceptionLine { get; private set; }
    public string? LastErrorLine { get; private set; }
    public bool VersionPrinted { get; private set; }

    // ---- install / update / repair ----
    public List<string> RequirementFailures { get; } = [];
    public double? FreeGiB { get; private set; }
    public double? RequiredGiB { get; private set; }
    public bool CannotProceed { get; private set; }
    public int? SharedMemorySuggestedMiB { get; private set; }
    public bool InstallFinished { get; private set; }
    public bool NothingToDo { get; private set; }
    public bool InstallFailed { get; private set; }
    public long? InstallSizeBytes { get; private set; }
    public long? DownloadSizeBytes { get; private set; }
    public long? ReusableBytes { get; private set; }
    public long? UnchangedBytes { get; private set; }
    public bool SummaryComplete { get; private set; }
    public bool ResumeFound { get; private set; }
    public int? ResumeSkippedFiles { get; private set; }
    public int WriteFailures { get; private set; }
    public string? FirstWriteFailure { get; private set; }
    public string? FirstFailedFile { get; private set; }
    public bool DiskFull { get; private set; }
    public bool SharingViolation { get; private set; }
    public bool AccessDenied { get; private set; }
    public int ChunkRetries { get; private set; }
    public bool ManifestUnavailable { get; private set; }
    public bool SdlDownloadFailed { get; private set; }
    public bool ManifestMissing { get; private set; }
    public bool DownloadStarted { get; private set; }
    public bool Finalizing { get; private set; }
    public bool SwitchedToRepair { get; private set; }

    // ---- verify ----
    public bool VerifyStarted { get; private set; }
    public bool VerifySucceeded { get; private set; }
    public bool VerifyFailed { get; private set; }
    public int VerifyCorrupted { get; private set; }
    public int VerifyMissing { get; private set; }
    public int VerifyOpenFailures { get; private set; }

    // ---- import / move / uninstall ----
    public bool Imported { get; private set; }
    public bool ImportPathMissing { get; private set; }
    public bool AlreadyInstalled { get; private set; }
    public bool ImportNoFiles { get; private set; }
    public bool ImportExeMissing { get; private set; }
    public bool MoveFinished { get; private set; }
    public bool MoveCrossDrive { get; private set; }
    public bool MoveTargetExists { get; private set; }
    public bool MoveFailed { get; private set; }
    public bool Uninstalled { get; private set; }
    public bool UninstallFailed { get; private set; }

    /// <summary>Phase implied by the lines so far (download/verify progress also moves it; see the runner).</summary>
    public JobPhase Phase { get; internal set; } = JobPhase.Preparing;

    /// <summary>Best human-readable technical detail for an error.</summary>
    public string? Detail => ExceptionLine ?? FirstWriteFailure ?? LastErrorLine;

    public LineClass Observe(ReadOnlySpan<char> line, bool stdout)
    {
        if (stdout)
        {
            return ObserveStdout(line);
        }

        if (line.Length > 1 && line[0] == '[')
        {
            if (line.StartsWith("[PYI-", StringComparison.Ordinal) && line.Contains("Failed to execute script", StringComparison.Ordinal))
            {
                PyInstallerCrash = true;
                return LineClass.Normal;
            }

            var close = line.IndexOf("] ", StringComparison.Ordinal);
            if (close > 0)
            {
                var rest = line[(close + 2)..];
                var colon = rest.IndexOf(": ", StringComparison.Ordinal);
                if (colon is > 0 and < 10)
                {
                    var logger = line[1..close];
                    var level = rest[..colon];
                    var msg = rest[(colon + 2)..];
                    return level switch
                    {
                        "INFO" => OnInfo(msg),
                        "WARNING" => OnWarning(logger, msg),
                        "ERROR" or "CRITICAL" => OnError(msg, line),
                        _ => LineClass.Normal,
                    };
                }
            }
        }

        return OnBareLine(line);
    }

    private LineClass ObserveStdout(ReadOnlySpan<char> line)
    {
        if (line.StartsWith(" ! Failure: ", StringComparison.Ordinal))
        {
            var msg = line[12..];
            RequirementFailures.Add(Cap(msg));
            ParseDiskSpace(msg);
        }
        else if (line.StartsWith("legendary version \"", StringComparison.Ordinal))
        {
            VersionPrinted = true;
        }
        else if (line.StartsWith("Please enter", StringComparison.Ordinal) || line.Contains("[Y/n]", StringComparison.Ordinal) ||
                 line.Contains("[y/N]", StringComparison.Ordinal))
        {
            // An interactive prompt we didn't answer; stdin is closed, so the next line is an EOFError traceback.
            PromptHitEof = true;
        }

        return LineClass.Normal;
    }

    private LineClass OnInfo(ReadOnlySpan<char> msg)
    {
        if (msg.StartsWith("Finished installation process in ", StringComparison.Ordinal))
        {
            InstallFinished = true;
            Phase = JobPhase.Finalizing;
        }
        else if (msg.StartsWith("Download size is 0", StringComparison.Ordinal))
        {
            NothingToDo = true;
        }
        else if (msg.StartsWith("Installation failed after ", StringComparison.Ordinal))
        {
            InstallFailed = true;
        }
        else if (msg.StartsWith("Install size: ", StringComparison.Ordinal))
        {
            InstallSizeBytes = MiBValue(msg[14..]);
        }
        else if (msg.StartsWith("Download size: ", StringComparison.Ordinal))
        {
            DownloadSizeBytes = MiBValue(msg[15..]);
        }
        else if (msg.StartsWith("Reusable size: ", StringComparison.Ordinal))
        {
            // "Reusable size: 0.00 MiB (chunks) / 0.00 MiB (unchanged / skipped)"
            var rest = msg[15..];
            ReusableBytes = MiBValue(rest);
            var slash = rest.IndexOf(" / ", StringComparison.Ordinal);
            if (slash > 0)
            {
                UnchangedBytes = MiBValue(rest[(slash + 3)..]);
            }

            SummaryComplete = true;
        }
        else if (msg.StartsWith("Found previously interrupted download", StringComparison.Ordinal))
        {
            ResumeFound = true;
        }
        else if (msg.StartsWith("Skipping ", StringComparison.Ordinal) && msg.EndsWith(" files based on resume data.", StringComparison.Ordinal))
        {
            ResumeSkippedFiles = IntValue(msg[9..]);
        }
        else if (msg.StartsWith("Download Manager running with process-id", StringComparison.Ordinal) ||
                 msg.StartsWith("Starting download workers", StringComparison.Ordinal))
        {
            DownloadStarted = true;
            Phase = JobPhase.Downloading;
        }
        else if (msg.StartsWith("Waiting for installation to finish", StringComparison.Ordinal))
        {
            Finalizing = true;
            Phase = JobPhase.Finalizing;
        }
        else if (msg.StartsWith("Verifying \"", StringComparison.Ordinal))
        {
            VerifyStarted = true;
            Phase = JobPhase.Verifying;
        }
        else if (msg.StartsWith("Verification finished successfully", StringComparison.Ordinal))
        {
            VerifySucceeded = true;
        }
        else if (msg.StartsWith(SignedInMarker, StringComparison.Ordinal))
        {
            SignedInAs = Quoted(msg[SignedInMarker.Length..]);
        }
        else if (msg.StartsWith("Stored credentials are still valid", StringComparison.Ordinal))
        {
            CredentialsStillValid = true;
        }
        else if (msg.StartsWith("User data deleted", StringComparison.Ordinal))
        {
            UserDataDeleted = true;
        }
        else if (msg.EndsWith("\" has been imported.", StringComparison.Ordinal))
        {
            Imported = true;
        }
        else if (msg.SequenceEqual("Finished."))
        {
            MoveFinished = true;
        }
        else if (msg.StartsWith("Game has been uninstalled", StringComparison.Ordinal))
        {
            Uninstalled = true;
        }
        else if (msg.StartsWith("Command was aborted via KeyboardInterrupt", StringComparison.Ordinal))
        {
            Aborted = true;
        }
        else if (msg.StartsWith("Game needs to be verified before updating, switching to repair mode", StringComparison.Ordinal))
        {
            SwitchedToRepair = true;
        }
        else if (msg.StartsWith("Removing \"", StringComparison.Ordinal))
        {
            Phase = JobPhase.Removing;
        }

        return LineClass.Normal;
    }

    private LineClass OnWarning(ReadOnlySpan<char> logger, ReadOnlySpan<char> msg)
    {
        if (msg.StartsWith("Chunk download for ", StringComparison.Ordinal) || msg.StartsWith("Job for ", StringComparison.Ordinal))
        {
            ChunkRetries++;
            if (LooksLikeNetwork(msg))
            {
                NetworkException = true;
            }

            return LineClass.Noise;
        }

        if (msg.StartsWith("Writer queue empty", StringComparison.Ordinal) || msg.StartsWith("Immediate exit requested", StringComparison.Ordinal))
        {
            return LineClass.Noise;
        }

        if (msg.StartsWith("Something in writing a file failed: ", StringComparison.Ordinal) ||
            (logger.SequenceEqual("FileWorker") && msg.StartsWith("Job ", StringComparison.Ordinal)))
        {
            // Worker-side detail (carries the OS error); the manager follows up with the countable "Writing for … failed!".
            FirstWriteFailure ??= Cap(msg);
            NoteOsError(msg);
        }
        else if (msg.StartsWith("Downloading SDL data failed", StringComparison.Ordinal))
        {
            SdlDownloadFailed = true;
            NoteOsError(msg);
        }
        else if (msg.StartsWith("Unable to download manifest from ", StringComparison.Ordinal))
        {
            // per-CDN warning; only fatal if every CDN fails (ValueError traceback)
        }
        else if (msg.StartsWith("The following exception occurred while waiting for the downloader", StringComparison.Ordinal))
        {
            InstallFailed = true;
            ExceptionLine ??= Cap(msg);
            if (LooksLikeNetwork(msg))
            {
                NetworkException = true;
            }

            NoteOsError(msg);
        }
        else if (msg.StartsWith("Removing game failed: ", StringComparison.Ordinal))
        {
            UninstallFailed = true;
            LastErrorLine = Cap(msg);
            NoteOsError(msg);
        }

        return LineClass.Normal;
    }

    private LineClass OnError(ReadOnlySpan<char> msg, ReadOnlySpan<char> line)
    {
        if (msg.StartsWith("Download for ", StringComparison.Ordinal) && msg.EndsWith("retrying...", StringComparison.Ordinal))
        {
            ChunkRetries++;
            return LineClass.Noise;
        }

        if (msg.StartsWith("Job for ", StringComparison.Ordinal) && msg.Contains("fetching next one", StringComparison.Ordinal))
        {
            ChunkRetries++;
            if (LooksLikeNetwork(msg))
            {
                NetworkException = true;
            }

            return LineClass.Noise;
        }

        LastErrorLine = Cap(line);

        if (msg.StartsWith("Writing for ", StringComparison.Ordinal) && msg.EndsWith(" failed!", StringComparison.Ordinal))
        {
            FirstFailedFile ??= new string(msg[12..^8]);
            NoteWriteFailure(msg);
        }
        else if (msg.StartsWith("Removing file failed", StringComparison.Ordinal) || msg.StartsWith("Renaming file failed", StringComparison.Ordinal) ||
                 msg.StartsWith("Creating symlink failed", StringComparison.Ordinal))
        {
            NoteWriteFailure(msg);
        }
        else if (msg.StartsWith("Stored credentials are no longer valid", StringComparison.Ordinal) ||
                 msg.StartsWith("Stored credentials were found but were no longer valid", StringComparison.Ordinal))
        {
            CredentialsInvalid = true;
        }
        else if (msg.StartsWith("HTTP request for login failed", StringComparison.Ordinal))
        {
            LoginHttpFailed = true;
        }
        else if (msg.StartsWith("Login failed", StringComparison.Ordinal) || msg.StartsWith("Log in failed", StringComparison.Ordinal) ||
                 msg.StartsWith("Login attempt failed", StringComparison.Ordinal) || msg.StartsWith("Logging in failed with", StringComparison.Ordinal) ||
                 msg.StartsWith("WebView login attempt failed", StringComparison.Ordinal) || msg.StartsWith("Login to EGS API failed", StringComparison.Ordinal))
        {
            LoginFailed = true;
            if (msg.Contains("corrective_action", StringComparison.Ordinal))
            {
                CorrectiveAction ??= Cap(msg);
            }

            if (LooksLikeNetwork(msg))
            {
                LoginHttpFailed = true;
            }
        }
        else if (msg.Contains("open the following URL to take action", StringComparison.Ordinal))
        {
            CorrectiveAction = Cap(msg);
        }
        else if (msg.StartsWith("Failed to acquire installed data lock", StringComparison.Ordinal))
        {
            LockFailed = true;
        }
        else if (msg.StartsWith("Installation cannot proceed", StringComparison.Ordinal))
        {
            CannotProceed = true;
        }
        else if (msg.Contains("is not currently installed", StringComparison.Ordinal) ||
                 msg.EndsWith("not installed, cannot uninstall!", StringComparison.Ordinal) ||
                 msg.StartsWith("No installed game found for", StringComparison.Ordinal) ||
                 msg.EndsWith("but app not installed!", StringComparison.Ordinal) ||
                 msg.EndsWith("is not installed!", StringComparison.Ordinal) ||
                 (msg.StartsWith("Game \"", StringComparison.Ordinal) && msg.EndsWith("\" is not installed", StringComparison.Ordinal)))
        {
            NotInstalled = true;
        }
        else if (msg.Contains("appears to be deleted, cannot launch", StringComparison.Ordinal) ||
                 (msg.StartsWith("Install path \"", StringComparison.Ordinal) && msg.Contains("does not exist", StringComparison.Ordinal)))
        {
            InstallDirMissing = true;
        }
        else if (msg.StartsWith("Game is out of date", StringComparison.Ordinal))
        {
            OutOfDate = true;
        }
        else if ((msg.StartsWith("Could not find \"", StringComparison.Ordinal) && msg.Contains("list of available games", StringComparison.Ordinal)) ||
                 msg.StartsWith("Did not find game \"", StringComparison.Ordinal) ||
                 msg.StartsWith("Legendary is missing game metadata", StringComparison.Ordinal) ||
                 msg.StartsWith("Metadata for \"", StringComparison.Ordinal) ||
                 msg.StartsWith("The selected title has to be installed via a third-party store", StringComparison.Ordinal))
        {
            NotOwned = true;
        }
        else if (msg.StartsWith("File does not match hash: ", StringComparison.Ordinal))
        {
            // counted by the summary line
        }
        else if (msg.StartsWith("Could not verify \"", StringComparison.Ordinal))
        {
            VerifyOpenFailures++;
            NoteOsError(msg);
        }
        else if (msg.StartsWith("Verification failed, ", StringComparison.Ordinal))
        {
            // "Verification failed, N file(s) corrupted, M file(s) are missing."
            VerifyFailed = true;
            VerifyCorrupted = IntValue(msg[21..]) ?? 0;
            var comma = msg[21..].IndexOf(", ", StringComparison.Ordinal);
            if (comma > 0)
            {
                VerifyMissing = IntValue(msg[(21 + comma + 2)..]) ?? 0;
            }
        }
        else if (msg.StartsWith("Manifest appears to be missing", StringComparison.Ordinal) ||
                 msg.StartsWith("No manifest could be loaded", StringComparison.Ordinal) ||
                 msg.StartsWith("To repair a game with a missing manifest", StringComparison.Ordinal))
        {
            ManifestMissing = true;
        }
        else if (msg.StartsWith("Specified path \"", StringComparison.Ordinal) && msg.EndsWith("does not exist!", StringComparison.Ordinal))
        {
            ImportPathMissing = true;
        }
        else if (msg.StartsWith("Game is already installed", StringComparison.Ordinal))
        {
            AlreadyInstalled = true;
        }
        else if (msg.StartsWith("No files belonging to ", StringComparison.Ordinal))
        {
            ImportNoFiles = true;
        }
        else if (msg.StartsWith("Game executable could not be found at", StringComparison.Ordinal))
        {
            ImportExeMissing = true;
        }
        else if (msg.StartsWith("Moving to a different drive is not supported", StringComparison.Ordinal))
        {
            MoveCrossDrive = true;
        }
        else if (msg.StartsWith("The target path already contains a folder called", StringComparison.Ordinal))
        {
            MoveTargetExists = true;
        }
        else if (msg.StartsWith("Moving failed with unknown error", StringComparison.Ordinal))
        {
            MoveFailed = true;
            NoteOsError(msg);
        }
        else if (msg.StartsWith("No base URLs found", StringComparison.Ordinal) || msg.StartsWith("No manifest URLs returned", StringComparison.Ordinal))
        {
            ManifestUnavailable = true;
        }
        else if (msg.StartsWith("Unable to get SDL data for", StringComparison.Ordinal))
        {
            // SDL unavailable entirely: Legendary installs every file (bigger, but complete). Not fatal.
        }
        else if (msg.StartsWith("Failed to run uninstaller", StringComparison.Ordinal))
        {
            // we always pass --skip-uninstaller; ignore
        }

        return LineClass.Normal;
    }

    private LineClass OnBareLine(ReadOnlySpan<char> line)
    {
        if (line.StartsWith("Traceback (most recent call last):", StringComparison.Ordinal))
        {
            Traceback = true;
            return LineClass.Normal;
        }

        if (!Traceback)
        {
            return LineClass.Normal;
        }

        // Frames ("  File ...") and source lines are indented; the exception line is not.
        if (line[0] == ' ' || line[0] == '\t')
        {
            if (line.Contains("in check_for_updates", StringComparison.Ordinal) || line.Contains("in get_version_information", StringComparison.Ordinal))
            {
                VersionCheckCrash = true;
            }

            return LineClass.Normal;
        }

        if (line.StartsWith("The above exception", StringComparison.Ordinal) || line.StartsWith("During handling", StringComparison.Ordinal))
        {
            return LineClass.Normal;
        }

        ExceptionLine = Cap(line);
        if (line.StartsWith("ValueError: No saved credentials", StringComparison.Ordinal))
        {
            NoSavedCredentials = true;
        }
        else if (line.StartsWith("EOFError", StringComparison.Ordinal))
        {
            PromptHitEof = true;
        }
        else if (line.StartsWith("MemoryError: Current shared memory cache is smaller than required", StringComparison.Ordinal))
        {
            const string marker = "--max-shared-memory ";
            var at = line.LastIndexOf(marker, StringComparison.Ordinal);
            if (at >= 0)
            {
                SharedMemorySuggestedMiB = IntValue(line[(at + marker.Length)..]);
            }
        }
        else if (line.StartsWith("ValueError: Unable to get manifest from any CDN", StringComparison.Ordinal) ||
                 line.Contains("Manifest sha hash mismatch", StringComparison.Ordinal) ||
                 line.Contains("No manifest URLs returned", StringComparison.Ordinal))
        {
            ManifestUnavailable = true;
        }
        else if (line.StartsWith("ValueError: Local manifest is missing", StringComparison.Ordinal))
        {
            ManifestMissing = true;
        }
        else if (line.StartsWith("PermissionError: No write access to", StringComparison.Ordinal))
        {
            AccessDenied = true;
        }

        if (line.Contains("api.legendary.gl", StringComparison.Ordinal))
        {
            VersionCheckCrash = true;
        }

        if (LooksLikeNetwork(line))
        {
            NetworkException = true;
        }

        NoteOsError(line);
        return LineClass.Normal;
    }

    private void NoteWriteFailure(ReadOnlySpan<char> msg)
    {
        WriteFailures++;
        FirstWriteFailure ??= Cap(msg);
        NoteOsError(msg);
    }

    /// <summary>OSError reprs/strs on Windows: errno 28 / WinError 112 = disk full; WinError 32/33 = sharing violation; 13/5 = access.</summary>
    private void NoteOsError(ReadOnlySpan<char> s)
    {
        if (s.Contains("No space left on device", StringComparison.Ordinal) || s.Contains("Errno 28]", StringComparison.Ordinal) ||
            s.Contains("Error(28,", StringComparison.Ordinal) || s.Contains("WinError 112", StringComparison.Ordinal) ||
            s.Contains("not enough space on the disk", StringComparison.Ordinal))
        {
            DiskFull = true;
        }
        else if (s.Contains("being used by another process", StringComparison.Ordinal) || s.Contains("WinError 32]", StringComparison.Ordinal) ||
                 s.Contains("WinError 33]", StringComparison.Ordinal) || s.Contains("locked a portion of the file", StringComparison.Ordinal))
        {
            SharingViolation = true;
        }
        else if (s.Contains("Permission denied", StringComparison.Ordinal) || s.Contains("Access is denied", StringComparison.Ordinal) ||
                 s.Contains("PermissionError", StringComparison.Ordinal) || s.Contains("WinError 5]", StringComparison.Ordinal))
        {
            AccessDenied = true;
        }
    }

    private void ParseDiskSpace(ReadOnlySpan<char> msg)
    {
        // "Not enough available disk space! 12.34 GiB < 98.76 GiB" — free first, required second (core.py:1738-1743)
        const string marker = "Not enough available disk space! ";
        if (!msg.StartsWith(marker, StringComparison.Ordinal))
        {
            return;
        }

        var s = msg[marker.Length..];
        var lt = s.IndexOf(" GiB < ", StringComparison.Ordinal);
        if (lt <= 0)
        {
            return;
        }

        var end = s[(lt + 7)..].IndexOf(" GiB", StringComparison.Ordinal);
        if (end <= 0)
        {
            return;
        }

        if (double.TryParse(s[..lt], NumberStyles.Float, CultureInfo.InvariantCulture, out var free) &&
            double.TryParse(s.Slice(lt + 7, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var required))
        {
            FreeGiB = free;
            RequiredGiB = required;
        }
    }

    public long? FreeBytesFromCheck => FreeGiB is { } g ? (long)(g * GiB) : null;

    public long? RequiredBytesFromCheck => RequiredGiB is { } g ? (long)(g * GiB) : null;

    internal static bool LooksLikeNetwork(ReadOnlySpan<char> s) =>
        s.Contains("ConnectionError", StringComparison.Ordinal) || s.Contains("ConnectTimeout", StringComparison.Ordinal) ||
        s.Contains("ReadTimeout", StringComparison.Ordinal) || s.Contains("ProxyError", StringComparison.Ordinal) ||
        s.Contains("SSLError", StringComparison.Ordinal) || s.Contains("NameResolutionError", StringComparison.Ordinal) ||
        s.Contains("Max retries exceeded", StringComparison.Ordinal) || s.Contains("getaddrinfo failed", StringComparison.Ordinal) ||
        s.Contains("Connection aborted", StringComparison.Ordinal) || s.Contains("Connection reset", StringComparison.Ordinal) ||
        s.Contains("RemoteDisconnected", StringComparison.Ordinal) || s.Contains("ChunkedEncodingError", StringComparison.Ordinal) ||
        s.Contains("IncompleteRead", StringComparison.Ordinal) || s.Contains("timed out", StringComparison.Ordinal) ||
        s.Contains("requests.exceptions.", StringComparison.Ordinal) || s.Contains("urllib3.exceptions.", StringComparison.Ordinal);

    private static long? MiBValue(ReadOnlySpan<char> s)
    {
        var sp = s.IndexOf(" MiB", StringComparison.Ordinal);
        return sp > 0 && double.TryParse(s[..sp], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (long)(v * MiB) : null;
    }

    private static int? IntValue(ReadOnlySpan<char> s)
    {
        var n = 0;
        while (n < s.Length && char.IsAsciiDigit(s[n]))
        {
            n++;
        }

        return n > 0 && int.TryParse(s[..n], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? Quoted(ReadOnlySpan<char> s)
    {
        var end = s.LastIndexOf('"');
        return end > 0 ? new string(s[..end]) : null;
    }

    private static string Cap(ReadOnlySpan<char> s) => s.Length <= MaxDetail ? new string(s) : string.Concat(s[..MaxDetail], "…");
}
