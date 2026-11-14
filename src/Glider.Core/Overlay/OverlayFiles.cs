using System.Text.Json;
using Glider.Core.Legendary;

namespace Glider.Core.Overlay;

/// <summary>Legendary's own record of the overlay it manages: <c>overlay_install.json</c> (an InstalledGame dump).</summary>
internal sealed record OverlayInstall(string Version, string InstallPath);

/// <summary>Legendary's cached "latest overlay build" (<c>overlay_version.json</c>, refreshed at most every 24 h by its login()).</summary>
internal sealed record OverlayLatest(string? Version, DateTimeOffset? CheckedAt);

/// <summary>
/// Reads the overlay files in Legendary's config dir without spawning it (lfs/lgndry.py:438-477). Glider never writes them:
/// <c>eos-overlay install/update/remove</c> own that.
/// </summary>
internal static class OverlayFiles
{
    /// <summary>The EOS overlay's Windows library; a folder is an overlay install iff it holds this file (core.py is_overlay_install).</summary>
    public const string Dll = "EOSOVH-Win64-Shipping.dll";

    public static string InstallJson(string config) => Path.Combine(config, "overlay_install.json");

    public static string VersionJson(string config) => Path.Combine(config, "overlay_version.json");

    public static bool IsOverlayFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(path, Dll));
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The managed install, or null when there is none (or the file is unreadable: treated as absent, like Legendary does).</summary>
    public static OverlayInstall? ReadInstall(string config)
    {
        using var doc = LegendaryFiles.ReadJson(InstallJson(config));
        if (doc is null)
        {
            return null;
        }

        var root = doc.RootElement;
        var path = LegendaryFiles.NullIfEmpty(LegendaryFiles.Str(root, "install_path"));
        return path is null ? null : new OverlayInstall(LegendaryFiles.Str(root, "version") ?? "", path);
    }

    public static OverlayLatest ReadLatest(string config)
    {
        using var doc = LegendaryFiles.ReadJson(VersionJson(config));
        if (doc is null)
        {
            return new OverlayLatest(null, null);
        }

        var root = doc.RootElement;
        var version = LegendaryFiles.Obj(root, "data") is { } data ? LegendaryFiles.NullIfEmpty(LegendaryFiles.Str(data, "buildVersion")) : null;
        DateTimeOffset? at = null;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("last_update", out var t) && t.ValueKind == JsonValueKind.Number &&
            t.TryGetDouble(out var seconds) && seconds > 0)
        {
            at = DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        }

        return new OverlayLatest(version, at);
    }
}
