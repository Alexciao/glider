using Glider.Core.Contracts;

namespace Glider.Core.Store;

/// <summary>What the Store view's WebView2 is doing right now, as far as its lifetime is concerned.</summary>
/// <param name="WantVisible">The Store tab is the active one (the page asked for <c>store.show</c> and has not sent <c>store.hide</c>).</param>
/// <param name="WindowHidden">The main window is hidden to the tray or minimized. A suspended page can't send <c>store.hide</c>, so this is tracked separately.</param>
/// <param name="PopupOpen">A sign-in or payment popup of the Store is open.</param>
/// <param name="CheckoutActive">Checkout activity was seen recently; a checkout must never be suspended or closed under the user.</param>
/// <param name="Suspended">The page is suspended.</param>
public readonly record struct StoreLifeState(bool WantVisible, bool WindowHidden, bool PopupOpen, bool CheckoutActive, bool Suspended)
{
    /// <summary>The user can see the Store: its tab is active and the window is up. Only then does it run at full memory.</summary>
    public bool OnScreen => WantVisible && !WindowHidden;
}

public enum StoreTimerAction
{
    /// <summary>Nothing to do (the user is looking at it, or it is already in the state the timer would reach).</summary>
    None,

    /// <summary>Do it now.</summary>
    Act,

    /// <summary>Not now (popup, checkout): look again after another full interval.</summary>
    Retry,
}

/// <summary>
/// The Store view's lifecycle rules, free of WPF so they can be tested: hidden (tab left, window in the tray) means IsVisible=false,
/// low memory, suspended after 60 s, closed after ~20 min without checkout activity (docs/research/store-webview.md, review H5).
/// </summary>
public static class StoreLifecycle
{
    public static readonly TimeSpan SuspendAfter = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan CloseAfter = TimeSpan.FromMinutes(20);

    /// <summary>Whether the suspend/close timers should be armed: the Store is not on screen.</summary>
    public static bool ShouldArmTimers(StoreLifeState s) => !s.OnScreen;

    /// <summary>The suspend timer fired. A popup or a checkout defers it (and the timer must be re-armed, or it would never be retried).</summary>
    public static StoreTimerAction OnSuspendTimer(StoreLifeState s) =>
        s.OnScreen || s.Suspended ? StoreTimerAction.None
        : s.PopupOpen || s.CheckoutActive ? StoreTimerAction.Retry
        : StoreTimerAction.Act;

    /// <summary>The close timer fired.</summary>
    public static StoreTimerAction OnCloseTimer(StoreLifeState s) =>
        s.OnScreen ? StoreTimerAction.None
        : s.PopupOpen || s.CheckoutActive ? StoreTimerAction.Retry
        : StoreTimerAction.Act;

    /// <summary>The window or the tab came back: a suspended page must be resumed before it is shown.</summary>
    public static bool ShouldResume(StoreLifeState s) => s.OnScreen && s.Suspended;

    /// <summary>The phase to publish for a live (not closed) view.</summary>
    public static StorePhase PhaseFor(StoreLifeState s) =>
        s.OnScreen ? StorePhase.Ready : s.Suspended ? StorePhase.Suspended : StorePhase.Hidden;
}
