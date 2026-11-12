using Microsoft.Win32;

namespace Glider.App;

internal enum ThemeChoice { System, Dark, Light }

/// <summary>Current theme as the host sees it (drives DWM dark mode, window + WebView2 background, ?theme=).</summary>
internal static class ThemeState
{
    public static ThemeChoice Mode { get; set; } = ThemeChoice.System;

    public static bool IsDark => Mode switch
    {
        ThemeChoice.Dark => true,
        ThemeChoice.Light => false,
        _ => SystemPrefersDark(),
    };

    public static uint BackgroundArgb => IsDark ? HostConstants.DarkBackgroundArgb : HostConstants.LightBackgroundArgb;

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    public static ThemeChoice Parse(string? value) => value?.ToLowerInvariant() switch
    {
        "dark" => ThemeChoice.Dark,
        "light" => ThemeChoice.Light,
        _ => ThemeChoice.System,
    };
}
