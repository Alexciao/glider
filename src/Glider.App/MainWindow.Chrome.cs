using System.Text.Json;
using System.Windows.Media;
using Glider.App.Native;
using Glider.Core.Contracts;

namespace Glider.App;

// Frame, hit-testing, Mica, window messages.
//
// Frame strategy (same idea as Windows Terminal): keep WS_OVERLAPPEDWINDOW (so Aero Snap, Win+arrows, DWM shadow, rounded
// corners and the *invisible* left/right/bottom resize borders all work) and make WM_NCCALCSIZE give the page the whole window
// except those standard borders. Only the title bar is removed. The top edge cannot be reached that way (nothing is
// left outside the client area there), so a thin child-HWND strip (CaptionOverlays) provides top resizing.
internal sealed partial class MainWindow
{
    private const string ImmersiveColorSet = "ImmersiveColorSet";

    private unsafe nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch ((uint)msg)
        {
            case Win32.WM_NCCALCSIZE when wParam != 0:
                OnNcCalcSize(hwnd, wParam, lParam);
                handled = true;
                return 0;

            case Win32.WM_SIZE:
                OnSize((int)wParam);
                break;

            case Win32.WM_WINDOWPOSCHANGED:
                _controller?.NotifyParentWindowPositionChanged();
                if (_revealed && IsVisible && !_minimized)
                {
                    _placementTimer.Stop();
                    _placementTimer.Start();
                }

                break;

            case Win32.WM_DPICHANGED:
                Dispatcher.BeginInvoke(() => _overlays?.Layout(_maximized));
                break;

            case Win32.WM_ACTIVATE:
                _focused = (wParam & 0xFFFF) != 0;
                if (_focused)
                {
                    FocusWeb();
                }

                PublishWindowInfo();
                break;

            case Win32.WM_SETFOCUS:
                FocusWeb();
                break;

            case Win32.WM_SETTINGCHANGE:
                if (lParam != 0 && System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) == ImmersiveColorSet && ThemeState.Mode == ThemeChoice.System)
                {
                    Dispatcher.BeginInvoke(ApplyBackdrop);
                }

                break;

            case Win32.WM_ENDSESSION:
                if (wParam != 0)
                {
                    WindowPlacementStore.Save(_rt.Paths, _hwnd);
                }

                break;
        }

        return 0;
    }

    private static unsafe void OnNcCalcSize(nint hwnd, nint wParam, nint lParam)
    {
        var p = (NCCALCSIZE_PARAMS*)lParam;
        var original = p->Rect0;

        // Let Windows compute the standard client rect (left/right/bottom frame, caption)…
        Win32.DefWindowProc(hwnd, Win32.WM_NCCALCSIZE, wParam, lParam);

        // …then take the caption back: the page owns the whole top.
        if (Win32.IsZoomed(hwnd))
        {
            var dpi = Win32.GetDpiForWindow(hwnd);
            var frame = Win32.GetSystemMetricsForDpi(Win32.SM_CYSIZEFRAME, dpi) + Win32.GetSystemMetricsForDpi(Win32.SM_CXPADDEDBORDER, dpi);
            p->Rect0.Top = original.Top + frame;
            ReserveAutoHideTaskbar(hwnd, ref p->Rect0);
        }
        else
        {
            // 1 px stays non-client: DWM draws its top border there and Windows still answers HTTOP for it.
            p->Rect0.Top = original.Top + 1;
        }
    }

    /// <summary>A maximized window must leave 2 px at an auto-hide taskbar's edge or the taskbar can never be revealed.</summary>
    private static void ReserveAutoHideTaskbar(nint hwnd, ref RECT client)
    {
        var monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new MONITORINFO { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref mi))
        {
            return;
        }

        foreach (var edge in new[] { Win32.ABE_LEFT, Win32.ABE_TOP, Win32.ABE_RIGHT, Win32.ABE_BOTTOM })
        {
            var abd = new APPBARDATA { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<APPBARDATA>(), Edge = edge, Rect = mi.Monitor };
            if (Win32.SHAppBarMessage(Win32.ABM_GETAUTOHIDEBAREX, ref abd) == 0)
            {
                continue;
            }

            switch (edge)
            {
                case Win32.ABE_LEFT: client.Left += 2; break;
                case Win32.ABE_TOP: client.Top += 2; break;
                case Win32.ABE_RIGHT: client.Right -= 2; break;
                case Win32.ABE_BOTTOM: client.Bottom -= 2; break;
            }
        }
    }

    private void OnSize(int sizeType)
    {
        var wasMinimized = _minimized;
        _minimized = sizeType == Win32.SIZE_MINIMIZED;
        _maximized = sizeType == Win32.SIZE_MAXIMIZED || (_minimized && _maximized);

        if (!_minimized)
        {
            UpdateWebBounds();
            _overlays?.Layout(_maximized);
        }

        if (_minimized && !wasMinimized)
        {
            EnterLowMemory();
        }
        else if (!_minimized && wasMinimized && IsVisible)
        {
            LeaveLowMemory();
        }

        PublishWindowInfo();
    }

    // ============================== backdrop (Mica / solid) ==============================

    /// <summary>
    /// Windows 11 22621+: DWMWA_SYSTEMBACKDROP_TYPE = Mica + immersive dark mode + frame extended over the client area, with a fully
    /// transparent WPF surface and WebView2 background. Otherwise a solid theme-colored window (Windows 10, older 11).
    /// </summary>
    private void ApplyBackdrop()
    {
        var dark = ThemeState.IsDark ? 1 : 0;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        var mica = false;
        if (HostEnvironment.SupportsMica)
        {
            var kind = Win32.DWMSBT_MAINWINDOW;
            mica = Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref kind, sizeof(int)) == 0;
        }

        IsMicaActive = mica;
        var margins = mica ? new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 } : new MARGINS { Left = 1, Right = 1, Top = 1, Bottom = 1 };
        Win32.DwmExtendFrameIntoClientArea(_hwnd, ref margins);

        var argb = ThemeState.BackgroundArgb;
        var solid = Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        Background = mica ? Brushes.Transparent : new SolidColorBrush(solid);
        if (_source?.CompositionTarget is { } target)
        {
            target.BackgroundColor = mica ? Colors.Transparent : solid; // WPF's own surface defaults to opaque black
        }

        ApplyDefaultBackground();
        HostEnvironment.ActiveBackdrop = mica ? BackdropKind.Mica : BackdropKind.Solid;
    }

    // ============================== UI → host control messages ==============================

    /// <summary>{"type":"host","op":"theme","theme":"dark|light|system"} · {"type":"host","op":"chrome","maxRightInset":46,"width":46,"height":40}</summary>
    private void OnHostMessage(JsonElement message)
    {
        if (!message.TryGetProperty("op", out var opEl))
        {
            return;
        }

        switch (opEl.GetString())
        {
            case "theme":
                ThemeState.Mode = ThemeState.Parse(message.TryGetProperty("theme", out var t) ? t.GetString() : null);
                ApplyBackdrop();
                break;

            case "chrome":
                int Get(string name, int fallback) => message.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : fallback;
                _overlays?.Configure(
                    Get("maxRightInset", HostConstants.MaxButtonRightInset),
                    Get("width", HostConstants.CaptionButtonWidth),
                    Get("height", HostConstants.CaptionButtonHeight));
                _overlays?.Layout(_maximized);
                break;
        }
    }

    private void OnCaptionEvent(string button, string state) =>
        _rt.Bridge.Send($"{{\"type\":\"event\",\"event\":\"caption\",\"data\":{{\"button\":\"{button}\",\"state\":\"{state}\"}}}}");

    private void PublishWindowInfo()
    {
        var info = new WindowInfo(_maximized, _focused);
        if (info == _lastPublished)
        {
            return;
        }

        _lastPublished = info;
        _rt.Sink.Publish(RpcEvents.Window, info);
    }
}
