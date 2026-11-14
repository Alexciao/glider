using Glider.Core.Contracts;

namespace Glider.Core.Game;

/// <summary>Result of looking at a folder the user pointed us at.</summary>
internal sealed record FolderInspection(bool Valid, string? AppName, string? GamePath, string? Version, bool VersionIsExact, string? Reason);

/// <summary>
/// Finds which game lives in a folder (the folder itself, a child, or an ancestor up to 4 levels) and reads its version
/// without running anything. The app comes from EGL's <c>.egstore\*.mancpn</c> (what Legendary's import checks), else
/// from a profile's layout probe (e.g. Fortnite's shipping exe), else from the expected app when the caller names one
/// (catalog FolderName). Version sources, most to least trustworthy:
///  1. EGL's .item for that location (<c>AppVersionString</c>) — exact; what EGL and Legendary compare.
///  2. <c>Engine\Build\Build.version</c> (UE build JSON: BranchName + Changelist) — display only.
/// </summary>
internal static class FolderInspector
{
    public static FolderInspection Inspect(string path, IEglService egl, string? expectedApp = null, string? expectedFolderName = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return Fail("Choose a full folder path, like D:\\Games\\MyGame.");
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full))
        {
            return Fail("That folder doesn't exist.");
        }

        // The folder itself, the expected folder name under it, then every profile's layout hint around it.
        var candidates = new List<string> { full };
        if (expectedFolderName is { Length: > 0 } fn)
        {
            candidates.Add(Path.Combine(full, fn));
        }

        foreach (var p in GameProfiles.All)
        {
            candidates.AddRange(p.QuickScanFolders.Select(f => Path.Combine(full, f)));
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Identify(candidate, egl, expectedApp) is { } hit)
            {
                return Found(hit.Folder, hit.App, egl);
            }
        }

        // Any drive, any folder name: the user may have picked a subfolder of the install (FortniteGame, Win64, …) or a
        // parent holding a differently named game folder.
        if (FindAround(full, egl, expectedApp) is { } around)
        {
            return Found(around.Folder, around.App, egl);
        }

        return Fail(expectedApp is null
            ? "No game was found here. Pick the game's own folder (the one Epic installed it into)."
            : "That game wasn't found here. Pick the folder Epic installed it into.");
    }

    private static FolderInspection Fail(string reason) => new(false, null, null, null, false, reason);

    private static FolderInspection Found(string folder, string app, IEglService egl)
    {
        var item = egl.FindByLocation(folder, app);
        if (item is not null && !string.IsNullOrEmpty(item.Version))
        {
            return new FolderInspection(true, app, folder, item.Version, true, null);
        }

        return new FolderInspection(true, app, folder, ReadBuildVersion(folder), false, null);
    }

    /// <summary>Which app this exact folder holds, or null.</summary>
    internal static (string Folder, string App)? Identify(string folder, IEglService egl, string? expectedApp)
    {
        if (!Directory.Exists(folder))
        {
            return null;
        }

        if (egl.DetectAppName(folder) is { } fromEgl)
        {
            return expectedApp is null || string.Equals(fromEgl, expectedApp, StringComparison.OrdinalIgnoreCase) ? (folder, fromEgl) : null;
        }

        if (GameProfiles.RecogniseByLayout(folder) is { } profile)
        {
            return expectedApp is null || string.Equals(profile.AppName, expectedApp, StringComparison.OrdinalIgnoreCase) ? (folder, profile.AppName) : null;
        }

        if (expectedApp is not null && !GameProfiles.For(expectedApp).HasLivenessProbe && GameProfiles.LooksLikeInstall(expectedApp, folder))
        {
            return (folder, expectedApp);
        }

        return null;
    }

    /// <summary>The game folder an ancestor (up to 4 levels: Win64 → Binaries → FortniteGame → game) or a direct child of <paramref name="folder"/>, or null.</summary>
    private static (string Folder, string App)? FindAround(string folder, IEglService egl, string? expectedApp)
    {
        var up = folder;
        for (var i = 0; i < 4 && Path.GetDirectoryName(up) is { Length: > 0 } parent; i++)
        {
            up = parent;
            if (egl.DetectAppName(up) is not null || GameProfiles.RecogniseByLayout(up) is not null)
            {
                return Identify(up, egl, expectedApp);
            }
        }

        foreach (var child in Egl.InstallLocator.SafeChildren(folder, CancellationToken.None))
        {
            if (egl.DetectAppName(child) is not null || GameProfiles.RecogniseByLayout(child) is not null)
            {
                if (Identify(child, egl, expectedApp) is { } hit)
                {
                    return hit;
                }
            }
        }

        return null;
    }

    /// <summary>"++Fortnite+Release-33.00-CL-12345678" from Engine\Build\Build.version, or null.</summary>
    internal static string? ReadBuildVersion(string gamePath)
    {
        var file = Path.Combine(gamePath, "Engine", "Build", "Build.version");
        var branch = Services.JsonFiles.ReadString(file, "BranchName");
        var cl = Services.JsonFiles.ReadString(file, "Changelist");
        if (string.IsNullOrEmpty(branch))
        {
            return null;
        }

        return string.IsNullOrEmpty(cl) || cl == "0" ? branch : $"{branch}-CL-{cl}";
    }
}
