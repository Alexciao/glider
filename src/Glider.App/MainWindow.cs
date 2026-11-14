using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Glider.App.Native;
using Glider.Core.Contracts;
using Microsoft.Web.WebView2.Core;
using DrawingColor = System.Drawing.Color;

namespace Glider.App;

/// <summary>
/// The frameless main window. A code-only WPF <see cref="Window"/> (no XAML/BAML) whose HWND hosts a raw
/// <see cref="CoreWebView2Controller"/> (no WPF WebView2 control, so no HwndHost/airspace plumbing). Frame, resize
/// borders, maximize inset, Mica, Snap Layouts and show-when-ready all live here and in <c>MainWindow.Chrome.cs</c>.
/// All members must be called on the UI thread.
/// </summary>
internal sealed partial class MainWindow : Window
{
    private readonly HostRuntime _rt;
    private nint _hwnd;
    private HwndSource? _source;
    private CaptionOverlays? _overlays;

    private CoreWebView2Controller? _controller;
    private CoreWebView2? _web;
    private bool _webStarted;
    private bool _revealed;
    private bool _quitting;
    private bool _startedHidden;
    private bool _maximized;
    private bool _focused;
    private bool _active = true; // activation state for the backdrop; starts true so the cloaked first frame is never solid
    private bool? _lastDark;
    private bool? _lastTranslucent;
    private bool _minimized;
    private volatile bool _visible; // shown and uncloaked (read from Core threads, so never WPF's IsVisible)
    private WindowInfo? _lastPublished;
    private DateTime _lastRecreate = DateTime.MinValue;

    private readonly DispatcherTimer _safetyTimer = new() { Interval = HostConstants.ReadyTimeout };
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly DispatcherTimer _placementTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public MainWindow(HostRuntime runtime)
    {
        _rt = runtime;
        Title = "Glider";
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        MinWidth = 900;
        MinHeight = 600;
        Width = 1180;
        Height = 760;
        UseLayoutRounding = true;

        _safetyTimer.Tick += (_, _) =>
        {
            _safetyTimer.Stop();
            HostLog.Info("app.ready not received in time; revealing the window anyway");
            Reveal();
        };
        _idleTimer.Tick += async (_, _) =>
        {
            _idleTimer.Stop();
            await SuspendWhenHiddenAsync();
        };
        _placementTimer.Tick += (_, _) =>
        {
            _placementTimer.Stop();
            SavePlacement();
        };
    }

    /// <summary>Test hook, raised on the UI thread just before the window is uncloaked.</summary>
    public Action? BeforeReveal { get; set; }

    public nint Hwnd => _hwnd;

    public bool IsRevealed => _revealed;

    public bool WebStarted => _webStarted;

    public bool IsMicaActive { get; private set; }

    public bool IsMinimized => _minimized;

    /// <summary>The Store view asked to hand keyboard focus back to the UI (Tab out of the Store page).</summary>
    public void FocusUi(bool previous)
    {
        try
        {
            _controller?.MoveFocus(previous ? CoreWebView2MoveFocusReason.Previous : CoreWebView2MoveFocusReason.Next);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("MoveFocus (from the Store) failed", ex);
        }
    }

    public WindowInfo Info => new(_maximized, _focused, _visible && !_minimized);

    public CoreWebView2? Web => _web;

    public CaptionOverlays? Overlays => _overlays;

    /// <summary>Creates the HWND (hidden), applies chrome, and — unless starting in the tray — shows it cloaked and starts the UI.</summary>
    public void Begin(bool startHidden)
    {
        _startedHidden = startHidden;
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        StartupTimeline.Mark("window-handle");
        _source = HwndSource.FromHwnd(_hwnd);
        _source!.AddHook(Hook);

        ApplyBackdrop();
        _overlays = new CaptionOverlays(_hwnd, ToggleMaximize, OnCaptionEvent);
        Win32.SetWindowPos(_hwnd, 0, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE | Win32.SWP_FRAMECHANGED);
        Closing += OnClosing;

        _rt.Bridge.ReadyReceived += OnUiReady;
        _rt.Bridge.HostMessageReceived += OnHostMessage;

        if (!startHidden)
        {
            ShowCloakedAndStart();
        }
    }

    // ============================== show / reveal ==============================

    private void ShowCloakedAndStart()
    {
        // A cloaked window is "visible" to Windows and WebView2 (so the page renders and paints) but DWM does not draw it.
        // It is uncloaked once the UI reports app.ready: no white/black flash, no half-painted frame.
        SetCloak(true);
        // Kick off the controller first: the browser process spins up out-of-process while WPF spends ~200 ms in Show().
        StartWebView();
        Show();
        WindowPlacementStore.Restore(_rt.Paths, _hwnd);
        ShowActivated = true;
        _safetyTimer.Start();
    }

    private void SetCloak(bool cloak)
    {
        var v = cloak ? 1 : 0;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_CLOAK, ref v, sizeof(int));
    }

    private void OnUiReady()
    {
        StartupTimeline.Mark("app-ready");
        Reveal();
    }

    /// <summary>The UI has painted (or the safety timeout fired): show the window.</summary>
    public void Reveal()
    {
        if (_revealed || _quitting)
        {
            return;
        }

        if (_startedHidden && !_webStarted)
        {
            return;
        }

        _revealed = true;
        _safetyTimer.Stop();
        if (!IsVisible)
        {
            return; // hidden to the tray before the reveal: Restore() will show it
        }

        BeforeReveal?.Invoke(); // test hook: grab the frame DWM is about to show
        SetCloak(false);
        _visible = true;
        StartupTimeline.Mark("revealed");
        Win32.SetForegroundWindow(_hwnd);
        FocusWeb();
        PublishWindowInfo();
        HostLog.Info("window revealed");

        // Startup garbage (WPF/JIT/WebView2 init) is dead by now: one compacting GC + working-set trim, off the UI thread.
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => MemoryTrimmer.Trim(), TaskScheduler.Default);
    }

    public void RestoreFromTray()
    {
        _idleTimer.Stop();
        if (!_webStarted)
        {
            // First ever show in a "start in tray" session: build the UI now.
            _revealed = false;
            _startedHidden = false;
            ShowCloakedAndStart();
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        SetCloak(false);
        _revealed = true;
        _visible = true;
        if (Win32.IsIconic(_hwnd))
        {
            Win32.ShowWindow(_hwnd, Win32.SW_RESTORE);
        }

        LeaveLowMemory();
        Win32.SetForegroundWindow(_hwnd);
        Activate();
        FocusWeb();
        PublishWindowInfo();
    }

    public void HideToTray()
    {
        if (!IsVisible)
        {
            return;
        }

        SavePlacement();
        _safetyTimer.Stop();
        _revealed = true;
        _visible = false;
        Hide();
        PublishWindowInfo();
        EnterLowMemory();
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    public void MinimizeWindow() => Win32.ShowWindow(_hwnd, Win32.SW_MINIMIZE);

    public void ToggleMaximize() => Win32.ShowWindow(_hwnd, Win32.IsZoomed(_hwnd) ? Win32.SW_RESTORE : Win32.SW_MAXIMIZE);

    public void QuitNow()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        HostLog.Info("quit requested");
        _safetyTimer.Stop();
        _idleTimer.Stop();
        _placementTimer.Stop();
        SavePlacement();
        _rt.Bridge.Detach();
        _rt.Platform.StoreIfCreated?.Dispose();
        try
        {
            _controller?.Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("Closing the WebView2 controller failed", ex);
        }

        _controller = null;
        _web = null;
        _overlays?.Dispose();
        _overlays = null;
        Application.Current.Shutdown();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_quitting)
        {
            return;
        }

        // Alt+F4 / system close: let Core decide (Settings.CloseToTray) via the same path as the HTML close button.
        e.Cancel = true;
        var rpc = _rt.Rpc;
        if (rpc is null)
        {
            HideToTray();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await rpc.Close().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HostLog.Warn("rpc.Close failed; hiding to tray", ex);
                _ = Dispatcher.BeginInvoke(HideToTray);
            }
        });
    }

    private void SavePlacement()
    {
        if (_hwnd != 0)
        {
            WindowPlacementStore.Save(_rt.Paths, _hwnd);
        }
    }

    // ============================== WebView2 ==============================

    private void StartWebView()
    {
        if (_webStarted)
        {
            return;
        }

        _webStarted = true;
        _ = InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var env = await _rt.EnvTask;
            StartupTimeline.Mark("env-ready");
            var options = env.CreateCoreWebView2ControllerOptions();
            // Set at creation (not after) so the very first frame already has the right background: transparent for Mica, theme color otherwise.
            options.DefaultBackgroundColor = IsMicaActive ? DrawingColor.Transparent : DrawingColor.FromArgb(unchecked((int)ThemeState.BackgroundArgb));
            var controller = await env.CreateCoreWebView2ControllerAsync(_hwnd, options);
            StartupTimeline.Mark("controller-ready");
            HostLog.Info($"WebView2 runtime {env.BrowserVersionString}");
            _controller = controller;
            ApplyDefaultBackground();
            UpdateWebBounds();

            var web = controller.CoreWebView2;
            _web = web;
            WebViewSetup.ConfigureSettings(web.Settings);
            var root = WebViewSetup.ResolveContentRoot(_rt.Paths, out var kind);
            HostLog.Info($"UI content: {kind} ({root})");
            WebViewSetup.MapContent(web, root, _rt.Paths.Cache);
            ArtHandler.Attach(web, _rt.GetArtCacheAsync);
            WebViewSetup.GuardNavigation(web, WebViewSetup.OpenExternalSafe);

            web.WebMessageReceived += (_, e) =>
            {
                if (WebViewSetup.IsAppOrigin(e.Source))
                {
                    _rt.Bridge.HandleIncoming(e.WebMessageAsJson);
                }
            };
            web.NavigationCompleted += (_, e) =>
            {
                StartupTimeline.Mark("navigation-completed");
                if (!e.IsSuccess)
                {
                    HostLog.Warn($"Navigation failed: {e.WebErrorStatus}");
                }

                _overlays?.Raise();
            };
            web.DOMContentLoaded += (_, _) => StartupTimeline.Mark("dom-content-loaded");
            web.ProcessFailed += OnProcessFailed;

            _rt.Bridge.Attach(web);
            _overlays?.Layout(_maximized);
            _overlays?.Raise();
            ApplyBackdrop(); // state may have changed while the environment was starting (power, transparency, theme)
            web.Navigate(WebViewSetup.BuildStartUrl(ThemeState.IsDark, IsMicaActive, _lastTranslucent != false));
        }
        catch (Exception ex)
        {
            HostLog.Error("WebView2 initialisation failed", ex);
            _webStarted = false;
            HostApp.FatalWebViewError(ex);
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        HostLog.Warn($"WebView2 process failed: {e.ProcessFailedKind} ({e.Reason}, exit {e.ExitCode})");
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
            case CoreWebView2ProcessFailedKind.FrameRenderProcessExited:
                Dispatcher.BeginInvoke(() =>
                {
                    // Whatever is queued for the dead page is dropped; the reloaded page loads its state fresh (and numbers its own requests).
                    _rt.Bridge.BeginNewPage();
                    _web?.Reload();
                });
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                if (DateTime.UtcNow - _lastRecreate > TimeSpan.FromSeconds(10))
                {
                    _lastRecreate = DateTime.UtcNow;
                    Dispatcher.BeginInvoke(RecreateWebView);
                }

                break;
        }
    }

    private void RecreateWebView()
    {
        _rt.Bridge.Detach();
        _rt.Bridge.BeginNewPage();
        _ = _rt.Platform.StoreIfCreated?.DestroyAsync(); // it belonged to the dead environment
        try
        {
            _controller?.Close();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("Closing the dead WebView2 controller failed", ex);
        }

        _controller = null;
        _web = null;
        _webStarted = false;
        _rt.RecreateEnvironment(); // the crashed browser process took the environment with it
        StartWebView();
    }

    private void FocusWeb()
    {
        if (_quitting)
        {
            return;
        }

        try
        {
            _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("MoveFocus failed", ex);
        }
    }

    private void UpdateWebBounds()
    {
        var c = _controller;
        if (c is null || !Win32.GetClientRect(_hwnd, out var rc))
        {
            return;
        }

        c.Bounds = new System.Drawing.Rectangle(0, 0, rc.Width, rc.Height);
    }

    private void ApplyDefaultBackground()
    {
        if (_controller is null)
        {
            return;
        }

        // Mica: fully transparent so the backdrop shows through. Solid (Windows 10): the theme color, so nothing ever paints white.
        try
        {
            _controller.DefaultBackgroundColor = IsMicaActive ? DrawingColor.Transparent : DrawingColor.FromArgb(unchecked((int)ThemeState.BackgroundArgb));
        }
        catch (Exception ex) when (ex is NotImplementedException or InvalidCastException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("DefaultBackgroundColor not settable", ex);
        }
    }

    // ============================== memory ==============================

    private void EnterLowMemory()
    {
        var c = _controller;
        var w = _web;
        if (c is null || w is null)
        {
            return;
        }

        try
        {
            c.IsVisible = false;
            w.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("EnterLowMemory failed", ex);
        }

        _rt.Platform.StoreIfCreated?.OnWindowHidden();
    }

    private void LeaveLowMemory()
    {
        var c = _controller;
        var w = _web;
        if (c is null || w is null)
        {
            _rt.Bridge.Hold(false);
            return;
        }

        try
        {
            c.IsVisible = true;
            if (w.IsSuspended)
            {
                w.Resume();
            }

            w.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("LeaveLowMemory failed", ex);
        }

        _rt.Bridge.Hold(false);
        _rt.Platform.StoreIfCreated?.OnWindowShown();
    }

    /// <summary>After ~8 s fully hidden: suspend the page (needs IsVisible=false) and trim the host's own working set.</summary>
    private async Task SuspendWhenHiddenAsync()
    {
        if (IsVisible)
        {
            return;
        }

        try
        {
            var w = _web;
            if (_rt.Platform.StoreIfCreated is { } store)
            {
                await store.SuspendNowAsync(); // before MemoryTrimmer below, so the trim sees the smaller footprint
            }

            if (w is not null && _controller is { IsVisible: false })
            {
                _rt.Bridge.Hold(true);
                var ok = await w.TrySuspendAsync();
                HostLog.Info($"WebView2 TrySuspend: {ok}");
                if (!ok)
                {
                    _rt.Bridge.Hold(false);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("TrySuspend failed", ex);
            _rt.Bridge.Hold(false);
        }

        MemoryTrimmer.Trim();
    }
}
