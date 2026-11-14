using Glider.Core.Links;

namespace Glider.Core.Tests.Store;

public class EpicLauncherUriTests
{
    private const string Fortnite = "com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=launch&silent=true";

    private static EpicLauncherLink Parse(string raw)
    {
        Assert.True(EpicLauncherUri.TryParse(raw, out var link), $"should parse: {raw}");
        return link;
    }

    [Fact]
    public void RealFortniteLinkGivesTheArtifactIdAsAppName()
    {
        var link = Parse(Fortnite);
        Assert.Equal(EpicLinkKind.Launch, link.Kind);
        Assert.Equal("Fortnite", link.AppName);
        Assert.Equal("fn", link.Namespace);
        Assert.Equal("4fe75bbc5a674f4f9b356b5c90567da5", link.CatalogItemId);
        Assert.True(link.Silent);
    }

    [Theory]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp", EpicLinkKind.Launch)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=launch", EpicLinkKind.Launch)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=install", EpicLinkKind.Install)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=installer", EpicLinkKind.Install)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=update", EpicLinkKind.Install)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=verify", EpicLinkKind.Verify)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=updatecheck", EpicLinkKind.UpdateCheck)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=uninstall", EpicLinkKind.Uninstall)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp?ACTION=INSTALL", EpicLinkKind.Install)]
    [InlineData("COM.EPICGAMES.LAUNCHER://APPS/fn%3Acat%3AApp", EpicLinkKind.Launch)]
    [InlineData("com.epicgames.launcher://apps/fn%3Acat%3AApp/", EpicLinkKind.Launch)]
    public void ActionsMapToKinds(string raw, EpicLinkKind kind) => Assert.Equal(kind, Parse(raw).Kind);

    [Fact]
    public void LegacyBareAppNameIsAccepted()
    {
        var link = Parse("com.epicgames.launcher://apps/Fortnite?action=launch");
        Assert.Equal(EpicLinkKind.Launch, link.Kind);
        Assert.Equal("Fortnite", link.AppName);
        Assert.Null(link.Namespace);
    }

    [Theory]
    [InlineData("com.epicgames.launcher://apps/fn:cat:App")]
    [InlineData("com.epicgames.launcher://apps/fn%3acat%3aApp")]
    public void ColonsMayBeRawOrEncoded(string raw) => Assert.Equal("App", Parse(raw).AppName);

    [Fact]
    public void ArgumentsEnvironmentAndInstallDirectoriesNeverReachTheLink()
    {
        var link = Parse("com.epicgames.launcher://apps/fn%3Acat%3AApp?action=launch&arg=-evil%20--flag&env.PATH=C%3A%5Cx&install=D%3A%5Cx&installtag=a&runningapptimeout=1");
        Assert.Equal(EpicLinkKind.Launch, link.Kind);
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
    public void NonGamePagesMapToTheirKind(string raw, EpicLinkKind kind) => Assert.Equal(kind, Parse(raw).Kind);

    [Theory]
    [InlineData("com.epicgames.launcher://store/p/knightica-3be45c", "knightica-3be45c", false)]
    [InlineData("com.epicgames.launcher://store/product/Alien-Isolation", "alien-isolation", false)]
    [InlineData("com.epicgames.launcher://store/p/thems-fightin-herds%2Fhome", "thems-fightin-herds", false)]
    [InlineData("com.epicgames.launcher://store/bundles/lisa-the-definitive-edition", "lisa-the-definitive-edition", true)]
    public void StoreProductLinksCarryAValidatedSlug(string raw, string slug, bool bundle)
    {
        var link = Parse(raw);
        Assert.Equal(EpicLinkKind.StorePage, link.Kind);
        Assert.Equal(slug, link.StoreSlug);
        Assert.Equal(bundle, link.StoreIsBundle);
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
        Assert.Null(link.StoreSlug);
    }

    [Fact]
    public void HasSchemeIsACheapCaseInsensitivePrefixTest()
    {
        Assert.True(EpicLauncherUri.HasScheme("Com.Epicgames.Launcher://anything"));
        Assert.False(EpicLauncherUri.HasScheme("https://com.epicgames.launcher://"));
        Assert.False(EpicLauncherUri.HasScheme(null));
    }

    [Fact]
    public void ParserNeverThrowsOnGarbage()
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

            if (ok && link.StoreSlug is { } slug)
            {
                Assert.Matches("^[a-z0-9][a-z0-9-]{0,127}$", slug);
            }
        }
    }
}
