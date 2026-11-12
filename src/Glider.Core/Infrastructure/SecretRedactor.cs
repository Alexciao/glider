using System.Text.RegularExpressions;

namespace Glider.Core.Infrastructure;

/// <summary>
/// Defence in depth for anything that reaches a log file or the clipboard: strips values shaped like Epic credentials
/// even if a component (or Legendary's own error text) printed one. Applied by <see cref="FileLog"/> on every line,
/// by the diagnostics export, and by the host for URLs it logs.
/// </summary>
public static partial class SecretRedactor
{
    public const string Mask = "<redacted>";

    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = AuthPassword().Replace(text, "$1" + Mask);
        text = KeyValue().Replace(text, "$1" + Mask);
        text = QueryParameter().Replace(text, "$1" + Mask);
        text = CodeArgument().Replace(text, "$1" + Mask);
        return EpicToken().Replace(text, Mask);
    }

    /// <summary>Scheme, host and path only: query strings and fragments of OAuth pages carry codes and tokens.</summary>
    public static string UrlForLog(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.IsFile)
        {
            var cut = url.IndexOfAny(['?', '#']);
            return cut < 0 ? Redact(url) : url[..cut] + "?…";
        }

        var hasMore = u.Query.Length > 0 || u.Fragment.Length > 0;
        return u.GetLeftPart(UriPartial.Path) + (hasMore ? "?…" : "");
    }

    [GeneratedRegex(@"(-AUTH_PASSWORD=)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthPassword();

    // JSON ("key": "v"), Python repr ('key': 'v') and key=value forms.
    [GeneratedRegex("""((?:access_token|refresh_token|id_token|authorization_?code|exchange_?code|ownership_?token|["']code["'])["']?\s*[:=]\s*["']?)[^"'\s,}&]+""", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"([?&#](?:code|access_token|refresh_token|id_token|token|exchange_code|exchangeCode)=)[^&#\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex QueryParameter();

    [GeneratedRegex(@"(--code[=\s]+)(?!\*\*\*)[^\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex CodeArgument();

    // Epic OAuth access/refresh tokens are "eg1~<base64url JWT>".
    [GeneratedRegex(@"\beg1~[A-Za-z0-9._\-~+/=]{8,}")]
    private static partial Regex EpicToken();
}
