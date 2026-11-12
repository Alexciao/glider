using System.Runtime.InteropServices;
using Glider.App.Native;

namespace Glider.App;

/// <summary>
/// Notification-area icon via raw Shell_NotifyIcon (no WinForms, no extra package: H.NotifyIcon would add a dependency and a
/// WPF visual tree for something a 150-line message window does). Also provides toasts (NIF_INFO balloons, rendered by
/// Windows 10/11 as native toasts) and the context menu (native popup menu, dark-aware).
/// </summary>
internal sealed class TrayIcon : NativeWindow
{
    private const uint CallbackMessage = Win32.WM_USER + 1;
    private const uint IdOpen = 1, IdPlay = 2, IdCheckUpdates = 3, IdQuit = 4;

    private readonly string _iconPath;
    private readonly Action _open;
    private readonly Action _play;
    private readonly Action _checkUpdates;
    private readonly Action _quit;
    private readonly uint _taskbarCreated;
    private nint _icon;
    private bool _added;

    public TrayIcon(string iconPath, Action open, Action play, Action checkUpdates, Action quit)
    {
        _iconPath = iconPath;
        _open = open;
        _play = play;
        _checkUpdates = checkUpdates;
        _quit = quit;
        _taskbarCreated = Win32.RegisterWindowMessage("TaskbarCreated");
        CreateWindow("Glider.Tray", 0, 0, Win32.HWND_MESSAGE, 0, 0, 0, 0, "Glider tray");
        TryEnableDarkMenus();
        Add();
    }

    private static void TryEnableDarkMenus()
    {
        try
        {
            _ = Win32.SetPreferredAppMode(1); // AllowDark: popup menus follow the system theme
            Win32.FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            HostLog.Warn("Dark popup menus unavailable", ex);
        }
    }

    private unsafe NOTIFYICONDATAW NewData(uint flags)
    {
        var d = new NOTIFYICONDATAW
        {
            Size = (uint)sizeof(NOTIFYICONDATAW),
            Hwnd = Handle,
            Id = 1,
            Flags = flags,
            CallbackMessage = CallbackMessage,
            Icon = _icon,
        };
        return d;
    }

    private static unsafe void CopyTo(char* dest, int capacity, string text)
    {
        var n = Math.Min(text.Length, capacity - 1);
        for (var i = 0; i < n; i++)
        {
            dest[i] = text[i];
        }

        dest[n] = '\0';
    }

    private unsafe void Add()
    {
        if (_icon == 0)
        {
            var size = Win32.GetSystemMetrics(Win32.SM_CXSMICON);
            _icon = Win32.LoadImage(0, _iconPath, Win32.IMAGE_ICON, size, size, Win32.LR_LOADFROMFILE);
            if (_icon == 0)
            {
                HostLog.Warn($"Tray icon not loaded from {_iconPath}");
            }
        }

        var d = NewData(Win32.NIF_MESSAGE | Win32.NIF_ICON | Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        CopyTo(d.Tip, 128, "Glider");
        _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref d);
        if (_added)
        {
            d.VersionOrTimeout = Win32.NOTIFYICON_VERSION_4;
            Win32.Shell_NotifyIcon(Win32.NIM_SETVERSION, ref d);
        }
        else
        {
            HostLog.Warn("Shell_NotifyIcon(NIM_ADD) failed (explorer not ready?); will retry on TaskbarCreated");
        }
    }

    public unsafe void ShowBalloon(string title, string message)
    {
        if (!_added)
        {
            return;
        }

        var d = NewData(Win32.NIF_INFO);
        CopyTo(d.InfoTitle, 64, title);
        CopyTo(d.Info, 256, message);
        d.InfoFlags = Win32.NIIF_INFO;
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref d);
    }

    private void ShowMenu(int x, int y)
    {
        var menu = Win32.CreatePopupMenu();
        try
        {
            Win32.AppendMenu(menu, Win32.MF_STRING, IdOpen, "Open Glider");
            Win32.AppendMenu(menu, Win32.MF_STRING, IdPlay, "Play");
            Win32.AppendMenu(menu, Win32.MF_STRING, IdCheckUpdates, "Check for updates");
            Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);
            Win32.AppendMenu(menu, Win32.MF_STRING, IdQuit, "Quit");

            // Required dance so the menu dismisses when the user clicks elsewhere.
            Win32.SetForegroundWindow(Handle);
            var cmd = Win32.TrackPopupMenuEx(menu, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON | Win32.TPM_BOTTOMALIGN, x, y, Handle, 0);
            Win32.PostMessage(Handle, 0, 0, 0);
            switch ((uint)cmd)
            {
                case IdOpen: _open(); break;
                case IdPlay: _play(); break;
                case IdCheckUpdates: _checkUpdates(); break;
                case IdQuit: _quit(); break;
            }
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    protected override nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (msg == CallbackMessage)
        {
            switch ((uint)Win32.LoWord(lParam))
            {
                case Win32.WM_LBUTTONUP:
                case Win32.NIN_SELECT:
                case Win32.NIN_BALLOONUSERCLICK:
                    _open();
                    break;
                case Win32.WM_RBUTTONUP:
                case Win32.WM_CONTEXTMENU:
                    ShowMenu(Win32.LoWord(wParam), Win32.HiWord(wParam));
                    break;
            }

            return 0;
        }

        if (msg == _taskbarCreated)
        {
            Add(); // explorer restarted: put the icon back
            return 0;
        }

        return base.WndProc(hwnd, msg, wParam, lParam);
    }

    public unsafe void Remove()
    {
        if (_added)
        {
            var d = NewData(0);
            Win32.Shell_NotifyIcon(Win32.NIM_DELETE, ref d);
            _added = false;
        }

        if (_icon != 0)
        {
            Win32.DestroyIcon(_icon);
            _icon = 0;
        }
    }

    protected override void OnDestroyed() => Remove();
}
