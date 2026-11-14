namespace Glider.Core.Saves;

/// <summary>Values for the variables Epic's <c>CloudSaveFolder</c> template may use (Legendary core.py:991-1060, Windows).</summary>
internal sealed record SavePathVariables(string InstallDir, string EpicId, string LocalAppData, string UserProfile)
{
    public static SavePathVariables ForCurrentUser(string installDir, string epicId) => new(
        installDir,
        epicId,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}

internal sealed record SavePathResolution(string? Path, string? UnresolvedVariable)
{
    public bool Resolved => Path is not null;
}

/// <summary>
/// Pure re-implementation of Legendary's <c>get_save_path</c> for Windows: backslashes become <c>/</c>, each component is
/// matched case-insensitively against the variable table, and the result is normalised (so <c>{AppData}/../Roaming/x</c> works).
/// Used to fail early on a template Legendary cannot resolve and to cross-check the folder it stores in installed.json; the
/// folder Legendary stored is what a sync really uses.
/// </summary>
internal static class SavePathResolver
{
    public static SavePathResolution Resolve(string template, SavePathVariables vars)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return new(null, "(empty template)");
        }

        // `{appdata}` is Epic's *Local* AppData; `{userdir}` is Documents; `{usersavedgames}` is %USERPROFILE%/Saved Games.
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{installdir}"] = vars.InstallDir,
            ["{epicid}"] = vars.EpicId,
            ["{appdata}"] = vars.LocalAppData,
            ["{userdir}"] = Path.Combine(vars.UserProfile, "Documents"),
            ["{userprofile}"] = vars.UserProfile,
            ["{usersavedgames}"] = Path.Combine(vars.UserProfile, "Saved Games"),
        };

        var parts = new List<string>();
        foreach (var part in template.Replace('\\', '/').Split('/'))
        {
            if (table.TryGetValue(part, out var value))
            {
                if (value.Length == 0)
                {
                    return new(null, part);
                }

                parts.Add(value);
            }
            else if (part.Contains('{', StringComparison.Ordinal) || part.Contains('%', StringComparison.Ordinal))
            {
                return new(null, part); // Legendary: "Path contains unprocessed variables"
            }
            else
            {
                parts.Add(part);
            }
        }

        try
        {
            return new(System.IO.Path.GetFullPath(System.IO.Path.Combine([.. parts])), null);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(null, template);
        }
    }

    /// <summary>
    /// A save folder that is, contains, or sits at the root of a place a download must not clear: Legendary deletes every file
    /// matching the filters in it before it writes the cloud copy, so an unfiltered "save folder" that is the game's install
    /// folder (or a parent of it, or a drive root) would delete the game.
    /// </summary>
    public static string? Refusal(string savePath, string? installPath, bool hasIncludeFilter)
    {
        string full;
        try
        {
            full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(savePath));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "The save folder path is not valid.";
        }

        if (System.IO.Path.GetPathRoot(full) is { } root && string.Equals(System.IO.Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
        {
            return "The save folder is a drive root.";
        }

        if (!hasIncludeFilter && installPath is { Length: > 0 })
        {
            var install = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(installPath));
            if (string.Equals(full, install, StringComparison.OrdinalIgnoreCase)
                || install.StartsWith(full + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return "The save folder is the game's own install folder and the game has no list of which files are saves.";
            }
        }

        return null;
    }
}
