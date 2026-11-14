using Glider.Core.Store;

namespace Glider.Core.Links;

public enum EpicLinkKind
{
    /// <summary>Well-formed but not something Glider acts on (social, invite, settings, path forms, unknown actions…).</summary>
    Unknown,
    /// <summary><c>apps/&lt;ns&gt;%3A&lt;catalogItemId&gt;%3A&lt;appName&gt;</c> or the legacy bare <c>apps/&lt;appName&gt;</c>; <see cref="EpicLink.Action"/> says what to do.</summary>
    App,
    /// <summary><c>store/p/&lt;slug&gt;</c>, <c>store/product/&lt;slug&gt;</c> (and <c>store/bundles/&lt;slug&gt;</c>, <see cref="EpicLink.IsBundle"/>).</summary>
    StorePage,
    /// <summary><c>store</c> and <c>store/&lt;anything else&gt;</c> that is not a product or bundle page.</summary>
    StoreHome,
    Library,
}

public enum EpicLinkAction
{
    Launch,
    /// <summary><c>install</c> and <c>installer*</c>.</summary>
    Install,
    Update,
    /// <summary><c>verify</c> and <c>updatecheck</c>: open the game page; the user presses the button.</summary>
    Verify,
    /// <summary>Always ignored by every caller (a web page must not trigger a removal); still reported so callers can log it.</summary>
    Uninstall,
    /// <summary>An <c>action=</c> value Glider does not know: ignore the link (reported with <see cref="EpicLinkKind.Unknown"/>).</summary>
    Unsupported,
}

/// <summary>
/// What an Epic launcher link asks for, after validation. Launch arguments (<c>arg</c>), environment (<c>env.*</c>), install paths and
/// every other query parameter are dropped on purpose: no member can carry them, so a web page cannot inject them into a game process.
/// <see cref="AppName"/> is Legendary's app name (the artifact id, last component); <see cref="Namespace"/>/<see cref="CatalogItemId"/>
/// are only present for the three-part form. <see cref="Slug"/> is a validated Store product/bundle slug.
/// </summary>
public sealed record EpicLink(
    EpicLinkKind Kind,
    string? AppName = null,
    string? Namespace = null,
    string? CatalogItemId = null,
    EpicLinkAction Action = EpicLinkAction.Launch,
    bool Silent = false,
    string? Slug = null,
    bool IsBundle = false)
{
    /// <summary>The result for anything malformed, hostile or not acted on.</summary>
    public static EpicLink Unknown { get; } = new(EpicLinkKind.Unknown);
}

/// <summary>
/// The one strict parser for <c>com.epicgames.launcher://</c> links (shapes: docs/research/store-webview.md §2.2–2.3). Pure and total
/// (never throws); it never touches the network, the registry or the library. Shared by the OS protocol handler
/// (<see cref="LinkService"/> via <see cref="EpicLinkMapper"/>) and the in-Store deep links (<see cref="StoreIntentRouter"/>).
/// Grammar: <c>apps/&lt;appRef&gt;[?action=..&amp;silent=..]</c>, <c>store[/p|product|bundles/&lt;slug&gt;[/home]]</c>, <c>library</c>.
/// Of the two rule sets this merges, the stricter applies: ASCII only, no userinfo/port/fragment/backslash/second scheme, percent-decoding
/// exactly once, tokens limited to <c>[A-Za-z0-9._-]{1,128}</c>, slugs through <see cref="StoreSlug"/>, no paths from a link, a malformed
/// query refuses the link, and only <c>action</c>/<c>silent</c> are ever read.
/// </summary>
public static class EpicLauncherUri
{
    public const string Scheme = "com.epicgames.launcher";
    public const int MaxLength = 2048;

    private const string Prefix = Scheme + "://";

    /// <summary>True for any string that starts with the scheme (case-insensitive), whatever follows. For cheap pre-checks.</summary>
    public static bool HasScheme(string? raw) =>
        raw is not null && raw.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a link. False (and <see cref="EpicLink.Unknown"/>) for anything malformed or hostile; true for well-formed links, which may
    /// still be <see cref="EpicLinkKind.Unknown"/> (nothing Glider acts on).
    /// </summary>
    public static bool TryParse(string? raw, out EpicLink link) => Parse(raw, out link) == LinkRejection.None;

    /// <summary>As <see cref="TryParse"/>, saying why a link was refused (for logs and tests).</summary>
    public static LinkRejection Parse(string? raw, out EpicLink link)
    {
        link = EpicLink.Unknown;
        var common = LinkText.SplitScheme(raw, Scheme, MaxLength, out var rest, out var query);
        if (common != LinkRejection.None)
        {
            return common;
        }

        // Links are ASCII; anything else must be percent-encoded (so a bidi override or lookalike never reaches a decoder).
        if (!IsAscii(rest) || !IsAscii(query))
        {
            return LinkRejection.Malformed;
        }

        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var route = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? "" : rest[(slash + 1)..];
        if (route.Length == 0 || route.Contains(':'))
        {
            return LinkRejection.Malformed; // userinfo / port / missing host
        }

        if (!LinkText.TrySegments(path, out var segments))
        {
            return LinkRejection.Malformed;
        }

        switch (route.ToLowerInvariant())
        {
            case "apps":
                return ParseApp(segments, query, out link);

            case "store":
                return ParseStore(segments, out link);

            case "library":
                if (segments.Count == 0)
                {
                    link = new EpicLink(EpicLinkKind.Library);
                }

                return LinkRejection.None; // "library/extra" is well-formed but not a destination we know

            default:
                return LinkRejection.None; // well-formed, nothing we act on (kind stays Unknown)
        }
    }

    private static bool IsAscii(string s)
    {
        foreach (var c in s)
        {
            if (c > 0x7e)
            {
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ apps/...

    private static LinkRejection ParseApp(List<string> segments, string query, out EpicLink link)
    {
        link = EpicLink.Unknown;
        if (segments.Count != 1)
        {
            // "apps/D%3A/Games/x" style paths arrive as several segments: a path form, never taken from a link.
            return segments.Count == 0 ? LinkRejection.BadAppName : LinkRejection.None;
        }

        if (!LinkText.TryDecode(segments[0], out var reference))
        {
            return LinkRejection.BadEncoding;
        }

        if (reference.Contains('/') || reference.Contains('\\') || reference.Contains("..", StringComparison.Ordinal))
        {
            return LinkRejection.None; // install-directory form (apps/D%3A%2FGames%2FMyGame): never act on a path from a link
        }

        string? ns = null, catalogId = null, appName;
        if (!reference.Contains(':'))
        {
            if (!IsToken(reference))
            {
                return LinkRejection.BadAppName; // also rejects double-encoded input ("%253A" decodes to "%3A")
            }

            appName = reference;
        }
        else
        {
            var parts = reference.Split(':');
            if (parts.Length != 3)
            {
                return LinkRejection.None; // drive-letter paths ("D:") and anything else with colons
            }

            if (!parts.All(IsToken))
            {
                return LinkRejection.BadAppName;
            }

            ns = parts[0];
            catalogId = parts[1];
            appName = parts[2];
        }

        var rejection = ParseQuery(query, out var action, out var silent);
        if (rejection != LinkRejection.None)
        {
            return rejection;
        }

        link = action == EpicLinkAction.Unsupported
            ? new EpicLink(EpicLinkKind.Unknown, Action: EpicLinkAction.Unsupported) // no app name: nothing can act on it
            : new EpicLink(EpicLinkKind.App, appName, ns, catalogId, action, silent);
        return LinkRejection.None;
    }

    /// <summary>An app-name component. <see cref="GliderLinkParser.IsValidAppName"/> is the shared rule (<c>[A-Za-z0-9][A-Za-z0-9._-]{0,127}</c>).</summary>
    private static bool IsToken(string s) => GliderLinkParser.IsValidAppName(s);

    /// <summary>Reads only <c>action</c> and <c>silent</c>; every other key (arg, env.*, install, installtag, ...) is dropped. Undecodable pairs refuse the link.</summary>
    private static LinkRejection ParseQuery(string query, out EpicLinkAction action, out bool silent)
    {
        silent = false;
        action = EpicLinkAction.Launch;
        if (query.Length == 0)
        {
            return LinkRejection.None;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? "" : pair[(eq + 1)..];
            if (!LinkText.TryDecode(key, out var k) || !LinkText.TryDecode(value, out var v))
            {
                return LinkRejection.BadEncoding;
            }

            if (k.Equals("silent", StringComparison.OrdinalIgnoreCase))
            {
                silent = v.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            else if (k.Equals("action", StringComparison.OrdinalIgnoreCase))
            {
                action = v.ToLowerInvariant() switch
                {
                    "launch" => EpicLinkAction.Launch,
                    "install" or "installer" => EpicLinkAction.Install,
                    var s when s.StartsWith("installer", StringComparison.Ordinal) => EpicLinkAction.Install,
                    "update" => EpicLinkAction.Update,
                    "verify" or "updatecheck" => EpicLinkAction.Verify,
                    "uninstall" => EpicLinkAction.Uninstall,
                    _ => EpicLinkAction.Unsupported,
                };
            }
        }

        return LinkRejection.None;
    }

    // ------------------------------------------------------------------ store/...

    private static LinkRejection ParseStore(List<string> segments, out EpicLink link)
    {
        link = new EpicLink(EpicLinkKind.StoreHome);
        if (segments.Count == 0)
        {
            return LinkRejection.None;
        }

        var section = segments[0].ToLowerInvariant();
        var isProduct = section is "p" or "product";
        var isBundle = section is "bundles" or "bundle";
        if (!isProduct && !isBundle)
        {
            return LinkRejection.None; // store/browse, store/free-games, ...: the Store's home
        }

        if (segments.Count == 1)
        {
            return LinkRejection.None; // "store/p" without a slug
        }

        // store/p/<slug> or store/p/<slug>/home (the feed writes slugs as "name/home"); more than that is not a page we know.
        if (segments.Count > 3 || (segments.Count == 3 && !segments[2].Equals("home", StringComparison.OrdinalIgnoreCase)))
        {
            link = EpicLink.Unknown;
            return LinkRejection.None;
        }

        if (!LinkText.TryDecode(segments[1], out var decoded))
        {
            link = EpicLink.Unknown;
            return LinkRejection.BadEncoding;
        }

        if (!StoreSlug.TryClean(decoded, out var slug))
        {
            link = EpicLink.Unknown;
            return LinkRejection.BadSlug;
        }

        link = new EpicLink(EpicLinkKind.StorePage, Slug: slug, IsBundle: isBundle);
        return LinkRejection.None;
    }
}

/// <summary>Maps an Epic link onto Glider's own destinations. The app name still has to be resolved against the library by the caller.</summary>
public static class EpicLinkMapper
{
    /// <summary>
    /// Null when the link should be ignored (uninstall, unknown kinds and actions). Launch → launch (the handler opens the install
    /// dialog instead when the game isn't installed); install/update → install dialog; verify → the game's page.
    /// </summary>
    public static GliderLink? ToGliderLink(EpicLink link)
    {
        switch (link.Kind)
        {
            case EpicLinkKind.App when link.AppName is { } app:
                return link.Action switch
                {
                    EpicLinkAction.Launch => GliderLink.Launch(app),
                    EpicLinkAction.Install or EpicLinkAction.Update => GliderLink.Install(app),
                    EpicLinkAction.Verify => GliderLink.Game(app),
                    _ => null,
                };
            case EpicLinkKind.StorePage when link.Slug is { } slug && !link.IsBundle:
                return GliderLink.Store(slug);
            case EpicLinkKind.StorePage: // a bundle: glider:// has no bundle destination, so the Store's home
                return GliderLink.Store();
            case EpicLinkKind.StoreHome:
                return GliderLink.Store();
            case EpicLinkKind.Library:
                return GliderLink.Library;
            default:
                return null;
        }
    }
}
