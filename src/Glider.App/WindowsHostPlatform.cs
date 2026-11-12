using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Glider.App.Native;
using Glider.Core;
using Glider.Core.Contracts;
using Microsoft.Win32;

namespace Glider.App;

/// <summary>
/// <see cref="IHostPlatform"/> for Windows. Every member is safe to call from any thread: UI work is marshalled to the
/// WPF dispatcher; the synchronous getters read cached state and never block.
/// </summary>
internal sealed class WindowsHostPlatform : IHostPlatform
{
    private readonly HostRuntime _rt;
    private readonly EpicLogin _login;
    private volatile MainWindow? _window;
    private volatile TrayIcon? _tray;

    public WindowsHostPlatform(HostRuntime rt)
    {
        _rt = rt;
        _login = new EpicLogin(rt);
    }

    private Dispatcher Ui => _rt.Ui;

    public void Attach(MainWindow window, TrayIcon tray)
    {
        _window = window;
        _tray = tray;
    }

    public bool IsWindows11 => HostEnvironment.IsWindows11;

    public BackdropKind Backdrop => HostEnvironment.ActiveBackdrop;

    // ---------------------------------------------------------------- auth

    public Task<string?> AcquireEpicAuthorizationCodeAsync(CancellationToken ct) =>
        Ui.InvokeAsync(() => _login.AcquireAsync(_window, ct)).Task.Unwrap();

    public Task ClearLoginSessionAsync() => Ui.InvokeAsync(() => _login.ClearSessionAsync()).Task.Unwrap();

    // ---------------------------------------------------------------- shell

    public Task<string?> PickFolderAsync(string? initialPath, string? title) =>
        Ui.InvokeAsync(() =>
        {
            var dialog = new OpenFolderDialog { Multiselect = false };
            if (!string.IsNullOrWhiteSpace(title))
            {
                dialog.Title = title;
            }

            if (!string.IsNullOrWhiteSpace(initialPath) && Directory.Exists(initialPath))
            {
                dialog.InitialDirectory = initialPath;
            }

            var owner = _window is { IsVisible: true } w ? w : null;
            var ok = owner is not null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            return ok == true ? dialog.FolderName : null;
        }).Task;

    public void OpenUrl(string url) => WebViewSetup.OpenExternalSafe(url);

    /// <summary>Full path: with UseShellExecute=false a bare "explorer.exe" is looked up in the app and current directories first.</summary>
    private static string ExplorerExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Folders open in Explorer; files are only revealed (selected), never executed.</summary>
    public void OpenPath(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo(ExplorerExe) { ArgumentList = { Path.GetFullPath(path) }, UseShellExecute = false })?.Dispose();
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(ExplorerExe) { ArgumentList = { "/select," + Path.GetFullPath(path) }, UseShellExecute = false })?.Dispose();
            }
            else
            {
                HostLog.Warn($"OpenPath: not found: {path}");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException)
        {
            HostLog.Warn("OpenPath failed", ex);
        }
    }

    public void SetClipboardText(string text) =>
        Ui.BeginInvoke(() =>
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(text, true);
                    return;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    Thread.Sleep(40); // clipboard briefly locked by another app
                }
            }

            HostLog.Warn("Clipboard stayed locked; text not copied");
        });

    public void ShowToast(string title, string message) => Ui.BeginInvoke(() => _tray?.ShowBalloon(title, message));

    public Task<int?> RunElevatedAsync(string exe, string arguments, CancellationToken ct) =>
        Task.Run(async () =>
        {
            try
            {
                var psi = new ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
                using var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
                return (int?)p.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: user declined the UAC prompt
            {
                return null;
            }
        }, ct);

    public void SetStartWithWindows(bool enabled) => StartupRegistration.Set(enabled);

    // ---------------------------------------------------------------- window

    public void MinimizeWindow() => Ui.BeginInvoke(() => _window?.MinimizeWindow());

    public void ToggleMaximizeWindow() => Ui.BeginInvoke(() => _window?.ToggleMaximize());

    public void CloseWindow(bool toTray)
    {
        if (toTray)
        {
            HideToTray();
        }
        else
        {
            Quit();
        }
    }

    public void HideToTray() => Ui.BeginInvoke(() => _window?.HideToTray());

    public void RestoreWindow() => Ui.BeginInvoke(() => _window?.RestoreFromTray());

    public void RevealWindow() => Ui.BeginInvoke(() => _window?.Reveal());

    public WindowInfo GetWindowInfo() => _window?.Info ?? new WindowInfo(false, false);

    public void Quit()
    {
        Ui.BeginInvoke(() =>
        {
            if (_window is { } w)
            {
                w.QuitNow();
            }
            else
            {
                Application.Current.Shutdown();
            }
        });
    }
}
