using System.Text.RegularExpressions;
using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Game;

/// <summary>
/// Easy Anti-Cheat (EOS) check before a launch. A game that ships EAC carries its bootstrapper in an <c>EasyAntiCheat</c>
/// folder next to a <c>Settings.json</c> holding the product id (easy.ac's generic command:
/// <c>EasyAntiCheat_EOS_Setup.exe install &lt;productid&gt;</c>). Profiles pin the path and a fallback id (Fortnite:
/// <c>FortniteGame\Binaries\Win64\EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe install prod-fn</c>). The setup runs elevated
/// once, only when the EAC service is missing, only from inside the game folder and only when Epic-signed.
/// </summary>
internal sealed partial class EacService
{
    public static readonly string[] ServiceNames = ["EasyAntiCheat_EOS", "EasyAntiCheat"];
    private static readonly string[] SetupNames = ["EasyAntiCheat_EOS_Setup.exe", "EasyAntiCheat_Setup.exe"];

    private readonly ServiceContext _c;
    private bool _knownInstalled;

    public EacService(ServiceContext context) => _c = context;

    public bool IsInstalled()
    {
        if (_knownInstalled)
        {
            return true;
        }

        try
        {
            _knownInstalled = ServiceNames.Any(_c.Options.ServiceQuery.IsInstalled);
        }
        catch (Exception e)
        {
            _c.Log.Warn("eac", "service query failed", e);
        }

        return _knownInstalled;
    }

    /// <summary>Returns true if Glider ran the EAC setup now. Throws EacSetupFailed if UAC was declined or setup failed.</summary>
    public async Task<bool> EnsureInstalledAsync(string app, string gamePath, Action onInstalling, CancellationToken ct)
    {
        if (IsInstalled())
        {
            return false;
        }

        if (ResolveSetup(app, gamePath) is not var (exe, args))
        {
            return false; // no Easy Anti-Cheat in this game
        }

        if (!File.Exists(exe))
        {
            // Nothing we can run; the game's own bootstrapper will report EAC problems itself.
            _c.Log.Warn("eac", $"EAC service missing and no setup at {exe}; launching anyway");
            return false;
        }

        // Elevating a file from a user-writable folder: it must stay inside the game folder and be Epic-signed, or any
        // program of this user could swap it and borrow Glider's UAC prompt.
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gamePath)) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(exe).StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new GliderException(Errors.EacSetupFailed($"The anti-cheat setup path points outside the game folder ({exe})."));
        }

        if (_c.Options.PublisherCheck.WhyUntrusted(exe) is { } why)
        {
            _c.Log.Error("eac", $"Not running {exe} elevated: {why}.");
            throw new GliderException(Errors.EacSetupFailed($"Glider won't run {Path.GetFileName(exe)} as administrator because {why}. Repair the game to restore it."));
        }

        onInstalling();
        _c.Log.Info("eac", $"installing Easy Anti-Cheat for {app}: {Path.GetFileName(exe)} {args}");
        var exit = await _c.Host.RunElevatedAsync(exe, args, ct);
        if (exit is null)
        {
            throw new GliderException(Errors.EacSetupFailed("Windows permission prompt was declined"));
        }

        if (exit != 0)
        {
            throw new GliderException(Errors.EacSetupFailed($"EasyAntiCheat setup exited with code {exit}"));
        }

        _knownInstalled = true;
        return true;
    }

    /// <summary>The setup and its arguments, or null when the game ships no Easy Anti-Cheat (or no usable product id).</summary>
    internal static (string Exe, string Args)? ResolveSetup(string app, string gamePath)
    {
        var profile = GameProfiles.For(app);
        if (profile.Eac is { } eac)
        {
            var exe = Path.Combine(gamePath, GameProfiles.Normalize(eac.SetupRelative));
            var id = ProductId(Path.GetDirectoryName(exe)!);
            return (exe, $"install {id ?? eac.DefaultProductId}");
        }

        if (GameProfiles.FindAntiCheatFolder(gamePath, "EasyAntiCheat", "Settings.json") is not { } folder)
        {
            return null;
        }

        var setup = SetupNames.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);
        var productId = ProductId(folder);
        return setup is null || productId is null ? null : (setup, $"install {productId}");
    }

    private static string? ProductId(string folder)
    {
        var id = JsonFiles.ReadString(Path.Combine(folder, "Settings.json"), "productid");
        return id is not null && SafeId().IsMatch(id) ? id : null;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeId();
}
