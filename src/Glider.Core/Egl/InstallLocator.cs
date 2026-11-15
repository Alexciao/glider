namespace Glider.Core.Egl;

/// <summary>
/// Quick scan for installs the Epic Games Launcher doesn't tell us about (manifest lost, install moved by hand, EGL
/// uninstalled). Only well-known folders on each drive are probed: each profile's folder names (e.g. <c>\Fortnite</c>,
/// <c>\Epic Games\Fortnite</c>) and the direct children of the usual library parents that carry EGL metadata
/// (<c>.egstore\*.mancpn</c>). Never a recursive walk of a whole drive.
/// </summary>
internal static class InstallLocator
{
    /// <summary>Parents (relative to a drive root) under which a profile's folder name is probed.</summary>
    internal static readonly string[][] KnownGameParents =
    [
        [],
        ["Epic Games"],
        ["Games"],
        ["Games", "Epic Games"],
        ["Program Files", "Epic Games"],
        ["Program Files (x86)", "Epic Games"],
        ["EpicLibrary"],      // a common custom library folder (seen on a real machine: D:\EpicLibrary\Fortnite)
        ["Epic Library"],
        ["EpicGames"],
        ["Games", "EpicLibrary"],
    ];

    /// <summary>Folders (relative to a drive root) whose direct children are also probed, to catch non-default folder names.</summary>
    internal static readonly string[][] KnownParents =
    [
        ["Games"],
        ["Epic Games"],
        ["Games", "Epic Games"],
        ["Program Files", "Epic Games"],
        ["Program Files (x86)", "Epic Games"],
        ["EpicLibrary"],
        ["Epic Library"],
        ["EpicGames"],
        ["Games", "EpicLibrary"],
    ];

    /// <summary>At most this many children of a known parent are probed.</summary>
    internal const int MaxChildrenPerParent = 64;

    /// <summary>
    /// (folder, app) pairs under the given drive roots, de-duplicated. A folder counts when a profile's probe recognises it or
    /// <paramref name="detectApp"/> reads an AppName from its EGL metadata.
    /// </summary>
    public static IReadOnlyList<(string Folder, string AppName)> QuickScan(IEnumerable<string> driveRoots, Func<string, string?> detectApp, CancellationToken ct)
    {
        var found = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string folder)
        {
            if (!Directory.Exists(folder) || seen.Contains(EglPath.Normalize(folder)))
            {
                return;
            }

            var app = detectApp(folder) ?? Game.GameProfiles.RecogniseByLayout(folder)?.AppName;
            if (app is not null && seen.Add(EglPath.Normalize(folder)))
            {
                found.Add((folder, app));
            }
        }

        var profileFolders = Game.GameProfiles.All.SelectMany(p => p.QuickScanFolders).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var root in driveRoots)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var parent in KnownGameParents)
            {
                foreach (var name in profileFolders)
                {
                    ct.ThrowIfCancellationRequested();
                    Add(Path.Combine([root, .. parent, name]));
                }
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
