using Glider.Core.Contracts;

namespace Glider.Core.Game;

/// <summary>Easy Anti-Cheat bootstrapper a game ships (run elevated once when the EAC service is missing).</summary>
public sealed record EacSetup(string SetupRelative, string DefaultProductId);

/// <summary>
/// Optional per-game quirks (docs/v0.2-design.md §2.1). Generic code reads these; nothing else may branch on a
/// specific game. Unknown games get <see cref="GameProfiles.Generic"/>.
/// </summary>
public sealed record GameProfile(
    string AppName,
    /// <summary>Process names (no .exe) that belong to a session even when re-parented (launcher stubs, anti-cheat wrappers).</summary>
    IReadOnlyList<string> TrackedProcessNames,
    /// <summary>The real game process: a session is "Running" once it appears (null = as soon as anything tracked runs).</summary>
    string? LivenessProcessName,
    /// <summary>File (relative to the install folder) that must exist for the folder to count as this game.</summary>
    string? LivenessExeRelative,
    AntiCheat AntiCheat,
    EacSetup? Eac,
    /// <summary>Folder names probed by the drive quick scan (under a drive root, \Games, \Epic Games…).</summary>
    IReadOnlyList<string> QuickScanFolders,
    /// <summary>Subfolder names that identify a game folder when the user picks a parent or a child of it.</summary>
    IReadOnlyList<string> LayoutHints,
    /// <summary>The title has selective downloads (SDL) from api.legendary.gl; installs default to <c>--skip-sdl</c>.</summary>
    bool HasSdl)
{
    public bool HasLivenessProbe => LivenessExeRelative is not null;

    public IEnumerable<string> AllTrackedNames => LivenessProcessName is null ? TrackedProcessNames : TrackedProcessNames.Append(LivenessProcessName);
}

public static class GameProfiles
{
    public const string FortniteAppName = "Fortnite";

    /// <summary>Entry #1: every v0.1 behaviour, plus the bootstrapper Fortnite 43.x starts (audit §0.3).</summary>
    public static GameProfile Fortnite { get; } = new(
        FortniteAppName,
        TrackedProcessNames:
        [
            "FortniteLauncher",
            "FortniteBootstrapper",
            "FortniteClient-Win64-Shipping_EAC_EOS",
            "FortniteClient-Win64-Shipping_EAC",
            "FortniteClient-Win64-Shipping_BE",
        ],
        LivenessProcessName: "FortniteClient-Win64-Shipping",
        LivenessExeRelative: @"FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe",
        AntiCheat: AntiCheat.EasyAntiCheat,
        Eac: new EacSetup(@"FortniteGame\Binaries\Win64\EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", "prod-fn"),
        QuickScanFolders: ["Fortnite"],
        LayoutHints: ["FortniteGame"],
        HasSdl: true);

    private static readonly Dictionary<string, GameProfile> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        [FortniteAppName] = Fortnite,
    };

    public static IReadOnlyCollection<GameProfile> All => Known.Values;

    public static GameProfile Generic(string appName) => new(appName, [], null, null, AntiCheat.None, null, [], [], false);

    public static GameProfile For(string appName) => Known.TryGetValue(appName, out var p) ? p : Generic(appName);

    public static bool HasProfile(string appName) => Known.ContainsKey(appName);

    /// <summary>The profile's liveness exe in <paramref name="gamePath"/> (null when the profile has none).</summary>
    public static string? LivenessExe(GameProfile profile, string gamePath) =>
        profile.LivenessExeRelative is { } rel ? Path.Combine(gamePath, Normalize(rel)) : null;

    /// <summary>
    /// Does <paramref name="gamePath"/> hold the game? Profile probe when there is one; otherwise the installed.json
    /// executable when known; otherwise a non-empty folder.
    /// </summary>
    public static bool LooksLikeInstall(string appName, string gamePath, string? executable = null)
    {
        if (string.IsNullOrWhiteSpace(gamePath))
        {
            return false;
        }

        var profile = For(appName);
        if (LivenessExe(profile, gamePath) is { } probe)
        {
            return File.Exists(probe);
        }

        try
        {
            if (!Directory.Exists(gamePath))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(executable))
            {
                return File.Exists(Path.Combine(gamePath, Normalize(executable)));
            }

            return Directory.EnumerateFileSystemEntries(gamePath).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The profile that recognises this folder by its probe (e.g. a Fortnite folder without EGL metadata), or null.</summary>
    public static GameProfile? RecogniseByLayout(string gamePath) =>
        All.FirstOrDefault(p => LivenessExe(p, gamePath) is { } probe && File.Exists(probe));

    /// <summary>
    /// Anti-cheat the install ships: the profile's when it declares one, else an <c>EasyAntiCheat</c> folder holding
    /// <c>Settings.json</c> (EAC) or a <c>BattlEye</c> folder, looked for at most three levels deep.
    /// </summary>
    public static AntiCheat DetectAntiCheat(string appName, string? installPath)
    {
        var profile = For(appName);
        if (profile.AntiCheat != AntiCheat.None || installPath is null)
        {
            return profile.AntiCheat;
        }

        return FindAntiCheatFolder(installPath, "EasyAntiCheat", "Settings.json") is not null ? AntiCheat.EasyAntiCheat
            : FindAntiCheatFolder(installPath, "BattlEye", null) is not null ? AntiCheat.BattlEye
            : AntiCheat.None;
    }

    /// <summary>Breadth-first search (≤ 3 levels, ≤ 400 folders) for a folder named <paramref name="name"/>.</summary>
    internal static string? FindAntiCheatFolder(string root, string name, string? mustContain)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            var queue = new Queue<(string Dir, int Depth)>();
            queue.Enqueue((root, 0));
            var visited = 0;
            while (queue.Count > 0 && visited++ < 400)
            {
                var (dir, depth) = queue.Dequeue();
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (string.Equals(Path.GetFileName(sub), name, StringComparison.OrdinalIgnoreCase) &&
                        (mustContain is null || File.Exists(Path.Combine(sub, mustContain))))
                    {
                        return sub;
                    }

                    if (depth < 3 && (File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0)
                    {
                        queue.Enqueue((sub, depth + 1));
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    internal static string Normalize(string relative) =>
        relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
}
