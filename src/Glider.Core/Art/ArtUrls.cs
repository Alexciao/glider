using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Glider.Core.Art;

/// <summary>The image variants the UI asks for. Each maps to one Epic key image and one stored thumbnail size.</summary>
public enum ArtKind
{
    /// <summary>Box art for the detail page (~600 px wide).</summary>
    Tall,

    /// <summary>Box art for the grid (~300 px wide).</summary>
    TallThumb,

    /// <summary>Hero / banner (~1280 px wide).</summary>
    Wide,

    /// <summary>Title logo (stored as delivered: logos are often transparent PNGs).</summary>
    Logo,
}

/// <summary>Which of a game's three key images feeds a variant.</summary>
public enum ArtSourceSlot { Tall, Wide, Logo }

/// <summary>Size and source of one <see cref="ArtKind"/>.</summary>
public sealed record ArtSpec(ArtKind Kind, string Token, ArtSourceSlot Slot, int Width)
{
    private static readonly ArtSpec[] All =
    [
        new(ArtKind.Tall, "tall", ArtSourceSlot.Tall, 600),
        new(ArtKind.TallThumb, "tall-thumb", ArtSourceSlot.Tall, 300),
        new(ArtKind.Wide, "wide", ArtSourceSlot.Wide, 1280),
        new(ArtKind.Logo, "logo", ArtSourceSlot.Logo, 0),
    ];

    public static ArtSpec For(ArtKind kind) => All[(int)kind];

    public static bool TryParse(string token, out ArtSpec spec)
    {
        foreach (var s in All)
        {
            if (string.Equals(s.Token, token, StringComparison.Ordinal))
            {
                spec = s;
                return true;
            }
        }

        spec = null!;
        return false;
    }

    /// <summary>Logos keep their delivered format and size; every other variant is resized.</summary>
    public bool Resizes => Width > 0;
}

/// <summary>
/// The art URL scheme (docs/v0.2-design.md §1): <c>https://art.glider/{appName}/{tall|tall-thumb|wide|logo}?v={hash}</c>, where
/// <c>v</c> is the first 10 hex digits of SHA-256 of the Epic source URL. The URL is immutable (changed art = new hash = new
/// URL), so the browser may cache it forever, and the same hash names the file on disk. Summaries only carry these short URLs;
/// the host resolves the Epic source from the catalog when the file isn't cached yet.
/// </summary>
public static class ArtUrls
{
    public const string Host = "art.glider";
    public const string Prefix = "https://art.glider/";
    public const int HashLength = 10;

    private static readonly Regex AppPattern = new("^[A-Za-z0-9_][A-Za-z0-9_.\\-]{0,127}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new("^[0-9a-f]{10}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>First 10 hex digits (lowercase) of SHA-256 over the UTF-8 source URL.</summary>
    public static string HashOf(string sourceUrl)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl), hash);
        return Convert.ToHexStringLower(hash[..(HashLength / 2)]);
    }

    /// <summary>Apps names that are safe as a folder name and a URL segment (Epic's are 32-hex ids or short words).</summary>
    public static bool IsValidApp(string app) => AppPattern.IsMatch(app) && !app.EndsWith('.');

    public static string Build(string app, ArtKind kind, string sourceUrl) =>
        $"{Prefix}{Uri.EscapeDataString(app)}/{ArtSpec.For(kind).Token}?v={HashOf(sourceUrl)}";

    /// <summary>Parses a request URL; false for anything but a well-formed art URL (wrong host/scheme, path tricks, bad hash).</summary>
    public static bool TryParse(string url, out string app, out ArtKind kind, out string? version)
    {
        app = "";
        kind = ArtKind.Tall;
        version = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) ||
            u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || u.UserInfo.Length != 0 ||
            !string.Equals(u.Host, Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Segments are read from the raw path: no decoding that could introduce separators or dot segments.
        var path = u.AbsolutePath;
        if (path.Contains('%', StringComparison.Ordinal) || path.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        var parts = path.Split('/', StringSplitOptions.None);
        if (parts.Length != 3 || parts[0].Length != 0 || !IsValidApp(parts[1]) || !ArtSpec.TryParse(parts[2], out var spec))
        {
            return false;
        }

        string? v = null;
        foreach (var pair in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (pair.StartsWith("v=", StringComparison.Ordinal))
            {
                v = pair[2..];
            }
        }

        if (v is not null && !HashPattern.IsMatch(v))
        {
            return false;
        }

        app = parts[1];
        kind = spec.Kind;
        version = v;
        return true;
    }
}
