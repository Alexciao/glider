using System.Text.RegularExpressions;
using Glider.Core.Contracts;
using Glider.Core.Services;

namespace Glider.Core.Game;

/// <summary>
/// Easy Anti-Cheat (EOS) check before the first launch. Fortnite ships its own bootstrapper:
/// <c>FortniteGame\Binaries\Win64\EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe install prod-fn</c>
/// (Epic's help article "How do I install Easy Anti-Cheat on my computer to play Fortnite?" — high confidence).
/// The product id is read from the adjacent <c>Settings.json</c> (<c>productid</c>) when present, which is how
/// easy.ac documents the generic command; <c>prod-fn</c> is the fallback. If the live manifest lists an EAC
/// prerequisite (`info --json` → manifest.prerequisites), its path/args win.
/// </summary>
internal sealed partial class EacService
{
    public static readonly string[] ServiceNames = ["EasyAntiCheat_EOS", "EasyAntiCheat"];
    public const string SetupRelative = @"FortniteGame\Binaries\Win64\EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe";
    public const string DefaultProductId = "prod-fn";

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
    public async Task<bool> EnsureInstalledAsync(string gamePath, Action onInstalling, CancellationToken ct)
    {
        if (IsInstalled())
        {
            return false;
        }

        var (exe, args) = ResolveSetup(gamePath);
        if (!File.Exists(exe))
        {
            // Nothing we can run; the game's own bootstrapper will report EAC problems itself.
            _c.Log.Warn("eac", $"EAC service missing and no setup at {exe}; launching anyway");
            return false;
        }

        onInstalling();
        _c.Log.Info("eac", $"installing Easy Anti-Cheat: {Path.GetFileName(exe)} {args}");
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

    internal (string Exe, string Args) ResolveSetup(string gamePath)
    {
        if (_c.LastGameInfo?.Prerequisites is { Path: { Length: > 0 } p } pre
            && p.Contains("EasyAntiCheat", StringComparison.OrdinalIgnoreCase))
        {
            return (Combine(gamePath, p), pre.Args ?? $"install {DefaultProductId}");
        }

        var exe = Combine(gamePath, SetupRelative);
        var settings = Path.Combine(Path.GetDirectoryName(exe)!, "Settings.json");
        var id = JsonFiles.ReadString(settings, "productid");
        return (exe, $"install {(id is not null && SafeId().IsMatch(id) ? id : DefaultProductId)}");
    }

    private static string Combine(string root, string relative) =>
        Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar));

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeId();
}
