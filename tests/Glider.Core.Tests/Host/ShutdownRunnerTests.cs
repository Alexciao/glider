using System.Diagnostics;
using Glider.App;

namespace Glider.Core.Tests.Host;

/// <summary>H1: the host quits on an STA thread whose SynchronizationContext is a dispatcher that has already shut down.</summary>
public class ShutdownRunnerTests
{
    /// <summary>Posts go nowhere, exactly like a <c>DispatcherSynchronizationContext</c> after <c>Application.Run</c> returned.</summary>
    private sealed class DeadContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
        }
    }

    private static T OnDeadContext<T>(Func<T> body)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DeadContext());
        try
        {
            return body();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void DisposalThatAwaitsStillFinishesWhenTheCallerContextIsDead()
    {
        var sw = Stopwatch.StartNew();
        var finished = OnDeadContext(() => ShutdownRunner.DisposeBlocking(
            async () =>
            {
                await Task.Delay(20);   // completes asynchronously: its continuation would be posted to the dead context
                await Task.Yield();
            },
            TimeSpan.FromSeconds(1.5)));

        Assert.True(finished, "disposal deadlocked on the dead dispatcher context");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"took {sw.Elapsed}");
    }

    [Fact]
    public void ASlowDisposalIsAbandonedAtTheTimeout()
    {
        var never = new TaskCompletionSource();
        var sw = Stopwatch.StartNew();

        var finished = ShutdownRunner.DisposeBlocking(() => new ValueTask(never.Task), TimeSpan.FromMilliseconds(150));

        Assert.False(finished);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5), $"took {sw.Elapsed}");
    }

    [Fact]
    public void AFailingDisposalIsReportedNotThrown()
    {
        Exception? seen = null;

        var finished = ShutdownRunner.DisposeBlocking(
            async () =>
            {
                await Task.Delay(1);
                throw new InvalidOperationException("boom");
            },
            TimeSpan.FromSeconds(2),
            ex => seen = ex);

        Assert.False(finished);
        Assert.IsType<InvalidOperationException>(seen);
    }
}
