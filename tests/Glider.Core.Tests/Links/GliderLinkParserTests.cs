using Glider.Core.Links;

namespace Glider.Core.Tests.Links;

public class GliderLinkParserTests
{
    private static GliderLink Ok(string raw)
    {
        Assert.Equal(LinkRejection.None, GliderLinkParser.Parse(raw, out var link));
        return link;
    }

    private static LinkRejection Why(string? raw) => GliderLinkParser.Parse(raw, out _);

    [Theory]
    [InlineData("glider://launch/Fortnite", GliderLinkKind.Launch, "Fortnite")]
    [InlineData("glider://install/Sugar", GliderLinkKind.Install, "Sugar")]
    [InlineData("glider://game/4a3b2c1d0e9f8a7b6c5d4e3f2a1b0c9d", GliderLinkKind.Game, "4a3b2c1d0e9f8a7b6c5d4e3f2a1b0c9d")]
    [InlineData("GLIDER://LAUNCH/Fortnite", GliderLinkKind.Launch, "Fortnite")]
    [InlineData("glider://launch/Fortnite/", GliderLinkKind.Launch, "Fortnite")] // Windows appends a trailing slash
    [InlineData("\"glider://launch/Fortnite\"", GliderLinkKind.Launch, "Fortnite")] // quoted on a command line
    [InlineData("glider://launch/%46ortnite", GliderLinkKind.Launch, "Fortnite")] // decoded once, then validated
    [InlineData("glider://launch/Some_Game-1.5", GliderLinkKind.Launch, "Some_Game-1.5")]
    public void AppLinks(string raw, GliderLinkKind kind, string app)
    {
        var link = Ok(raw);
        Assert.Equal(kind, link.Kind);
        Assert.Equal(app, link.AppName);
    }

    [Fact]
    public void RoutesOfEveryKind()
    {
        Assert.Equal("game/Fortnite", Ok("glider://launch/Fortnite").Route);
        Assert.Equal("game/Fortnite", Ok("glider://game/Fortnite").Route);
        Assert.Equal("game/Fortnite/install", Ok("glider://install/Fortnite").Route);
        Assert.Equal("downloads", Ok("glider://downloads").Route);
        Assert.Equal("downloads", Ok("glider://downloads/").Route);
        Assert.Equal("library", Ok("glider://library").Route);
        Assert.Equal("store", Ok("glider://store").Route);
        Assert.Equal("store/p/fortnite", Ok("glider://store/p/fortnite").Route);
        Assert.Equal("store/p/grand-theft-auto-v_enhanced", Ok("glider://store/p/grand-theft-auto-v_enhanced").Route);
    }

    [Fact]
    public void UriRoundTrips()
    {
        foreach (var raw in new[] { "glider://launch/Fortnite", "glider://install/Sugar", "glider://game/Foo.Bar", "glider://downloads", "glider://library", "glider://store", "glider://store/p/some-game" })
        {
            var link = Ok(raw);
            Assert.Equal(raw, link.Uri);
            Assert.Equal(link, Ok(link.Uri));
        }
    }

    [Fact]
    public void OnlyLaunchNeedsNoWindow()
    {
        Assert.False(Ok("glider://launch/Fortnite").NeedsWindow);
        Assert.True(Ok("glider://install/Fortnite").NeedsWindow);
        Assert.True(Ok("glider://game/Fortnite").NeedsWindow);
        Assert.True(Ok("glider://downloads").NeedsWindow);
        Assert.True(Ok("glider://store").NeedsWindow);
    }

    [Fact]
    public void QueryIsIgnored_NothingAWebPageAppendsChangesTheAction()
    {
        var link = Ok("glider://launch/Fortnite?arg=%2D%2Devil&altExe=x&gui=false");
        Assert.Equal(GliderLink.Launch("Fortnite"), link);
        Assert.Equal("glider://launch/Fortnite", link.Uri);
    }

    [Theory]
    [InlineData(null, LinkRejection.Empty)]
    [InlineData("", LinkRejection.Empty)]
    [InlineData("   ", LinkRejection.Empty)]
    [InlineData("http://launch/Fortnite", LinkRejection.BadScheme)]
    [InlineData("glider:launch/Fortnite", LinkRejection.BadScheme)]
    [InlineData("com.epicgames.launcher://apps/x", LinkRejection.BadScheme)]
    [InlineData("gliderx://launch/Fortnite", LinkRejection.BadScheme)]
    [InlineData("glider://settings", LinkRejection.UnknownRoute)]
    [InlineData("glider://launch/", LinkRejection.BadAppName)]
    [InlineData("glider://launch", LinkRejection.BadAppName)]
    [InlineData("glider://", LinkRejection.Malformed)]
    [InlineData("glider:///launch/Fortnite", LinkRejection.Malformed)]
    [InlineData("glider://launch//Fortnite", LinkRejection.Malformed)]
    [InlineData("glider://launch/a/b", LinkRejection.Malformed)]
    [InlineData("glider://downloads/x", LinkRejection.Malformed)]
    [InlineData("glider://store/p", LinkRejection.Malformed)]
    [InlineData("glider://store/x/y", LinkRejection.Malformed)]
    [InlineData("glider://store/p/a/b", LinkRejection.Malformed)]
    public void RefusedShapes(string? raw, LinkRejection why) => Assert.Equal(why, Why(raw));

    [Theory]
    [InlineData("glider://launch/..", LinkRejection.BadAppName)]
    [InlineData("glider://launch/../Windows", LinkRejection.Malformed)]
    [InlineData("glider://launch/..%2FWindows%2Fcmd", LinkRejection.BadAppName)]
    [InlineData("glider://launch/%2e%2e", LinkRejection.BadAppName)]
    [InlineData("glider://launch/.hidden", LinkRejection.BadAppName)]
    [InlineData("glider://launch/a..b", LinkRejection.BadAppName)]
    [InlineData("glider://launch/C%3A%5CWindows%5Csystem32%5Ccmd.exe", LinkRejection.BadAppName)]
    [InlineData("glider://launch/C:\\Windows\\cmd.exe", LinkRejection.Malformed)]
    [InlineData("glider://launch/\\\\server\\share", LinkRejection.Malformed)]
    public void PathTraversalIsNeverAnAppName(string raw, LinkRejection why) => Assert.Equal(why, Why(raw));

    [Theory]
    [InlineData("glider://launch/Fort%22nite", LinkRejection.BadAppName)] // quote
    [InlineData("glider://launch/Fortnite%20--evil", LinkRejection.BadAppName)] // space → extra argument
    [InlineData("glider://launch/Fortnite&calc", LinkRejection.BadAppName)]
    [InlineData("glider://launch/Fortnite%26calc", LinkRejection.BadAppName)]
    [InlineData("glider://launch/Fortnite;calc", LinkRejection.BadAppName)]
    [InlineData("glider://launch/Fortnite|calc", LinkRejection.BadAppName)]
    [InlineData("glider://launch/Fortnite`calc", LinkRejection.BadAppName)]
    [InlineData("glider://launch/$(calc)", LinkRejection.BadAppName)]
    [InlineData("glider://launch/Fortnite%0Acalc", LinkRejection.BadEncoding)] // newline
    [InlineData("glider://launch/Fortnite%00", LinkRejection.BadEncoding)] // NUL
    [InlineData("glider://launch/Fortnite%7F", LinkRejection.BadEncoding)]
    [InlineData("glider://launch/Fortn%C3%AFte", LinkRejection.BadAppName)] // non-ASCII
    [InlineData("glider://launch/Fortn\u00efte", LinkRejection.BadAppName)]
    [InlineData("glider://launch/\u202eetnitroF", LinkRejection.BadAppName)] // bidi override
    public void InjectionAttempts(string raw, LinkRejection why) => Assert.Equal(why, Why(raw));

    [Theory]
    [InlineData("glider://launch/%2541")] // double encoding decodes to "%41"
    [InlineData("glider://launch/%4")]
    [InlineData("glider://launch/%")]
    [InlineData("glider://launch/%zz")]
    [InlineData("glider://launch/%C3%28")] // invalid UTF-8
    [InlineData("glider://launch/%FF%FE")]
    public void BrokenEncodingIsRefused(string raw) => Assert.Equal(LinkRejection.BadEncoding, Why(raw));

    [Theory]
    [InlineData("glider://launch/http://evil.example/x")]
    [InlineData("glider://launch/Fortnite#frag")]
    [InlineData("glider://user@launch/Fortnite")]
    [InlineData("glider://user:pass@launch/Fortnite")]
    [InlineData("glider://launch:80/Fortnite")]
    [InlineData("glider://launch/Fortnite?next=glider://install/x#y")]
    public void AuthorityFragmentAndNestedSchemesAreMalformed(string raw) => Assert.Equal(LinkRejection.Malformed, Why(raw));

    [Fact]
    public void LengthLimits()
    {
        Assert.Equal(LinkRejection.None, Why("glider://launch/" + new string('a', GliderLinkParser.MaxAppNameLength)));
        Assert.Equal(LinkRejection.BadAppName, Why("glider://launch/" + new string('a', GliderLinkParser.MaxAppNameLength + 1)));
        Assert.Equal(LinkRejection.TooLong, Why("glider://launch/" + new string('a', GliderLinkParser.MaxLength)));
        Assert.Equal(LinkRejection.TooLong, Why("glider://launch/Fortnite?" + new string('x', 100_000)));
        Assert.Equal(LinkRejection.None, Why("glider://store/p/" + new string('a', GliderLinkParser.MaxSlugLength)));
        Assert.Equal(LinkRejection.BadSlug, Why("glider://store/p/" + new string('a', GliderLinkParser.MaxSlugLength + 1)));
    }

    [Theory]
    [InlineData("glider://store/p/Fort.nite")]
    [InlineData("glider://store/p/-fortnite")]
    [InlineData("glider://store/p/fort%20nite")]
    [InlineData("glider://store/p/..")]
    [InlineData("glider://store/p/a%2Fb")]
    public void BadSlugs(string raw) => Assert.NotEqual(LinkRejection.None, Why(raw));

    [Fact]
    public void ControlCharactersAnywhereAreRefused()
    {
        Assert.Equal(LinkRejection.ControlCharacters, Why("glider://launch/Fortnite\tx"));
        Assert.Equal(LinkRejection.ControlCharacters, Why("glider://launch/Fort\0nite"));
        Assert.Equal(LinkRejection.ControlCharacters, Why("glider://launch/Fortnite --evil"));
        Assert.Equal(LinkRejection.ControlCharacters, Why("glider://launch/Fortnite\r\nHost: x"));
    }

    [Fact]
    public void FactoriesRefuseInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => GliderLink.Launch("../x"));
        Assert.Throws<ArgumentException>(() => GliderLink.Install(""));
        Assert.Throws<ArgumentException>(() => GliderLink.Store("a b"));
        Assert.Equal("glider://launch/Fortnite", GliderLinkParser.LaunchUri("Fortnite"));
        Assert.Null(GliderLinkParser.LaunchUri("not valid"));
        Assert.Null(GliderLinkParser.LaunchUri(null));
        Assert.Equal("B", GliderLink.Launch("A").WithApp("B").AppName);
        Assert.Equal(GliderLink.Downloads, GliderLink.Downloads.WithApp("x"));
    }

    [Fact]
    public void RandomInputNeverThrowsAndAcceptedLinksAreAlwaysSafe()
    {
        var rng = new Random(20260514);
        const string alphabet = "glider:/%?#@\\.-_ aAzZ09&=;\"'`$()|<>\0\r\n\t\u00e9\u202e";
        var accepted = 0;
        for (var i = 0; i < 20_000; i++)
        {
            var len = rng.Next(0, 60);
            var s = i % 3 == 0 ? "glider://" : i % 3 == 1 ? "glider://launch/" : "";
            s += string.Create(len, rng.Next(), (span, seed) =>
            {
                var r = new Random(seed);
                for (var k = 0; k < span.Length; k++)
                {
                    span[k] = alphabet[r.Next(alphabet.Length)];
                }
            });
            if (GliderLinkParser.TryParse(s, out var link))
            {
                accepted++;
                if (link.AppName is { } app)
                {
                    Assert.True(GliderLinkParser.IsValidAppName(app), s);
                }

                Assert.Equal(link, Ok(link.Uri));
            }
        }

        Assert.True(accepted > 0, "the generator should produce some valid links too");
    }
}
