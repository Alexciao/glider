using Glider.Core.Legendary;
using Glider.Core.Tests.TestSupport;

namespace Glider.Core.Tests.Legendary;

/// <summary>One Legendary process inside login() at a time; launch jumps the wait list (L§1.6, L§1.8).</summary>
public class AuthGateTests
{
    [Fact]
    public async Task ALaunchJumpsTheLine_AheadOfEarlierEntrants()
    {
        var gate = new AuthGate();
        var ct = TestContext.Current.CancellationToken;
        var holder = await gate.EnterAsync(false, "install", ct);
        var order = new List<string>();
        var list = gate.EnterAsync(false, "list", ct).ContinueWith(t => { lock (order) { order.Add("list"); } return t.Result; }, TaskScheduler.Default);
        var info = gate.EnterAsync(false, "info", ct).ContinueWith(t => { lock (order) { order.Add("info"); } return t.Result; }, TaskScheduler.Default);
        await Task.Delay(20, ct);
        var launch = gate.EnterAsync(true, "launch", ct).ContinueWith(t => { lock (order) { order.Add("launch"); } return t.Result; }, TaskScheduler.Default);
        await Task.Delay(20, ct);
        Assert.Equal(3, gate.Waiting);

        holder.Release();
        (await launch).Release();
        (await list).Release();
        (await info).Release();
        Assert.Equal(["launch", "list", "info"], order);
        Assert.False(gate.Held);
    }

    [Fact]
    public async Task ALeaseIsReleasedAfterTheMaximumHold_AndReleasingTwiceIsHarmless()
    {
        var time = new ManualTimeProvider();
        var gate = new AuthGate(time, TimeSpan.FromSeconds(45));
        var ct = TestContext.Current.CancellationToken;
        var stuck = await gate.EnterAsync(false, "silent", ct);
        var next = gate.EnterAsync(false, "next", ct);
        Assert.False(next.IsCompleted);

        time.Advance(TimeSpan.FromSeconds(46)); // a login that never printed anything
        var lease = await next.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.True(stuck.Released);
        stuck.Release();
        Assert.True(gate.Held); // the old lease can't release the new holder
        lease.Release();
        Assert.False(gate.Held);
    }

    [Fact]
    public async Task ACancelledWaiterNeverHoldsTheGate()
    {
        var gate = new AuthGate();
        var ct = TestContext.Current.CancellationToken;
        var holder = await gate.EnterAsync(false, "a", ct);
        using var cts = new CancellationTokenSource();
        var cancelled = gate.EnterAsync(true, "b", cts.Token);
        var after = gate.EnterAsync(false, "c", ct);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        holder.Release();
        var lease = await after.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal("c", lease.Label);
        lease.Release();
    }

    [Theory]
    [InlineData("[Core] INFO: Trying to re-use existing login session...", true, false)]
    [InlineData("[Core] INFO: Logging in...", true, false)]
    [InlineData("[Core] INFO: Falling back to using refresh token...", true, false)]
    [InlineData("[cli] INFO: Testing existing login data if present...", true, false)]
    [InlineData("[cli] INFO: Preparing download for \"Fortnite\"...", false, false)]
    [InlineData("[DLManager] INFO: = Progress: 1.00% (1/100)", false, true)]
    [InlineData("{\"game_executable\": \"x\"}", false, true)]
    public void RecognisesLoginAndPostLoginLines(string line, bool login, bool pastLogin)
    {
        Assert.Equal(login, AuthGate.IsLoginLine(line));
        Assert.Equal(pastLogin, AuthGate.IsPastLogin(line));
    }

    [Fact]
    public async Task TheRunnerHoldsTheGateOnlyThroughTheLogin()
    {
        using var env = new FakeLegendaryEnv();
        env.Script("info", """
            err [Core] INFO: Trying to re-use existing login session...
            sleep 200
            err [cli] INFO: Getting game info...
            sleep 1500
            out {"game": null}
            """);
        env.Script("status", """
            out {"account": "<not logged in>"}
            """);
        using var cli = new LegendaryCli(env.Paths, env.Log, new LegendaryOptions());
        var ct = TestContext.Current.CancellationToken;

        var info = cli.GetGameInfoAsync("Fortnite", ct);
        await Task.Delay(100, ct);
        Assert.True(cli.Runner.Gate.Held); // inside its login
        for (var i = 0; i < 300 && cli.Runner.Gate.Held; i++)
        {
            await Task.Delay(10, ct);
        }

        Assert.False(cli.Runner.Gate.Held); // released at the first post-login line, long before the process exits
        Assert.False(info.IsCompleted);
        await cli.RefreshSessionAsync(ct).ContinueWith(_ => { }, ct); // another login can start while info still runs
        await Assert.ThrowsAsync<Glider.Core.Contracts.GliderException>(() => info);
    }
}
