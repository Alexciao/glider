using System.Globalization;
using System.Text;

namespace Glider.Core.Links;

public enum GliderLinkKind
{
    /// <summary><c>glider://launch/&lt;app&gt;</c>: start the game at once (no window needed).</summary>
    Launch,
    /// <summary><c>glider://install/&lt;app&gt;</c>: open the game's install dialog. Never installs silently.</summary>
    Install,
    /// <summary><c>glider://game/&lt;app&gt;</c>: the game's page.</summary>
    Game,
    /// <summary><c>glider://downloads</c>.</summary>
    Downloads,
    /// <summary><c>glider://store</c> or <c>glider://store/p/&lt;slug&gt;</c>.</summary>
    Store,
    /// <summary><c>glider://library</c> (also produced by mapped Epic links).</summary>
    Library,
}

/// <summary>Why a link was refused. Only for logs and tests: the UI never sees why a link was ignored.</summary>
public enum LinkRejection
{
    None,
    Empty,
    TooLong,
    ControlCharacters,
    BadScheme,
    Malformed,
    UnknownRoute,
    BadAppName,
    BadSlug,
    BadEncoding,
}

/// <summary>A validated in-app destination. Instances only come from <see cref="GliderLinkParser"/> or the factory methods, so every field is already safe.</summary>
public sealed record GliderLink
{
    private GliderLink(GliderLinkKind kind, string? appName = null, string? slug = null)
    {
        Kind = kind;
        AppName = appName;
        Slug = slug;
    }

    public GliderLinkKind Kind { get; }

    /// <summary>Legendary app name for Launch / Install / Game links.</summary>
    public string? AppName { get; }

    /// <summary>Store product slug for <c>store/p/&lt;slug&gt;</c>.</summary>
    public string? Slug { get; }

    /// <summary>The links that start a game or need no window: nothing visible has to open.</summary>
    public bool NeedsWindow => Kind != GliderLinkKind.Launch;

    public static GliderLink Launch(string appName) => new(GliderLinkKind.Launch, Require(appName));

    public static GliderLink Install(string appName) => new(GliderLinkKind.Install, Require(appName));

    public static GliderLink Game(string appName) => new(GliderLinkKind.Game, Require(appName));

    public static GliderLink Downloads { get; } = new(GliderLinkKind.Downloads);

    public static GliderLink Library { get; } = new(GliderLinkKind.Library);

    public static GliderLink Store(string? slug = null) =>
        slug is null ? new(GliderLinkKind.Store) : new(GliderLinkKind.Store, slug: GliderLinkParser.IsValidSlug(slug) ? slug : throw new ArgumentException("Invalid store slug", nameof(slug)));

    /// <summary>The same link with its app resolved to the library's exact (case-correct) app name.</summary>
    public GliderLink WithApp(string appName) => AppName is null ? this : new(Kind, Require(appName), Slug);

    /// <summary>The <c>glider://</c> form. App names and slugs are validated identifiers, so nothing needs escaping.</summary>
    public string Uri => Kind switch
    {
        GliderLinkKind.Launch => "glider://launch/" + AppName,
        GliderLinkKind.Install => "glider://install/" + AppName,
        GliderLinkKind.Game => "glider://game/" + AppName,
        GliderLinkKind.Downloads => "glider://downloads",
        GliderLinkKind.Library => "glider://library",
        _ => Slug is null ? "glider://store" : "glider://store/p/" + Slug,
    };

    /// <summary>The UI route for the `navigate` event: <c>game/&lt;app&gt;</c>, <c>game/&lt;app&gt;/install</c>, <c>downloads</c>, <c>library</c>, <c>store</c>, <c>store/product/&lt;slug&gt;</c> (the Store's typed route; the UI also reads the legacy <c>store/p/&lt;slug&gt;</c>).</summary>
    public string Route => Kind switch
    {
        GliderLinkKind.Launch or GliderLinkKind.Game => "game/" + AppName,
        GliderLinkKind.Install => "game/" + AppName + "/install",
        GliderLinkKind.Downloads => "downloads",
        GliderLinkKind.Library => "library",
        _ => Slug is null ? "store" : "store/product/" + Slug,
    };

    private static string Require(string appName) =>
        GliderLinkParser.IsValidAppName(appName) ? appName : throw new ArgumentException("Invalid app name", nameof(appName));
}

/// <summary>
/// Strict parser for <c>glider://</c> links. Links come from anywhere (a web page, a shortcut someone edited, another program),
/// so everything not on the allow-list is refused: app names are Legendary-style identifiers, nothing is ever treated as a path,
/// queries are ignored, and percent-encoding is decoded exactly once.
/// </summary>
public static class GliderLinkParser
{
    public const string Scheme = "glider";
    public const int MaxLength = 2048;
    public const int MaxAppNameLength = 128;
    public const int MaxSlugLength = 128;

    /// <summary>Legendary app names are alphanumeric (32-hex ids, "Fortnite", "Sugar"); dots, dashes and underscores appear in a few.</summary>
    public static bool IsValidAppName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > MaxAppNameLength || !char.IsAsciiLetterOrDigit(name[0]))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            {
                return false;
            }
        }

        return !name.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>Epic product slugs: lower-case words with dashes (some carry digits or underscores).</summary>
    public static bool IsValidSlug(string? slug)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length > MaxSlugLength || !char.IsAsciiLetterOrDigit(slug[0]))
        {
            return false;
        }

        foreach (var c in slug)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    public static bool TryParse(string? raw, out GliderLink link) => Parse(raw, out link) == LinkRejection.None;

    /// <summary>Returns <see cref="LinkRejection.None"/> and the link, or the reason it was refused (link is then a harmless Library value).</summary>
    public static LinkRejection Parse(string? raw, out GliderLink link)
    {
        link = GliderLink.Library;
        var common = LinkText.SplitScheme(raw, Scheme, MaxLength, out var rest, out var query);
        if (common != LinkRejection.None)
        {
            return common;
        }

        // rest = "<route>[/<segment>...]" (no leading slashes: "glider:///x" is malformed, not "x").
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var route = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? "" : rest[(slash + 1)..];
        if (route.Length == 0 || route.Length > 16 || !route.All(char.IsAsciiLetter))
        {
            return route.Length == 0 || route.Contains('@') || route.Contains(':') ? LinkRejection.Malformed : LinkRejection.UnknownRoute;
        }

        if (!LinkText.TrySegments(path, out var segments))
        {
            return LinkRejection.Malformed;
        }

        _ = query; // never read: nothing a web page appends may change what a link does
        switch (route.ToLowerInvariant())
        {
            case "launch" or "install" or "game":
            {
                if (segments.Count != 1)
                {
                    return segments.Count == 0 ? LinkRejection.BadAppName : LinkRejection.Malformed;
                }

                if (!LinkText.TryDecode(segments[0], out var app))
                {
                    return LinkRejection.BadEncoding;
                }

                if (!IsValidAppName(app))
                {
                    return LinkRejection.BadAppName;
                }

                link = route.ToLowerInvariant() switch
                {
                    "launch" => GliderLink.Launch(app),
                    "install" => GliderLink.Install(app),
                    _ => GliderLink.Game(app),
                };
                return LinkRejection.None;
            }

            case "downloads" or "library":
                if (segments.Count != 0)
                {
                    return LinkRejection.Malformed;
                }

                link = route.Equals("downloads", StringComparison.OrdinalIgnoreCase) ? GliderLink.Downloads : GliderLink.Library;
                return LinkRejection.None;

            case "store":
                if (segments.Count == 0)
                {
                    link = GliderLink.Store();
                    return LinkRejection.None;
                }

                if (segments.Count != 2 || !segments[0].Equals("p", StringComparison.OrdinalIgnoreCase))
                {
                    return LinkRejection.Malformed;
                }

                if (!LinkText.TryDecode(segments[1], out var slug))
                {
                    return LinkRejection.BadEncoding;
                }

                if (!IsValidSlug(slug))
                {
                    return LinkRejection.BadSlug;
                }

                link = GliderLink.Store(slug);
                return LinkRejection.None;

            default:
                return LinkRejection.UnknownRoute;
        }
    }

    /// <summary><c>glider://launch/&lt;app&gt;</c> for a shortcut or the <c>--launch</c> switch; null when the name is not a valid app name.</summary>
    public static string? LaunchUri(string? appName) => IsValidAppName(appName) ? GliderLink.Launch(appName!).Uri : null;
}

/// <summary>Scheme/segment/percent-decoding rules shared by the <c>glider://</c> and <c>com.epicgames.launcher://</c> parsers.</summary>
internal static class LinkText
{
    /// <summary>
    /// Checks length, control characters, scheme, userinfo/fragment/backslash and embedded second schemes; returns what follows
    /// <c>scheme://</c> without its query, and the query string (still raw).
    /// </summary>
    public static LinkRejection SplitScheme(string? raw, string scheme, int maxLength, out string rest, out string query)
    {
        rest = "";
        query = "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            return LinkRejection.Empty;
        }

        if (raw.Length > maxLength)
        {
            return LinkRejection.TooLong;
        }

        // Command lines wrap URLs in quotes and Windows may add a trailing slash; nothing else is trimmed.
        var text = raw.Trim().Trim('"');
        foreach (var c in text)
        {
            if (c < 0x20 || c == 0x7f || char.IsWhiteSpace(c))
            {
                return LinkRejection.ControlCharacters;
            }
        }

        var prefix = scheme + "://";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return LinkRejection.BadScheme;
        }

        var tail = text[prefix.Length..];
        if (tail.Contains('#') || tail.Contains('\\') || tail.Contains("://", StringComparison.Ordinal))
        {
            return LinkRejection.Malformed;
        }

        var q = tail.IndexOf('?', StringComparison.Ordinal);
        if (q >= 0)
        {
            query = tail[(q + 1)..];
            tail = tail[..q];
        }

        // A route is "name[/segments]": a leading slash, userinfo or a port means something else was meant.
        if (tail.StartsWith('/') || tail.Contains('@'))
        {
            return LinkRejection.Malformed;
        }

        rest = tail;
        return LinkRejection.None;
    }

    /// <summary>Splits "a/b/" into ["a","b"]: one trailing slash is fine (Windows adds it), empty segments in the middle are not.</summary>
    public static bool TrySegments(string path, out List<string> segments)
    {
        segments = [];
        if (path.Length == 0)
        {
            return true;
        }

        var parts = path.Split('/');
        var count = parts.Length;
        if (parts[^1].Length == 0)
        {
            count--;
        }

        for (var i = 0; i < count; i++)
        {
            if (parts[i].Length == 0)
            {
                segments = [];
                return false;
            }

            segments.Add(parts[i]);
        }

        return true;
    }

    /// <summary>
    /// Decodes one path segment: only valid %XX escapes, only valid UTF-8, no control characters, and a decoded "%" is refused
    /// (double encoding). Decoding happens before any validation of the value.
    /// </summary>
    public static bool TryDecode(string segment, out string decoded)
    {
        decoded = "";
        if (segment.IndexOf('%', StringComparison.Ordinal) < 0)
        {
            decoded = segment;
            return true;
        }

        var bytes = new List<byte>(segment.Length);
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            if (c != '%')
            {
                if (c > 0x7f)
                {
                    foreach (var b in Encoding.UTF8.GetBytes(c.ToString()))
                    {
                        bytes.Add(b);
                    }

                    continue;
                }

                bytes.Add((byte)c);
                continue;
            }

            if (i + 2 >= segment.Length || !char.IsAsciiHexDigit(segment[i + 1]) || !char.IsAsciiHexDigit(segment[i + 2])
                || !byte.TryParse(segment.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            bytes.Add(value);
            i += 2;
        }

        try
        {
            decoded = new UTF8Encoding(false, true).GetString(bytes.ToArray());
        }
        catch (ArgumentException)
        {
            return false;
        }

        foreach (var c in decoded)
        {
            if (c < 0x20 || c == 0x7f || c == '%')
            {
                decoded = "";
                return false;
            }
        }

        return true;
    }
}
