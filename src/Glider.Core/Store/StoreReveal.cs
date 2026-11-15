using Glider.Core.Infrastructure;

namespace Glider.Core.Store;

/// <summary>What kind of document a top-level navigation landed on, as far as showing the native Store view is concerned.</summary>
public enum StorePageKind
{
    /// <summary>about:blank or nothing: there is nothing to look at.</summary>
    Blank,

    /// <summary><c>/id/exchange</c>: the single-sign-on hop. It redirects; it is never a page to show.</summary>
    Exchange,

    /// <summary>Epic's sign-in page: the web session is missing or expired.</summary>
    Login,

    /// <summary>A Store (or other Epic) page the user can look at.</summary>
    Content,
}

/// <summary>What the host must do after feeding <see cref="StoreRevealTracker"/> an event.</summary>
public enum StoreRevealEffect
{
    None,

    /// <summary>The final page is there: show the native view.</summary>
    Reveal,

    /// <summary>The navigation ended on Epic's sign-in page: run the SSO (or tell the user they are signed out).</summary>
    LoginPage,

    /// <summary>Nothing will ever be shown for this navigation: hide the native view and show the error with Retry / Open in browser.</summary>
    Fail,
}

public enum StoreFailure
{
    None,

    /// <summary>The allowlist cancelled a navigation (a redirect to a host the Store may not load) and nothing else is loading.</summary>
    Blocked,

    /// <summary>Connection-level failure (offline, DNS, reset, timeout in the network stack).</summary>
    Network,

    /// <summary>The page failed to load for another reason, or the navigation was cancelled and nothing replaced it.</summary>
    Other,

    /// <summary>No top-level navigation finished in time.</summary>
    Timeout,
}

/// <summary>One event's verdict: what to do, and (for <see cref="StoreRevealEffect.Fail"/>) why, with a short word for the log.</summary>
public readonly record struct StoreRevealResult(StoreRevealEffect Effect, StoreFailure Failure = StoreFailure.None, string Reason = "")
{
    public static StoreRevealResult None { get; } = new(StoreRevealEffect.None);
}

/// <summary>
/// When the Store's native view may be put on screen (the host keeps it hidden, the HTML placeholder shows "Opening…", until then),
/// and when a navigation chain has failed so badly that the placeholder must show the error instead. Pure and tested; the host
/// feeds it the WebView2 events and obeys the returned effect.
/// <para>
/// A Store open is a chain: store → (<c>/id/exchange</c> → store) or (→ <c>/id/login</c> → SSO → …), several server and script
/// redirects under one or more navigation ids. The old rule ("reveal on the first DOMContentLoaded") showed intermediate documents
/// and half-built single-page shells; a cancelled redirect never revealed anything and never failed either, so the placeholder
/// spun forever or the view stayed blank. Now: only a successfully loaded <see cref="StorePageKind.Content"/> page reveals (or a
/// content page that is DOM-ready for <see cref="RevealGrace"/> while its slow subresources still load); a navigation the policy
/// cancelled with nothing else in flight, a failed load, a cancelled-and-never-replaced load, and silence past
/// <see cref="LoadTimeout"/> all fail.
/// </para>
/// </summary>
public sealed class StoreRevealTracker
{
    /// <summary>A content page that is DOM-ready is shown after this even if its load event is still pending (Epic's Store streams in for a long time).</summary>
    public static readonly TimeSpan RevealGrace = TimeSpan.FromMilliseconds(1500);

    /// <summary>The longest a chain may go without ending (each allowed hop restarts it; the host extends it while it mints an exchange code).</summary>
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(25);

    /// <summary>After a navigation was cancelled with nothing in flight, the replacement navigation (if any) must start within this.</summary>
    public static readonly TimeSpan StallGrace = TimeSpan.FromSeconds(4);

    private readonly HashSet<ulong> _inFlight = [];
    private readonly Dictionary<ulong, StoreNavDecision> _blocked = [];
    private DateTimeOffset? _deadline;
    private DateTimeOffset? _domReadyAt;

    /// <summary>A real Store page is on screen (or may be): the native view may be visible.</summary>
    public bool Revealed { get; private set; }

    /// <summary>A navigation chain is pending (something may still be revealed or fail): the host's watchdog timer should tick.</summary>
    public bool Pending => _deadline is not null || _domReadyAt is not null;

    /// <summary>The view was closed, an error is shown, or the user is signed out: nothing is revealed, nothing pending.</summary>
    public void Reset()
    {
        Revealed = false;
        Clear();
    }

    /// <summary>The host starts a navigation itself (Navigate, Reload, the SSO hop): arm the deadline, plus <paramref name="extra"/> for known slow prework.</summary>
    public void BeginHostNavigation(DateTimeOffset now, TimeSpan extra = default)
    {
        _domReadyAt = null;
        _deadline = now + LoadTimeout + extra;
    }

    /// <summary>The host is waiting on something that is not a navigation (the exchange code): the chain may not time out before <paramref name="until"/>.</summary>
    public void ExtendDeadline(DateTimeOffset until)
    {
        if (_deadline is { } d && until > d)
        {
            _deadline = until;
        }
    }

    public void OnNavigationStarting(ulong id, StoreNavDecision decision, DateTimeOffset now)
    {
        if (decision == StoreNavDecision.Allow)
        {
            _inFlight.Add(id);
            _domReadyAt = null; // a new document is on its way: the old DOM-ready no longer counts
            _deadline = now + LoadTimeout;
        }
        else
        {
            _blocked[id] = decision;
        }
    }

    /// <summary><paramref name="url"/> is the document that became DOM-ready (the view's current source).</summary>
    public void OnDomContentLoaded(string? url, DateTimeOffset now)
    {
        if (Classify(url) == StorePageKind.Content)
        {
            _domReadyAt ??= now;
        }
    }

    public StoreRevealResult OnNavigationCompleted(ulong id, bool success, bool cancelled, bool networkError, string? url, DateTimeOffset now)
    {
        _inFlight.Remove(id);
        if (_blocked.Remove(id, out var decision))
        {
            // The policy cancelled this navigation (or one of its redirect hops). Intentional hand-offs (browser, launcher link) are
            // not failures; a plain cancel with nothing showing and nothing else loading is the blank page.
            if (decision == StoreNavDecision.Cancel && !Revealed && _inFlight.Count == 0)
            {
                Clear();
                return new StoreRevealResult(StoreRevealEffect.Fail, StoreFailure.Blocked, "navigation blocked by the allowlist");
            }

            return StoreRevealResult.None;
        }

        if (!success)
        {
            if (_inFlight.Count > 0)
            {
                return StoreRevealResult.None; // superseded: the newer navigation decides
            }

            if (cancelled)
            {
                // Cancelled with no replacement in sight (Stop(), a script cancelling its own load). Give a replacement a moment, then fail.
                if (!Revealed)
                {
                    var stall = now + StallGrace;
                    _deadline = _deadline is { } d && d < stall ? d : stall;
                }

                return StoreRevealResult.None;
            }

            Clear();
            Revealed = false;
            return new StoreRevealResult(StoreRevealEffect.Fail, networkError ? StoreFailure.Network : StoreFailure.Other, "load failed");
        }

        if (_inFlight.Count > 0)
        {
            return StoreRevealResult.None; // an older navigation finishing under a newer one
        }

        switch (Classify(url))
        {
            case StorePageKind.Content:
                var already = Revealed;
                Clear();
                Revealed = true;
                return already ? StoreRevealResult.None : new StoreRevealResult(StoreRevealEffect.Reveal, Reason: "page loaded");
            case StorePageKind.Login:
                Clear();
                Revealed = false;
                return new StoreRevealResult(StoreRevealEffect.LoginPage);
            default:
                return StoreRevealResult.None; // about:blank / the exchange hop: a redirect is expected; the deadline covers one that never comes
        }
    }

    /// <summary>Called a few times a second while <see cref="Pending"/>.</summary>
    public StoreRevealResult Tick(DateTimeOffset now)
    {
        if (!Revealed && _domReadyAt is { } ready && now - ready >= RevealGrace)
        {
            Clear();
            Revealed = true;
            return new StoreRevealResult(StoreRevealEffect.Reveal, Reason: "DOM ready, still loading");
        }

        if (_deadline is { } deadline && now >= deadline && _domReadyAt is null)
        {
            if (Revealed)
            {
                Clear(); // a page the user is already looking at may take as long as it takes to load its next page
                return StoreRevealResult.None;
            }

            Clear();
            return new StoreRevealResult(StoreRevealEffect.Fail, StoreFailure.Timeout, "no navigation finished in time");
        }

        return StoreRevealResult.None;
    }

    public static StorePageKind Classify(string? url)
    {
        if (string.IsNullOrEmpty(url) || url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return StorePageKind.Blank;
        }

        if (StoreSso.IsExchangeUrl(url))
        {
            return StorePageKind.Exchange;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var u)
            && u.Host.EndsWith("epicgames.com", StringComparison.OrdinalIgnoreCase)
            && u.AbsolutePath.StartsWith("/id/login", StringComparison.OrdinalIgnoreCase))
        {
            return StorePageKind.Login;
        }

        return StorePageKind.Content;
    }

    private void Clear()
    {
        _inFlight.Clear();
        _blocked.Clear();
        _deadline = null;
        _domReadyAt = null;
    }
}

/// <summary>The one line the host writes per top-level navigation event: host and path only, never a query string, fragment or credential.</summary>
public static class StoreNavLog
{
    public static string Starting(string? url, StoreNavDecision decision, bool redirected, bool userInitiated) =>
        $"nav → {Describe(url)} [{decision}{(redirected ? ", redirect" : "")}{(userInitiated ? ", user" : "")}]";

    public static string Completed(string? url, bool success, string status, int httpStatus, TimeSpan elapsed) =>
        $"nav ✓ {Describe(url)} [{(success ? "ok" : status)}{(httpStatus > 0 ? $" {httpStatus}" : "")}, {elapsed.TotalMilliseconds:0} ms]";

    /// <summary>Scheme://host/path, cut at 120 characters; custom schemes (launcher links, javascript:) are named by scheme only.</summary>
    public static string Describe(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return "(none)";
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return "(unparsable)";
        }

        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)
        {
            return url.Equals("about:blank", StringComparison.OrdinalIgnoreCase) ? "about:blank" : u.Scheme + ":…";
        }

        var s = SecretRedactor.UrlForLog(u.GetLeftPart(UriPartial.Path));
        return s.Length <= 120 ? s : s[..120] + "…";
    }
}
