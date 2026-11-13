using System.Text.Json;
using Glider.App;

namespace Glider.Core.Tests.Host;

public class EdgeUserAgentMetadataTests
{
    // What the 0.1.3 login view reported (taken from --login-audit): WebView2's extra brand, and Chromium's slot order differs from Edge's.
    private const string WebView2Hints = """
        {"architecture":"x86","bitness":"64","brands":[],"formFactors":["Desktop"],
         "fullVersionList":[{"brand":"Chromium","version":"157.0.8103.39"},{"brand":"Microsoft Edge WebView2","version":"157.0.4341.46"},
                            {"brand":"Not-A.Brand","version":"99.0.0.0"},{"brand":"Microsoft Edge","version":"157.0.4341.46"}],
         "mobile":false,"model":"","platform":"Windows","platformVersion":"19.0.0","uaFullVersion":"157.0.4341.46","wow64":false}
        """;

    private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/157.0.0.0 Safari/537.36 Edg/157.0.0.0";

    private static JsonElement Parse(string? json) => JsonDocument.Parse(json!).RootElement.Clone();

    [Fact]
    public void Drops_the_WebView2_brand_and_orders_brands_like_Edge_157()
    {
        var root = Parse(EdgeUserAgentMetadata.BuildOverrideJson(Ua, WebView2Hints));
        Assert.Equal(Ua, root.GetProperty("userAgent").GetString());
        var meta = root.GetProperty("userAgentMetadata");
        var brands = meta.GetProperty("brands").EnumerateArray().Select(b => $"{b.GetProperty("brand").GetString()};{b.GetProperty("version").GetString()}").ToArray();
        Assert.Equal(["Not-A.Brand;99", "Microsoft Edge;157", "Chromium;157"], brands);
        var full = meta.GetProperty("fullVersionList").EnumerateArray().Select(b => $"{b.GetProperty("brand").GetString()};{b.GetProperty("version").GetString()}").ToArray();
        Assert.Equal(["Not-A.Brand;99.0.0.0", "Microsoft Edge;157.0.4341.46", "Chromium;157.0.8103.39"], full);
        Assert.DoesNotContain("WebView2", root.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Carries_over_the_platform_values_of_the_runtime()
    {
        var meta = Parse(EdgeUserAgentMetadata.BuildOverrideJson(Ua, WebView2Hints)).GetProperty("userAgentMetadata");
        Assert.Equal("Windows", meta.GetProperty("platform").GetString());
        Assert.Equal("19.0.0", meta.GetProperty("platformVersion").GetString());
        Assert.Equal("x86", meta.GetProperty("architecture").GetString());
        Assert.Equal("64", meta.GetProperty("bitness").GetString());
        Assert.False(meta.GetProperty("mobile").GetBoolean());
        Assert.False(meta.GetProperty("wow64").GetBoolean());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"fullVersionList":[{"brand":"Chromium","version":"157.0.1.1"}]}""")] // no Edge, no grease: leave the view alone
    [InlineData("""{"fullVersionList":[{"brand":"Microsoft Edge","version":"x"},{"brand":"Chromium","version":"1.0"},{"brand":"Not-A.Brand","version":"99.0.0.0"}]}""")]
    public void Returns_null_when_the_runtime_data_is_not_what_we_expect(string hints) =>
        Assert.Null(EdgeUserAgentMetadata.BuildOverrideJson(Ua, hints));
}
