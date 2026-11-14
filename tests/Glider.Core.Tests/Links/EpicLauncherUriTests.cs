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
    [InlineData("updatecheck", EpicLinkAction.Verify)] // one action for both: the game page, the user presses the button
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
    [InlineData("com.epicgames.launcher://store/product/fortnite/home", EpicLinkKind.StorePage, "fortnite")] // the feed's "name/home" form keeps its page
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

    // ------------------------------------------------------------------------------------------------------------------
    // The in-Store deep-link rules (previously a second parser in Store/): the same parser, strictest rules, no data beyond the
    // app name / namespace / catalog id / slug ever reaches a caller.
    // ------------------------------------------------------------------------------------------------------------------

    private const string FortniteLaunch = "com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=launch&silent=true";

    private static EpicLink Strict(string raw)
    {
        Assert.True(EpicLauncherUri.TryParse(raw, out var link), $"should parse: {raw}");
        return link;
    }

    [Fact]
    public void RealFortniteLinkGivesTheArtifactIdAsAppName()
    {
        var link = Strict(FortniteLaunch);
        Assert.Equal(EpicLinkKind.App, link.Kind);
        Assert.Equal(EpicLinkAction.Launch, link.Action);
        Assert.Equal("Fortnite", link.AppName);
        Assert.Equal("fn", link.Namespace);
        Assert.Equal("4fe75bbc5a674f4f9b356b5c90567da5", link.CatalogItemId);
        Assert.True(link.Silent);
    }

    [Theory]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp", EpicLinkAction.Launch)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=launch", EpicLinkAction.Launch)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=install", EpicLinkAction.Install)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=installer", EpicLinkAction.Install)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=installer-redist", EpicLinkAction.Install)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=update", EpicLinkAction.Update)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=verify", EpicLinkAction.Verify)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=updatecheck", EpicLinkAction.Verify)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=uninstall", EpicLinkAction.Uninstall)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?ACTION=INSTALL", EpicLinkAction.Install)]
    [InlineData("COM.EPICGAMES.LAUNCHER://APPS/fn%3Acat%3AApp", EpicLinkAction.Launch)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp/", EpicLinkAction.Launch)]
    public void ActionsMapToAppLinksWithThatAction(string raw, EpicLinkAction action)
    {
        var link = Strict(raw);
        Assert.Equal(EpicLinkKind.App, link.Kind);
        Assert.Equal(action, link.Action);
        Assert.Equal("App", link.AppName);
    }

    [Fact]
    public void LegacyBareAppNameIsAcceptedWithoutNamespace()
    {
        var link = Strict("com.epicgames.launcher://apps/Fortnite?action=launch");
        Assert.Equal(EpicLinkKind.App, link.Kind);
        Assert.Equal(EpicLinkAction.Launch, link.Action);
        Assert.Equal("Fortnite", link.AppName);
        Assert.Null(link.Namespace);
    }

    [Theory]
    [InlineData("com.epicgames.launcher://apps/fn:cat:App")]
    [InlineData("com.epicgames.launcher://apps/fn%3acat%3aApp")]
    public void ColonsMayBeRawOrEncoded(string raw) => Assert.Equal("App", Strict(raw).AppName);

    [Fact]
    public void ArgumentsEnvironmentAndInstallDirectoriesNeverReachTheLink()
    {
        var link = Strict("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=launch&arg=-evil%20--flag&env.PATH=C%3A%5Cx&install=D%3A%5Cx&installtag=a&runningapptimeout=1");
        Assert.Equal(EpicLinkKind.App, link.Kind);
        Assert.Equal(EpicLinkAction.Launch, link.Action);
        Assert.Equal("App", link.AppName);
        // The record has no member that could carry any of them.
        Assert.DoesNotContain("evil", link.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PATH", link.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("com.epicgames.launcher://apps/D%3A%2FGames%2FMyGame?action=launch&silent=true")] // install-directory form
    [InlineData("com.epicgames.launcher://apps/D%3A%5CGames%5CMyGame")]
    [InlineData("com.epicgames.launcher://apps/D:Games")] // drive-letter-ish, two parts
    [InlineData("com.epicgames.launcher://apps/..%2F..%2Fx")]
    [InlineData("com.epicgames.launcher://apps/a%3A..%3Ab")]
    [InlineData("com.epicgames.launcher://apps/a%3Ab")] // two parts
    [InlineData("com.epicgames.launcher://apps/a%3Ab%3Ac%3Ad")] // four parts
    [InlineData("com.epicgames.launcher://apps/fn%253Acat%253AApp")] // double-encoded
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp%00")] // NUL
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp%0Aextra")] // newline
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3A%20App")] // space
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3Aa%20b")]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3Aapp%3Bcalc")]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3Aapp&calc")]
    [InlineData("com.epicgames.launcher://apps/")]
    [InlineData("com.epicgames.launcher://apps")]
    [InlineData("com.epicgames.launcher://apps/one/two")]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=format-c")] // unknown action
    public void PathFormsAndAbuseAreIgnored(string raw)
    {
        // Either rejected outright or understood as "nothing to do"; never a Launch/Install with a poisoned name.
        var ok = EpicLauncherUri.TryParse(raw, out var link);
        Assert.True(!ok || link.Kind == EpicLinkKind.Unknown, $"{raw} -> {link}");
        Assert.Null(link.AppName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://store.epicgames.com/")]
    [InlineData("com.epicgames.launcher:")]
    [InlineData("com.epicgames.launcher:/apps/x")]
    [InlineData("com.epicgames.launcherx://apps/x")]
    [InlineData("xcom.epicgames.launcher://apps/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///c:/windows/system32/calc.exe")]
    [InlineData("com.epicgames.launcher://user:pw@apps/x")]
    [InlineData("com.epicgames.launcher://apps:80/x")]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp#frag")]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp\\..\\x")]
    [InlineData("com.epicgames.launcher://apps/https://evil.test/x")]
    [InlineData("com.epicgames.launcher://apps/x?redirect=http://evil.test")]
    [InlineData("com.epicgames.launcher://store/p/x?u=file://c")]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp\r\nx")]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp\u0007")]
    [InlineData("com.epicgames.launcher://apps/caf\u00e9")]
    [InlineData("com.epicgames.launcher://\u202eapps/x")]
    [InlineData("com.epicgames.launcher://")]
    [InlineData("com.epicgames.launcher:///apps/x")]
    [InlineData("com.epicgames.launcher://apps//x")]
    public void MalformedAndHostileInputIsRejected(string raw)
    {
        Assert.False(EpicLauncherUri.TryParse(raw, out var link), $"should reject: {raw}");
        Assert.Equal(EpicLinkKind.Unknown, link.Kind);
    }

    [Fact]
    public void NullAndOverlongAreRejected()
    {
        Assert.False(EpicLauncherUri.TryParse(null, out _));
        var longName = new string('a', EpicLauncherUri.MaxLength);
        Assert.False(EpicLauncherUri.TryParse("com.epicgames.launcher://apps/" + longName, out _));
        Assert.False(EpicLauncherUri.TryParse("com.epicgames.launcher://apps/fn%3Acat%3A" + new string('a', 129), out var link) && link.Kind != EpicLinkKind.Unknown);
    }

    [Theory]
    [InlineData("com.epicgames.launcher://store", EpicLinkKind.StoreHome)]
    [InlineData("com.epicgames.launcher://store/", EpicLinkKind.StoreHome)]
    [InlineData("com.epicgames.launcher://store/browse", EpicLinkKind.StoreHome)]
    [InlineData("com.epicgames.launcher://store/free-games", EpicLinkKind.StoreHome)]
    [InlineData("com.epicgames.launcher://library", EpicLinkKind.Library)]
    [InlineData("com.epicgames.launcher://social", EpicLinkKind.Unknown)]
    [InlineData("com.epicgames.launcher://settings", EpicLinkKind.Unknown)]
    [InlineData("com.epicgames.launcher://invite/abc", EpicLinkKind.Unknown)]
    [InlineData("com.epicgames.launcher://fab", EpicLinkKind.Unknown)]
    [InlineData("com.epicgames.launcher://library/extra", EpicLinkKind.Unknown)]
    public void NonGamePagesMapToTheirKind(string raw, EpicLinkKind kind) => Assert.Equal(kind, Strict(raw).Kind);

    [Theory]
    [InlineData("com.epicgames.launcher://store/p/knightica-3be45c", "knightica-3be45c", false)]
    [InlineData("com.epicgames.launcher://store/product/Alien-Isolation", "alien-isolation", false)]
    [InlineData("com.epicgames.launcher://store/p/thems-fightin-herds%2Fhome", "thems-fightin-herds", false)]
    [InlineData("com.epicgames.launcher://store/bundles/lisa-the-definitive-edition", "lisa-the-definitive-edition", true)]
    public void StoreProductLinksCarryAValidatedSlug(string raw, string slug, bool bundle)
    {
        var link = Strict(raw);
        Assert.Equal(EpicLinkKind.StorePage, link.Kind);
        Assert.Equal(slug, link.Slug);
        Assert.Equal(bundle, link.IsBundle);
    }

    [Theory]
    [InlineData("com.epicgames.launcher://store/p/..%2F..%2Fx")]
    [InlineData("com.epicgames.launcher://store/p/a%20b")]
    [InlineData("com.epicgames.launcher://store/p/-x")]
    [InlineData("com.epicgames.launcher://store/p/%2Fevil.test")]
    [InlineData("com.epicgames.launcher://store/p/0123456789abcdef0123456789abcdef")] // a raw id has no page
    [InlineData("com.epicgames.launcher://store/p/a/b")]
    public void BadStoreSlugsAreIgnored(string raw)
    {
        var ok = EpicLauncherUri.TryParse(raw, out var link);
        Assert.True(!ok || link.Kind == EpicLinkKind.Unknown, raw);
        Assert.Null(link.Slug);
    }

    [Fact]
    public void HasSchemeIsACheapCaseInsensitivePrefixTest()
    {
        Assert.True(EpicLauncherUri.HasScheme("Com.Epicgames.Launcher://anything"));
        Assert.False(EpicLauncherUri.HasScheme("https://com.epicgames.launcher://"));
        Assert.False(EpicLauncherUri.HasScheme(null));
    }

    [Fact]
    public void ParserNeverThrowsOnGarbage_AndOnlyEverYieldsValidatedNames()
    {
        var rng = new Random(42);
        const string alphabet = "abcXYZ0123456789%:/?&=#@\\.-_ ";
        for (var i = 0; i < 4000; i++)
        {
            var s = "com.epicgames.launcher://" + new string(Enumerable.Range(0, rng.Next(0, 60)).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
            var ok = EpicLauncherUri.TryParse(s, out var link);
            if (ok && link.AppName is { } name)
            {
                Assert.Matches("^[A-Za-z0-9._-]{1,128}$", name);
            }

            if (ok && link.Slug is { } slug)
            {
                Assert.Matches("^[a-z0-9][a-z0-9-]{0,127}$", slug);
            }
        }
    }
}
