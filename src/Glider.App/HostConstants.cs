namespace Glider.App;

/// <summary>
/// Values the frontend and Core must agree with. Mirrored in docs/decisions/host.md; change both together.
/// </summary>
internal static class HostConstants
{
    /// <summary>The virtual host the UI is served from (maps to wwwroot).</summary>
    public const string AppHost = "app.glider";

    public const string AppOrigin = "https://app.glider/";

    /// <summary>Art/metadata cache URL prefix. Served from AppPaths.Cache by a WebResourceRequested handler.</summary>
    public const string CacheUrlPrefix = "https://app.glider/cache/";

    // ---- HTML caption buttons (CSS px). The frontend lays them out top-right: min, max, close. ----
    public const int CaptionButtonWidth = 46;
    public const int CaptionButtonHeight = 40;

    /// <summary>Distance (CSS px) from the window's right edge to the right edge of the MAXIMIZE button (= width of the close button).</summary>
    public const int MaxButtonRightInset = 46;

    /// <summary>Thickness (CSS px) of the top resize strip that sits over the page, so the window can be resized from its top edge.</summary>
    public const int TopResizeStrip = 4;

    // ---- theme colors: window background + WebView2 default background (must match the CSS --bg on Windows 10 "solid") ----
    public const uint DarkBackgroundArgb = 0xFF202020;
    public const uint LightBackgroundArgb = 0xFFF3F3F3;

    public const string MainMutexName = @"Local\Glider.SingleInstance";
    public const string WebViewProfileEpic = "GliderEpic";
    public const string WebViewInstallLink = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    public const string RunKeyValueName = "Glider";

    /// <summary>Window is revealed even if the UI never calls app.ready.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(3);
}
