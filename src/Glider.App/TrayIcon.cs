using System.Runtime.InteropServices;
using Glider.App.Native;
using Glider.Core.Tray;

namespace Glider.App;

/// <summary>
/// Notification-area icon via raw Shell_NotifyIcon (no WinForms, no extra package: H.NotifyIcon would add a dependency and a
/// WPF visual tree for something a 150-line message window does). Also provides toasts (NIF_INFO balloons, rendered by
/// Windows 10/11 as native toasts) and the context menu (native popup menu, dark-aware).
/// </summary>
internal sealed class TrayIcon : NativeWindow
{
    private const uint CallbackMessage = Win32.WM_USER + 1;
    private const uint IdFirst = 100;

    private readonly string _iconPath;
    private readonly Action _open;
    private readonly Func<TrayMenu> _buildMenu;
    private readonly Action<TrayItem> _run;
    private readonly uint _taskbarCreated;
    private nint _icon;
    private bool _added;
    private string _tooltip = "Glider";

    /// <param name="buildMenu">Called when the menu opens: the pure <see cref="TrayMenuModel"/> over the latest snapshot and library.</param>
    /// <param name="run">Runs the chosen item (never called for separators and info lines).</param>
    public TrayIcon(string iconPath, Action open, Func<TrayMenu> buildMenu, Action<TrayItem> run)
    {
        _iconPath = iconPath;
        _open = open;
        _buildMenu = buildMenu;
        _run = run;
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
        CopyTo(d.Tip, 128, _tooltip);
        _added = Win32.Shell_NotifyIcon(Win32.NIM_ADD, ref d);
        HostLog.Info($"tray icon added: {_added}");
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

    /// <summary>Updates the hover tooltip (UI thread). The caller throttles; unchanged text is skipped here.</summary>
    public unsafe void SetTooltip(string text)
    {
        if (text == _tooltip)
        {
            return;
        }

        _tooltip = text;
        if (!_added)
        {
            return;
        }

        var d = NewData(Win32.NIF_TIP | Win32.NIF_SHOWTIP);
        CopyTo(d.Tip, 128, text);
        Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref d);
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
        var ok = Win32.Shell_NotifyIcon(Win32.NIM_MODIFY, ref d);
        HostLog.Info($"balloon \"{title}\": {ok}");
    }

    private void ShowMenu(int x, int y)
    {
        TrayMenu model;
        try
        {
            model = _buildMenu();
        }
        catch (Exception ex)
        {
            HostLog.Warn("Tray menu model failed", ex);
            model = new TrayMenu([TrayItem.Cmd("Open Glider", TrayCommand.Open, isDefault: true), TrayItem.Separator, TrayItem.Cmd("Quit Glider", TrayCommand.Quit)], "Glider");
        }

        var menu = Win32.CreatePopupMenu();
        try
        {
            var commands = new List<TrayItem>();
            Append(menu, model.Items, commands);

            // Required dance so the menu dismisses when the user clicks elsewhere.
            Win32.SetForegroundWindow(Handle);
            var cmd = Win32.TrackPopupMenuEx(menu, Win32.TPM_RETURNCMD | Win32.TPM_RIGHTBUTTON | Win32.TPM_BOTTOMALIGN, x, y, Handle, 0);
            Win32.PostMessage(Handle, 0, 0, 0);
            var index = (long)(uint)cmd - IdFirst;
            if (cmd != 0 && index >= 0 && index < commands.Count)
            {
                _run(commands[(int)index]);
            }
        }
        finally
        {
            Win32.DestroyMenu(menu); // also destroys the submenus
        }
    }

    /// <summary>Adds the model's items; each command gets the next id so the chosen id maps back to its <see cref="TrayItem"/>.</summary>
    private static void Append(nint menu, IReadOnlyList<TrayItem> items, List<TrayItem> commands)
    {
        foreach (var item in items)
        {
            var label = item.Label.Replace("&", "&&", StringComparison.Ordinal);
            switch (item.Kind)
            {
                case TrayItemKind.Separator:
                    Win32.AppendMenu(menu, Win32.MF_SEPARATOR, 0, null);
                    break;
                case TrayItemKind.Info:
                    Win32.AppendMenu(menu, Win32.MF_STRING | Win32.MF_GRAYED, 0, label);
                    break;
                case TrayItemKind.Submenu:
                {
                    var sub = Win32.CreatePopupMenu();
                    Append(sub, item.Children ?? [], commands);
                    Win32.AppendMenu(menu, Win32.MF_POPUP | Win32.MF_STRING, (nuint)sub, label);
                    break;
                }

                default:
                {
                    var id = IdFirst + (uint)commands.Count;
                    commands.Add(item);
                    Win32.AppendMenu(menu, Win32.MF_STRING | (item.Checked ? Win32.MF_CHECKED : 0), id, label);
                    if (item.Default)
                    {
                        Win32.SetMenuDefaultItem(menu, id, 0);
                    }

                    break;
                }
            }
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
