using System.Text.Json;
using Glider.Core.Auth;
using Glider.Core.Contracts;
using Glider.Core.Tests.Services;

namespace Glider.Core.Tests.Auth;

public class EpicSignInTests
{
    private const string Code = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

    [Theory]
    [InlineData(Code)]
    [InlineData("  " + Code + "\r\n")]
    [InlineData("\"" + Code + "\"")]
    [InlineData("{\"redirectUrl\":\"https://www.epicgames.com/id/api/redirect?clientId=x\",\"sid\":null,\"authorizationCode\":\"" + Code + "\"}")]
    [InlineData("{\n  \"redirectUrl\": \"x\",\n  \"authorizationCode\": \"" + Code + "\"\n}\n")]
    [InlineData("\"authorizationCode\":\"" + Code + "\"")]
    [InlineData("\"authorizationCode\": \"" + Code + "\",")]
    [InlineData("\"{\\\"authorizationCode\\\":\\\"" + Code + "\\\"}\"")]
    public void Pasted_text_yields_the_code(string pasted) => Assert.Equal(Code, EpicSignIn.ParsePastedCode(pasted));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello world")]
    [InlineData("{\"authorizationCode\":null}")]
    [InlineData("{\"sid\":\"x\"}")]
    [InlineData("{not json")]
    [InlineData("https://www.epicgames.com/id/api/redirect?clientId=x")]
    public void Pasted_text_without_a_code_is_rejected(string? pasted) => Assert.Null(EpicSignIn.ParsePastedCode(pasted));

    [Fact]
    public void Strict_parser_still_requires_the_redirect_json()
    {
        Assert.Equal(Code, EpicSignIn.ParseAuthorizationCode("{\"authorizationCode\":\"" + Code + "\"}"));
        Assert.Null(EpicSignIn.ParseAuthorizationCode("{\"authorizationCode\":null}"));
        Assert.ThrowsAny<JsonException>(() => EpicSignIn.ParseAuthorizationCode("Not found"));
    }

    [Fact]
    public void Login_url_is_a_fixed_epicgames_https_url_with_the_redirect_flow()
    {
        var u = new Uri(EpicSignIn.LoginUrl);
        Assert.Equal("https", u.Scheme);
        Assert.Equal("www.epicgames.com", u.Host);
        Assert.Contains("responseType%3Dcode", EpicSignIn.LoginUrl, StringComparison.Ordinal);
        Assert.StartsWith(EpicSignIn.RedirectPrefix, Uri.UnescapeDataString(new Uri(EpicSignIn.LoginUrl).Query.Split('=', 2)[1]), StringComparison.Ordinal);
    }
}

public class BrowserSignInServiceTests
{
    [Fact]
    public async Task OpenBrowserLogin_opens_only_the_fixed_login_url()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        await rig.Service.OpenBrowserLogin();
        Assert.Equal([EpicSignIn.LoginUrl], rig.Host.Opened);
    }

    [Fact]
    public async Task SignInWithCode_accepts_pasted_json_and_signs_in_without_the_login_window()
    {
        using var rig = new Rig();
        rig.Legendary.Account = null;
        await rig.StartWarmAsync();
        var state = await rig.Service.SignInWithCode(new AuthCodeRequest("{\"redirectUrl\":\"x\",\"authorizationCode\":\"a1b2c3d4e5f60718293a4b5c6d7e8f90\"}"));
        Assert.Equal(AuthStatus.SignedIn, state.Status);
        Assert.True(rig.Calls.Has("legendary.signIn:a1b2c3d4e5f60718293a4b5c6d7e8f90"));
        Assert.False(rig.Calls.Has("host.login"));
    }

    [Fact]
    public async Task SignInWithCode_rejects_text_without_a_code_and_keeps_the_signed_out_state()
    {
        using var rig = new Rig();
        rig.Legendary.Account = null;
        await rig.StartWarmAsync();
        var e = await Assert.ThrowsAsync<GliderException>(() => rig.Service.SignInWithCode(new AuthCodeRequest("nope")));
        Assert.Equal(ErrorCode.AuthFailed, e.Error.Code);
        Assert.Equal(AuthStatus.SignedOut, (await rig.Service.GetSnapshot()).Auth.Status);
        Assert.DoesNotContain(rig.Calls.All, c => c.StartsWith("legendary.signIn", StringComparison.Ordinal));
    }
}
