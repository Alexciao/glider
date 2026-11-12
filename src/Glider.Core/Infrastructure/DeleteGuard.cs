namespace Glider.Core.Infrastructure;

/// <summary>
/// Last line of defence before Glider deletes a directory tree itself (cancel cleanup of a fresh install, the source
/// of a cross-drive move, a partial move copy). Callers already decide <i>which</i> folder is theirs; this refuses
/// folders that must never go whatever that decision says: drive/share roots, links (a junction's target may be
/// anything), and any folder that is or contains a Windows/user/Glider folder. Paths are compared after
/// <see cref="Path.GetFullPath(string)"/>, so <c>..</c> segments can't sneak past.
/// </summary>
internal static class DeleteGuard
{
    private static readonly Environment.SpecialFolder[] Protected =
    [
        Environment.SpecialFolder.Windows,
        Environment.SpecialFolder.System,
        Environment.SpecialFolder.SystemX86,
        Environment.SpecialFolder.ProgramFiles,
        Environment.SpecialFolder.ProgramFilesX86,
        Environment.SpecialFolder.CommonProgramFiles,
        Environment.SpecialFolder.CommonApplicationData,
        Environment.SpecialFolder.UserProfile,
        Environment.SpecialFolder.ApplicationData,
        Environment.SpecialFolder.LocalApplicationData,
        Environment.SpecialFolder.Desktop,
        Environment.SpecialFolder.MyDocuments,
        Environment.SpecialFolder.CommonDesktopDirectory,
        Environment.SpecialFolder.CommonDocuments,
    ];

    /// <summary>Why <paramref name="path"/> must not be deleted recursively, or null when it may be.</summary>
    /// <param name="extraProtected">More folders that must survive (Glider's data root, its install directory…).</param>
    public static string? Refusal(string? path, params string?[] extraProtected)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return "it is not an absolute path";
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "it is not a valid path";
        }

        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root) || Same(Path.TrimEndingDirectorySeparator(root), full) || Path.GetDirectoryName(full) is null)
        {
            return "it is the root of a drive";
        }

        foreach (var p in ProtectedFolders(extraProtected))
        {
            if (Contains(full, p))
            {
                return $"it is or contains \"{p}\"";
            }
        }

        try
        {
            if (Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            {
                return "it is a link (junction or symbolic link) to another folder";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"its attributes can't be read ({e.Message})";
        }

        return null;
    }

    /// <summary>Deletes the tree unless <see cref="Refusal"/> objects (then logs and leaves it). Never throws.</summary>
    /// <returns>True when the folder is gone afterwards.</returns>
    public static bool TryDeleteTree(string path, ILog log, params string?[] extraProtected)
    {
        if (Refusal(path, extraProtected) is { } why)
        {
            log.Error("files", $"Refusing to delete \"{path}\": {why}.");
            return false;
        }

        try
        {
            if (Directory.Exists(path))
            {
                // Unlink junctions/symlinks first (never descending into them): .NET's recursive delete doesn't follow
                // them either, but it reports a junction as "access denied" after trying DeleteVolumeMountPoint on it.
                RemoveLinksInside(path);
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn("files", $"Could not fully delete \"{path}\": {e.Message}");
            return !Directory.Exists(path);
        }
    }

    private static void RemoveLinksInside(string root)
    {
        var top = new EnumerationOptions { RecurseSubdirectories = false, AttributesToSkip = 0, IgnoreInaccessible = true };
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var dir in new DirectoryInfo(pending.Pop()).EnumerateDirectories("*", top))
            {
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(dir.FullName, recursive: false); // removes the link itself, not its target
                }
                else
                {
                    pending.Push(dir.FullName);
                }
            }
        }
    }

    private static IEnumerable<string> ProtectedFolders(string?[] extra)
    {
        foreach (var f in Protected)
        {
            string p;
            try
            {
                p = Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify);
            }
            catch (PlatformNotSupportedException)
            {
                continue;
            }

            if (p.Length > 0 && Path.IsPathFullyQualified(p))
            {
                yield return Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            }
        }

        foreach (var p in extra)
        {
            if (!string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p))
            {
                yield return Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            }
        }

        yield return Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
    }

    /// <summary>True if <paramref name="inner"/> is <paramref name="outer"/> or below it.</summary>
    private static bool Contains(string outer, string inner) =>
        Same(outer, inner) || inner.StartsWith(outer + Path.DirectorySeparatorChar, Comparison);

    private static bool Same(string a, string b) => string.Equals(a, b, Comparison);

    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
