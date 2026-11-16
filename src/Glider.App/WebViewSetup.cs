using System.Diagnostics;
using Glider.Core;
using Microsoft.Web.WebView2.Core;

namespace Glider.App;

/// <summary>Everything about configuring a CoreWebView2 for the app UI: content folder, hardening, local URL serving, navigation guard.</summary>
internal static class WebViewSetup
{
    /// <summary>Debug builds allow dev tools, context menus and F5/Ctrl+R; Release locks the page down.</summary>
    public static readonly bool DeveloperMode = IsDebugBuild();

    private static bool IsDebugBuild()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    // OverlayScrollbar + msOverlayScrollbarWinStyle(+Animation): Windows 11's thin overlay scrollbars (shown while scrolling or
    // hovered) instead of the classic browser bars with arrows, in the Store and Glider's own UI alike.
    public const string DefaultBrowserArguments = "--disable-features=msSmartScreenProtection,msEdgeShopping,msEdgeCollections,msUndersideButton,msEdgeUpsell --enable-features=OverlayScrollbar,msOverlayScrollbarWinStyle,msOverlayScrollbarWinStyleAnimation";

    public static CoreWebView2EnvironmentOptions CreateEnvironmentOptions() => new()
    {
        // Measured in docs/perf-host.md: only flags that measurably help are kept.
        // GLIDER_WV2_ARGS replaces the default (diagnostics / perf experiments only).
        AdditionalBrowserArguments = Environment.GetEnvironmentVariable("GLIDER_WV2_ARGS") ?? DefaultBrowserArguments,
        IsCustomCrashReportingEnabled = true,
        EnableTrackingPrevention = false,
    };

    public static Task<CoreWebView2Environment> CreateEnvironmentAsync(AppPaths paths)
    {
        Directory.CreateDirectory(paths.WebView2);
        return CoreWebView2Environment.CreateAsync(null, paths.WebView2, CreateEnvironmentOptions());
    }

    /// <summary>wwwroot next to the exe; Debug fallback frontend/dist in the repo; last resort the built-in placeholder page.</summary>
    public static string ResolveContentRoot(AppPaths paths, out string kind)
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (File.Exists(Path.Combine(shipped, "index.html")))
        {
            kind = "wwwroot";
            return shipped;
        }

        if (DeveloperMode)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Glider.slnx")))
                {
                    var dist = Path.Combine(dir.FullName, "frontend", "dist");
                    if (File.Exists(Path.Combine(dist, "index.html")))
                    {
                        kind = "frontend/dist";
                        return dist;
                    }

                    break;
                }
            }
        }

        var placeholderDir = Path.Combine(paths.Root, "placeholder");
        Directory.CreateDirectory(placeholderDir);
        using (var res = typeof(WebViewSetup).Assembly.GetManifestResourceStream("placeholder.html"))
        {
            if (res is not null)
            {
                using var file = File.Create(Path.Combine(placeholderDir, "index.html"));
                res.CopyTo(file);
            }
        }

        kind = "placeholder";
        return placeholderDir;
    }

    public static void ConfigureSettings(CoreWebView2Settings s)
    {
        s.AreDevToolsEnabled = DeveloperMode;
        s.AreDefaultContextMenusEnabled = DeveloperMode;
        s.AreBrowserAcceleratorKeysEnabled = DeveloperMode; // F5 / Ctrl+R / Ctrl+P / F12 …
        s.IsZoomControlEnabled = false;
        s.IsPinchZoomEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = true;
        s.IsScriptEnabled = true;
        // Makes CSS `app-region: drag|no-drag` work (takes effect on the next navigation, so it must be set before Navigate).
        s.IsNonClientRegionSupportEnabled = true;
    }

    /// <summary>
    /// Settings for the Store view and its popups (docs/research/store-webview.md section 6): no bridge to the host (web messages and
    /// host objects off), no saved passwords or autofill in the Glider profile, no built-in error page (Glider draws its own).
    /// </summary>
    public static void ConfigureStoreSettings(CoreWebView2Settings s)
    {
        s.AreDevToolsEnabled = DeveloperMode;
        s.AreDefaultContextMenusEnabled = DeveloperMode;
        s.AreBrowserAcceleratorKeysEnabled = DeveloperMode;
        s.IsWebMessageEnabled = false;
        s.AreHostObjectsAllowed = false;
        s.IsPasswordAutosaveEnabled = false;
        s.IsGeneralAutofillEnabled = false;
        s.IsStatusBarEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsScriptEnabled = true; // Epic's own page needs it; nothing of ours is ever injected
    }

    /// <summary>
    /// Maps https://app.glider/ → UI folder and https://cache.glider/ → AppPaths.Cache. (A WebResourceRequested handler cannot serve a
    /// sub-path of a mapped host: verified that virtual-host requests never raise the event, so the cache gets its own host.)
    /// </summary>
    public static void MapContent(CoreWebView2 web, string contentRoot, string cacheRoot)
    {
        web.SetVirtualHostNameToFolderMapping(HostConstants.AppHost, contentRoot, CoreWebView2HostResourceAccessKind.Allow);
        web.SetVirtualHostNameToFolderMapping(HostConstants.CacheHost, cacheRoot, CoreWebView2HostResourceAccessKind.Allow);
    }

    /// <summary>Only https://app.glider/ may load in the main view; everything else opens in the system browser.</summary>
    public static void GuardNavigation(CoreWebView2 web, Action<string> openExternal)
    {
        web.NavigationStarting += (_, e) =>
        {
            if (!IsAppUri(e.Uri))
            {
                e.Cancel = true;
                if (e.IsUserInitiated)
                {
                    openExternal(e.Uri);
                }
            }
        };
        web.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (e.IsUserInitiated)
            {
                openExternal(e.Uri);
            }
        };
        web.DownloadStarting += (_, e) => e.Cancel = true;
        web.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
    }

    public static bool IsAppUri(string uri) => IsAppOrigin(uri) || uri == "about:blank";

    /// <summary>Only the app's own pages may talk to the host (about:blank may be navigated to, but never sends RPCs).</summary>
    public static bool IsAppOrigin(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
        u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.UserInfo.Length == 0 &&
        string.Equals(u.Host, HostConstants.AppHost, StringComparison.OrdinalIgnoreCase);

    public static string BuildStartUrl(bool dark, bool mica, bool translucent = true) =>
        $"{HostConstants.AppOrigin}index.html?theme={(dark ? "dark" : "light")}&backdrop={(mica ? "mica" : "solid")}{(mica && !translucent ? "&surface=solid" : "")}";

    public static void OpenExternalSafe(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeMailto))
        {
            try
            {
                Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                HostLog.Warn("OpenUrl failed", ex);
            }
        }
        else
        {
            HostLog.Warn($"Blocked external URL with unsupported scheme: {url}");
        }
    }
}
