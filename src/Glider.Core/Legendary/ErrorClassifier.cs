using Glider.Core.Contracts;

namespace Glider.Core.Legendary;

internal enum LegendaryCommandKind
{
    Version,
    Status,
    AuthCode,
    AuthDelete,
    Info,
    Install,
    Update,
    Repair,
    Verify,
    Import,
    Move,
    Uninstall,
    Launch,
    List,
    ListInstalled,
    SaveList,
    SaveSync,
    EosOverlay,
    Activate,
    ThirdPartyLaunch,
}

/// <summary>
/// Maps a finished Legendary run to a human error (<see cref="Errors"/>), or null for success. Exit codes are
/// mostly 0 even on failure (legendary.md §1.6), so success requires the command's positive marker; everything
/// else is decided from the accumulated <see cref="LegendaryOutputFacts"/>.
/// </summary>
internal static class ErrorClassifier
{
    /// <param name="freeBytes">Free space at the install location, for disk-full errors detected mid-install.</param>
    public static GliderError? Classify(LegendaryCommandKind kind, LegendaryRunResult run, long? freeBytes = null)
    {
        var f = run.Facts;
        if (run.TimedOut)
        {
            return Errors.Timeout("Legendary") with { Detail = Tail(run) };
        }

        if (run.Stalled)
        {
            return Errors.Network($"No progress for {run.StallTimeout.TotalSeconds:0} s ({f.ChunkRetries} chunk retries); the connection to Epic's servers seems lost.");
        }

        // Successes whose marker co-exists with "failure-looking" text.
        if (kind == LegendaryCommandKind.Uninstall && f.NotInstalled)
        {
            return null; // already gone: uninstall is idempotent
        }

        if (kind == LegendaryCommandKind.AuthCode && (f.SignedInAs is not null || f.CredentialsStillValid))
        {
            return null;
        }

        if (Common(kind, run, freeBytes) is { } common)
        {
            return common;
        }

        switch (kind)
        {
            case LegendaryCommandKind.Install or LegendaryCommandKind.Update or LegendaryCommandKind.Repair:
                if (f.InstallFinished || f.NothingToDo)
                {
                    return null;
                }

                if (f.ManifestMissing)
                {
                    return Errors.Corrupted("Legendary's copy of the installed manifest is missing.");
                }

                break;

            case LegendaryCommandKind.Verify:
                if (f.VerifySucceeded)
                {
                    return null;
                }

                if (f.VerifyFailed)
                {
                    var detail = $"{f.VerifyCorrupted} file(s) corrupted, {f.VerifyMissing} file(s) missing.";
                    return f.VerifyCorrupted == 0 && f.VerifyOpenFailures > 0 && f.SharingViolation
                        ? Errors.FilesLocked(detail)
                        : Errors.Corrupted(detail);
                }

                if (f.ManifestMissing)
                {
                    return Errors.Corrupted("Legendary's copy of the installed manifest is missing.");
                }

                break;

            case LegendaryCommandKind.Import:
                if (f.Imported)
                {
                    return null;
                }

                if (f.ImportPathMissing)
                {
                    return Errors.InvalidFolder("That folder doesn't exist.");
                }

                if (f.AlreadyInstalled)
                {
                    return Errors.AlreadyInstalled();
                }

                if (f.ImportNoFiles)
                {
                    return Errors.InvalidFolder("No files of this game were found in this folder.");
                }

                if (f.ImportExeMissing)
                {
                    return Errors.InvalidFolder("The game executable is missing from this folder.");
                }

                break;

            case LegendaryCommandKind.Move:
                if (f.MoveFinished)
                {
                    return null;
                }

                if (f.MoveTargetExists)
                {
                    return Errors.PermissionDenied("The destination already contains a folder with this name.");
                }

                if (f.MoveFailed || f.MoveCrossDrive)
                {
                    return OsError(f) ?? Errors.LegendaryFailed(f.Detail);
                }

                break;

            case LegendaryCommandKind.Uninstall:
                if (f.Uninstalled && !f.UninstallFailed)
                {
                    return null;
                }

                if (f.UninstallFailed)
                {
                    return OsError(f) ?? Errors.LegendaryFailed(f.Detail);
                }

                break;

            case LegendaryCommandKind.AuthCode:
                return Errors.AuthFailed(f.Detail);

            case LegendaryCommandKind.AuthDelete:
                if (f.UserDataDeleted)
                {
                    return null;
                }

                break;

            case LegendaryCommandKind.Launch:
                if (f.OutOfDate)
                {
                    return Errors.LaunchFailed("The game is out of date and must be updated first.");
                }

                break;

            case LegendaryCommandKind.Version:
                if (f.VersionPrinted)
                {
                    return null;
                }

                break;

            case LegendaryCommandKind.EosOverlay:
                if (f.OverlayUpToDate || f.OverlayDownloaded)
                {
                    return null;
                }

                if (f.OverlayNotInstalled)
                {
                    return Errors.OverlayFailed("Legendary has no managed overlay to update.");
                }

                break;

            case LegendaryCommandKind.Activate:
                if (f.NoLinkedAccount)
                {
                    return Errors.AccountNotLinked("Ubisoft Connect");
                }

                if (f.ActivationFailed)
                {
                    return Errors.ActivationFailed("Ubisoft Connect", f.Detail);
                }

                if (f.ActivationDone)
                {
                    return null;
                }

                break;

            case LegendaryCommandKind.ThirdPartyLaunch:
                if (f.ThirdPartyUnknown)
                {
                    return Errors.LaunchFailed("Legendary doesn't have this game's data yet; refresh the library and try again.");
                }

                break;

            case LegendaryCommandKind.List or LegendaryCommandKind.ListInstalled:
                if (run.ExitCode == 0)
                {
                    return null; // the files on disk are what Glider reads; stdout is not parsed
                }

                break;

            case LegendaryCommandKind.Status or LegendaryCommandKind.Info:
                // Success is "valid JSON on stdout", decided by the caller; only reached when that failed.
                break;

            case LegendaryCommandKind.SaveList or LegendaryCommandKind.SaveSync:
                return null; // no success marker exists (exit code 0 either way); the save service checks the result on disk
        }

        return Errors.LegendaryFailed(f.Detail ?? $"No success marker (exit code {run.ExitCode}). {Tail(run)}");
    }

    private static GliderError? Common(LegendaryCommandKind kind, LegendaryRunResult run, long? freeBytes)
    {
        var f = run.Facts;
        if (f.PromptHitEof)
        {
            return Errors.LegendaryFailed("Legendary asked an interactive question: " + (f.Detail ?? Tail(run)));
        }

        if (f.NoSavedCredentials)
        {
            return Errors.NotSignedIn();
        }

        if (f.CorrectiveAction is not null)
        {
            return Errors.AuthFailed(f.CorrectiveAction);
        }

        if (f.CredentialsInvalid)
        {
            return Errors.AuthExpired();
        }

        if (f.LoginHttpFailed)
        {
            return Errors.Network(f.LastErrorLine);
        }

        if (f.LoginFailed)
        {
            return kind == LegendaryCommandKind.AuthCode ? Errors.AuthFailed(f.Detail) : Errors.AuthExpired(f.Detail);
        }

        if (f.LockFailed)
        {
            // "Failed to acquire installed data lock" (exit 0): busy, never a failure — the writer lane retries (L§1.8).
            return Errors.LegendaryLocked(f.Detail);
        }

        if (f.RequiredBytesFromCheck is { } required && f.FreeBytesFromCheck is { } free)
        {
            return Errors.InsufficientSpace(required, free, f.RequirementFailures.FirstOrDefault());
        }

        if (f.SharedMemorySuggestedMiB is { } mib)
        {
            return Errors.LegendaryFailed($"The download cache needs {mib} MiB of memory. Set a larger cache in Settings, or close other programs.");
        }

        if (f.RequirementFailures.Count > 0)
        {
            var first = f.RequirementFailures[0];
            return first.Contains("does not exist", StringComparison.Ordinal)
                ? Errors.PermissionDenied(first)
                : Errors.LegendaryFailed(first);
        }

        if (f.NotOwned)
        {
            return Errors.NotOwned() with { Detail = f.Detail };
        }

        if (f.NotInstalled)
        {
            return Errors.NotInstalled();
        }

        if (f.InstallDirMissing)
        {
            return Errors.NotInstalled() with { Detail = f.Detail };
        }

        if (f.WriteFailures > 0)
        {
            var detail = $"{f.WriteFailures} file write(s) failed. First: {f.FirstFailedFile ?? "?"} ({f.FirstWriteFailure})";
            return OsError(f, freeBytes, detail) ?? Errors.Corrupted(detail);
        }

        if (f.ManifestUnavailable)
        {
            return Errors.CdnUnavailable(f.Detail);
        }

        if (f.Traceback || f.PyInstallerCrash)
        {
            if (f.NetworkException || f.VersionCheckCrash)
            {
                return Errors.Network(f.ExceptionLine);
            }

            return OsError(f, freeBytes, f.ExceptionLine) ?? Errors.LegendaryFailed(f.ExceptionLine ?? Tail(run));
        }

        if (f.InstallFailed)
        {
            return f.NetworkException ? Errors.Network(f.Detail) : OsError(f, freeBytes, f.Detail) ?? Errors.LegendaryFailed(f.Detail);
        }

        if (f.Aborted)
        {
            return Errors.Cancelled();
        }

        return null;
    }

    private static GliderError? OsError(LegendaryOutputFacts f, long? freeBytes = null, string? detail = null)
    {
        detail ??= f.Detail;
        if (f.DiskFull)
        {
            var free = freeBytes ?? 0;
            var required = Math.Max(f.InstallSizeBytes ?? 0, free + 1);
            return Errors.InsufficientSpace(required, free, detail);
        }

        if (f.SharingViolation)
        {
            return Errors.FilesLocked(detail);
        }

        if (f.AccessDenied)
        {
            return Errors.PermissionDenied(detail);
        }

        return null;
    }

    private static string Tail(LegendaryRunResult run) => Infrastructure.SecretRedactor.Redact(string.Join(" | ", run.Tail.TakeLast(5)));
}
