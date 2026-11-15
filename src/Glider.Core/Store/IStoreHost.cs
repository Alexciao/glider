using Glider.Core.Contracts;

namespace Glider.Core.Store;

/// <summary>A <c>com.epicgames.launcher://</c> link the Store page tried to open (raw: Core parses it with <see cref="Links.EpicLauncherUri"/>).</summary>
/// <param name="Uri">The raw link. Never logged in full (it may carry arguments); the parser drops everything but the whitelist.</param>
/// <param name="UserInitiated">The navigation came from a user gesture.</param>
/// <param name="InitiatingOrigin">Origin of the page/frame that asked; empty for host-initiated navigations.</param>
public sealed record StoreLinkClick(string Uri, bool UserInitiated, string? InitiatingOrigin);

/// <summary>
/// The native Store view (a second WebView2 child of the main window), as Core sees it. Implemented by Glider.App; every member is
/// safe to call from any thread. The host owns everything about the view itself (bounds, DPI, navigation allowlist, suspend/close
/// timers); Core decides what to open and what to do with links and checkout activity.
/// </summary>
public interface IStoreHost
{
    /// <summary>The view's state changed (throttled by the host). Payload is already sanitized (no query strings).</summary>
    event Action<StoreState>? StateChanged;

    /// <summary>The page tried to open a launcher deep link.</summary>
    event Action<StoreLinkClick>? LauncherLink;

    /// <summary>A checkout page was seen (see <see cref="StoreNavigationPolicy.IsCheckoutUrl"/>).</summary>
    event Action? CheckoutActivity;

    /// <summary>The main window became the foreground window again.</summary>
    event Action? WindowActivated;

    /// <summary>Mints a one-time 32-hex web exchange code for the signed-in Epic account (never log it); null when unavailable.</summary>
    Func<CancellationToken, Task<string?>>? ExchangeCodeProvider { get; set; }

    StoreState State { get; }

    /// <summary>Creates the view on first use, places it over <paramref name="bounds"/> and shows it. A non-null <paramref name="url"/> (built by Core, re-validated by the host) is loaded.</summary>
    Task<StoreState> ShowAsync(string? url, StoreBounds bounds);

    void SetBounds(StoreBounds bounds);

    /// <summary>Hide the view (low memory target now, suspended after a while, closed after a long idle).</summary>
    Task<StoreState> HideAsync();

    Task<StoreState> BackAsync();

    Task<StoreState> ReloadAsync();

    /// <summary>Hide under an HTML modal (a snapshot stands in) or restore.</summary>
    Task<StoreState> SetCoveredAsync(bool covered);

    /// <summary>
    /// Creates the view hidden and loads the Store (restoring the web session first when it is missing) in the background, so the
    /// first <see cref="ShowAsync"/> finds a page that is already there. Idempotent; failures are logged, never shown. Core calls it
    /// once, a few seconds after sign-in, at low priority.
    /// </summary>
    Task WarmAsync() => Task.CompletedTask;

    /// <summary>Opens the current sanitized Store URL in the default browser.</summary>
    void OpenInBrowser();

    /// <summary>Closes the view and forgets its snapshot (sign-out, account switch).</summary>
    Task DestroyAsync();
}
