using System.Text.RegularExpressions;
using Glider.Core.Store;

namespace Glider.Core.Links;

/// <summary>What a <c>com.epicgames.launcher://</c> link asks for. Everything outside this closed set is <see cref="Unknown"/>.</summary>
public enum EpicLinkKind
{
    /// <summary>Syntactically fine, but nothing Glider acts on (social, invite, settings, path form, ...).</summary>
    Unknown,

    /// <summary><c>apps/&lt;ns&gt;:&lt;catalog&gt;:&lt;app&gt;</c> with no action or <c>action=launch</c>.</summary>
    Launch,

    /// <summary><c>action=install</c>, <c>installer*</c> or <c>update</c>: open the install sheet, never start a download.</summary>
    Install,

    /// <summary><c>action=verify</c>: open the game page; the user presses the button.</summary>
    Verify,

    /// <summary><c>action=updatecheck</c>: open the game page.</summary>
    UpdateCheck,

    /// <summary><c>action=uninstall</c>: always ignored (a web page must not trigger a removal).</summary>
    Uninstall,

    /// <summary><c>store/p/&lt;slug&gt;</c>, <c>store/product/&lt;slug&gt;</c>, <c>store/bundles/&lt;slug&gt;</c>.</summary>
    StorePage,

    /// <summary><c>store</c> and <c>store/&lt;anything else&gt;</c>.</summary>
    StoreHome,

    /// <summary><c>library</c>.</summary>
    Library,
}

/// <summary>
/// The only data taken from an Epic launcher link. <see cref="AppName"/> is Legendary's app name (the artifact id, third
/// component); <see cref="Namespace"/>/<see cref="CatalogItemId"/> are only present for the three-part form. Launch
/// arguments, environment variables and paths from the link are never represented here: a web page must not inject them.
/// </summary>
public sealed record EpicLauncherLink(
    EpicLinkKind Kind,
    string? AppName = null,
    string? Namespace = null,
    string? CatalogItemId = null,
    string? StoreSlug = null,
    bool StoreIsBundle = false,
    bool Silent = false)
{
    public static EpicLauncherLink Unknown { get; } = new(EpicLinkKind.Unknown);
}

/// <summary>
/// Strict parser for <c>com.epicgames.launcher://</c> links (docs/research/store-webview.md §2.2-2.3). Pure, total (never
/// throws), allocation-light. Shared by the Store view (links clicked on the web Store) and the protocol handler.
/// Grammar: <c>apps/&lt;appRef&gt;[?action=..&amp;silent=..]</c>, <c>store[/p|product|bundles/&lt;slug&gt;]</c>, <c>library</c>.
/// </summary>
public static partial class EpicLauncherUri
{
    public const string Scheme = "com.epicgames.launcher";
    public const int MaxLength = 2048;

    private const string Prefix = Scheme + "://";

    /// <summary>True for any string that starts with the scheme (case-insensitive), whatever follows. For cheap pre-checks.</summary>
    public static bool HasScheme(string? raw) =>
        raw is not null && raw.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a link. Returns false (and <see cref="EpicLauncherLink.Unknown"/>) for anything malformed or hostile: wrong or
    /// missing scheme, over-long, control characters, userinfo/port/fragment, an embedded second scheme. Returns true with
    /// <see cref="EpicLinkKind.Unknown"/> for well-formed links Glider does not act on.
    /// </summary>
    public static bool TryParse(string? raw, out EpicLauncherLink link)
    {
        link = EpicLauncherLink.Unknown;
        if (string.IsNullOrEmpty(raw) || raw.Length > MaxLength || !HasScheme(raw))
        {
            return false;
        }

        foreach (var ch in raw)
        {
            if (ch < 0x20 || ch == 0x7f || ch > 0x7e)
            {
                return false; // control characters and non-ASCII (links are ASCII; anything else must be percent-encoded)
            }
        }

        var rest = raw[Prefix.Length..];
        if (rest.Contains('#') || rest.Contains('\\') || rest.Contains("://", StringComparison.Ordinal))
        {
            return false; // fragment, backslash, embedded second scheme
        }

        string path = rest, query = "";
        var q = rest.IndexOf('?');
        if (q >= 0)
        {
            path = rest[..q];
            query = rest[(q + 1)..];
        }

        var segments = path.Split('/', StringSplitOptions.None);
        // A single trailing slash is fine ("apps/x/"); empty segments elsewhere are malformed.
        if (segments.Length > 1 && segments[^1].Length == 0)
        {
            segments = segments[..^1];
        }

        var host = segments[0];
        if (host.Length == 0 || host.Contains('@') || host.Contains(':'))
        {
            return false; // userinfo or port
        }

        if (segments.Skip(1).Any(s => s.Length == 0))
        {
            return false;
        }

        link = host.ToLowerInvariant() switch
        {
            "apps" => ParseApps(segments, query),
            "store" => ParseStore(segments),
            "library" => segments.Length == 1 ? new EpicLauncherLink(EpicLinkKind.Library) : EpicLauncherLink.Unknown,
            _ => EpicLauncherLink.Unknown,
        };
        return true;
    }

    // ------------------------------------------------------------------ apps/...

    private static EpicLauncherLink ParseApps(string[] segments, string query)
    {
        if (segments.Length != 2)
        {
            return EpicLauncherLink.Unknown; // "apps/D%3A/Games/..." style path forms and bare "apps"
        }

        var decoded = Unescape(segments[1]);
        if (decoded is null || decoded.Length == 0 || decoded.Any(c => c < 0x20 || c == 0x7f))
        {
            return EpicLauncherLink.Unknown;
        }

        // Install-directory form (apps/D%3A%2FGames%2FMyGame): never take a path from a web page.
        if (decoded.IndexOfAny(['/', '\\']) >= 0 || decoded.Contains("..", StringComparison.Ordinal))
        {
            return EpicLauncherLink.Unknown;
        }

        string? ns = null, catalogId = null, appName;
        if (decoded.Contains(':'))
        {
            var parts = decoded.Split(':');
            if (parts.Length != 3 || !parts.All(p => Token().IsMatch(p)))
            {
                return EpicLauncherLink.Unknown; // drive-letter paths ("D:Games"), too many/few components
            }

            ns = parts[0];
            catalogId = parts[1];
            appName = parts[2];
        }
        else
        {
            if (!Token().IsMatch(decoded))
            {
                return EpicLauncherLink.Unknown; // also rejects double-encoded input ("%253A" decodes to "%3A")
            }

            appName = decoded;
        }

        if (!ParseQuery(query, out var action, out var silent))
        {
            return EpicLauncherLink.Unknown;
        }

        var kind = action switch
        {
            null or "launch" => EpicLinkKind.Launch,
            "install" or "update" or "installer" or "installer-redist" => EpicLinkKind.Install,
            "verify" => EpicLinkKind.Verify,
            "updatecheck" => EpicLinkKind.UpdateCheck,
            "uninstall" => EpicLinkKind.Uninstall,
            _ => EpicLinkKind.Unknown,
        };
        return kind == EpicLinkKind.Unknown
            ? EpicLauncherLink.Unknown
            : new EpicLauncherLink(kind, appName, ns, catalogId, Silent: silent);
    }

    /// <summary>Reads only <c>action</c> and <c>silent</c>; every other key (arg, env.*, install, installtag, ...) is dropped.</summary>
    private static bool ParseQuery(string query, out string? action, out bool silent)
    {
        action = null;
        silent = false;
        if (query.Length == 0)
        {
            return true;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Unescape(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : Unescape(pair[(eq + 1)..]);
            if (key is null || value is null)
            {
                return false;
            }

            if (key.Equals("action", StringComparison.OrdinalIgnoreCase))
            {
                action = value.ToLowerInvariant();
            }
            else if (key.Equals("silent", StringComparison.OrdinalIgnoreCase))
            {
                silent = value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
        }

        return true;
    }

    // ------------------------------------------------------------------ store/...

    private static EpicLauncherLink ParseStore(string[] segments)
    {
        if (segments.Length == 1)
        {
            return new EpicLauncherLink(EpicLinkKind.StoreHome);
        }

        var section = segments[1].ToLowerInvariant();
        var isProduct = section is "p" or "product";
        var isBundle = section is "bundles" or "bundle";
        if (!isProduct && !isBundle)
        {
            return new EpicLauncherLink(EpicLinkKind.StoreHome);
        }

        if (segments.Length != 3)
        {
            return segments.Length == 2 ? new EpicLauncherLink(EpicLinkKind.StoreHome) : EpicLauncherLink.Unknown;
        }

        var slug = Unescape(segments[2]);
        return slug is not null && StoreSlug.TryClean(slug, out var clean)
            ? new EpicLauncherLink(EpicLinkKind.StorePage, StoreSlug: clean, StoreIsBundle: isBundle)
            : EpicLauncherLink.Unknown;
    }

    /// <summary>Percent-decodes exactly once; null when the input is not valid percent-encoding.</summary>
    private static string? Unescape(string s)
    {
        try
        {
            return Uri.UnescapeDataString(s);
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,128}$")]
    private static partial Regex Token();
}
