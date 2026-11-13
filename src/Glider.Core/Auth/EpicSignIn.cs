using System.Text.Json;

namespace Glider.Core.Auth;

/// <summary>Parsing of what Epic's login page hands to the launcher bridge (Legendary's flow).</summary>
public static class EpicSignIn
{
    /// <summary>
    /// The value Epic's page passes to <c>window.ue.signinprompt.requestexchangecodesignin</c>, as the host received it (raw JSON text of
    /// the web-message field, or plain text): a string, a quoted string, or an object holding the code. Null when nothing code-shaped is found.
    /// </summary>
    public static string? ParseExchangeCode(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            if (text.StartsWith('"'))
            {
                return ParseExchangeCode(JsonSerializer.Deserialize<string>(text));
            }

            if (text.StartsWith('{'))
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var name in new[] { "exchangeCode", "exchange_code", "exchangeToken", "code" })
                {
                    if (doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && LooksLikeCode(v.GetString() ?? ""))
                    {
                        return v.GetString();
                    }
                }

                return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return LooksLikeCode(text) ? text : null;
    }

    /// <summary>Epic's codes are 32 hex characters today; accept any plausible token-shaped string so a format change doesn't lock users out.</summary>
    private static bool LooksLikeCode(string s) => s.Length is >= 16 and <= 128 && s.All(char.IsAsciiLetterOrDigit);
}
