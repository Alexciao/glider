using System.Globalization;
using System.Text.RegularExpressions;
using Glider.Core.Contracts;

namespace Glider.Core.Store;

/// <summary>Validation for Epic product/bundle slugs (they come from the web feed and from links: untrusted).</summary>
public static partial class StoreSlug
{
    /// <summary>
    /// Cleans and validates a slug: trims, drops a trailing "/home" (the feed has "name/home"), lowercases, and accepts only
    /// <c>[a-z0-9][a-z0-9-]{0,127}</c>. Returns false for anything else (including a raw 32-hex id, which has no page).
    /// </summary>
    public static bool TryClean(string? raw, out string slug)
    {
        slug = "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var s = raw.Trim();
        if (s.EndsWith("/home", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^5];
        }

        s = s.ToLowerInvariant();
        if (!Valid().IsMatch(s) || HexId().IsMatch(s))
        {
            return false;
        }

        slug = s;
        return true;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,127}$")]
    private static partial Regex Valid();

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex HexId();
}

/// <summary>The Store's path locales (docs/research/store-webview.md §5.3) and the mapping from Glider's UI language.</summary>
public static class StoreLocale
{
    public const string Default = "en-US";

    private static readonly string[] Known =
        ["en-US", "de", "fr", "it", "es-ES", "es-MX", "pt-BR", "pl", "ru", "tr", "ja", "ko", "zh-CN", "zh-Hant", "th", "ar"];

    /// <summary>Maps a .NET culture name ("it-IT", "pt", "zh-TW") to a Store locale; unknown → <see cref="Default"/>.</summary>
    public static string FromCulture(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return Default;
        }

        var name = cultureName.Trim();
        var exact = Known.FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var lower = name.ToLowerInvariant();
        if (lower.StartsWith("zh", StringComparison.Ordinal))
        {
            return lower.Contains("tw", StringComparison.Ordinal) || lower.Contains("hk", StringComparison.Ordinal) || lower.Contains("hant", StringComparison.Ordinal)
                ? "zh-Hant"
                : "zh-CN";
        }

        if (lower.StartsWith("es", StringComparison.Ordinal))
        {
            return lower is "es-es" or "es" ? "es-ES" : "es-MX";
        }

        if (lower.StartsWith("pt", StringComparison.Ordinal))
        {
            return "pt-BR";
        }

        var lang = lower.Split('-')[0];
        return Known.FirstOrDefault(k => k.Equals(lang, StringComparison.OrdinalIgnoreCase)) ?? Default;
    }

    public static string FromCurrentCulture() => FromCulture(CultureInfo.CurrentUICulture.Name);

    public static bool IsKnown(string? locale) => locale is not null && Known.Contains(locale, StringComparer.Ordinal);
}

/// <summary>Builds Store URLs from typed targets. The UI never passes a URL; the host never navigates to anything built elsewhere.</summary>
public static partial class StoreUrls
{
    public const string Origin = "https://store.epicgames.com";

    /// <summary>The Store home for the locale.</summary>
    public static string Home(string locale) => $"{Origin}/{Locale(locale)}/";

    /// <summary>
    /// The URL for a target. A missing/invalid slug falls back to the free-games page (bundles/products) so the user still lands
    /// somewhere useful; <see cref="StoreTargetKind.LastVisited"/> resumes <paramref name="lastPath"/> when it is a safe Store path.
    /// </summary>
    public static string Build(StoreTarget? target, string locale, string? lastPath = null)
    {
        locale = Locale(locale);
        var kind = target?.Kind ?? StoreTargetKind.Home;
        switch (kind)
        {
            case StoreTargetKind.Product:
                return StoreSlug.TryClean(target?.Slug, out var p) ? $"{Origin}/{locale}/p/{p}" : $"{Origin}/{locale}/free-games";
            case StoreTargetKind.Bundle:
                return StoreSlug.TryClean(target?.Slug, out var b) ? $"{Origin}/{locale}/bundles/{b}" : $"{Origin}/{locale}/free-games";
            case StoreTargetKind.FreeGames:
                return $"{Origin}/{locale}/free-games";
            case StoreTargetKind.Wishlist:
                return $"{Origin}/{locale}/wishlist";
            case StoreTargetKind.Browse:
                return $"{Origin}/{locale}/browse";
            case StoreTargetKind.LastVisited:
                return TrySafePath(lastPath, out var path) ? Origin + path : Home(locale);
            case StoreTargetKind.Library:
            case StoreTargetKind.Home:
            default:
                return Home(locale);
        }
    }

    /// <summary>
    /// A path to remember and resume: starts with a slash, only URL-safe path characters, no query/fragment, bounded.
    /// Everything else is dropped so the stored value can never carry a code or a foreign URL.
    /// </summary>
    public static bool TrySafePath(string? path, out string safe)
    {
        safe = "";
        if (string.IsNullOrEmpty(path) || path.Length > 300 || !SafePath().IsMatch(path))
        {
            return false;
        }

        safe = path;
        return true;
    }

    /// <summary>Origin + path of an https Epic URL (no query, no fragment, no userinfo); null for anything unparsable.</summary>
    public static string? Sanitize(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return $"https://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}{u.AbsolutePath}";
    }

    private static string Locale(string locale) => StoreLocale.IsKnown(locale) ? locale : StoreLocale.Default;

    [GeneratedRegex(@"^/[A-Za-z0-9/_\-.]*$")]
    private static partial Regex SafePath();
}

/// <summary>Builds the exchange-code web sign-in URL (<c>/id/exchange?exchangeCode=..&amp;redirectUrl=..</c>). The result contains a credential.</summary>
public static partial class StoreSso
{
    public const string ExchangePath = "/id/exchange";

    /// <summary>
    /// Null when the code is not a 32-hex exchange code or the target is not an Epic page (host must be store.epicgames.com or
    /// www.epicgames.com, https, no userinfo/port). Never log the result: use <see cref="Infrastructure.SecretRedactor.UrlForLog"/>.
    /// </summary>
    public static string? Build(string? code, string? targetUrl)
    {
        if (code is null || !Code().IsMatch(code))
        {
            return null;
        }

        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var t)
            || t.Scheme != Uri.UriSchemeHttps || !t.IsDefaultPort || t.UserInfo.Length != 0
            || !(t.Host.Equals("store.epicgames.com", StringComparison.OrdinalIgnoreCase) || t.Host.Equals("www.epicgames.com", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return $"https://www.epicgames.com{ExchangePath}?exchangeCode={code}&redirectUrl={Uri.EscapeDataString(t.AbsoluteUri)}";
    }

    public static bool IsExchangeUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && u.Host.EndsWith("epicgames.com", StringComparison.OrdinalIgnoreCase)
        && u.AbsolutePath.StartsWith(ExchangePath, StringComparison.OrdinalIgnoreCase);

    public static bool IsValidCode(string? code) => code is not null && Code().IsMatch(code);

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex Code();
}
