using Glider.Core.Auth;

namespace Glider.Core.Tests.Auth;

public class EpicSignInTests
{
    private const string Code = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

    [Theory]
    [InlineData(Code)]
    [InlineData("  " + Code + "\r\n")]
    [InlineData("\"" + Code + "\"")]
    [InlineData("\"\\\"" + Code + "\\\"\"")]
    [InlineData("{\"exchangeCode\":\"" + Code + "\"}")]
    [InlineData("{\"exchange_code\":\"" + Code + "\",\"x\":1}")]
    [InlineData("{\"code\":\"" + Code + "\"}")]
    public void The_bridge_value_yields_the_exchange_code(string value) => Assert.Equal(Code, EpicSignIn.ParseExchangeCode(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("[\"a\"]")]
    [InlineData("{}")]
    [InlineData("{\"code\":null}")]
    [InlineData("{not json")]
    [InlineData("hello world")]
    [InlineData("https://www.epicgames.com/id/login")]
    public void Anything_else_is_rejected(string? value) => Assert.Null(EpicSignIn.ParseExchangeCode(value));
}
