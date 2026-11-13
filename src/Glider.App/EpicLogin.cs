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

/// <summary>
/// Identity variants of the login view. Only <c>--login-debug</c> picks anything but <see cref="Legendary"/> (what Glider ships);
/// every variant uses Legendary's start sequence and bridge, they differ in the identity the page sees.
/// </summary>
internal enum LoginVariant
{
    Legendary,
    /// <summary>WebView2's default identity (no user agent override).</summary>
    Plain,
    InPrivate,
    /// <summary>Heroic's login user agent (no Chrome/Edg token, "EpicGamesLauncher").</summary>
    EpicUa,
}

/// <summary>
/// Epic sign-in, Legendary's webview flow (the only one Epic's invisible captcha accepts): a second top-level window with its own
/// WebView2 controller in the SAME environment (one browser process group, lowest RAM) but a dedicated profile ("GliderEpic"), so Epic's
/// cookies never mix with the app UI and can be wiped on sign-out. It opens Epic's logout URL (which lands on the login page), identifies
/// as <c>EpicGamesLauncher/&lt;version&gt;</c> and injects the <c>window.ue</c> launcher bridge after each load; Epic's page then hands an
/// exchange code to <c>requestexchangecodesignin</c>, which we return. Social-login popups get a native-looking popup window.
/// </summary>
internal sealed class EpicLogin
{
    /// <summary>Set only by <c>--login-debug</c>: which identity variant to run and where to write the trail.</summary>
    internal static LoginDebug? Debug { get; set; }

    internal static LoginVariant Variant => Debug?.Variant ?? LoginVariant.Legendary;

    internal static nint LastHwndForTests => LoginWindow.LastHwnd;

    private readonly HostRuntime _rt;
    private Task<string?>? _current;
    private LoginWindow? _window;

    public EpicLogin(HostRuntime rt) => _rt = rt;

    /// <summary>UI thread. Returns the exchange code, null if the user closed the window. One window at a time (a second call joins the first).</summary>
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

    /// <summary>Self-test: have the open login page call <c>window.open</c> and report what popup came out of it.</summary>
    internal Task<string> PopupSmokeAsync() => _window?.PopupSmokeAsync() ?? Task.FromResult("no login window");

    /// <summary>Legendary passes <c>EpicGamesLauncher/{egl_version}</c> to pywebview as the whole UA (core.py's default version).</summary>
    internal const string LegendaryUserAgent = "EpicGamesLauncher/11.0.1-14907503+++Portal+Release-Live";

    /// <summary>What Heroic's Epic login window sets (HeroicGamesLauncher src/frontend/screens/WebView/index.tsx).</summary>
    internal const string EpicLauncherUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) EpicGamesLauncher";

    private const string LogoutUrl = "https://www.epicgames.com/id/logout?productName=epic-games&redirectUrl=https://www.epicgames.com/id/login";

    /// <summary>Legendary's <c>window_js</c> with pywebview.api.* replaced by WebView2 web messages.</summary>
    private const string BridgeJs = """
        window.ue = {
          signinprompt: {
            requestexchangecodesignin: function (code, param) { window.chrome.webview.postMessage({ n: 'requestexchangecodesignin', code: code, p: param ? 1 : 0 }); },
            registersignincompletecallback: function () { window.chrome.webview.postMessage({ n: 'registersignincompletecallback' }); }
          },
          common: { launchexternalurl: function (url) { window.chrome.webview.postMessage({ n: 'launchexternalurl', url: String(url) }); } }
        };
        """;

    /// <summary>
    /// Settings, identity and diagnostics for an Epic login view (the login window and its popups; never the app UI's WebView2).
    /// Epic issues its accepted invisible captcha token only in launcher mode: the <c>EpicGamesLauncher</c> user agent plus the
    /// <c>window.ue</c> bridge (see DECISIONS.md); Edge-identity spoofing did not help.
    /// </summary>
    internal static async Task ConfigureAsync(CoreWebView2Controller controller, LoginVariant variant = LoginVariant.Legendary)
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
        AttachReloadSupport(controller);
        AttachTrace(web);
        TrySetTrackingPrevention(web, CoreWebView2TrackingPreventionLevel.Balanced);
        if (variant != LoginVariant.Plain)
        {
            // pywebview: user_agent replaces the whole UA. One Emulation call, no client-hint metadata, web messages stay on (the bridge).
            var ua = variant == LoginVariant.EpicUa ? EpicLauncherUserAgent : LegendaryUserAgent;
            web.Settings.UserAgent = ua;
            await web.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", JsonSerializer.Serialize(new { userAgent = ua }));
        }
    }

    /// <summary>
    /// F5 / Ctrl+R / Ctrl+F5 reload (the other accelerator keys stay off), and an automatic reload (max 2 per view) when Epic rejects
    /// the invisible captcha token (<c>captcha_invalid</c> on /id/api/login*); only those 400 bodies are read.
    /// </summary>
    private static void AttachReloadSupport(CoreWebView2Controller controller)
    {
        var web = controller.CoreWebView2;
        controller.AcceleratorKeyPressed += (_, e) =>
        {
            if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown))
            {
                return;
            }

            var ctrl = System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LeftCtrl) || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RightCtrl);
            if (e.VirtualKey == 0x74 /* F5 */ || (ctrl && e.VirtualKey == 0x52 /* R */))
            {
                e.Handled = true;
                Note("login: manual reload (F5 / Ctrl+R)");
                web.Reload();
            }
        };

        var reloads = 0;
        web.WebResourceResponseReceived += async (_, e) =>
        {
            try
            {
                if (e.Response.StatusCode != 400 || reloads >= 2 || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u)
                    || !u.Host.EndsWith("epicgames.com", StringComparison.OrdinalIgnoreCase) || !u.AbsolutePath.StartsWith("/id/api/login", StringComparison.Ordinal))
                {
                    return;
                }

                var stream = await e.Response.GetContentAsync();
                if (stream is null)
                {
                    return;
                }

                using var sr = new StreamReader(stream);
                if ((await sr.ReadToEndAsync()).Contains("errors.com.epicgames.accountportal.captcha_invalid", StringComparison.Ordinal) && reloads < 2)
                {
                    reloads++;
                    Note($"login: auto-reload due to captcha_invalid ({reloads}/2) after {Glider.Core.Infrastructure.SecretRedactor.UrlForLog(u.AbsoluteUri)}");
                    web.Reload();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException or ObjectDisposedException)
            {
            }
        };
    }

    private static void Note(string message)
    {
        HostLog.Info(message);
        Debug?.Log(message);
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

    private static readonly string[] DiagnosticHosts = ["epicgames.com", "hcaptcha.com", "arkoselabs.com", "funcaptcha.com", "arkoselabs.cn"];
    private static readonly string[] StaticExtensions = [".js", ".css", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".ico", ".woff", ".woff2", ".ttf", ".map"];

    /// <summary>
    /// So a failed sign-in can be diagnosed from the log: navigation starts and, for Epic and its captcha providers, the status and URL
    /// path of each response (never query, headers, cookies or bodies; the redactor runs on every line). Managed events only, no DevTools session.
    /// </summary>
    private static void AttachTrace(CoreWebView2 web)
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
    }

    /// <summary>The native-feeling window chrome shared by the login window and its popups: themed caption, sizing, placement, focus.</summary>
    private abstract class ChromeWindow : Window
    {
        private readonly Window? _placementOwner;
        private int _placements;
        private readonly List<PopupWindow> _popups = [];
        protected nint Hwnd;
        protected CoreWebView2Controller? Controller;

        protected ChromeWindow(CoreWebView2Environment env, Window? owner, string title)
        {
            Env = env;
            Title = title;
            MinWidth = 420;
            MinHeight = 420;
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
                    Controller?.Close();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    HostLog.Warn("Closing the login controller failed", ex);
                }

                OnClosedCore();
            };
            // The page must see real focus (document.hasFocus(), window focus events); captcha scripts check it.
            Activated += (_, _) => FocusPage();
        }

        protected CoreWebView2Environment Env { get; }

        protected abstract int ClientWidthDip { get; }

        protected abstract int ClientHeightDip { get; }

        protected virtual void OnClosedCore()
        {
        }

        internal IReadOnlyList<PopupWindow> Popups => _popups;

        protected void Prepare()
        {
            Hwnd = new WindowInteropHelper(this).EnsureHandle();
            // Match the app: dark title bar (and a theme-colored caption on Windows 11).
            var dark = ThemeState.IsDark ? 1 : 0;
            Win32.DwmSetWindowAttribute(Hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            if (HostEnvironment.IsWindows11)
            {
                var argb = ThemeState.BackgroundArgb;
                var colorRef = (int)(((argb & 0xFF) << 16) | (argb & 0xFF00) | ((argb >> 16) & 0xFF)); // COLORREF = 0x00BBGGRR
                Win32.DwmSetWindowAttribute(Hwnd, Win32.DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
                var text = ThemeState.IsDark ? 0x00FFFFFF : 0x001A1A1A;
                Win32.DwmSetWindowAttribute(Hwnd, Win32.DWMWA_TEXT_COLOR, ref text, sizeof(int));
            }

            Place();
            HwndSource.FromHwnd(Hwnd)?.AddHook(Hook);
        }

        private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
        {
            switch ((uint)msg)
            {
                case Win32.WM_SIZE when Controller is not null:
                    Resize();
                    break;
                case Win32.WM_MOVE or Win32.WM_WINDOWPOSCHANGED when Controller is not null:
                    // Without this WebView2 keeps the old screen position: window.screenX/Y and popup positions go stale after a move.
                    Controller.NotifyParentWindowPositionChanged();
                    break;
                case Win32.WM_SETFOCUS:
                case Win32.WM_ACTIVATE when Win32.LoWord(wParam) != 0:
                    // The host window got focus or was activated: hand real keyboard focus to the page (captcha scripts look at it).
                    Dispatcher.BeginInvoke(FocusPage, DispatcherPriority.Input);
                    break;
                case Win32.WM_DPICHANGED when _placements < 3:
                    // The first placement can cross a monitor DPI boundary: re-apply once the DPI switch has settled.
                    Dispatcher.BeginInvoke(Place, DispatcherPriority.Send);
                    break;
            }

            return 0;
        }

        /// <summary>
        /// Client area of <see cref="ClientWidthDip"/> x <see cref="ClientHeightDip"/>, at most 90 % of the work area of the monitor holding
        /// the owner (or the cursor), centered on the owner and kept fully on that monitor. All in physical pixels at that monitor's DPI.
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
            var style = (uint)Win32.GetWindowLongPtr(Hwnd, Win32.GWL_STYLE);
            var exStyle = (uint)Win32.GetWindowLongPtr(Hwnd, Win32.GWL_EXSTYLE);
            var frame = new RECT { Right = ClientWidthDip * (int)dpi / 96, Bottom = ClientHeightDip * (int)dpi / 96 };
            Win32.AdjustWindowRectExForDpi(ref frame, style, false, exStyle, dpi);
            var work = info.Work;
            var hasAnchor = anchor.Right > anchor.Left;
            var p = LoginWindowLayout.Place(work.Left, work.Top, work.Right, work.Bottom, frame.Width, frame.Height,
                hasAnchor ? (anchor.Left + anchor.Right) / 2 : null, anchor.Bottom > anchor.Top ? (anchor.Top + anchor.Bottom) / 2 : null);
            Win32.SetWindowPos(Hwnd, 0, p.X, p.Y, p.Width, p.Height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }

        protected void FocusPage()
        {
            try
            {
                Controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                HostLog.Warn("Focusing the login page failed", ex);
            }
        }

        protected void Resize()
        {
            if (Controller is not null && Win32.GetClientRect(Hwnd, out var rc))
            {
                Controller.Bounds = new System.Drawing.Rectangle(0, 0, rc.Width, rc.Height);
            }
        }

        protected async Task CreateControllerAsync()
        {
            var options = Env.CreateCoreWebView2ControllerOptions();
            options.ProfileName = HostConstants.WebViewProfileEpic;
            options.IsInPrivateModeEnabled = Variant == LoginVariant.InPrivate;
            options.DefaultBackgroundColor = DrawingColor.FromArgb(unchecked((int)ThemeState.BackgroundArgb));
            Controller = await Env.CreateCoreWebView2ControllerAsync(Hwnd, options);
            Controller.DefaultBackgroundColor = DrawingColor.FromArgb(unchecked((int)ThemeState.BackgroundArgb));
            Controller.IsVisible = true;
            Resize();
            Controller.NotifyParentWindowPositionChanged();
            await ConfigureAsync(Controller, Variant);
            AttachPopups(Controller.CoreWebView2);
        }

        /// <summary>
        /// Social logins (Google, Facebook, Xbox, PlayStation...) open a popup via window.open / target=_blank. It gets a window that looks
        /// like this one instead of a default Edge popup, and the page receives that popup's CoreWebView2, so window.opener and
        /// postMessage (which OAuth popups need) keep working. Only web popups are allowed.
        /// </summary>
        private void AttachPopups(CoreWebView2 web) => web.NewWindowRequested += OnNewWindowRequested;

        private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            if (!LoginWindowLayout.IsAllowedPopupUrl(e.Uri))
            {
                e.Handled = true;
                HostLog.Info("login: blocked a popup with an unsupported URL scheme");
                return;
            }

            var deferral = e.GetDeferral();
            try
            {
                var popup = new PopupWindow(Env, this);
                _popups.Add(popup);
                popup.Closed += (_, _) => _popups.Remove(popup);
                await popup.InitAsync();
                e.NewWindow = popup.WebView;
                e.Handled = true;
                HostLog.Info("login: opened a sign-in popup");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                HostLog.Warn("Opening a login popup failed", ex);
                e.Handled = true;
            }
            finally
            {
                deferral.Complete();
            }
        }
    }

    /// <summary>A social-login popup: the login window's chrome around a CoreWebView2 that the opener page owns.</summary>
    private sealed class PopupWindow : ChromeWindow
    {
        public PopupWindow(CoreWebView2Environment env, Window owner)
            : base(env, owner, "Sign in")
        {
        }

        protected override int ClientWidthDip => 480;

        protected override int ClientHeightDip => 640;

        public CoreWebView2 WebView => Controller!.CoreWebView2;

        public async Task InitAsync()
        {
            Prepare();
            Show();
            Activate();
            await CreateControllerAsync();
            var web = WebView;
            web.DocumentTitleChanged += (_, _) => Title = string.IsNullOrWhiteSpace(web.DocumentTitle) ? "Sign in" : web.DocumentTitle;
            web.WindowCloseRequested += (_, _) => Close();
            // Navigations inside a popup stay normal web navigations; nothing else (javascript:, data:, file:, custom schemes) may load.
            web.NavigationStarting += (_, e) => e.Cancel = !LoginWindowLayout.IsAllowedPopupUrl(e.Uri);
            FocusPage();
        }
    }

    private sealed class LoginWindow : ChromeWindow
    {
        private readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CoreWebView2Controller> _controllerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _shown;
        private bool _finished;
        private bool _injectBridge;

        /// <summary>HWND of the most recent login window (self-test screenshots).</summary>
        internal static nint LastHwnd { get; private set; }

        public LoginWindow(CoreWebView2Environment env, Window? owner)
            : base(env, owner, "Sign in to Epic Games")
        {
            Width = 520;
            Height = 780;
            MinHeight = 560;
        }

        protected override int ClientWidthDip => 520;

        protected override int ClientHeightDip => 780;

        protected override void OnClosedCore() => _done.TrySetResult(null);

        public Task<string?> RunAsync()
        {
            Prepare();
            LastHwnd = Hwnd;
            // Show first: a WebView2 controller created under a never-shown parent can stay unpainted (blank window).
            ShowNow();
            _ = StartAsync(navigate: true);
            return _done.Task;
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
                await CreateControllerAsync();
                var web = Controller!.CoreWebView2;
                if (Debug is { } dbg)
                {
                    await dbg.AttachAsync(web);
                }

                web.NavigationCompleted += OnNavigationCompleted;
                web.WebMessageReceived += OnBridgeMessage;
                _controllerReady.TrySetResult(Controller);
                if (navigate && IsActive)
                {
                    FocusPage();
                }

                if (navigate)
                {
                    // Legendary on Windows opens the logout URL first (drops existing cookies; it redirects to the login page) and
                    // does not inject the bridge into that first load.
                    _injectBridge = false;
                    web.Navigate(LogoutUrl);
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

        /// <summary>The page called the window.ue bridge. Only the NAMES and the code's shape are logged, never the code.</summary>
        private void OnBridgeMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                var name = root.TryGetProperty("n", out var n) ? n.GetString() : null;
                Note($"login bridge invoked: {name}");
                switch (name)
                {
                    case "requestexchangecodesignin" when !_finished:
                        _finished = true;
                        var code = root.TryGetProperty("code", out var c) ? EpicSignIn.ParseExchangeCode(c.GetRawText()) : null;
                        if (code is null)
                        {
                            Note("login bridge: the exchange code had an unexpected shape");
                            _done.TrySetException(new GliderException(Errors.AuthFailed("Epic's sign-in page returned an unexpected code")));
                        }
                        else
                        {
                            _done.TrySetResult(code);
                        }

                        Close();
                        break;
                    case "launchexternalurl" when root.TryGetProperty("url", out var u):
                        WebViewSetup.OpenExternalSafe(u.GetString() ?? "");
                        break;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                HostLog.Warn("login bridge message could not be read", ex);
            }
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            try
            {
                var web = Controller?.CoreWebView2;
                var uri = web?.Source ?? "";
                if (IsActive)
                {
                    FocusPage();
                }

                // Never the query/fragment: IdP callbacks (Google, Xbox, PSN…) carry one-time codes there.
                HostLog.Info($"login navigation: {Glider.Core.Infrastructure.SecretRedactor.UrlForLog(uri)} success={e.IsSuccess} status={e.WebErrorStatus}");
                if (_finished || web is null)
                {
                    return;
                }

                // Legendary's on_loaded: inject window.ue after every load but the first (the logout page, after which injection is on).
                if (_injectBridge)
                {
                    await web.ExecuteScriptAsync(BridgeJs);
                    Note("login bridge injected (window.ue)");
                }
                else if (uri.Contains("logout", StringComparison.OrdinalIgnoreCase))
                {
                    _injectBridge = true;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
            {
                HostLog.Warn("Login navigation handler failed", ex);
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
            if (Controller is not null)
            {
                // Re-assert bounds now that the parent is on screen.
                Resize();
                Controller.NotifyParentWindowPositionChanged();
            }
        }

        public void BringToFront()
        {
            ShowNow();
            if (Win32.IsIconic(Hwnd))
            {
                Win32.ShowWindow(Hwnd, Win32.SW_RESTORE);
            }

            Activate();
        }

        public async Task ClearAsync()
        {
            var controller = await _controllerReady.Task.ConfigureAwait(true);
            await controller.CoreWebView2.Profile.ClearBrowsingDataAsync();
        }

        public async Task<string> PopupSmokeAsync()
        {
            var web = (await _controllerReady.Task.ConfigureAwait(true)).CoreWebView2;
            web.NavigateToString("<!doctype html><title>popup smoke</title><body>x</body>");
            await Task.Delay(800);
            await web.ExecuteScriptAsync("window.open('about:blank'); window.open('javascript:alert(1)');");
            await Task.Delay(2500);
            var p = Popups.Count > 0 ? Popups[0] : null;
            if (p is null)
            {
                return "no popup was created";
            }

            var web2 = p.WebView;
            var s = web2.Settings;
            Win32.GetClientRect(new WindowInteropHelper(p).Handle, out var rc);
            var result = $"{Popups.Count} popup(s); title='{p.Title}' owner={p.Owner is not null} taskbar={p.ShowInTaskbar} client={rc.Width}x{rc.Height}px "
                + $"contextMenu={s.AreDefaultContextMenusEnabled} accelerators={s.AreBrowserAcceleratorKeysEnabled} autofill={s.IsGeneralAutofillEnabled} password={s.IsPasswordAutosaveEnabled} ua={web2.Settings.UserAgent}";
            p.Close();
            return result;
        }
    }
}
