using Glider.Core.Contracts;

namespace Glider.Core.Game;

/// <summary>Result of looking at a folder the user pointed us at.</summary>
internal sealed record FolderInspection(bool Valid, string? GamePath, string? Version, bool VersionIsExact, string? Reason);

/// <summary>
/// Finds the game folder (the folder itself or its <c>Fortnite</c> child) and reads its version without
/// running anything. Version sources, most to least trustworthy:
///  1. EGL's .item for that location (<c>AppVersionString</c>) — exact; what EGL and Legendary compare.
///  2. <c>Engine\Build\Build.version</c> (UE build JSON: BranchName + Changelist) — display only, the string
///     format differs from Epic's build version, so it is never used for equality.
/// </summary>
internal static class FolderInspector
{
    public static FolderInspection Inspect(string path, IEglService egl, string folderName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return new FolderInspection(false, null, null, false, "Choose a full folder path, like D:\\Games\\Fortnite.");
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!Directory.Exists(full))
        {
            return new FolderInspection(false, null, null, false, "That folder doesn't exist.");
        }

        string? game = null;
        foreach (var candidate in new[] { full, Path.Combine(full, folderName), Path.Combine(full, FortniteLayout.AppName) })
        {
            if (FortniteLayout.LooksLikeInstall(candidate))
            {
                game = candidate;
                break;
            }
        }

        if (game is null)
        {
            return new FolderInspection(false, null, null, false,
                "Fortnite wasn't found here. Pick the folder that contains FortniteGame (usually named Fortnite).");
        }

        var item = egl.FindByLocation(game);
        if (item is not null && !string.IsNullOrEmpty(item.Version))
        {
            return new FolderInspection(true, game, item.Version, true, null);
        }

        return new FolderInspection(true, game, ReadBuildVersion(game), false, null);
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
