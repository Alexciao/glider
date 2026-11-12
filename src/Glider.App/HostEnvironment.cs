using Glider.App.Native;
using Glider.Core.Contracts;
using Microsoft.Win32;

namespace Glider.App;

internal static class HostEnvironment
{
    private static readonly int Build = Environment.OSVersion.Version.Build;

    /// <summary>Windows 11 (build 22000+).</summary>
    public static bool IsWindows11 => Build >= 22000;

    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE needs Windows 11 22621+.</summary>
    public static bool SupportsMica { get; } = Build >= 22621 && Environment.GetEnvironmentVariable("GLIDER_FORCE_SOLID") != "1"; // env var: test the Windows 10 path

    /// <summary>What the window actually ended up with (updated once the DWM call has been made).</summary>
    public static volatile BackdropKind ActiveBackdrop = SupportsMica ? BackdropKind.Mica : BackdropKind.Solid;

    /// <summary>Settings > Personalization > Colors > "Transparency effects". Off means Windows paints a flat colour instead of Mica.</summary>
    public static bool TransparencyEffectsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int v || v != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    /// <summary>Battery Saver is on (Windows drops Mica while it is).</summary>
    public static bool BatterySaverOn() => Win32.GetSystemPowerStatus(out var status) && status.SystemStatusFlag == 1;
}
