using Glider.Core.Store;

namespace Glider.Core.Tests.Store;

/// <summary>When the Store's native view may be shown, and when a chain of redirects has failed (the "blank Store tab" rules).</summary>
public class StoreRevealTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private const string Home = "https://store.epicgames.com/en-US/";
    private const string Exchange = "https://www.epicgames.com/id/exchange";
    private const string Login = "https://www.epicgames.com/id/login/epic";

    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    private static StoreRevealResult Done(StoreRevealTracker t, ulong id, string url, double s, bool ok = true, bool cancelled = false, bool network = false) =>
        t.OnNavigationCompleted(id, ok, cancelled, network, url, At(s));

    [Fact]
    public void ASimpleLoadRevealsWhenTheFinalPageHasLoaded()
    {
        var t = new StoreRevealTracker();
        t.BeginHostNavigation(At(0));
        t.OnNavigationStarting(1, StoreNavDecision.Allow, At(0));
        t.OnDomContentLoaded(Home, At(0.4));
        Assert.False(t.Revealed); // DOM-ready alone does not show the view
        var r = Done(t, 1, Home, 0.9);
        Assert.Equal(StoreRevealEffect.Reveal, r.Effect);
        Assert.True(t.Revealed);
        Assert.False(t.Pending);
    }

    [Fact]
    public void TheSsoRedirectChainRevealsOnlyTheFinalStorePage()
    {
        var t = new StoreRevealTracker();
        t.BeginHostNavigation(At(0));
        // store → /id/exchange (one navigation id, redirect hops) → store
        t.OnNavigationStarting(7, StoreNavDecision.Allow, At(0));
        t.OnNavigationStarting(7, StoreNavDecision.Allow, At(0.3)); // redirect hop
        t.OnDomContentLoaded(Exchange, At(0.5)); // an intermediate document is DOM-ready: not content
        Assert.Equal(StoreRevealEffect.None, Tick(t, 3).Effect);
        Assert.False(t.Revealed);
        t.OnNavigationStarting(7, StoreNavDecision.Allow, At(1.0)); // → store
        var r = Done(t, 7, Home, 1.6);
        Assert.Equal(StoreRevealEffect.Reveal, r.Effect);
    }

    private static StoreRevealResult Tick(StoreRevealTracker t, double s) => t.Tick(At(s));

    [Fact]
    public void ALoadedExchangePageNeverRevealsAndTimesOutIfNoRedirectFollows()
    {
        var t = new StoreRevealTracker();
        t.BeginHostNavigation(At(0));
        t.OnNavigationStarting(1, StoreNavDecision.Allow, At(0));
        Assert.Equal(StoreRevealEffect.None, Done(t, 1, Exchange, 1).Effect);
        Assert.False(t.Revealed);
        Assert.True(t.Pending);
        var r = Tick(t, 1 + StoreRevealTracker.LoadTimeout.TotalSeconds + 1);
        Assert.Equal(StoreRevealEffect.Fail, r.Effect);
        Assert.Equal(StoreFailure.Timeout, r.Failure);
        Assert.False(t.Pending);
    }

    [Fact]
    public void ARedirectTheAllowlistCancelledEndsInAnErrorNotABlankView()
    {
        var t = new StoreRevealTracker();
        t.BeginHostNavigation(At(0));
        t.OnNavigationStarting(1, StoreNavDecision.Allow, At(0));
        t.OnNavigationStarting(1, StoreNavDecision.Cancel, At(0.4)); // redirect to a host the Store may not load
        var r = Done(t, 1, "about:blank", 0.5, ok: false, cancelled: true);
        Assert.Equal(StoreRevealEffect.Fail, r.Effect);
        Assert.Equal(StoreFailure.Blocked, r.Failure);
        Assert.False(t.Revealed);
    }

    [Theory]
    [InlineData(StoreNavDecision.OpenInSystemBrowser)]
    [InlineData(StoreNavDecision.LauncherLink)]
    public void AHandOffToTheBrowserOrTheLauncherIsNotAFailure(StoreNavDecision decision)
    {
        var t = Revealed();
        t.OnNavigationStarting(2, decision, At(10));
        Assert.Equal(StoreRevealEffect.None, Done(t, 2, Home, 10.1, ok: false, cancelled: true).Effect);
        Assert.True(t.Revealed); // the page the user was reading stays
    }

    [Fact]
    public void ABlockedNavigationWhileAPageIsShownKeepsThePage()
    {
        var t = Revealed();
        t.OnNavigationStarting(2, StoreNavDecision.Cancel, At(10));
        Assert.Equal(StoreRevealEffect.None, Done(t, 2, Home, 10.1, ok: false, cancelled: true).Effect);
        Assert.True(t.Revealed);
    }

    [Fact]
    public void ACancelledNavigationThatAnotherOneReplacedIsIgnored()
    {
        var t = new StoreRevealTracker();
        t.BeginHostNavigation(At(0));
        t.OnNavigationStarting(1, StoreNavDecision.Allow, At(0));
        t.OnNavigationStarting(2, StoreNavDecision.Allow, At(0.2)); // the host navigated again
        Assert.Equal(StoreRevealEffect.None, Done(t, 1, "about:blank", 0.25, ok: false, cancelled: true).Effect);
        Assert.Equal(StoreRevealEffect.Reveal, Done(t, 2, Home, 1).Effect);
    }

    [Fact]
    public void ACancelledNavigationNothingReplacesFailsAfterAShortGrace()
    {
        var t = new StoreRevealTracker();
        t.BeginHostNavigation(At(0));
        t.OnNavigationStarting(1, StoreNavDecision.Allow, At(0));
        Assert.Equal(StoreRevealEffect.None, Done(t, 1, "about:blank", 0.5, ok: false, cancelled: true).Effect);
        Assert.Equal(StoreRevealEffect.None, Tick(t, 2).Effect);
        var r = Tick(t, 0.5 + StoreRevealTracker.StallGrace.TotalSeconds + 0.1);
        Assert.Equal(StoreRevealEffect.Fail, r.Effect);
    }

    [Theory]
    [InlineData(true, StoreFailure.Network)]
    [InlineData(false, StoreFailure.Other)]
    public void AFailedLoadFailsWithItsKind(bool network, StoreFailure expected)
    {
        var t = new StoreRevealTracker();
        t.BeginHostNavigation(At(0));
        t.OnNavigationStarting(1, StoreNavDecision.Allow, At(0));
        var r = Done(t, 1, Home, 2, ok: false, network: network);
        Assert.Equal(StoreRevealEffect.Fail, r.Effect);
        Assert.Equal(expected, r.Failure);
    }

    [Fact]
    public void ALoginPageIsReportedNotRevealed()
    {
        var t = new StoreRevealTracker();
        t.BeginHostNavigation(At(0));
        t.OnNavigationStarting(1, StoreNavDecision.Allow, At(0));
        t.OnDomContentLoaded(Login, At(0.5));
        Assert.Equal(StoreRevealEffect.LoginPage, Done(t, 1, Login, 0.8).Effect);
        Assert.False(t.Revealed);
        Assert.False(t.Pending); // the host decides what happens next and re-arms
    }

    [Fact]
    public void ASlowPageIsShownOnceItsDomHasBeenReadyForAWhile()
    {
        var t = new StoreRevealTracker();
        t.BeginHostNavigation(At(0));
        t.OnNavigationStarting(1, StoreNavDecision.Allow, At(0));
        t.OnDomContentLoaded(Home, At(1));
        Assert.Equal(StoreRevealEffect.None, Tick(t, 1 + StoreRevealTracker.RevealGrace.TotalSeconds - 0.1).Effect);
        var r = Tick(t, 1 + StoreRevealTracker.RevealGrace.TotalSeconds + 0.1);
        Assert.Equal(StoreRevealEffect.Reveal, r.Effect);
        Assert.True(t.Revealed);
        // the page's own load event arriving later changes nothing
        Assert.Equal(StoreRevealEffect.None, Done(t, 1, Home, 9).Effect);
    }

    [Fact]
    public void ANewNavigationRestartsTheDomReadyClock()
    {
        var t = new StoreRevealTracker();
        t.BeginHostNavigation(At(0));
        t.OnNavigationStarting(1, StoreNavDecision.Allow, At(0));
        t.OnDomContentLoaded(Home, At(1));
        t.OnNavigationStarting(2, StoreNavDecision.Allow, At(2)); // redirect away before the grace ran out
        Assert.Equal(StoreRevealEffect.None, Tick(t, 3).Effect);
        Assert.False(t.Revealed);
    }

    [Fact]
    public void SilenceFailsAfterTheLoadTimeoutButExtendingMakesRoomForTheExchangeCode()
    {
        var t = new StoreRevealTracker();
        t.BeginHostNavigation(At(0), TimeSpan.FromSeconds(25)); // first open: the code may take a while
        Assert.Equal(StoreRevealEffect.None, Tick(t, 30).Effect);
        Assert.Equal(StoreRevealEffect.Fail, Tick(t, 51).Effect);

        var u = new StoreRevealTracker();
        u.BeginHostNavigation(At(0));
        u.ExtendDeadline(At(80));
        Assert.Equal(StoreRevealEffect.None, Tick(u, 60).Effect);
        Assert.Equal(StoreRevealEffect.Fail, Tick(u, 81).Effect);
    }

    [Fact]
    public void ASlowNavigationInsideAPageTheUserIsReadingNeverTearsItDown()
    {
        var t = Revealed();
        t.OnNavigationStarting(2, StoreNavDecision.Allow, At(10));
        var r = Tick(t, 10 + StoreRevealTracker.LoadTimeout.TotalSeconds + 1);
        Assert.Equal(StoreRevealEffect.None, r.Effect);
        Assert.True(t.Revealed);
        Assert.False(t.Pending);
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        var t = Revealed();
        t.Reset();
        Assert.False(t.Revealed);
        Assert.False(t.Pending);
    }

    [Theory]
    [InlineData(null, StorePageKind.Blank)]
    [InlineData("about:blank", StorePageKind.Blank)]
    [InlineData("https://www.epicgames.com/id/exchange?exchangeCode=x", StorePageKind.Exchange)]
    [InlineData("https://www.epicgames.com/id/login/epic?redirectUrl=x", StorePageKind.Login)]
    [InlineData("https://store.epicgames.com/en-US/p/knightica", StorePageKind.Content)]
    [InlineData("https://www.epicgames.com/help/en-US", StorePageKind.Content)]
    public void PagesAreClassified(string? url, StorePageKind kind) => Assert.Equal(kind, StoreRevealTracker.Classify(url));

    private static StoreRevealTracker Revealed()
    {
        var t = new StoreRevealTracker();
        t.BeginHostNavigation(At(0));
        t.OnNavigationStarting(1, StoreNavDecision.Allow, At(0));
        Assert.Equal(StoreRevealEffect.Reveal, Done(t, 1, Home, 1).Effect);
        return t;
    }
}

public class StoreNavLogTests
{
    [Fact]
    public void LogLinesCarryHostAndPathOnly()
    {
        var starting = StoreNavLog.Starting("https://www.epicgames.com/id/exchange?exchangeCode=0123456789abcdef0123456789abcdef&redirectUrl=https%3A%2F%2Fstore.epicgames.com%2F#frag", StoreNavDecision.Allow, redirected: true, userInitiated: false);
        Assert.Contains("https://www.epicgames.com/id/exchange", starting, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789abcdef", starting, StringComparison.Ordinal);
        Assert.DoesNotContain("redirectUrl", starting, StringComparison.Ordinal);
        Assert.DoesNotContain("frag", starting, StringComparison.Ordinal);
        Assert.Contains("Allow", starting, StringComparison.Ordinal);
        Assert.Contains("redirect", starting, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("com.epicgames.launcher://apps/Fortnite?action=launch&silent=true", "com.epicgames.launcher:…")]
    [InlineData("javascript:alert(1)", "javascript:…")]
    [InlineData("about:blank", "about:blank")]
    [InlineData(null, "(none)")]
    [InlineData("not a url", "(unparsable)")]
    public void OtherSchemesAreNamedByTheirSchemeAlone(string? url, string expected) => Assert.Equal(expected, StoreNavLog.Describe(url));

    [Fact]
    public void ACompletionLineSaysHowItEndedAndHowLong()
    {
        var line = StoreNavLog.Completed("https://store.epicgames.com/en-US/?code=secret", success: false, "ConnectionReset", 0, TimeSpan.FromMilliseconds(1234));
        Assert.Contains("ConnectionReset", line, StringComparison.Ordinal);
        Assert.Contains("1234 ms", line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", line, StringComparison.Ordinal);
    }

    [Fact]
    public void VeryLongPathsAreCut()
    {
        var url = "https://store.epicgames.com/" + new string('a', 400);
        Assert.True(StoreNavLog.Describe(url).Length <= 125);
    }
}

public class StoreSsoMatchTests
{
    private const string Code = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void TheBrowsersReencodingOfTheExchangeUrlStillMatches()
    {
        var started = StoreSso.Build(Code, "https://store.epicgames.com/en-US/")!;
        // Chromium may report the redirect target with literal slashes / lower-case hex.
        var seen = $"https://www.epicgames.com/id/exchange?exchangeCode={Code}&redirectUrl=https%3a%2f%2fstore.epicgames.com%2fen-US%2f";
        Assert.True(StoreSso.IsSameExchange(seen, started));
        Assert.True(StoreSso.IsSameExchange($"https://www.epicgames.com/id/exchange?exchangeCode={Code.ToUpperInvariant()}&redirectUrl=https://store.epicgames.com/en-US/", started));
    }

    [Fact]
    public void AnotherCodeOrTargetOrHostDoesNotMatch()
    {
        var started = StoreSso.Build(Code, "https://store.epicgames.com/en-US/")!;
        Assert.False(StoreSso.IsSameExchange($"https://www.epicgames.com/id/exchange?exchangeCode={new string('f', 32)}&redirectUrl=https%3A%2F%2Fstore.epicgames.com%2Fen-US%2F", started));
        Assert.False(StoreSso.IsSameExchange($"https://www.epicgames.com/id/exchange?exchangeCode={Code}&redirectUrl=https%3A%2F%2Fstore.epicgames.com%2Fde%2F", started));
        Assert.False(StoreSso.IsSameExchange($"https://accounts.epicgames.com/id/exchange?exchangeCode={Code}&redirectUrl=https%3A%2F%2Fstore.epicgames.com%2Fen-US%2F", started));
        Assert.False(StoreSso.IsSameExchange("https://www.epicgames.com/id/exchange", started));
        Assert.False(StoreSso.IsSameExchange(null, started));
        Assert.False(StoreSso.IsSameExchange(started, null));
    }
}
