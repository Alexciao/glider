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
/// Epic sign-in. A second top-level window with its own WebView2 controller in the SAME environment (one browser process group,
/// lowest RAM) but a dedicated profile ("GliderEpic"), so Epic's cookies never mix with the app UI and can be wiped on sign-out.
/// Flow (Legendary's documented one): load the login page with redirectUrl=…/id/api/redirect?clientId=…&amp;responseType=code;
/// after login Epic lands on a page whose body is JSON containing <c>authorizationCode</c>; we read it and close.
/// </summary>
internal sealed class EpicLogin
{
    private const string ClientId = "34a02cf8f4414e29b15921876da36f9a";
    public const string RedirectPrefix = "https://www.epicgames.com/id/api/redirect";

    public const string LoginUrl =
        "https://www.epicgames.com/id/login?redirectUrl=https%3A%2F%2Fwww.epicgames.com%2Fid%2Fapi%2Fredirect%3FclientId%3D" + ClientId + "%26responseType%3Dcode";

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

    internal static string? ParseAuthorizationCode(string? innerText)
    {
        if (string.IsNullOrWhiteSpace(innerText))
        {
            return null;
        }

        var text = innerText.Trim();
        if (text.StartsWith('"'))
        {
            text = JsonSerializer.Deserialize<string>(text) ?? "";
        }

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("authorizationCode", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } s
            ? s
            : null;
    }

    private sealed class LoginWindow : Window
    {
        private readonly CoreWebView2Environment _env;
        private readonly TaskCompletionSource<string?> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<CoreWebView2Controller> _controllerReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly DispatcherTimer _showTimer = new() { Interval = TimeSpan.FromSeconds(6) };
        private nint _hwnd;
        private CoreWebView2Controller? _controller;
        private bool _shown;
        private bool _finished;
        private int _redirectMisses;

        public LoginWindow(CoreWebView2Environment env, Window? owner)
        {
            _env = env;
            Title = "Sign in to Epic Games";
            Width = 520;
            Height = 780;
            MinWidth = 420;
            MinHeight = 560;
            ShowInTaskbar = owner is null;
            if (owner is not null)
            {
                Owner = owner;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            var argb = ThemeState.BackgroundArgb;
            Background = new SolidColorBrush(Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            Closed += (_, _) =>
            {
                _showTimer.Stop();
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
            _showTimer.Tick += (_, _) => ShowNow();
        }

        public Task<string?> RunAsync()
        {
            Prepare();
            _showTimer.Start();
            _ = StartAsync(navigate: true);
            return _done.Task;
        }

        private void Prepare()
        {
            _hwnd = new WindowInteropHelper(this).EnsureHandle();
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

            HwndSource.FromHwnd(_hwnd)?.AddHook(Hook);
        }

        private nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
        {
            if ((uint)msg == Win32.WM_SIZE && _controller is not null)
            {
                Resize();
            }

            return 0;
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
                options.IsInPrivateModeEnabled = false;
                options.DefaultBackgroundColor = DrawingColor.FromArgb(unchecked((int)ThemeState.BackgroundArgb));
                _controller = await _env.CreateCoreWebView2ControllerAsync(_hwnd, options);
                _controller.DefaultBackgroundColor = DrawingColor.FromArgb(unchecked((int)ThemeState.BackgroundArgb));
                Resize();
                var web = _controller.CoreWebView2;
                web.Settings.AreDevToolsEnabled = WebViewSetup.DeveloperMode;
                web.Settings.AreDefaultContextMenusEnabled = WebViewSetup.DeveloperMode;
                web.Settings.IsZoomControlEnabled = false;
                web.Settings.IsStatusBarEnabled = false;
                web.Settings.IsPasswordAutosaveEnabled = true;
                web.NavigationCompleted += OnNavigationCompleted;
                _controllerReady.TrySetResult(_controller);
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
                HostLog.Info($"login navigation: {(uri.Length > 80 ? uri[..80] : uri)} success={e.IsSuccess} status={e.WebErrorStatus}");
                if (_finished || web is null)
                {
                    return;
                }

                if (!uri.StartsWith(RedirectPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    ShowNow(); // a real login page: show the window (not for an instant redirect from an existing session)
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
            _showTimer.Stop();
            if (_shown || _finished)
            {
                return;
            }

            _shown = true;
            Show();
            Activate();
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
