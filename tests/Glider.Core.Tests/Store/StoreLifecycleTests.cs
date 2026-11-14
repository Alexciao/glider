using Glider.Core.Contracts;
using Glider.Core.Store;

namespace Glider.Core.Tests.Store;

/// <summary>The Store WebView2's lifetime rules (review H5): a window in the tray counts as hidden even while the Store tab stays "active".</summary>
public class StoreLifecycleTests
{
    private static StoreLifeState State(bool want = false, bool windowHidden = false, bool popup = false, bool checkout = false, bool suspended = false) =>
        new(want, windowHidden, popup, checkout, suspended);

    [Fact]
    public void TheStoreTabOpenInAVisibleWindow_IsOnScreen_AndIsNeverSuspendedOrClosed()
    {
        var s = State(want: true);
        Assert.True(s.OnScreen);
        Assert.False(StoreLifecycle.ShouldArmTimers(s));
        Assert.Equal(StoreTimerAction.None, StoreLifecycle.OnSuspendTimer(s));
        Assert.Equal(StoreTimerAction.None, StoreLifecycle.OnCloseTimer(s));
        Assert.Equal(StorePhase.Ready, StoreLifecycle.PhaseFor(s));
    }

    [Fact]
    public void TheStoreTabOpenWhileTheWindowSitsInTheTray_IsHidden_SoItSuspendsAndCloses()
    {
        var s = State(want: true, windowHidden: true);
        Assert.False(s.OnScreen);
        Assert.True(StoreLifecycle.ShouldArmTimers(s));
        Assert.Equal(StoreTimerAction.Act, StoreLifecycle.OnSuspendTimer(s));
        Assert.Equal(StoreTimerAction.Act, StoreLifecycle.OnCloseTimer(s));
        Assert.Equal(StorePhase.Hidden, StoreLifecycle.PhaseFor(s));
        Assert.Equal(StorePhase.Suspended, StoreLifecycle.PhaseFor(s with { Suspended = true }));
    }

    [Fact]
    public void AlreadySuspended_TheSuspendTimerHasNothingToDo_ButTheCloseTimerStillCloses()
    {
        var s = State(want: true, windowHidden: true, suspended: true);
        Assert.Equal(StoreTimerAction.None, StoreLifecycle.OnSuspendTimer(s));
        Assert.Equal(StoreTimerAction.Act, StoreLifecycle.OnCloseTimer(s));
    }

    [Fact]
    public void ALeftTabIsHiddenToo()
    {
        var s = State(want: false);
        Assert.True(StoreLifecycle.ShouldArmTimers(s));
        Assert.Equal(StoreTimerAction.Act, StoreLifecycle.OnSuspendTimer(s));
        Assert.Equal(StoreTimerAction.Act, StoreLifecycle.OnCloseTimer(s));
    }

    [Fact]
    public void ACheckoutOrAPopupDefersSuspendAndCloseAndAsksForARetry()
    {
        foreach (var s in new[] { State(windowHidden: true, want: true, checkout: true), State(want: false, popup: true) })
        {
            Assert.Equal(StoreTimerAction.Retry, StoreLifecycle.OnSuspendTimer(s));
            Assert.Equal(StoreTimerAction.Retry, StoreLifecycle.OnCloseTimer(s));
        }

        // …and once the checkout window has passed, the retry goes through.
        Assert.Equal(StoreTimerAction.Act, StoreLifecycle.OnSuspendTimer(State(windowHidden: true, want: true)));
    }

    [Fact]
    public void ComingBackToTheStoreTab_ResumesASuspendedPage_OnlyWhenItWillBeOnScreen()
    {
        Assert.True(StoreLifecycle.ShouldResume(State(want: true, suspended: true)));
        Assert.False(StoreLifecycle.ShouldResume(State(want: true, windowHidden: true, suspended: true))); // still in the tray
        Assert.False(StoreLifecycle.ShouldResume(State(want: false, suspended: true))); // another tab is active: stay suspended
        Assert.False(StoreLifecycle.ShouldResume(State(want: true)));
    }

    [Fact]
    public void TheTimings_AreSixtySecondsAndTwentyMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), StoreLifecycle.SuspendAfter);
        Assert.Equal(TimeSpan.FromMinutes(20), StoreLifecycle.CloseAfter);
    }
}
