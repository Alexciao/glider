using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Glider.App.Native;
using Glider.Core.Auth;
using Glider.Core.Contracts;
using Microsoft.Web.WebView2.Core;
using DrawingColor = System.Drawing.Color;

namespace Glider.App;

/// <summary>Identity variants of the login view; only <c>--login-debug</c> picks anything but <see cref="Current"/>.</summary>
internal enum LoginVariant
{
    Current,
    Plain,
    InPrivate,
    NoUa,
}

/// <summary>
/// Epic sign-in. A second top-level window with its own WebView2 controller in the SAME environment (one browser process group,
/// lowest RAM) but a dedicated profile ("GliderEpic"), so Epic's cookies never mix with the app UI and can be wiped on sign-out.
/// Flow (Legendary's documented one): load the login page with redirectUrl=…/id/api/redirect?clientId=…&amp;responseType=code;
/// after login Epic lands on a page whose body is JSON containing <c>authorizationCode</c>; we read it and close.
/// </summary>
internal sealed class EpicLogin
{
    /// <summary>Set only by <c>--login-debug</c>: which identity variant to run and where to write the trail.</summary>
    internal static LoginDebug? Debug { get; set; }

    internal static nint LastHwndForTests => LoginWindow.LastHwnd;

    public const string RedirectPrefix = EpicSignIn.RedirectPrefix;
    public const string LoginUrl = EpicSignIn.LoginUrl;

    private readonly HostRuntime _rt;
    private Task<string?>? _current;
    private LoginWindow? _window;

    public EpicLogin(HostRuntime rt) => _rt = rt;

    /// <summary>UI thread. Returns the code, null if the user closed the window. One window at a time (a second call joins the first).</summary>
    public Task<string?> AcquireAsync(Window? owner, CancellationToken ct)
    {
        if (_current is { IsCompleted: false })
        {
            _window?.BringToFront();
            return _current;
        }

        _current = RunAsync(owner, ct);
        return _current;
    }

    private async Task<string?> RunAsync(Window? owner, CancellationToken ct)
    {
        var env = await _rt.EnvTask;
        var win = new LoginWindow(env, owner is { IsVisible: true } ? owner : null);
        _window = win;
        using var reg = ct.Register(() => _rt.Ui.BeginInvoke(win.Close));
        try
        {
            var code = await win.RunAsync();
            ct.ThrowIfCancellationRequested();
            return code;
        }
        finally
        {
            _window = null;
        }
    }

    /// <summary>Sign-out: wipe the login profile's cookies and storage.</summary>
    public async Task ClearSessionAsync()
    {
        if (_window is not null)
        {
            await _window.ClearAsync();
            return;
        }

        var env = await _rt.EnvTask;
        var win = new LoginWindow(env, null);
        try
        {
            await win.InitForClearAsync();
            await win.ClearAsync();
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>Settings and browser-identity compatibility for the Epic login view only (never the app UI's WebView2).</summary>
    internal static async Task ConfigureAsync(CoreWebView2Controller controller, LoginVariant variant = LoginVariant.Current)
    {
        var web = controller.CoreWebView2;
        web.Settings.AreDevToolsEnabled = WebViewSetup.DeveloperMode;
        web.Settings.AreDefaultContextMenusEnabled = WebViewSetup.DeveloperMode;
        web.Settings.AreBrowserAcceleratorKeysEnabled = WebViewSetup.DeveloperMode;
        web.Settings.IsZoomControlEnabled = false;
        web.Settings.IsPinchZoomEnabled = false;
        web.Settings.IsSwipeNavigationEnabled = false;
        web.Settings.IsStatusBarEnabled = false;
        // No Edge "Save password?" bubble or autofill dropdown on top of Epic's own form.
        web.Settings.IsPasswordAutosaveEnabled = false;
        web.Settings.IsGeneralAutofillEnabled = false;
        if (variant == LoginVariant.Plain)
        {
            // Diagnostic baseline: WebView2's default identity (default tracking prevention, no rewrites, no injected script).
            TrySetTrackingPrevention(web, CoreWebView2TrackingPreventionLevel.Balanced);
            return;
        }

        // Epic's page has no business with the host bridge; with these off there is no window.chrome.webview, which real Edge lacks
        // (found by --login-audit).
        web.Settings.IsWebMessageEnabled = false;
        web.Settings.AreHostObjectsAllowed = false;
        // hCaptcha/Arkose frames need third-party storage: never let tracking prevention touch this profile.
        TrySetTrackingPrevention(web, CoreWebView2TrackingPreventionLevel.None);

        // Real Edge has no window.chrome.webview (the settings above do not remove the object itself).
        if (variant != LoginVariant.NoUa)
        {
            await web.AddScriptToExecuteOnDocumentCreatedAsync("try { delete window.chrome.webview; } catch (e) {}");
        }

        await MatchEdgeClientHintsAsync(web);
        AttachDiagnostics(web);
    }

    private static void TrySetTrackingPrevention(CoreWebView2 web, CoreWebView2TrackingPreventionLevel level)
    {
        try
        {
            web.Profile.PreferredTrackingPreventionLevel = level;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("Could not set tracking prevention for the login profile", ex);
        }
    }

    /// <summary>
    /// WebView2 adds a "Microsoft Edge WebView2" brand to sec-ch-ua / navigator.userAgentData; the installed Edge never does. Replaces the
    /// brand list (built from this runtime's own values) for this view only. Best effort: on any failure the view runs as before.
    /// </summary>
    private static async Task MatchEdgeClientHintsAsync(CoreWebView2 web)
    {
        // navigator.userAgentData only exists in a secure context, and about:blank is not one: probe a synthetic https page that is
        // served from memory (nothing goes on the network).
        const string probeUrl = "https://glider-probe.invalid/";
        var loaded = new TaskCompletionSource();
        void Serve(object? sender, CoreWebView2WebResourceRequestedEventArgs e) =>
            e.Response = web.Environment.CreateWebResourceResponse(new MemoryStream("<!doctype html><title>probe</title>"u8.ToArray()), 200, "OK", "Content-Type: text/html");
        void Done(object? sender, CoreWebView2NavigationCompletedEventArgs e) => loaded.TrySetResult();
        web.AddWebResourceRequestedFilter(probeUrl + "*", CoreWebView2WebResourceContext.All);
        web.WebResourceRequested += Serve;
        web.NavigationCompleted += Done;
        try
        {
            web.Navigate(probeUrl);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var probe = await web.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
            {
                expression = "navigator.userAgentData.getHighEntropyValues(['architecture','bitness','model','platformVersion','fullVersionList','wow64']).then(v => JSON.stringify(v))",
                awaitPromise = true,
                returnByValue = true,
            }));
            using var doc = JsonDocument.Parse(probe);
            var json = doc.RootElement.GetProperty("result").GetProperty("value").GetString() ?? "";
            if (EdgeUserAgentMetadata.BuildOverrideJson(web.Settings.UserAgent, json) is { } over)
            {
                await web.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", over);
                HostLog.Info("login view: client hints match Microsoft Edge");
            }
            else
            {
                HostLog.Warn("login view: runtime client hints not in the expected shape, left unchanged");
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or TimeoutException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("login view: matching Edge client hints failed", ex);
        }
        finally
        {
            web.NavigationCompleted -= Done;
            web.WebResourceRequested -= Serve;
            web.RemoveWebResourceRequestedFilter(probeUrl + "*", CoreWebView2WebResourceContext.All);
        }
    }

    private static readonly string[] DiagnosticHosts = ["epicgames.com", "hcaptcha.com", "arkoselabs.com", "funcaptcha.com", "arkoselabs.cn"];
    private static readonly string[] StaticExtensions = [".js", ".css", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".ico", ".woff", ".woff2", ".ttf", ".map"];

    /// <summary>
    /// So the next "wrong response" can be diagnosed from the log: navigation events and, for Epic and its captcha providers, the status
    /// and URL path of each response (never query, headers, cookies or bodies; the redactor runs on every line).
    /// </summary>
    private static void AttachDiagnostics(CoreWebView2 web)
    {
        web.NavigationStarting += (_, e) =>
            HostLog.Info($"login nav start: {Glider.Core.Infrastructure.SecretRedactor.UrlForLog(e.Uri)} user={e.IsUserInitiated} redirect={e.IsRedirected}");
        web.WebResourceResponseReceived += (_, e) =>
        {
            try
            {
                if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u) || !DiagnosticHosts.Any(h => u.Host.EndsWith(h, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                var status = e.Response.StatusCode;
                if (status < 400 && StaticExtensions.Any(x => u.AbsolutePath.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                {
                    return;
                }

                HostLog.Info($"login http {status} {e.Request.Method} {Glider.Core.Infrastructure.SecretRedactor.UrlForLog(u.AbsoluteUri)}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                // the response object can already be gone
            }
        };

        // Requests that never got a response (blocked, CORS, DNS...) are invisible to the event above: log error + host/path.
        var urls = new Dictionary<string, string>();
        web.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent").DevToolsProtocolEventReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                var id = doc.RootElement.GetProperty("requestId").GetString() ?? "";
                var url = doc.RootElement.GetProperty("request").GetProperty("url").GetString() ?? "";
                if (urls.Count > 400)
                {
                    urls.Clear();
                }

                urls[id] = Glider.Core.Infrastructure.SecretRedactor.UrlForLog(url);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
            {
            }
        };
        web.GetDevToolsProtocolEventReceiver("Network.loadingFailed").DevToolsProtocolEventReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                var r = doc.RootElement;
                var id = r.GetProperty("requestId").GetString() ?? "";
                var url = urls.GetValueOrDefault(id, "?");
                if (DiagnosticHosts.Any(h => url.Contains(h, StringComparison.OrdinalIgnoreCase)) && !(r.TryGetProperty("canceled", out var c) && c.ValueKind == JsonValueKind.True))
                {
                    var err = r.TryGetProperty("errorText", out var t) ? t.GetString() : "?";
                    var blocked = r.TryGetProperty("blockedReason", out var b) ? b.GetString() : "-";
                    HostLog.Warn($"login net failed: {url} error={err} blocked={blocked}");
                }
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException)
            {
            }
        };
        _ = web.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
    }

    internal static string? ParseAuthorizationCode(string? innerText) => EpicSignIn.ParseAuthorizationCode(innerText);

    private sealed class LoginWindow : Window
    {
        private readonly CoreWebView2Environment _env;
        private readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CoreWebView2Controller> _controllerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Window? _placementOwner;
        private nint _hwnd;
        private CoreWebView2Controller? _controller;
        private bool _shown;
        private bool _finished;
        private int _redirectMisses;
        private int _placements;

        /// <summary>HWND of the most recent login window (self-test screenshots).</summary>
        internal static nint LastHwnd { get; private set; }

        public LoginWindow(CoreWebView2Environment env, Window? owner)
        {
            _env = env;
            Title = "Sign in to Epic Games";
            Width = 520;
            Height = 780;
            MinWidth = 420;
            MinHeight = 560;
            ShowInTaskbar = owner is null;
            _placementOwner = owner;
            if (owner is not null)
            {
                Owner = owner;
            }

            var argb = ThemeState.BackgroundArgb;
            Background = new SolidColorBrush(Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            // The WebView2 child paints DefaultBackgroundColor (the theme background) until Epic's page renders, so there is no
            // white flash. It is visible and sized from the start: a hidden/zero-size page loads Epic's captcha scripts with
            // document.visibilityState == "hidden", which looks like a bot.
            Closed += (_, _) =>
            {
                try
                {
                    _controller?.Close();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    HostLog.Warn("Closing the login controller failed", ex);
                }

                _done.TrySetResult(null);
            };
            // The page must see real focus (document.hasFocus(), window focus events); captcha scripts check it.
            Activated += (_, _) => FocusPage();
        }

        public Task<string?> RunAsync()
        {
            Prepare();
            // Show first: a WebView2 controller created under a never-shown parent can stay unpainted (blank window).
            ShowNow();
            _ = StartAsync(navigate: true);
            return _done.Task;
        }

        private void Prepare()
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
            LastHwnd = _hwnd;
            // Match the app: dark title bar (and a theme-colored caption on Windows 11).
            var dark = ThemeState.IsDark ? 1 : 0;
            Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            if (HostEnvironment.IsWindows11)
            {
                var argb = ThemeState.BackgroundArgb;
                var colorRef = (int)(((argb & 0xFF) << 16) | (argb & 0xFF00) | ((argb >> 16) & 0xFF)); // COLORREF = 0x00BBGGRR
                Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
                var text = ThemeState.IsDark ? 0x00FFFFFF : 0x001A1A1A;
                Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_TEXT_COLOR, ref text, sizeof(int));
            }

            Place();
            HwndSource.FromHwnd(_hwnd)?.AddHook(Hook);
        }

        private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
        {
            switch ((uint)msg)
            {
                case Win32.WM_SIZE when _controller is not null:
                    Resize();
                    break;
                case Win32.WM_MOVE or Win32.WM_WINDOWPOSCHANGED when _controller is not null:
                    // Without this WebView2 keeps the old screen position: window.screenX/Y and popup positions go stale after a move.
                    _controller.NotifyParentWindowPositionChanged();
                    break;
                case Win32.WM_DPICHANGED when _placements < 3:
                    // The first placement can cross a monitor DPI boundary: re-apply once the DPI switch has settled.
                    Dispatcher.BeginInvoke(Place, DispatcherPriority.Send);
                    break;
            }

            return 0;
        }

        private const int ClientWidthDip = 520;
        private const int ClientHeightDip = 780;

        /// <summary>
        /// Client area of 520 x 780 DIPs (Epic's login page wants ~480-520 px of width and 720-800 px of height, so it needs no
        /// scrolling), at most 90 % of the work area of the monitor holding the owner (or the cursor), centered on the owner and kept
        /// fully on that monitor. All in physical pixels at that monitor's DPI.
        /// </summary>
        private void Place()
        {
            _placements++;
            RECT anchor = default;
            nint monitor;
            if (_placementOwner is not null && new WindowInteropHelper(_placementOwner).Handle is var oh && oh != 0
                && !Win32.IsIconic(oh) && Win32.GetWindowRect(oh, out anchor))
            {
                monitor = Win32.MonitorFromWindow(oh, Win32.MONITOR_DEFAULTTONEAREST);
            }
            else
            {
                Win32.GetCursorPos(out var pt);
                var probe = new RECT { Left = pt.X, Top = pt.Y, Right = pt.X + 1, Bottom = pt.Y + 1 };
                monitor = Win32.MonitorFromRect(in probe, Win32.MONITOR_DEFAULTTONEAREST);
                anchor = default;
            }

            var info = new MONITORINFO { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
            if (monitor == 0 || !Win32.GetMonitorInfo(monitor, ref info))
            {
                return;
            }

            var dpi = Win32.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX : 96u;
            var style = (uint)Win32.GetWindowLongPtr(_hwnd, Win32.GWL_STYLE);
            var exStyle = (uint)Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE);
            var frame = new RECT { Right = ClientWidthDip * (int)dpi / 96, Bottom = ClientHeightDip * (int)dpi / 96 };
            Win32.AdjustWindowRectExForDpi(ref frame, style, false, exStyle, dpi);
            var work = info.Work;
            var w = Math.Min(frame.Width, work.Width * 9 / 10);
            var h = Math.Min(frame.Height, work.Height * 9 / 10);
            var cx = anchor.Right > anchor.Left ? (anchor.Left + anchor.Right) / 2 : (work.Left + work.Right) / 2;
            var cy = anchor.Bottom > anchor.Top ? (anchor.Top + anchor.Bottom) / 2 : (work.Top + work.Bottom) / 2;
            var x = Math.Clamp(cx - w / 2, work.Left, work.Right - w);
            var y = Math.Clamp(cy - h / 2, work.Top, work.Bottom - h);
            Win32.SetWindowPos(_hwnd, 0, x, y, w, h, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }

        private void FocusPage()
        {
            try
            {
                _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                HostLog.Warn("Focusing the login page failed", ex);
            }
        }

        private void Resize()
        {
            if (_controller is not null && Win32.GetClientRect(_hwnd, out var rc))
            {
                _controller.Bounds = new System.Drawing.Rectangle(0, 0, rc.Width, rc.Height);
            }
        }

        public Task InitForClearAsync()
        {
            Prepare();
            return StartAsync(navigate: false);
        }

        private async Task StartAsync(bool navigate)
        {
            try
            {
                var options = _env.CreateCoreWebView2ControllerOptions();
                options.ProfileName = HostConstants.WebViewProfileEpic;
                options.IsInPrivateModeEnabled = Debug?.Variant == LoginVariant.InPrivate;
                options.DefaultBackgroundColor = DrawingColor.FromArgb(unchecked((int)ThemeState.BackgroundArgb));
                _controller = await _env.CreateCoreWebView2ControllerAsync(_hwnd, options);
                _controller.DefaultBackgroundColor = DrawingColor.FromArgb(unchecked((int)ThemeState.BackgroundArgb));
                _controller.IsVisible = true;
                Resize();
                _controller.NotifyParentWindowPositionChanged();
                var web = _controller.CoreWebView2;
                await ConfigureAsync(_controller, Debug?.Variant ?? LoginVariant.Current);
                if (Debug is { } dbg)
                {
                    await dbg.AttachAsync(web);
                }

                web.NavigationCompleted += OnNavigationCompleted;
                _controllerReady.TrySetResult(_controller);
                if (navigate && IsActive)
                {
                    FocusPage();
                }

                if (navigate)
                {
                    web.Navigate(LoginUrl);
                }
            }
            catch (Exception ex)
            {
                HostLog.Error("Epic login window failed to start", ex);
                _controllerReady.TrySetException(ex);
                _done.TrySetException(new GliderException(Errors.AuthFailed(ex.Message), ex));
                Close();
            }
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                var web = _controller?.CoreWebView2;
                var uri = web?.Source ?? "";
                // Never the query/fragment: IdP callbacks (Google, Xbox, PSN…) carry one-time codes there.
                HostLog.Info($"login navigation: {Glider.Core.Infrastructure.SecretRedactor.UrlForLog(uri)} success={e.IsSuccess} status={e.WebErrorStatus}");
                if (_finished || web is null)
                {
                    return;
                }

                if (!uri.StartsWith(RedirectPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var body = await web.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
                string? code;
                try
                {
                    code = ParseAuthorizationCode(body);
                }
                catch (JsonException)
                {
                    code = null;
                }

                if (code is not null)
                {
                    _finished = true;
                    _done.TrySetResult(code);
                    Close();
                }
                else if (++_redirectMisses >= 2)
                {
                    _finished = true;
                    _done.TrySetException(new GliderException(Errors.AuthFailed("Epic's redirect page held no authorizationCode")));
                    Close();
                }
                else
                {
                    web.Navigate(LoginUrl); // session was not valid after all; show the login form
                }
            }
            catch (Exception ex)
            {
                HostLog.Error("Reading the Epic redirect failed", ex);
                if (!_finished)
                {
                    _finished = true;
                    _done.TrySetException(new GliderException(Errors.AuthFailed(ex.Message), ex));
                    Close();
                }
            }
        }

        private void ShowNow()
        {
            if (_shown || _finished)
            {
                return;
            }

            _shown = true;
            Show();
            Activate();
            if (_controller is not null)
            {
                // Re-assert bounds now that the parent is on screen.
                Resize();
                _controller.NotifyParentWindowPositionChanged();
            }
        }

        public void BringToFront()
        {
            ShowNow();
            if (Win32.IsIconic(_hwnd))
            {
                Win32.ShowWindow(_hwnd, Win32.SW_RESTORE);
            }

            Activate();
        }

        public async Task ClearAsync()
        {
            var controller = await _controllerReady.Task.ConfigureAwait(true);
            await controller.CoreWebView2.Profile.ClearBrowsingDataAsync();
        }
    }
}
