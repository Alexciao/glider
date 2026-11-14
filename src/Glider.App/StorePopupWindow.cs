using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Glider.App.Native;
using Glider.Core.Store;
using Microsoft.Web.WebView2.Core;
using DrawingColor = System.Drawing.Color;

namespace Glider.App;

/// <summary>
/// A login / payment popup opened by the Store page (3-D Secure, PayPal, social sign-in). It hosts the CoreWebView2 the opener page
/// owns, so <c>window.opener</c> and <c>postMessage</c> keep working. There is no address bar, so the caption shows the host and a
/// padlock (updated on every navigation): a look-alike page is visible to the user. https only; closes on <c>window.close()</c>.
/// </summary>
internal sealed class StorePopupWindow : Window
{
    private readonly CoreWebView2Environment _env;
    private CoreWebView2Controller? _controller;
    private nint _hwnd;

    public StorePopupWindow(CoreWebView2Environment env, Window owner)
    {
        _env = env;
        Owner = owner;
        ShowInTaskbar = false;
        Title = "Epic Games";
        MinWidth = 420;
        MinHeight = 420;
        Width = 480;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var argb = ThemeState.BackgroundArgb;
        Background = new SolidColorBrush(Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        SizeChanged += (_, _) => Resize();
        Activated += (_, _) => Focus_();
        Closed += (_, _) =>
        {
            try
            {
                _controller?.Close();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                HostLog.Warn("Closing a Store popup failed", ex);
            }
        };
    }

    public CoreWebView2 WebView => _controller!.CoreWebView2;

    public async Task InitAsync()
    {
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        var dark = ThemeState.IsDark ? 1 : 0;
        Win32.DwmSetWindowAttribute(_hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        Show();
        var options = _env.CreateCoreWebView2ControllerOptions();
        options.ProfileName = HostConstants.WebViewProfileEpic;
        options.DefaultBackgroundColor = DrawingColor.FromArgb(unchecked((int)ThemeState.BackgroundArgb));
        _controller = await _env.CreateCoreWebView2ControllerAsync(_hwnd, options);
        _controller.IsVisible = true;
        Resize();
        var web = _controller.CoreWebView2;
        WebViewSetup.ConfigureStoreSettings(web.Settings);
        web.NavigationStarting += (_, e) =>
        {
            if (!StoreNavigationPolicy.AllowPopupNavigation(e.Uri))
            {
                e.Cancel = true;
            }
        };
        web.NavigationCompleted += (_, _) => UpdateCaption(web);
        web.SourceChanged += (_, _) => UpdateCaption(web);
        web.DocumentTitleChanged += (_, _) => UpdateCaption(web);
        web.WindowCloseRequested += (_, _) => Close();
        web.DownloadStarting += (_, e) => e.Cancel = true;
        web.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        web.ServerCertificateErrorDetected += (_, e) => e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
        // A popup never opens further windows.
        web.NewWindowRequested += (_, e) => e.Handled = true;
        Focus_();
    }

    private void UpdateCaption(CoreWebView2 web)
    {
        if (!Uri.TryCreate(web.Source, UriKind.Absolute, out var u))
        {
            Title = "Epic Games";
            return;
        }

        var lockGlyph = u.Scheme == Uri.UriSchemeHttps ? "🔒 " : "";
        Title = $"{lockGlyph}{u.Host}";
    }

    private void Resize()
    {
        if (_controller is not null && Win32.GetClientRect(_hwnd, out var rc))
        {
            _controller.Bounds = new System.Drawing.Rectangle(0, 0, rc.Width, rc.Height);
        }
    }

    private void Focus_()
    {
        try
        {
            _controller?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            HostLog.Warn("Focusing a Store popup failed", ex);
        }
    }
}
