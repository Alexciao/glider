using Glider.App.Native;

namespace Glider.App;

/// <summary>
/// Two tiny child HWNDs laid over the WebView2 (verified in the spike: hit-testing only works on a child HWND,
/// not on the top-level, because the WebView2 HWND covers the client area):
/// <list type="bullet">
/// <item><see cref="MaxButtonOverlay"/> over the HTML maximize button: returns HTMAXBUTTON so Windows 11 shows the Snap Layouts flyout.</item>
/// <item><see cref="TopEdgeOverlay"/> along the top edge: returns HTTOP/HTTOPLEFT/HTTOPRIGHT so the window can be resized from the top
/// (left/right/bottom resize borders live in the frame Windows keeps outside the client area, see MainWindow.Chrome).</item>
/// </list>
/// </summary>
internal sealed class CaptionOverlays : IDisposable
{
    private readonly nint _parent;
    private readonly TopEdgeOverlay _top;
    private readonly MaxButtonOverlay _max;

    private int _maxRightInsetCss = HostConstants.MaxButtonRightInset;
    private int _maxWidthCss = HostConstants.CaptionButtonWidth;
    private int _maxHeightCss = HostConstants.CaptionButtonHeight;

    public CaptionOverlays(nint parent, Action toggleMaximize, Action<string, string> captionEvent)
    {
        _parent = parent;
        _top = new TopEdgeOverlay(parent);
        _max = new MaxButtonOverlay(parent, toggleMaximize, captionEvent);
    }

    /// <summary>The frontend can override where its maximize button is (CSS px) via the "host"/"chrome" message.</summary>
    public void Configure(int rightInsetCss, int widthCss, int heightCss)
    {
        _maxRightInsetCss = Math.Clamp(rightInsetCss, 0, 600);
        _maxWidthCss = Math.Clamp(widthCss, 8, 200);
        _maxHeightCss = Math.Clamp(heightCss, 8, 200);
    }

    public void Layout(bool maximized)
    {
        if (!Win32.GetClientRect(_parent, out var client))
        {
            return;
        }

        var dpi = Win32.GetDpiForWindow(_parent);
        if (dpi == 0)
        {
            dpi = 96;
        }

        int Scale(int css) => (int)Math.Round(css * dpi / 96.0);

        var maxW = Scale(_maxWidthCss);
        var maxH = Scale(_maxHeightCss);
        Win32.SetWindowPos(_max.Handle, Win32.HWND_TOP, client.Width - Scale(_maxRightInsetCss) - maxW, 0, maxW, maxH, Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

        if (maximized)
        {
            Win32.SetWindowPos(_top.Handle, 0, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_HIDEWINDOW);
        }
        else
        {
            _top.CornerWidth = Scale(14);
            Win32.SetWindowPos(_top.Handle, Win32.HWND_TOP, 0, 0, client.Width, Scale(HostConstants.TopResizeStrip), Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
            // keep the max-button overlay above the top strip
            Win32.SetWindowPos(_max.Handle, Win32.HWND_TOP, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
    }

    /// <summary>Re-stack above the WebView2 child windows (call after the controller exists and after navigations).</summary>
    public void Raise()
    {
        Win32.SetWindowPos(_top.Handle, Win32.HWND_TOP, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        Win32.SetWindowPos(_max.Handle, Win32.HWND_TOP, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    public nint MaxHandle => _max.Handle;

    public nint TopHandle => _top.Handle;

    public void Dispose()
    {
        _max.Dispose();
        _top.Dispose();
    }

    private abstract class OverlayBase : NativeWindow
    {
        protected OverlayBase(string className, nint parent)
        {
            CreateWindow(className, Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPSIBLINGS, 0, parent, 0, 0, 1, 1);
        }

        protected override nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
        {
            // Never paint: the overlay is pure hit-testing. (Verified not to draw a black box, see docs/perf-host.md / host.md.)
            if (msg == Win32.WM_ERASEBKGND)
            {
                return 1;
            }

            return base.WndProc(hwnd, msg, wParam, lParam);
        }
    }

    private sealed class TopEdgeOverlay : OverlayBase
    {
        private readonly nint _parent;

        public TopEdgeOverlay(nint parent) : base("Glider.TopEdge", parent) => _parent = parent;

        public int CornerWidth { get; set; } = 14;

        protected override nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
        {
            switch (msg)
            {
                case Win32.WM_NCHITTEST:
                    {
                        var pt = new POINT { X = Win32.LoWord(lParam), Y = Win32.HiWord(lParam) };
                        Win32.ScreenToClient(hwnd, ref pt);
                        Win32.GetClientRect(hwnd, out var rc);
                        if (pt.X < CornerWidth)
                        {
                            return Win32.HTTOPLEFT;
                        }

                        return pt.X >= rc.Width - CornerWidth ? Win32.HTTOPRIGHT : Win32.HTTOP;
                    }

                // A child cannot run the sizing loop for its parent: forward the press so the top-level's DefWindowProc starts it.
                case Win32.WM_NCLBUTTONDOWN:
                    Win32.SendMessage(_parent, Win32.WM_NCLBUTTONDOWN, wParam, lParam);
                    return 0;
            }

            return base.WndProc(hwnd, msg, wParam, lParam);
        }
    }

    private sealed class MaxButtonOverlay : OverlayBase
    {
        private readonly Action _toggle;
        private readonly Action<string, string> _event;
        private bool _tracking;
        private string _state = "leave";

        public MaxButtonOverlay(nint parent, Action toggle, Action<string, string> captionEvent) : base("Glider.MaxButton", parent)
        {
            _toggle = toggle;
            _event = captionEvent;
        }

        private void SetState(string state)
        {
            if (_state != state)
            {
                _state = state;
                _event("max", state);
            }
        }

        protected override nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam)
        {
            switch (msg)
            {
                case Win32.WM_NCHITTEST:
                    return Win32.HTMAXBUTTON;

                case Win32.WM_NCMOUSEMOVE:
                    if (!_tracking)
                    {
                        var tme = new TRACKMOUSEEVENT { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<TRACKMOUSEEVENT>(), Flags = Win32.TME_LEAVE | Win32.TME_NONCLIENT, Track = hwnd };
                        _tracking = Win32.TrackMouseEvent(ref tme);
                    }

                    if (_state != "down")
                    {
                        SetState("hover");
                    }

                    // Let the system see the move so the Snap Layouts flyout logic keeps working.
                    return base.WndProc(hwnd, msg, wParam, lParam);

                case Win32.WM_NCMOUSELEAVE:
                    _tracking = false;
                    SetState("leave");
                    return 0;

                case Win32.WM_NCLBUTTONDOWN:
                case Win32.WM_NCLBUTTONDBLCLK:
                    SetState("down");
                    return 0;

                case Win32.WM_NCLBUTTONUP:
                    if (_state == "down")
                    {
                        SetState("hover");
                        _toggle();
                    }

                    return 0;
            }

            return base.WndProc(hwnd, msg, wParam, lParam);
        }
    }
}
