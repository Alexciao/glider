using System.Text.Json;
using Glider.Core;

namespace Glider.App;

/// <summary>
/// Reads the two settings the host needs BEFORE Core is up (theme for the first paint, start-in-tray) straight from
/// state\settings.json (camelCase, written by Core's settings store). Missing/unreadable = defaults.
/// </summary>
internal static class HostSettingsPeek
{
    public static (ThemeChoice Theme, bool StartMinimized) Read(AppPaths paths)
    {
        try
        {
            var path = Path.Combine(paths.State, "settings.json");
            if (!File.Exists(path))
            {
                return (ThemeChoice.System, false);
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var theme = root.TryGetProperty("theme", out var t) && t.ValueKind == JsonValueKind.String ? ThemeState.Parse(t.GetString()) : ThemeChoice.System;
            var minimized = root.TryGetProperty("startMinimized", out var m) && m.ValueKind == JsonValueKind.True;
            return (theme, minimized);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            HostLog.Warn("Could not peek settings.json", ex);
            return (ThemeChoice.System, false);
        }
    }
}
