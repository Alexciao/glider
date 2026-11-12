using Glider.Core.Contracts;

namespace Glider.App;

internal static class HostEnvironment
{
    private static readonly int Build = Environment.OSVersion.Version.Build;

    /// <summary>Windows 11 (build 22000+).</summary>
    public static bool IsWindows11 => Build >= 22000;

    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE needs Windows 11 22621+.</summary>
    public static bool SupportsMica => Build >= 22621;

    /// <summary>What the window actually ended up with (updated once the DWM call has been made).</summary>
    public static volatile BackdropKind ActiveBackdrop = SupportsMica ? BackdropKind.Mica : BackdropKind.Solid;
}
