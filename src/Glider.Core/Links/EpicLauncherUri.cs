namespace Glider.Core.Links;

public enum EpicLinkKind
{
    /// <summary>Well-formed but not something Glider acts on (social, invite, settings, path forms…).</summary>
    Unknown,
    /// <summary><c>apps/&lt;ns&gt;%3A&lt;catalogItemId&gt;%3A&lt;appName&gt;</c> or the legacy bare <c>apps/&lt;appName&gt;</c>.</summary>
    App,
    /// <summary><c>store/p/&lt;slug&gt;</c> or <c>store/product/&lt;slug&gt;</c>.</summary>
    StorePage,
    /// <summary><c>store</c> and <c>store/&lt;anything else&gt;</c>.</summary>
    StoreHome,
    Library,
}

public enum EpicLinkAction
{
    Launch,
    Install,
    Update,
    Verify,
    Uninstall,
    /// <summary>An <c>action=</c> value Glider does not know: ignore the link.</summary>
    Unsupported,
}

/// <summary>What an Epic launcher link asks for, after validation. <c>arg</c>, <c>env.*</c>, install paths and other query parameters are dropped on purpose.</summary>
public sealed record EpicLink(
    EpicLinkKind Kind,
    string? AppName = null,
    string? Namespace = null,
    string? CatalogItemId = null,
    EpicLinkAction Action = EpicLinkAction.Launch,
    bool Silent = false,
    string? Slug = null);

/// <summary>
/// Pure parser for <c>com.epicgames.launcher://</c> links (shapes: docs/research/store-webview.md §2.2–2.3). It never touches the
/// network, the registry or the library; <see cref="EpicLinkMapper"/> turns the result into a Glider destination.
/// </summary>
public static class EpicLauncherUri
{
    public const string Scheme = "com.epicgames.launcher";
    public const int MaxLength = 2048;

    public static bool TryParse(string? raw, out EpicLink link) => Parse(raw, out link) == LinkRejection.None;

    public static LinkRejection Parse(string? raw, out EpicLink link)
    {
        link = new EpicLink(EpicLinkKind.Unknown);
        var common = LinkText.SplitScheme(raw, Scheme, MaxLength, out var rest, out var query);
        if (common != LinkRejection.None)
        {
            return common;
        }

        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var route = slash < 0 ? rest : rest[..slash];
        var path = slash < 0 ? "" : rest[(slash + 1)..];
        if (route.Length == 0 || route.Contains(':'))
        {
            return LinkRejection.Malformed;
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
                if (segments.Count == 2 && (segments[0].Equals("p", StringComparison.OrdinalIgnoreCase) || segments[0].Equals("product", StringComparison.OrdinalIgnoreCase)))
                {
                    if (!LinkText.TryDecode(segments[1], out var slug))
                    {
                        return LinkRejection.BadEncoding;
                    }

                    if (!GliderLinkParser.IsValidSlug(slug))
                    {
                        return LinkRejection.BadSlug;
                    }

                    link = new EpicLink(EpicLinkKind.StorePage, Slug: slug);
                    return LinkRejection.None;
                }

                link = new EpicLink(EpicLinkKind.StoreHome);
                return LinkRejection.None;

            case "library":
                link = new EpicLink(EpicLinkKind.Library);
                return LinkRejection.None;

            default:
                return LinkRejection.None; // well-formed, nothing we act on (kind stays Unknown)
        }
    }

    private static LinkRejection ParseApp(List<string> segments, string query, out EpicLink link)
    {
        link = new EpicLink(EpicLinkKind.Unknown);
        if (segments.Count != 1)
        {
            // "apps/D%3A/Games/x" style paths arrive as several segments: a path form, never taken from a link.
            return segments.Count == 0 ? LinkRejection.BadAppName : LinkRejection.None;
        }

        if (!LinkText.TryDecode(segments[0], out var reference))
        {
            return LinkRejection.BadEncoding;
        }

        var action = ParseQuery(query, out var silent);
        if (reference.Contains('/') || reference.Contains('\\') || reference.Contains("..", StringComparison.Ordinal))
        {
            return LinkRejection.None; // install-directory form (apps/D%3A%2FGames%2FMyGame): never act on a path from a link
        }

        if (!reference.Contains(':'))
        {
            if (!GliderLinkParser.IsValidAppName(reference))
            {
                return LinkRejection.BadAppName;
            }

            link = new EpicLink(EpicLinkKind.App, reference, Action: action, Silent: silent);
            return LinkRejection.None;
        }

        var parts = reference.Split(':');
        if (parts.Length != 3)
        {
            return LinkRejection.None; // drive-letter paths ("D:") and anything else with colons
        }

        if (!parts.All(GliderLinkParser.IsValidAppName))
        {
            return LinkRejection.BadAppName;
        }

        link = new EpicLink(EpicLinkKind.App, parts[2], parts[0], parts[1], action, silent);
        return LinkRejection.None;
    }

    /// <summary>Only <c>action</c> and <c>silent</c> are read, and only values on the allow-list count.</summary>
    private static EpicLinkAction ParseQuery(string query, out bool silent)
    {
        silent = false;
        var action = EpicLinkAction.Launch;
        if (query.Length == 0)
        {
            return action;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? pair : pair[..eq];
            var value = eq < 0 ? "" : pair[(eq + 1)..];
            if (!LinkText.TryDecode(key, out var k) || !LinkText.TryDecode(value, out var v))
            {
                continue;
            }

            if (k.Equals("silent", StringComparison.OrdinalIgnoreCase))
            {
                silent = v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
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

        return action;
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
            case EpicLinkKind.StorePage:
                return GliderLink.Store(link.Slug);
            case EpicLinkKind.StoreHome:
                return GliderLink.Store();
            case EpicLinkKind.Library:
                return GliderLink.Library;
            default:
                return null;
        }
    }
}
