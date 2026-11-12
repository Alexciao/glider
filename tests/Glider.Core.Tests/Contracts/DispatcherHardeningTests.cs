using System.Collections.Concurrent;
using System.Text.Json;
using Glider.Core.Contracts;

namespace Glider.Core.Tests.Contracts;

/// <summary>Security review: every request gets exactly one answer, and internals don't leak into it.</summary>
public class DispatcherHardeningTests
{
    private sealed class CaptureTransport : IRpcTransport
    {
        public ConcurrentQueue<string> Sent { get; } = new();

        public void Send(string json) => Sent.Enqueue(json);
    }

    public class Throwing : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            throw new InvalidOperationException(@"boom at C:\Users\someone\secret\path.cs refresh_token=eg1~abcdefghijkl");
    }

    [Fact]
    public async Task UnexpectedExceptionsAnswerOnceWithoutStackTraceOrSecrets()
    {
        var rpc = System.Reflection.DispatchProxy.Create<IGliderRpc, Throwing>();
        var transport = new CaptureTransport();
        Exception? reported = null;
        var d = new RpcDispatcher(rpc, transport, (_, e) => reported = e);

        await d.HandleAsync("""{"type":"request","id":3,"method":"app.getSnapshot"}""");

        var msg = Assert.Single(transport.Sent);
        using var doc = JsonDocument.Parse(msg);
        Assert.Equal(3, doc.RootElement.GetProperty("id").GetInt32());
        var detail = doc.RootElement.GetProperty("error").GetProperty("detail").GetString() ?? "";
        Assert.Equal("unknown", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain("   at ", detail, StringComparison.Ordinal); // no stack frames
        Assert.DoesNotContain("abcdefghijkl", detail, StringComparison.Ordinal);
        Assert.NotNull(reported); // the full exception still goes to the host log
    }
}
