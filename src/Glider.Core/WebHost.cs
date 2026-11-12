namespace Glider.Core;

/// <summary>
/// URL scheme the host serves local content under (Glider.App maps these in WebView2).
/// Core builds art URLs with <see cref="CacheUrl"/>; the host serves <c>AppPaths.Cache</c> at <see cref="CachePrefix"/>.
/// </summary>
public static class WebHost
{
    public const string Host = "app.glider";
    public const string CachePrefix = "https://app.glider/cache/";

    /// <summary>URL for a file under AppPaths.Cache, e.g. "art/hero.jpg" → https://app.glider/cache/art/hero.jpg.</summary>
    public static string CacheUrl(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return CachePrefix + string.Join('/', segments.Select(Uri.EscapeDataString));
    }
}
