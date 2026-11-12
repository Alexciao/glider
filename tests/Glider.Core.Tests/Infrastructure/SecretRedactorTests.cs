using System.Text;
using Glider.Core.Infrastructure;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Infrastructure;

/// <summary>Security review: shapes of Epic credentials that the old redaction (JSON double quotes only) missed.</summary>
public class SecretRedactorTests
{
    [Theory]
    [InlineData("""{"access_token": "eg1~abcDEF123", "x": 1}""", "eg1~abcDEF123")]
    [InlineData("""InvalidCredentialsError({'refresh_token': 'eg1~zzzz9999yyyy', 'errorCode': 'x'})""", "zzzz9999yyyy")]
    [InlineData("""{'code': 'f00dfeedf00dfeedf00dfeedf00dfeed'}""", "f00dfeedf00dfeed")]
    [InlineData("""{"authorizationCode":"0123456789abcdef0123456789abcdef","redirectUrl":"x"}""", "0123456789abcdef")]
    [InlineData("GET https://www.epicgames.com/id/api/login/google?code=4%2F0AQSTgQG&state=s", "4%2F0AQSTgQG")]
    [InlineData("POST /account/api/oauth/token grant_type=refresh_token&refresh_token=abcdef0123456789", "abcdef0123456789")]
    [InlineData("Fortnite.exe -AUTH_LOGIN=unused -AUTH_PASSWORD=aabbccdd -AUTH_TYPE=exchangecode", "aabbccdd")]
    [InlineData("exchangeCode: 99aa88bb77cc66dd", "99aa88bb77cc66dd")]
    [InlineData("legendary auth --code deadbeefcafebabe --disable-webview", "deadbeefcafebabe")]
    [InlineData("bearer eg1~eyJhbGciOiJIUzI1NiJ9.payload.sig", "eyJhbGciOiJIUzI1NiJ9")]
    public void RemovesTheSecret(string line, string secret)
    {
        var redacted = SecretRedactor.Redact(line);
        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[cli] INFO: Download size: 1234.56 MiB (Compressed)")]
    [InlineData(@"[cli] ERROR: File is missing: ""FortniteGame/Content/Paks/pakchunk0-WindowsClient.pak""")]
    [InlineData("> legendary auth --code *** --disable-webview")]
    public void LeavesOrdinaryLinesAlone(string line) => Assert.Equal(line, SecretRedactor.Redact(line));

    [Theory]
    [InlineData("https://www.epicgames.com/id/api/redirect?clientId=34a0&responseType=code", "https://www.epicgames.com/id/api/redirect?…")]
    [InlineData("https://accounts.google.com/o/oauth2/auth#access_token=ya29.x", "https://accounts.google.com/o/oauth2/auth?…")]
    [InlineData("https://www.epicgames.com/id/login", "https://www.epicgames.com/id/login")]
    [InlineData("", "")]
    public void UrlsAreLoggedWithoutQueryOrFragment(string url, string expected) => Assert.Equal(expected, SecretRedactor.UrlForLog(url));

    [Fact]
    public async Task TheLogFileItselfIsRedacted()
    {
        using var dir = new TempDir();
        await using var log = new FileLog(dir.Path, new ManualTimeProvider());
        log.Warn("legendary", "err| [cli] ERROR: Logging in failed with {'refresh_token': 'eg1~leakedtoken123'}");
        await log.FlushAsync();
        var text = Encoding.UTF8.GetString((await AtomicFile.TryReadAllBytesAsync(log.CurrentFile, TestContext.Current.CancellationToken))!);
        Assert.DoesNotContain("leakedtoken123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("leakedtoken123", log.Tail(10), StringComparison.Ordinal);
    }
}
