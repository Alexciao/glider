using Glider.Core.Links;

namespace Glider.Core.Tests.Links;

public class EpicLauncherUriTests
{
    private const string Fortnite = "com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite";

    private static EpicLink Ok(string raw)
    {
        Assert.Equal(LinkRejection.None, EpicLauncherUri.Parse(raw, out var link));
        return link;
    }

    private static LinkRejection Why(string? raw) => EpicLauncherUri.Parse(raw, out _);

    [Fact]
    public void TheDocumentedFortniteExample()
    {
        var link = Ok(Fortnite + "?action=launch&silent=true");
        Assert.Equal(new EpicLink(EpicLinkKind.App, "Fortnite", "fn", "4fe75bbc5a674f4f9b356b5c90567da5", EpicLinkAction.Launch, true), link);
    }

    [Fact]
    public void NoActionMeansLaunch_AndSilentDefaultsOff()
    {
        var link = Ok(Fortnite);
        Assert.Equal(EpicLinkAction.Launch, link.Action);
        Assert.False(link.Silent);
    }

    [Theory]
    [InlineData("install", EpicLinkAction.Install)]
    [InlineData("INSTALL", EpicLinkAction.Install)]
    [InlineData("installer", EpicLinkAction.Install)]
    [InlineData("installerFoo", EpicLinkAction.Install)]
    [InlineData("update", EpicLinkAction.Update)]
    [InlineData("verify", EpicLinkAction.Verify)]
    [InlineData("updatecheck", EpicLinkAction.Verify)]
    [InlineData("uninstall", EpicLinkAction.Uninstall)]
    [InlineData("launch", EpicLinkAction.Launch)]
    [InlineData("shutdown", EpicLinkAction.Unsupported)]
    [InlineData("", EpicLinkAction.Unsupported)]
    public void Actions(string action, EpicLinkAction expected) => Assert.Equal(expected, Ok(Fortnite + "?action=" + action).Action);

    [Fact]
    public void LaunchArgumentsEnvironmentAndPathsAreDropped()
    {
        var withExtras = Ok(Fortnite + "?action=launch&arg=--evil&env.PATH=x&install=D%3A%2Fx&installtag=y&runningapptimeout=1&altExe=z");
        Assert.Equal(Ok(Fortnite + "?action=launch"), withExtras);
    }

    [Fact]
    public void LegacyBareAppName()
    {
        var link = Ok("com.epicgames.launcher://apps/Fortnite?action=install");
        Assert.Equal(new EpicLink(EpicLinkKind.App, "Fortnite", Action: EpicLinkAction.Install), link);
    }

    [Theory]
    [InlineData("com.epicgames.launcher://apps/D%3A%2FGames%2FMyGame?action=launch&silent=true")] // install-directory form
    [InlineData("com.epicgames.launcher://apps/D%3A%5CGames%5CMyGame")]
    [InlineData("com.epicgames.launcher://apps/D:/Games/MyGame")]
    [InlineData("com.epicgames.launcher://apps/D%3A")] // drive letter
    [InlineData("com.epicgames.launcher://apps/a%3Ab")] // two parts
    [InlineData("com.epicgames.launcher://apps/a%3Ab%3Ac%3Ad")] // four parts
    [InlineData("com.epicgames.launcher://apps/%2E%2E%2F%2E%2E%2Fx")]
    [InlineData("com.epicgames.launcher://social")]
    [InlineData("com.epicgames.launcher://invite/abc")]
    [InlineData("com.epicgames.launcher://settings")]
    [InlineData("com.epicgames.launcher://fab")]
    [InlineData("com.epicgames.launcher://coderedemption/ABC")]
    public void UnsupportedShapesAreWellFormedButUnknown(string raw)
    {
        var link = Ok(raw);
        Assert.Equal(EpicLinkKind.Unknown, link.Kind);
        Assert.Null(link.AppName);
        Assert.Null(EpicLinkMapper.ToGliderLink(link));
    }

    [Theory]
    [InlineData("com.epicgames.launcher://store/p/fortnite", EpicLinkKind.StorePage, "fortnite")]
    [InlineData("com.epicgames.launcher://store/product/fortnite/home", EpicLinkKind.StoreHome, null)]
    [InlineData("com.epicgames.launcher://store/product/grand-theft-auto-v", EpicLinkKind.StorePage, "grand-theft-auto-v")]
    [InlineData("com.epicgames.launcher://store", EpicLinkKind.StoreHome, null)]
    [InlineData("com.epicgames.launcher://store/browse", EpicLinkKind.StoreHome, null)]
    [InlineData("com.epicgames.launcher://library", EpicLinkKind.Library, null)]
    [InlineData("com.epicgames.launcher://library?filter=x", EpicLinkKind.Library, null)]
    public void StoreAndLibrary(string raw, EpicLinkKind kind, string? slug)
    {
        var link = Ok(raw);
        Assert.Equal(kind, link.Kind);
        Assert.Equal(slug, link.Slug);
    }

    [Theory]
    [InlineData(null, LinkRejection.Empty)]
    [InlineData("", LinkRejection.Empty)]
    [InlineData("glider://launch/Fortnite", LinkRejection.BadScheme)]
    [InlineData("com.epicgames.launcher:apps/x", LinkRejection.BadScheme)]
    [InlineData("com.epicgames.launcher://apps/x#frag", LinkRejection.Malformed)]
    [InlineData("com.epicgames.launcher://user@apps/x", LinkRejection.Malformed)]
    [InlineData("com.epicgames.launcher://apps/http://evil.example/x", LinkRejection.Malformed)]
    [InlineData("com.epicgames.launcher://apps/Fortnite?next=com.epicgames.launcher://apps/x", LinkRejection.Malformed)]
    [InlineData("com.epicgames.launcher://apps//Fortnite", LinkRejection.Malformed)]
    [InlineData("com.epicgames.launcher://apps/Fort\0nite", LinkRejection.ControlCharacters)]
    [InlineData("com.epicgames.launcher://apps/Fort nite", LinkRejection.ControlCharacters)]
    [InlineData("com.epicgames.launcher://apps/Fort%0Anite", LinkRejection.BadEncoding)]
    [InlineData("com.epicgames.launcher://apps/%2541", LinkRejection.BadEncoding)]
    [InlineData("com.epicgames.launcher://apps/fn%3A%3AFortnite", LinkRejection.BadAppName)] // empty catalog id
    [InlineData("com.epicgames.launcher://apps/fn%3Axyz%3AFort%22nite", LinkRejection.BadAppName)]
    [InlineData("com.epicgames.launcher://apps/Fortnite%26calc", LinkRejection.BadAppName)]
    [InlineData("com.epicgames.launcher://store/p/bad.slug", LinkRejection.BadSlug)]
    public void Refusals(string? raw, LinkRejection why) => Assert.Equal(why, Why(raw));

    [Fact]
    public void LengthLimit()
    {
        Assert.Equal(LinkRejection.TooLong, Why(Fortnite + "?" + new string('a', EpicLauncherUri.MaxLength)));
        Assert.Equal(LinkRejection.BadAppName, Why("com.epicgames.launcher://apps/" + new string('a', 129)));
    }

    [Fact]
    public void SchemeAndHostAreCaseInsensitive()
    {
        Assert.Equal(Ok(Fortnite), Ok(Fortnite.Replace("com.epicgames.launcher", "COM.EpicGames.Launcher").Replace("apps", "APPS")));
    }

    [Fact]
    public void MapperSendsEachActionWhereItBelongs()
    {
        Assert.Equal(GliderLink.Launch("Fortnite"), EpicLinkMapper.ToGliderLink(Ok(Fortnite)));
        Assert.Equal(GliderLink.Install("Fortnite"), EpicLinkMapper.ToGliderLink(Ok(Fortnite + "?action=install")));
        Assert.Equal(GliderLink.Install("Fortnite"), EpicLinkMapper.ToGliderLink(Ok(Fortnite + "?action=update")));
        Assert.Equal(GliderLink.Game("Fortnite"), EpicLinkMapper.ToGliderLink(Ok(Fortnite + "?action=verify")));
        Assert.Null(EpicLinkMapper.ToGliderLink(Ok(Fortnite + "?action=uninstall")));
        Assert.Null(EpicLinkMapper.ToGliderLink(Ok(Fortnite + "?action=shutdown")));
        Assert.Equal(GliderLink.Store("fortnite"), EpicLinkMapper.ToGliderLink(Ok("com.epicgames.launcher://store/p/fortnite")));
        Assert.Equal(GliderLink.Store(), EpicLinkMapper.ToGliderLink(Ok("com.epicgames.launcher://store")));
        Assert.Equal(GliderLink.Library, EpicLinkMapper.ToGliderLink(Ok("com.epicgames.launcher://library")));
    }

    [Fact]
    public void RandomInputNeverThrows()
    {
        var rng = new Random(77);
        const string alphabet = "com.epigsaunch:/%?#@\\&=A3F .-_\0\n\u00e9";
        for (var i = 0; i < 20_000; i++)
        {
            var chars = new char[rng.Next(0, 80)];
            for (var k = 0; k < chars.Length; k++)
            {
                chars[k] = alphabet[rng.Next(alphabet.Length)];
            }

            var tail = new string(chars);
            _ = EpicLauncherUri.TryParse("com.epicgames.launcher://" + tail, out var link);
            if (link.AppName is { } app)
            {
                Assert.True(GliderLinkParser.IsValidAppName(app));
            }
        }
    }
}
