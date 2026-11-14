using Glider.Core;
using Glider.Core.Infrastructure.Migrations;
using Glider.Core.Links;
using Glider.Core.Shortcuts;
using Velopack.Locators;

namespace Glider.App;

/// <summary>The real shell for <see cref="IShellIntegration"/>: per-user Desktop and Start-menu folders, HKCU, IShellLink.</summary>
internal sealed class WindowsShellIntegration : IShellIntegration
{
    public WindowsShellIntegration(string? launchExePath = null, bool? managesGliderScheme = null)
    {
        LaunchExePath = launchExePath ?? StartupRegistration.ResolveLaunchPath();
        ManagesGliderScheme = managesGliderScheme ?? IsVelopackInstall();
    }

    public string LaunchExePath { get; }

    public string? DesktopFolder => NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

    public string? StartMenuFolder => NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.Programs));

    public IShellLinks Links { get; } = new WindowsShellLinks();

    public IRegistryAccess Registry { get; } = new WindowsRegistry();

    public bool ManagesGliderScheme { get; }

    private static string? NonEmpty(string path) => string.IsNullOrWhiteSpace(path) ? null : path;

    private static bool IsVelopackInstall()
    {
        try
        {
            return VelopackLocator.IsCurrentSet && VelopackLocator.Current is { } loc && !string.IsNullOrEmpty(loc.RootAppDir) && !loc.IsPortable;
        }
        catch (Exception ex)
        {
            HostLog.Warn("Velopack locator unavailable; protocol registration is not managed", ex);
            return false;
        }
    }
}

/// <summary>
/// What the installer/uninstaller hooks do (Velopack runs them in a separate short-lived process before the app starts): register
/// <c>glider://</c> on install and update, and on uninstall remove it, hand the Epic link handler back and delete the shortcuts
/// Glider created. Every step is best effort and never blocks setup. Glider's data is outside the install root (schema 3), so
/// uninstall keeps it; the hook only finishes a data move this install never got to make.
/// </summary>
internal static class ShellRegistration
{
    public static void OnInstalledOrUpdated()
    {
        try
        {
            var paths = AppPaths.ForCurrentUser();
            Registrar(paths).EnsureGliderScheme();
        }
        catch (Exception ex)
        {
            HostLog.Warn("glider:// registration failed", ex);
        }
    }

    public static void OnUninstalling()
    {
        var moved = MoveDataOutOfInstallRoot();
        var paths = AppPaths.ForCurrentUser();
        var outside = !string.Equals(paths.Root, DataRoot.LegacyRootFor(DataRoot.LocalAppDataFolder), StringComparison.OrdinalIgnoreCase);
        if (outside)
        {
            HostLog.Init(paths.Logs);
            foreach (var message in moved?.Messages ?? [])
            {
                HostLog.Info("data root: " + message);
            }

            HostLog.Info($"Glider is being uninstalled; its data in {paths.Root} is kept (delete that folder to remove it).");
        }

        var shell = new WindowsShellIntegration();
        try
        {
            var registrar = Registrar(paths, shell);
            registrar.RemoveGliderScheme();
            registrar.DisableEpicHandler();
        }
        catch (Exception ex)
        {
            HostLog.Warn("Removing the protocol registrations failed", ex);
        }

        try
        {
            ShortcutCleanup.RemoveAllOurs(shell.Links, shell.LaunchExePath, shell.DesktopFolder, shell.StartMenuFolder);
        }
        catch (Exception ex)
        {
            HostLog.Warn("Removing the shortcuts failed", ex);
        }
    }

    /// <summary>
    /// Data still in the install root (this version never started, or its move was deferred) would be deleted with it: move it now,
    /// renames only (no time for a cross-volume copy here), and only when no Glider is running (a running one moved it at its start).
    /// </summary>
    private static DataRootMoveResult? MoveDataOutOfInstallRoot()
    {
        try
        {
            using var mutex = new Mutex(true, HostConstants.MainMutexName, out var first);
            if (!first)
            {
                return null;
            }

            try
            {
                return DataRoot.Prepare(null, new DataRootMoverOptions { AllowCopy = false, OverlayRegistry = new Glider.Core.Overlay.WindowsOverlayRegistry() });
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            HostLog.Warn("Moving the data out of the install root before uninstall failed", ex);
            return null;
        }
    }

    private static ProtocolRegistrar Registrar(AppPaths paths, WindowsShellIntegration? shell = null)
    {
        shell ??= new WindowsShellIntegration();
        return new ProtocolRegistrar(shell.Registry, shell.LaunchExePath, Path.Combine(paths.State, "protocol-backup.json"), HostLog.Info);
    }
}
