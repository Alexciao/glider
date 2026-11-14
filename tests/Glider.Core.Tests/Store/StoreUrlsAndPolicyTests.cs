using Glider.Core.Contracts;
using Glider.Core.Store;

namespace Glider.Core.Tests.Store;

public class StoreUrlsTests
{
    [Theory]
    [InlineData(StoreTargetKind.Home, null, "https://store.epicgames.com/it/")]
    [InlineData(StoreTargetKind.FreeGames, null, "https://store.epicgames.com/it/free-games")]
    [InlineData(StoreTargetKind.Wishlist, null, "https://store.epicgames.com/it/wishlist")]
    [InlineData(StoreTargetKind.Browse, null, "https://store.epicgames.com/it/browse")]
    [InlineData(StoreTargetKind.Library, null, "https://store.epicgames.com/it/")]
    [InlineData(StoreTargetKind.Product, "knightica-3be45c", "https://store.epicgames.com/it/p/knightica-3be45c")]
    [InlineData(StoreTargetKind.Product, "Thems-Fightin-Herds/home", "https://store.epicgames.com/it/p/thems-fightin-herds")]
    [InlineData(StoreTargetKind.Bundle, "lisa-the-definitive-edition", "https://store.epicgames.com/it/bundles/lisa-the-definitive-edition")]
    public void TargetsBecomeUrls(StoreTargetKind kind, string? slug, string expected) =>
        Assert.Equal(expected, StoreUrls.Build(new StoreTarget(kind, slug), "it"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../evil")]
    [InlineData("a b")]
    [InlineData("https://evil.test/")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void BadProductSlugsFallBackToTheFreeGamesPage(string? slug) =>
        Assert.Equal("https://store.epicgames.com/en-US/free-games", StoreUrls.Build(new StoreTarget(StoreTargetKind.Product, slug), "en-US"));

    [Fact]
    public void UnknownLocaleFallsBackAndNullTargetIsHome()
    {
        Assert.Equal("https://store.epicgames.com/en-US/", StoreUrls.Build(null, "klingon"));
        Assert.Equal("https://store.epicgames.com/en-US/", StoreUrls.Home("../x"));
    }

    [Theory]
    [InlineData("/it/p/knightica-3be45c", true)]
    [InlineData("/", true)]
    [InlineData("/en-US/free-games", true)]
    [InlineData("//evil.test/x", true)] // path characters only; becomes https://store.epicgames.com//evil.test/x on the same origin
    [InlineData("/x?code=abc", false)]
    [InlineData("/x#frag", false)]
    [InlineData("https://evil.test/", false)]
    [InlineData("/x y", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlySafePathsAreRemembered(string? path, bool safe) => Assert.Equal(safe, StoreUrls.TrySafePath(path, out _));

    [Fact]
    public void LastVisitedResumesASafePathElseHome()
    {
        Assert.Equal("https://store.epicgames.com/de/p/x", StoreUrls.Build(new StoreTarget(StoreTargetKind.LastVisited, null), "en-US", "/de/p/x"));
        Assert.Equal("https://store.epicgames.com/en-US/", StoreUrls.Build(new StoreTarget(StoreTargetKind.LastVisited, null), "en-US", "/x?code=1"));
        Assert.Equal("https://store.epicgames.com/en-US/", StoreUrls.Build(new StoreTarget(StoreTargetKind.LastVisited, null), "en-US", null));
    }

    [Theory]
    [InlineData("https://store.epicgames.com/en-US/p/x?code=secret&a=1#f", "https://store.epicgames.com/en-US/p/x")]
    [InlineData("https://user:pw@store.epicgames.com/x", "https://store.epicgames.com/x")]
    public void SanitizeDropsQueryFragmentAndUserinfo(string url, string expected) => Assert.Equal(expected, StoreUrls.Sanitize(url));

    [Theory]
    [InlineData("http://store.epicgames.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("nonsense")]
    [InlineData(null)]
    public void SanitizeRejectsNonHttps(string? url) => Assert.Null(StoreUrls.Sanitize(url));

    [Theory]
    [InlineData("it-IT", "it")]
    [InlineData("it", "it")]
    [InlineData("en-GB", "en-US")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("es-ES", "es-ES")]
    [InlineData("es-AR", "es-MX")]
    [InlineData("zh-TW", "zh-Hant")]
    [InlineData("zh-Hans-CN", "zh-CN")]
    [InlineData("ja-JP", "ja")]
    [InlineData("xx", "en-US")]
    [InlineData("", "en-US")]
    [InlineData(null, "en-US")]
    public void CulturesMapToStoreLocales(string? culture, string expected) => Assert.Equal(expected, StoreLocale.FromCulture(culture));
}

public class StoreSsoTests
{
    private const string Code = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void BuildsTheExchangeUrlWithTheTargetEncoded()
    {
        var url = StoreSso.Build(Code, "https://store.epicgames.com/it/p/knightica-3be45c");
        Assert.Equal($"https://www.epicgames.com/id/exchange?exchangeCode={Code}&redirectUrl=https%3A%2F%2Fstore.epicgames.com%2Fit%2Fp%2Fknightica-3be45c", url);
    }

    [Theory]
    [InlineData("https://evil.test/")]
    [InlineData("http://store.epicgames.com/")]
    [InlineData("https://store.epicgames.com.evil.test/")]
    [InlineData("https://user@store.epicgames.com/")]
    [InlineData("https://store.epicgames.com:8443/")]
    [InlineData("https://accounts.epicgames.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData(null)]
    public void RefusesTargetsOutsideTheStore(string? target) => Assert.Null(StoreSso.Build(Code, target));

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    [InlineData("0123456789abcdef0123456789abcde&")]
    [InlineData(null)]
    public void RefusesAnythingButA32HexCode(string? code) => Assert.Null(StoreSso.Build(code, "https://store.epicgames.com/"));

    [Fact]
    public void RecognisesExchangeUrls()
    {
        Assert.True(StoreSso.IsExchangeUrl("https://www.epicgames.com/id/exchange?exchangeCode=x"));
        Assert.True(StoreSso.IsExchangeUrl("https://epicgames.com/ID/Exchange"));
        Assert.False(StoreSso.IsExchangeUrl("https://www.epicgames.com/id/login"));
        Assert.False(StoreSso.IsExchangeUrl("https://evil.test/id/exchange"));
    }
}

public class StoreNavigationPolicyTests
{
    [Theory]
    [InlineData("https://store.epicgames.com/en-US/")]
    [InlineData("https://store.epicgames.com/en-US/p/x?utm=1")]
    [InlineData("https://launcher.store.epicgames.com/purchase?offers=1")]
    [InlineData("https://www.epicgames.com/id/login")]
    [InlineData("https://epicgames.com/")]
    [InlineData("https://accounts.epicgames.com/")]
    [InlineData("https://help.epicgames.com/")]
    [InlineData("https://www.unrealengine.com/marketplace")]
    [InlineData("https://payment-website-pci.ol.epicgames.com/purchase")]
    [InlineData("https://STORE.EPICGAMES.COM/")]
    [InlineData("about:blank")]
    public void EpicPagesLoad(string url) => Assert.Equal(StoreNavDecision.Allow, StoreNavigationPolicy.Decide(url, userInitiated: false, checkoutActive: false));

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>1</script>")]
    [InlineData("file:///c:/windows/system32/cmd.exe")]
    [InlineData("blob:https://store.epicgames.com/abc")]
    [InlineData("ftp://store.epicgames.com/")]
    [InlineData("wss://store.epicgames.com/")]
    [InlineData("ms-settings:privacy")]
    [InlineData("calculator:")]
    [InlineData("http://store.epicgames.com/")]
    [InlineData("https://user:pw@store.epicgames.com/")]
    [InlineData("https://store.epicgames.com:8443/")]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData(null)]
    public void DangerousSchemesAndShapesNeverLoadWhateverTheState(string? url)
    {
        foreach (var user in new[] { false, true })
        {
            foreach (var checkout in new[] { false, true })
            {
                var d = StoreNavigationPolicy.Decide(url, user, checkout);
                Assert.Equal(StoreNavDecision.Cancel, d);
            }
        }
    }

    [Theory]
    [InlineData("https://store.epicgames.com.evil.test/")]
    [InlineData("https://evilepicgames.com/")]
    [InlineData("https://epicgames.com.evil.test/")]
    [InlineData("https://notepicgames.com/")]
    [InlineData("https://example.com/")]
    public void LookAlikeHostsAreForeign(string url)
    {
        Assert.False(StoreNavigationPolicy.IsEpicUrl(url));
        Assert.NotEqual(StoreNavDecision.Allow, StoreNavigationPolicy.Decide(url, false, false));
        Assert.NotEqual(StoreNavDecision.Allow, StoreNavigationPolicy.Decide(url, true, false));
    }

    [Fact]
    public void ForeignHostsAreSilentUnlessTheUserClickedAndOpenOutsideTheApp()
    {
        const string foreign = "https://example.com/offer";
        Assert.Equal(StoreNavDecision.Cancel, StoreNavigationPolicy.Decide(foreign, userInitiated: false, checkoutActive: false));
        Assert.Equal(StoreNavDecision.OpenInSystemBrowser, StoreNavigationPolicy.Decide(foreign, userInitiated: true, checkoutActive: false));
    }

    [Fact]
    public void PaymentRedirectsAreAllowedOnlyDuringCheckout()
    {
        const string bank = "https://secure.bank.example/3ds";
        Assert.Equal(StoreNavDecision.Allow, StoreNavigationPolicy.Decide(bank, userInitiated: false, checkoutActive: true));
        Assert.NotEqual(StoreNavDecision.Allow, StoreNavigationPolicy.Decide(bank, userInitiated: false, checkoutActive: false));
    }

    [Fact]
    public void LauncherLinksAreHandedOffNotLoaded()
    {
        Assert.Equal(StoreNavDecision.LauncherLink, StoreNavigationPolicy.Decide("com.epicgames.launcher://apps/x", true, false));
        Assert.Equal(StoreNavDecision.LauncherLink, StoreNavigationPolicy.Decide("COM.EPICGAMES.LAUNCHER://apps/x", false, true));
    }

    [Fact]
    public void ExchangeUrlsOnlyLoadWhenGliderStartedThem()
    {
        const string url = "https://www.epicgames.com/id/exchange?exchangeCode=0123456789abcdef0123456789abcdef&redirectUrl=x";
        Assert.Equal(StoreNavDecision.Cancel, StoreNavigationPolicy.Decide(url, true, false));
        Assert.Equal(StoreNavDecision.Allow, StoreNavigationPolicy.Decide(url, false, false, glideStartedExchange: true));
    }

    [Theory]
    [InlineData("https://store.epicgames.com/x", true)]
    [InlineData("https://tracker.example.net/pixel", true)]
    [InlineData("about:blank", true)]
    [InlineData("about:srcdoc", true)]
    [InlineData("http://store.epicgames.com/x", false)]
    [InlineData("javascript:1", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("com.epicgames.launcher://apps/x", false)]
    [InlineData("file:///c:/x", false)]
    [InlineData("", false)]
    public void FramesAreHttpsOnly(string url, bool allowed) => Assert.Equal(allowed, StoreNavigationPolicy.AllowFrame(url));

    [Theory]
    // login popups (Google/Facebook/...) from the sign-in page
    [InlineData("https://accounts.google.com/o/oauth2/auth", true, "https://www.epicgames.com/id/login", false, 0, StorePopupDecision.Popup)]
    [InlineData("about:blank", true, "https://www.epicgames.com/id/login", false, 0, StorePopupDecision.Popup)]
    // payment popups during checkout
    [InlineData("https://www.paypal.com/checkout", true, "https://store.epicgames.com/en-US/p/x", true, 0, StorePopupDecision.Popup)]
    // limit
    [InlineData("https://www.paypal.com/checkout", true, "https://store.epicgames.com/en-US/p/x", true, 2, StorePopupDecision.OpenInSystemBrowser)]
    // outside login/checkout: a user click on a foreign link goes to the browser, a script never opens anything
    [InlineData("https://example.com/", true, "https://store.epicgames.com/en-US/", false, 0, StorePopupDecision.OpenInSystemBrowser)]
    [InlineData("https://example.com/", false, "https://store.epicgames.com/en-US/", false, 0, StorePopupDecision.Block)]
    [InlineData("about:blank", false, "https://store.epicgames.com/en-US/", false, 0, StorePopupDecision.Block)]
    [InlineData("about:blank", true, "https://store.epicgames.com/en-US/", false, 0, StorePopupDecision.Block)]
    // Epic pages with target=_blank just navigate
    [InlineData("https://store.epicgames.com/en-US/p/y", true, "https://store.epicgames.com/en-US/", false, 0, StorePopupDecision.NavigateInPlace)]
    [InlineData("https://store.epicgames.com/en-US/p/y", false, "https://store.epicgames.com/en-US/", false, 0, StorePopupDecision.NavigateInPlace)]
    // never a window for these
    [InlineData("javascript:alert(1)", true, "https://www.epicgames.com/id/login", false, 0, StorePopupDecision.Block)]
    [InlineData("file:///c:/x", true, "https://www.epicgames.com/id/login", true, 0, StorePopupDecision.Block)]
    [InlineData("http://example.com/", true, "https://www.epicgames.com/id/login", true, 0, StorePopupDecision.Block)]
    [InlineData("com.epicgames.launcher://apps/x", false, "https://store.epicgames.com/", false, 0, StorePopupDecision.LauncherLink)]
    public void PopupsAreForLoginAndPaymentOnly(string target, bool user, string current, bool checkout, int open, StorePopupDecision expected) =>
        Assert.Equal(expected, StoreNavigationPolicy.DecidePopup(target, user, current, checkout, open));

    [Fact]
    public void ExchangeUrlsNeverOpenInAPopup() =>
        Assert.NotEqual(StorePopupDecision.NavigateInPlace,
            StoreNavigationPolicy.DecidePopup("https://www.epicgames.com/id/exchange?exchangeCode=0123456789abcdef0123456789abcdef", true, "https://store.epicgames.com/", false, 0));

    [Theory]
    [InlineData("https://payment-website-pci.ol.epicgames.com/purchase?x=1", false, true)]
    [InlineData("https://payment-website-pci.ol.epicgames.com/Purchase", false, true)]
    [InlineData("https://payment-website-pci.ol.epicgames.com/other", false, false)]
    [InlineData("https://store.epicgames.com/en-US/purchase", true, true)]
    [InlineData("https://launcher.store.epicgames.com/purchase?offers=1", true, true)]
    [InlineData("https://store.epicgames.com/en-US/checkout", true, true)]
    [InlineData("https://store.epicgames.com/en-US/p/x", true, false)]
    [InlineData("https://store.epicgames.com/en-US/purchase", false, false)] // subframes only count on the payment hosts
    [InlineData("https://evil.test/purchase", true, false)]
    [InlineData("https://evil.ol.epicgames.com.evil.test/purchase", false, false)]
    [InlineData("http://store.epicgames.com/purchase", true, false)]
    [InlineData(null, true, false)]
    public void CheckoutUrlsAreRecognised(string? url, bool mainFrame, bool expected) =>
        Assert.Equal(expected, StoreNavigationPolicy.IsCheckoutUrl(url, mainFrame));

    [Theory]
    [InlineData("about:blank", true)]
    [InlineData("https://accounts.google.com/", true)]
    [InlineData("http://x.test/", false)]
    [InlineData("javascript:1", false)]
    [InlineData("data:text/html,x", false)]
    [InlineData("com.epicgames.launcher://x", false)]
    public void PopupNavigationIsHttpsOnly(string url, bool allowed) => Assert.Equal(allowed, StoreNavigationPolicy.AllowPopupNavigation(url));
}
