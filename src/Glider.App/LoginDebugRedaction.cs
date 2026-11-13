using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Glider.Core.Infrastructure;

namespace Glider.App;

// Pure (no WebView2/WPF types) so tests/Glider.Core.Tests can link this file.

/// <summary>
/// Text handling for the interactive <c>--login-debug</c> log. The user types real credentials into the window, so nothing from a
/// request or response may reach the log unless it is an error description: values of credential-like keys, e-mail addresses and
/// anything shaped like an Epic token are masked, and bodies are cut short.
/// </summary>
public static partial class LoginDebugRedaction
{
    public const string Mask = "<redacted>";

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "code", "token", "password", "email", "sid", "sessionid", "otp", "pin", "secret", "username", "login", "phone",
        "authorizationcode", "exchangecode", "access_token", "refresh_token", "id_token", "accesstoken", "refreshtoken",
    };

    /// <summary>Values of these keys are hidden; <c>errorCode</c>, <c>errorMessage</c>, <c>numericErrorCode</c>, <c>messageVars</c> are kept on purpose.</summary>
    public static bool IsSensitiveKey(string key) =>
        SensitiveKeys.Contains(key)
        || key.EndsWith("token", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("password", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("authorizationcode", StringComparison.OrdinalIgnoreCase)
        || key.Contains("exchangecode", StringComparison.OrdinalIgnoreCase)
        || key.Contains("email", StringComparison.OrdinalIgnoreCase)
        || key.Contains("captcha", StringComparison.OrdinalIgnoreCase);

    /// <summary>A response body for the log: JSON with sensitive values masked (anything else is text-masked), at most <paramref name="max"/> chars.</summary>
    public static string RedactBody(string? body, int max = 600)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        string text;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var sb = new StringBuilder();
            using (var ms = new MemoryStream())
            {
                using (var w = new Utf8JsonWriter(ms))
                {
                    Write(doc.RootElement, w);
                }

                sb.Append(Encoding.UTF8.GetString(ms.ToArray()));
            }

            text = sb.ToString();
        }
        catch (JsonException)
        {
            text = JsonLikeSecret().Replace(body, "$1" + Mask);
        }

        return MaskText(text, max);
    }

    /// <summary>E-mail addresses and token-shaped values masked, whitespace collapsed, cut to <paramref name="max"/> chars.</summary>
    public static string MaskText(string? text, int max)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        text = SecretRedactor.Redact(EmailAddress().Replace(text, Mask));
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>Names (never values) of the keys of a JSON object body, or of a form-encoded body; empty for anything else.</summary>
    public static string[] BodyKeyNames(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? [.. doc.RootElement.EnumerateObject().Select(p => p.Name)] : [];
        }
        catch (JsonException)
        {
            return body.Contains('=')
                ? [.. body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => Uri.UnescapeDataString(p.Split('=')[0])).Where(n => n.Length is > 0 and < 64)]
                : [];
        }
    }

    /// <summary>Scheme, host and path only (queries and fragments of OAuth pages carry one-time codes).</summary>
    public static string UrlNoQuery(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : SecretRedactor.UrlForLog(url);

    private static void Write(JsonElement e, Utf8JsonWriter w)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject())
                {
                    w.WritePropertyName(p.Name);
                    if (IsSensitiveKey(p.Name) && p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object or JsonValueKind.Array))
                    {
                        w.WriteStringValue(Mask);
                    }
                    else
                    {
                        Write(p.Value, w);
                    }
                }

                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var i in e.EnumerateArray())
                {
                    Write(i, w);
                }

                w.WriteEndArray();
                break;
            default:
                e.WriteTo(w);
                break;
        }
    }

    [GeneratedRegex("[A-Za-z0-9._%+\\-]+@[A-Za-z0-9.\\-]+\\.[A-Za-z]{2,}")]
    private static partial Regex EmailAddress();

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("(\"(?:code|token|password|email|sid|[a-z_]*token|[a-z_]*password|authorizationCode|exchangeCode)\"\\s*:\\s*\")[^\"]*", RegexOptions.IgnoreCase)]
    private static partial Regex JsonLikeSecret();
}
