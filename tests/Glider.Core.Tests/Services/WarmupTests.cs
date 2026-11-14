using System.Collections.Concurrent;
using Glider.Core.Contracts;

namespace Glider.Core.Tests.Services;

/// <summary>Startup diagnostics and the serializer warm-up (docs/perf.md): neither may change what the UI sees.</summary>
public class WarmupTests
{
    private sealed class Capture : IRpcTransport
    {
        public ConcurrentQueue<string> Sent { get; } = new();

        public void Send(string json) => Sent.Enqueue(json);
    }

    [Fact]
    public async Task TheDispatcher_ReportsEachHandledRequest_WithItsDurationAndSize()
    {
        var transport = new Capture();
        var handled = new List<(string Method, double Ms, int Chars)>();
        using var rig = new Rig();
        var d = new RpcDispatcher(rig.Service, transport, onHandled: (m, ms, chars) => handled.Add((m, ms, chars)));

        await d.HandleAsync("""{"type":"request","id":1,"method":"app.getSnapshot"}""");
        await d.HandleAsync("""{"type":"request","id":2,"method":"no.such.method"}"""); // errors are not "handled" results

        var (method, ms, chars) = Assert.Single(handled);
        Assert.Equal("app.getSnapshot", method);
        Assert.True(ms >= 0);
        Assert.Equal(transport.Sent.First().Length, chars);
    }

    [Fact]
    public async Task WarmingTheSerializers_ChangesNothingTheUiCanSee()
    {
        using var rig = new Rig();
        await rig.StartWarmAsync();
        var revision = rig.Service.LibraryForTests.Revision;
        var events = rig.Sink.Events.Count;
        var calls = rig.Calls.All.Count;

        rig.Service.WarmSerializers();

        Assert.Equal(revision, rig.Service.LibraryForTests.Revision);
        Assert.Equal(events, rig.Sink.Events.Count);
        Assert.Equal(calls, rig.Calls.All.Count);
        Assert.DoesNotContain(rig.Log.Lines, l => l.Contains("warm-up failed", StringComparison.Ordinal));
    }
}
