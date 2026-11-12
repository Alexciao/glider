namespace Glider.Core.Egl;

/// <summary>
/// Quick scan for Fortnite installs the Epic Games Launcher doesn't tell us about (manifest lost, install moved by hand,
/// EGL uninstalled). Only well-known folders on each drive are probed: never a recursive walk of a whole drive.
/// </summary>
internal static class InstallLocator
{
    /// <summary>Folders (relative to a drive root) that commonly hold the game itself.</summary>
    internal static readonly string[][] KnownGameFolders =
    [
        ["Fortnite"],
        ["Epic Games", "Fortnite"],
        ["Games", "Fortnite"],
        ["Games", "Epic Games", "Fortnite"],
        ["Program Files", "Epic Games", "Fortnite"],
        ["Program Files (x86)", "Epic Games", "Fortnite"],
    ];

    /// <summary>Folders (relative to a drive root) whose direct children are also probed, to catch non-default folder names.</summary>
    internal static readonly string[][] KnownParents =
    [
        ["Games"],
        ["Epic Games"],
        ["Games", "Epic Games"],
        ["Program Files", "Epic Games"],
    ];

    /// <summary>At most this many children of a known parent are probed.</summary>
    internal const int MaxChildrenPerParent = 64;

    /// <summary>Game folders (FortniteGame\Binaries\Win64\…Shipping.exe present) under the given drive roots, de-duplicated.</summary>
    public static IReadOnlyList<string> QuickScan(IEnumerable<string> driveRoots, CancellationToken ct)
    {
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string folder)
        {
            if (FortniteLayout.LooksLikeInstall(folder) && seen.Add(EglPath.Normalize(folder)))
            {
                found.Add(folder);
            }
        }

        foreach (var root in driveRoots)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var rel in KnownGameFolders)
            {
                ct.ThrowIfCancellationRequested();
                Add(Path.Combine([root, .. rel]));
            }

            foreach (var rel in KnownParents)
            {
                ct.ThrowIfCancellationRequested();
                var parent = Path.Combine([root, .. rel]);
                foreach (var child in SafeChildren(parent, ct))
                {
                    Add(child);
                }
            }
        }

        return found;
    }

    /// <summary>Immediate subfolders of <paramref name="dir"/> (capped); empty when missing or unreadable.</summary>
    internal static IEnumerable<string> SafeChildren(string dir, CancellationToken ct)
    {
        var list = new List<string>();
        try
        {
            if (!Directory.Exists(dir))
            {
                return list;
            }

            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                ct.ThrowIfCancellationRequested();
                list.Add(d);
                if (list.Count >= MaxChildrenPerParent)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A drive that vanished or a folder we can't list: nothing to find there.
        }

        return list;
    }
}
