using System.Diagnostics;
using Glider.Core;
using Microsoft.Web.WebView2.Core;

namespace Glider.App;

/// <summary>Everything about configuring a CoreWebView2 for the app UI: content folder, hardening, local URL serving, navigation guard.</summary>
internal static class WebViewSetup
{
#if DEBUG
    public const bool DeveloperMode = true;
#else
    public const bool DeveloperMode = false;
#endif

    public static CoreWebView2EnvironmentOptions CreateEnvironmentOptions() => new()
    {
        // Measured in docs/perf-host.md: only flags that measurably help are kept.
        AdditionalBrowserArguments = "--disable-features=msSmartScreenProtection,msEdgeShopping,msEdgeCollections,msUndersideButton,msEdgeUpsell",
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

    /// <summary>Maps the UI host and serves AppPaths.Cache at https://app.glider/cache/.</summary>
    public static void MapContent(CoreWebView2 web, string contentRoot, string cacheRoot)
    {
        web.SetVirtualHostNameToFolderMapping(HostConstants.AppHost, contentRoot, CoreWebView2HostResourceAccessKind.Allow);

        var cacheFull = Path.GetFullPath(cacheRoot);
        var cachePrefix = cacheFull.EndsWith(Path.DirectorySeparatorChar) ? cacheFull : cacheFull + Path.DirectorySeparatorChar;
        var env = web.Environment;
        web.AddWebResourceRequestedFilter(WebHost.CachePrefix + "*", CoreWebView2WebResourceContext.All);
        web.WebResourceRequested += (_, e) =>
        {
            try
            {
                var uri = e.Request.Uri;
                if (!uri.StartsWith(WebHost.CachePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                var rel = uri[WebHost.CachePrefix.Length..];
                var cut = rel.IndexOfAny(['?', '#']);
                if (cut >= 0)
                {
                    rel = rel[..cut];
                }

                var full = Path.GetFullPath(Path.Combine(cacheFull, Uri.UnescapeDataString(rel).Replace('/', Path.DirectorySeparatorChar)));
                if (!full.StartsWith(cachePrefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                {
                    e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
                    return;
                }

                var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                e.Response = env.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {MimeFor(full)}\r\nCache-Control: no-cache");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or UriFormatException)
            {
                HostLog.Warn("cache request failed", ex);
                e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
            }
        };
    }

    private static string MimeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".json" => "application/json",
        _ => "application/octet-stream",
    };

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

    public static bool IsAppUri(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) &&
        ((u.Scheme == Uri.UriSchemeHttps && string.Equals(u.Host, HostConstants.AppHost, StringComparison.OrdinalIgnoreCase)) || uri == "about:blank");

    public static string BuildStartUrl(bool dark, bool mica) =>
        $"{HostConstants.AppOrigin}index.html?theme={(dark ? "dark" : "light")}&backdrop={(mica ? "mica" : "solid")}";

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
