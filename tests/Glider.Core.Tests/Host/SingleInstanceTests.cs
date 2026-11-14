using Glider.App;

namespace Glider.Core.Tests.Host;

/// <summary>H1: a relaunch during the previous instance's shutdown must not be swallowed.</summary>
public class SingleInstanceTests
{
    [Fact]
    public void ARelaunchWhileTheFirstInstanceQuitsWaitsForItAndBecomesTheNewFirstInstance()
    {
        var scope = ".test-" + Guid.NewGuid().ToString("N");
        using var ownerHasMutex = new ManualResetEventSlim();
        using var coreDisposed = new ManualResetEventSlim();
        using var relaunchDone = new ManualResetEventSlim();
        SingleInstance? owner = null;
        SingleInstance? relaunched = null;

        // A mutex belongs to the thread that took it, so each "process" runs on its own thread.
        var first = new Thread(() =>
        {
            owner = SingleInstance.TryAcquire(scope);
            owner!.Listen(_ => { });
            ownerHasMutex.Set();
            coreDisposed.Wait();
            owner.Dispose();
        });
        first.Start();
        Assert.True(ownerHasMutex.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // The dispatcher began shutting down (Program: ShutdownStarted): nobody answers activations any more, Core is still disposing.
        owner!.StopListening();
        Thread.Sleep(200);

        var second = new Thread(() =>
        {
            relaunched = SingleInstance.TryAcquire(scope);
            relaunchDone.Set();
            relaunched?.Dispose();
        });
        second.Start();

        Assert.False(relaunchDone.Wait(TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken), "the relaunch must wait for the quitting instance, not exit");

        coreDisposed.Set(); // Core is disposed and the mutex released
        Assert.True(relaunchDone.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the relaunch never took over");
        first.Join();
        second.Join();
        Assert.NotNull(relaunched);
    }
}
