using Glider.App;

namespace Glider.Core.Tests.Host;

/// <summary>M4: while the main WebView2 is suspended the bridge queue must stay small, and the replay must not be a render storm.</summary>
public class BridgeQueueTests
{
    private static string Event(string name, int n) => $"{{\"type\":\"event\",\"event\":\"{name}\",\"data\":{{\"n\":{n}}}}}";

    private static string Response(int id) => $"{{\"type\":\"response\",\"id\":{id},\"result\":null,\"error\":null}}";

    private static List<string> Drain(BridgeQueue q)
    {
        var all = new List<string>();
        while (q.TryDequeue(out var json))
        {
            all.Add(json);
        }

        return all;
    }

    [Fact]
    public void WhileHeldOnlyTheLatestLibraryDeltaIsKept()
    {
        var q = new BridgeQueue { Held = true };
        for (var i = 1; i <= 700; i++)
        {
            q.Enqueue(Event("library", i));
        }

        Assert.Equal(1, q.Count);
        Assert.Equal([Event("library", 700)], Drain(q));
    }

    [Fact]
    public void WhenNotHeldEveryLibraryDeltaIsDeliveredInOrder()
    {
        var q = new BridgeQueue();
        q.Enqueue(Event("library", 1));
        q.Enqueue(Event("library", 2));
        q.Enqueue(Event("library", 3));

        Assert.Equal([Event("library", 1), Event("library", 2), Event("library", 3)], Drain(q));
    }

    [Fact]
    public void HoldingCollapsesDeltasThatWereAlreadyQueued()
    {
        var q = new BridgeQueue();
        q.Enqueue(Event("library", 1));
        q.Enqueue(Event("library", 2));
        q.Enqueue(Event("notice", 1));
        q.Held = true;
        q.Enqueue(Event("library", 3));

        // 1 and 2 are gone: the UI sees a revision gap in the surviving delta and reloads with library.get.
        Assert.Equal([Event("notice", 1), Event("library", 3)], Drain(q));
    }

    [Fact]
    public void WhileHeldOnlyTheNewestNoticesAreKept()
    {
        var q = new BridgeQueue { Held = true };
        for (var i = 1; i <= 100; i++)
        {
            q.Enqueue(Event("notice", i));
        }

        Assert.Equal(BridgeQueue.HeldNoticeCap, q.Count);
        var kept = Drain(q);
        Assert.Equal(Event("notice", 100 - BridgeQueue.HeldNoticeCap + 1), kept[0]);
        Assert.Equal(Event("notice", 100), kept[^1]);
    }

    [Fact]
    public void NoticesAreNotCappedWhenNotHeld()
    {
        var q = new BridgeQueue();
        for (var i = 1; i <= 100; i++)
        {
            q.Enqueue(Event("notice", i));
        }

        Assert.Equal(100, q.Count);
    }

    [Fact]
    public void NoticeCapSurvivesDrainingAndRefilling()
    {
        var q = new BridgeQueue();
        for (var i = 1; i <= 5; i++)
        {
            q.Enqueue(Event("notice", i));
        }

        Assert.True(q.TryDequeue(out _));
        Assert.True(q.TryDequeue(out _));
        q.Held = true;
        for (var i = 6; i <= 60; i++)
        {
            q.Enqueue(Event("notice", i));
        }

        var kept = Drain(q);
        Assert.Equal(BridgeQueue.HeldNoticeCap, kept.Count);
        Assert.Equal(Event("notice", 60), kept[^1]);
        Assert.Equal(0, q.Count);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("progress")]
    [InlineData("window")]
    public void StateProgressAndWindowAlwaysKeepOnlyTheLatest(string name)
    {
        var q = new BridgeQueue();
        q.Enqueue(Event(name, 1));
        q.Enqueue(Event(name, 2));
        q.Enqueue(Event(name, 3));

        Assert.Equal([Event(name, 3)], Drain(q));
    }

    [Fact]
    public void ResponsesAreNeverDroppedOrReordered()
    {
        var q = new BridgeQueue { Held = true };
        q.Enqueue(Response(1));
        q.Enqueue(Event("library", 1));
        q.Enqueue(Response(2));
        q.Enqueue(Event("library", 2));
        q.Enqueue(Response(3));

        Assert.Equal([Response(1), Response(2), Event("library", 2), Response(3)], Drain(q));
    }

    [Fact]
    public void ADayInTheTrayStaysBounded()
    {
        var q = new BridgeQueue { Held = true };
        for (var i = 0; i < 50_000; i++)
        {
            q.Enqueue(Event("library", i));
            q.Enqueue(Event("notice", i));
            q.Enqueue(Event("state", i));
            q.Enqueue(Event("progress", i));
        }

        Assert.Equal(1 + BridgeQueue.HeldNoticeCap + 1 + 1, q.Count);
    }

    [Fact]
    public void ClearDropsEverythingAndTheQueueKeepsWorking()
    {
        var q = new BridgeQueue { Held = true };
        q.Enqueue(Event("library", 1));
        q.Enqueue(Event("notice", 1));
        q.Enqueue(Event("state", 1));
        q.Clear();

        Assert.Equal(0, q.Count);
        q.Enqueue(Event("library", 2));
        q.Enqueue(Event("notice", 2));
        q.Enqueue(Event("state", 2));
        Assert.Equal([Event("library", 2), Event("notice", 2), Event("state", 2)], Drain(q));
    }

    [Fact]
    public void ReleasingTheHoldDeliversTheFoldedQueueThenResumesNormalOrder()
    {
        var q = new BridgeQueue { Held = true };
        q.Enqueue(Event("library", 1));
        q.Enqueue(Event("library", 2));
        q.Held = false;
        q.Enqueue(Event("library", 3));
        q.Enqueue(Event("library", 4));

        Assert.Equal([Event("library", 2), Event("library", 3), Event("library", 4)], Drain(q));
    }
}
