namespace Glider.App;

/// <summary>
/// Values the frontend and Core must agree with. Mirrored in docs/decisions/host.md; change both together.
/// </summary>
internal static class HostConstants
{
    /// <summary>The virtual host the UI is served from (maps to wwwroot).</summary>
    public const string AppHost = "app.glider";

    public const string AppOrigin = "https://app.glider/";

    /// <summary>Second virtual host: AppPaths.Cache is served at https://cache.glider/&lt;relative path&gt; (mirrored by Glider.Core.WebHost).</summary>
    public const string CacheHost = "cache.glider";

    public const string CacheUrlPrefix = "https://cache.glider/";

    // ---- HTML caption buttons (CSS px). The frontend lays them out top-right: min, max, close. ----
    public const int CaptionButtonWidth = 46;
    public const int CaptionButtonHeight = 40;

    /// <summary>Distance (CSS px) from the window's right edge to the right edge of the MAXIMIZE button (= width of the close button).</summary>
    public const int MaxButtonRightInset = 46;

    /// <summary>Thickness (CSS px) of the top resize strip that sits over the page, so the window can be resized from its top edge.</summary>
    public const int TopResizeStrip = 4;

    // ---- theme colors: window background + WebView2 default background. Must equal the sRGB value of the solid (non-Mica) CSS
    // --g-bg in frontend/src/styles/tokens.css: dark oklch(0.165 0.006 265), light oklch(0.975 0.002 265). ----
    public const uint DarkBackgroundArgb = 0xFF0D0E11;
    public const uint LightBackgroundArgb = 0xFFF6F7F8;

    public const string MainMutexName = @"Local\Glider.SingleInstance";
    public const string WebViewProfileEpic = "GliderEpic";

    /// <summary>The embedded Store (docs/research/store-webview.md); same profile as sign-in.</summary>
    public const string StoreOrigin = "https://store.epicgames.com/";
    public const string WebViewInstallLink = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    public const string RunKeyValueName = "Glider";

    /// <summary>Window is revealed even if the UI never calls app.ready.</summary>
    public static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(3);
}
