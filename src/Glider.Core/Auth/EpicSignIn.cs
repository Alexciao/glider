using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glider.Core.Auth;

/// <summary>Epic's sign-in endpoints and the pure parsing of the authorization code (Legendary's documented flow).</summary>
public static partial class EpicSignIn
{
    private const string ClientId = "34a02cf8f4414e29b15921876da36f9a";
    public const string RedirectPrefix = "https://www.epicgames.com/id/api/redirect";

    /// <summary>The only URL Glider ever opens for sign-in (embedded window and system browser): fixed, never user-supplied.</summary>
    public const string LoginUrl =
        "https://www.epicgames.com/id/login?redirectUrl=https%3A%2F%2Fwww.epicgames.com%2Fid%2Fapi%2Fredirect%3FclientId%3D" + ClientId + "%26responseType%3Dcode";

    /// <summary>Strict: the redirect page's text must be the JSON (or a JSON string holding it). Throws <see cref="JsonException"/> otherwise.</summary>
    public static string? ParseAuthorizationCode(string? innerText)
    {
        if (string.IsNullOrWhiteSpace(innerText))
        {
            return null;
        }

        var text = innerText.Trim();
        if (text.StartsWith('"'))
        {
            text = JsonSerializer.Deserialize<string>(text) ?? "";
        }

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("authorizationCode", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } s
            ? s
            : null;
    }

    /// <summary>
    /// Lenient, for text the user pasted from their browser: the whole JSON page, a JSON fragment (<c>"authorizationCode":"…"</c>),
    /// or just the code (quoted or not). Null when nothing that looks like a code is found.
    /// </summary>
    public static string? ParsePastedCode(string? pasted)
    {
        var text = pasted?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            return text.StartsWith('{') || text.StartsWith('"') && text.Contains("authorizationCode", StringComparison.Ordinal)
                ? ParseAuthorizationCode(text)
                : LooksLikeCode(text.Trim('"', '\'', ' ')) ? text.Trim('"', '\'', ' ') : FromFragment(text);
        }
        catch (JsonException)
        {
            return FromFragment(text);
        }
    }

    private static string? FromFragment(string text)
    {
        var m = CodeFragment().Match(text);
        return m.Success && LooksLikeCode(m.Groups[1].Value) ? m.Groups[1].Value : null;
    }

    /// <summary>Epic's codes are 32 hex characters today; accept any plausible token-shaped string so a format change doesn't lock users out.</summary>
    private static bool LooksLikeCode(string s) => s.Length is >= 16 and <= 128 && s.All(char.IsAsciiLetterOrDigit);

    [GeneratedRegex("\"authorizationCode\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex CodeFragment();
}
