using Glider.Core.Links;

namespace Glider.Core.Store;

/// <summary>What the host does with a navigation request of the Store view.</summary>
public enum StoreNavDecision
{
    /// <summary>Let it load in the Store view.</summary>
    Allow,

    /// <summary>Block it silently.</summary>
    Cancel,

    /// <summary>Block it in the Store view and open it in the user's default browser (user clicked a link to a foreign site).</summary>
    OpenInSystemBrowser,

    /// <summary>A <c>com.epicgames.launcher://</c> link: block it and hand it to <see cref="EpicLauncherUri"/>.</summary>
    LauncherLink,
}

/// <summary>What a <c>window.open</c>/<c>target=_blank</c> request of the Store page may become.</summary>
public enum StorePopupDecision
{
    /// <summary>Ignore it.</summary>
    Block,

    /// <summary>The target is an Epic page: load it in the Store view itself (no extra window).</summary>
    NavigateInPlace,

    /// <summary>Open a small native popup that hosts the page's own window (login/payment popups; window.opener keeps working).</summary>
    Popup,

    /// <summary>A launcher deep link: treat as one.</summary>
    LauncherLink,

    /// <summary>A user-clicked foreign link: default browser.</summary>
    OpenInSystemBrowser,
}

/// <summary>
/// The Store view's navigation allowlist (docs/research/store-webview.md §6.2). Pure and table-tested. The host feeds it every
/// top-level <c>NavigationStarting</c>, <c>FrameNavigationStarting</c> and <c>NewWindowRequested</c>; nothing else decides.
/// </summary>
public static class StoreNavigationPolicy
{
    public const int MaxPopups = 2;

    /// <summary>Hosts that may load in the Store view (exact host, or any subdomain of epicgames.com).</summary>
    public static readonly string[] ExactHosts =
    [
        "store.epicgames.com",
        "launcher.store.epicgames.com",
        "www.epicgames.com",
        "epicgames.com",
        "accounts.epicgames.com",
        "help.epicgames.com",
        "www.unrealengine.com",
    ];

    /// <summary>True for https, no userinfo, default port, host in the Epic set.</summary>
    public static bool IsEpicUrl(string? url) =>
        TryHttps(url, out var u) && IsEpicHost(u.Host);

    public static bool IsEpicHost(string host)
    {
        if (ExactHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return host.EndsWith(".epicgames.com", StringComparison.OrdinalIgnoreCase) && host.Length > ".epicgames.com".Length;
    }

    /// <summary>
    /// Top-level navigation.
    /// <paramref name="checkoutActive"/>: a checkout page was seen (payment redirects to other hosts are allowed then).
    /// <paramref name="glideStartedExchange"/>: the host itself navigated to the exchange URL (anything else on /id/exchange is a
    /// replay of a spent code, e.g. Back, and is cancelled).
    /// </summary>
    public static StoreNavDecision Decide(string? url, bool userInitiated, bool checkoutActive, bool glideStartedExchange = false)
    {
        if (string.IsNullOrEmpty(url))
        {
            return StoreNavDecision.Cancel;
        }

        if (EpicLauncherUri.HasScheme(url))
        {
            return StoreNavDecision.LauncherLink;
        }

        if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return StoreNavDecision.Allow;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return StoreNavDecision.Cancel;
        }

        // javascript:, data:, file:, blob:, ftp:, ws(s):, http:, and every other custom scheme.
        if (u.Scheme != Uri.UriSchemeHttps || u.UserInfo.Length != 0 || !u.IsDefaultPort)
        {
            return StoreNavDecision.Cancel;
        }

        if (IsEpicHost(u.Host))
        {
            if (StoreSso.IsExchangeUrl(url) && !glideStartedExchange)
            {
                return StoreNavDecision.Cancel;
            }

            return StoreNavDecision.Allow;
        }

        if (checkoutActive)
        {
            return StoreNavDecision.Allow; // payment redirects (3-D Secure, bank pages); the toolbar shows host + padlock
        }

        return userInitiated ? StoreNavDecision.OpenInSystemBrowser : StoreNavDecision.Cancel;
    }

    /// <summary>Subframes (the checkout iframe, captcha, ads): https only; Epic's own pages and third-party widgets are the page's business.</summary>
    public static bool AllowFrame(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase) || url.Equals("about:srcdoc", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TryHttps(url, out _);
    }

    /// <summary>A launcher link opened as a frame or via <c>window.open</c> is still only a launcher link.</summary>
    public static bool IsLauncherLink(string? url) => EpicLauncherUri.HasScheme(url);

    /// <summary>
    /// <c>NewWindowRequested</c>. Popups are for login and payment only: https, user-initiated, at most <see cref="MaxPopups"/>, and
    /// only while on a sign-in page or in checkout. Epic pages opened with target=_blank just navigate in place.
    /// </summary>
    public static StorePopupDecision DecidePopup(string? targetUrl, bool userInitiated, string? currentUrl, bool checkoutActive, int openPopups)
    {
        if (EpicLauncherUri.HasScheme(targetUrl))
        {
            return StorePopupDecision.LauncherLink;
        }

        if (targetUrl is not null && targetUrl.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
        {
            // OAuth popups start at about:blank and are then navigated by the opener; same gating as a real popup.
            return userInitiated && openPopups < MaxPopups && (checkoutActive || OnSignInPage(currentUrl)) ? StorePopupDecision.Popup : StorePopupDecision.Block;
        }

        if (!TryHttps(targetUrl, out var t))
        {
            return StorePopupDecision.Block;
        }

        if (IsEpicHost(t.Host) && !(StoreSso.IsExchangeUrl(targetUrl)))
        {
            return StorePopupDecision.NavigateInPlace;
        }

        if (!userInitiated)
        {
            return StorePopupDecision.Block;
        }

        if ((checkoutActive || OnSignInPage(currentUrl)) && openPopups < MaxPopups)
        {
            return StorePopupDecision.Popup;
        }

        return StorePopupDecision.OpenInSystemBrowser;
    }

    /// <summary>Inside the popup: https only (plus about:blank).</summary>
    public static bool AllowPopupNavigation(string? url) =>
        url is not null && (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase) || TryHttps(url, out _));

    // ------------------------------------------------------------------ checkout detection (docs §4.1)

    /// <summary>
    /// A navigation that means "a purchase/claim is in progress": a subframe on an <c>*.ol.epicgames.com</c> host with a path
    /// starting <c>/purchase</c>, or a top-level path containing <c>/purchase</c> or <c>/checkout</c> on the store hosts.
    /// </summary>
    public static bool IsCheckoutUrl(string? url, bool isMainFrame)
    {
        if (!TryHttps(url, out var u))
        {
            return false;
        }

        var path = u.AbsolutePath;
        if (!isMainFrame)
        {
            return u.Host.EndsWith(".ol.epicgames.com", StringComparison.OrdinalIgnoreCase)
                && path.StartsWith("/purchase", StringComparison.OrdinalIgnoreCase);
        }

        var storeHost = u.Host.Equals("store.epicgames.com", StringComparison.OrdinalIgnoreCase)
            || u.Host.Equals("launcher.store.epicgames.com", StringComparison.OrdinalIgnoreCase);
        return storeHost
            && (path.Contains("/purchase", StringComparison.OrdinalIgnoreCase) || path.Contains("/checkout", StringComparison.OrdinalIgnoreCase));
    }

    private static bool OnSignInPage(string? currentUrl) =>
        TryHttps(currentUrl, out var u) && IsEpicHost(u.Host) && u.AbsolutePath.StartsWith("/id/", StringComparison.OrdinalIgnoreCase);

    private static bool TryHttps(string? url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u)
            && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Length == 0 && u.IsDefaultPort)
        {
            uri = u;
            return true;
        }

        uri = null!;
        return false;
    }
}
