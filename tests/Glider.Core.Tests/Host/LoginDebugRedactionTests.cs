using Glider.App;

namespace Glider.Core.Tests.Host;

public class LoginDebugRedactionTests
{
    [Fact]
    public void Error_bodies_keep_the_error_fields_and_hide_credential_values()
    {
        const string body = """
            {"errorCode":"errors.com.epicgames.accountportal.captcha_invalid","errorMessage":"Captcha invalid","numericErrorCode":18200,
             "messageVars":["abc"],"email":"someone@example.com","password":"hunter2","access_token":"a1b2c3d4e5f60718293a4b5c6d7e8f90",
             "refresh_token":"r","authorizationCode":"a1b2c3d4e5f60718293a4b5c6d7e8f90","exchangeCode":"x","code":"123456",
             "nested":{"token":"t","keep":"yes"},"captcha":"p0_abc"}
            """;
        var text = LoginDebugRedaction.RedactBody(body, 2000);
        Assert.Contains("errors.com.epicgames.accountportal.captcha_invalid", text, StringComparison.Ordinal);
        Assert.Contains("Captcha invalid", text, StringComparison.Ordinal);
        Assert.Contains("18200", text, StringComparison.Ordinal);
        Assert.Contains("\"keep\":\"yes\"", text, StringComparison.Ordinal);
        foreach (var secret in new[] { "someone@example.com", "hunter2", "a1b2c3d4e5f60718293a4b5c6d7e8f90", "123456", "p0_abc", "\"r\"", "\"x\"", "\"t\"" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Bodies_are_cut_to_the_limit()
    {
        var text = LoginDebugRedaction.RedactBody("{\"errorMessage\":\"" + new string('x', 2000) + "\"}");
        Assert.True(text.Length <= 601, text.Length.ToString());
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_json_text_masks_addresses_tokens_and_key_value_secrets()
    {
        var text = LoginDebugRedaction.RedactBody("<html>user person@example.org said \"password\": \"s3cret\" and \"errorCode\": \"e1\"</html>");
        Assert.DoesNotContain("person@example.org", text, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", text, StringComparison.Ordinal);
        Assert.Contains("e1", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("errorCode", false)]
    [InlineData("numericErrorCode", false)]
    [InlineData("errorMessage", false)]
    [InlineData("messageVars", false)]
    [InlineData("code", true)]
    [InlineData("Token", true)]
    [InlineData("refresh_token", true)]
    [InlineData("exchangeCode", true)]
    [InlineData("authorizationCode", true)]
    [InlineData("password", true)]
    [InlineData("email", true)]
    [InlineData("hcaptchaResponse", true)]
    public void Sensitive_key_rule(string key, bool sensitive) => Assert.Equal(sensitive, LoginDebugRedaction.IsSensitiveKey(key));

    [Fact]
    public void Body_key_names_never_include_values()
    {
        Assert.Equal(["email", "password", "captcha"], LoginDebugRedaction.BodyKeyNames("{\"email\":\"a@b.co\",\"password\":\"pw\",\"captcha\":\"c\"}"));
        Assert.Equal(["email", "password"], LoginDebugRedaction.BodyKeyNames("email=a%40b.co&password=pw"));
        Assert.Empty(LoginDebugRedaction.BodyKeyNames("[1,2]"));
        Assert.Empty(LoginDebugRedaction.BodyKeyNames(null));
    }

    [Fact]
    public void Urls_lose_query_and_fragment()
    {
        Assert.Equal("https://www.epicgames.com/id/api/redirect", LoginDebugRedaction.UrlNoQuery("https://www.epicgames.com/id/api/redirect?clientId=x&code=abc#frag"));
    }

    [Fact]
    public void Console_text_is_masked_and_truncated()
    {
        var text = LoginDebugRedaction.MaskText("failed for me@example.com " + new string('y', 500), 300);
        Assert.DoesNotContain("me@example.com", text, StringComparison.Ordinal);
        Assert.True(text.Length <= 301);
    }
}
