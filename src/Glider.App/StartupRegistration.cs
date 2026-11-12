using Microsoft.Win32;
using Velopack.Locators;

namespace Glider.App;

/// <summary>HKCU Run entry for "start with Windows". Points at Velopack's stable root stub so it survives updates.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                key.SetValue(HostConstants.RunKeyValueName, $"\"{ResolveLaunchPath()}\" --background", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(HostConstants.RunKeyValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            HostLog.Warn("Could not update the Run key", ex);
        }
    }

    /// <summary>
    /// Installed: <c>%LocalAppData%\Glider\Glider.exe</c> (the stub Velopack names after --packTitle; it forwards to current\Glider.exe
    /// and is the one path that does not move on update). Dev / portable: the running exe.
    /// </summary>
    public static string ResolveLaunchPath()
    {
        try
        {
            if (VelopackLocator.IsCurrentSet && VelopackLocator.Current is { } loc && !string.IsNullOrEmpty(loc.RootAppDir))
            {
                var stub = Path.Combine(loc.RootAppDir, "Glider.exe");
                if (File.Exists(stub))
                {
                    return stub;
                }
            }
        }
        catch (Exception ex)
        {
            HostLog.Warn("Velopack locator unavailable; using the current exe for autostart", ex);
        }

        return Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Glider.exe");
    }
}
