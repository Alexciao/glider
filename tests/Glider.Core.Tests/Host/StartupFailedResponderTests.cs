using System.Text.Json;
using Glider.App;
using Glider.Core.Contracts;

namespace Glider.Core.Tests.Host;

/// <summary>M3: when Core composition fails, every UI request is still answered, with a reason the user can act on.</summary>
public class StartupFailedResponderTests : IDisposable
{
    private sealed class Capture : IRpcTransport
    {
        public readonly List<string> Sent = [];

        public void Send(string json) => Sent.Add(json);
    }

    private readonly string _logs = Path.Combine(Path.GetTempPath(), "glider-startupfail-" + Guid.NewGuid().ToString("N"));
    private readonly Capture _transport = new();
    private readonly List<string> _copied = [];
    private readonly List<string> _actions = [];

    public StartupFailedResponderTests() => Directory.CreateDirectory(_logs);

    public void Dispose()
    {
        Directory.Delete(_logs, true);
        GC.SuppressFinalize(this);
    }

    private StartupFailedResponder Make(Exception? failure = null) => new(
        _transport,
        failure ?? new IOException("Access to the path 'D:/Glider/data' is denied."),
        _logs,
        _copied.Add,
        new Dictionary<string, Action> { ["app.quit"] = () => _actions.Add("quit"), ["window.close"] = () => _actions.Add("close"), ["window.minimize"] = () => _actions.Add("minimize") });

    private static string Request(int id, string method, string? args = null) =>
        $"{{\"type\":\"request\",\"id\":{id},\"method\":\"{method}\",\"params\":{args ?? "null"}}}";

    private JsonElement OnlyResponse()
    {
        var json = Assert.Single(_transport.Sent);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public async Task AnUnansweredRequestBecomesAStartupFailedErrorWithTheReason()
    {
        await Make().HandleAsync(Request(7, "library.get"));

        var r = OnlyResponse();
        Assert.Equal("response", r.GetProperty("type").GetString());
        Assert.Equal(7, r.GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("result").ValueKind);
        var error = r.GetProperty("error");
        Assert.Equal("Glider couldn't start", error.GetProperty("title").GetString());
        Assert.Contains("denied", error.GetProperty("message").GetString());
        Assert.Equal("openLogs", error.GetProperty("fix").GetString());
        Assert.Contains(_logs, error.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task EveryMethodGetsAnAnswerEvenWithParams()
    {
        var responder = Make();
        await responder.HandleAsync(Request(1, "app.getSnapshot"));
        await responder.HandleAsync(Request(2, "game.launch", "{\"appName\":\"x\"}"));
        await responder.HandleAsync(Request(3, "no.such.method"));

        Assert.Equal([1, 2, 3], _transport.Sent.Select(s => JsonDocument.Parse(s).RootElement.GetProperty("id").GetInt32()));
        Assert.All(_transport.Sent, s => Assert.Equal("Glider couldn't start", JsonDocument.Parse(s).RootElement.GetProperty("error").GetProperty("title").GetString()));
    }

    [Fact]
    public async Task LogsAreServedByTheHostAndRedacted()
    {
        File.WriteAllLines(Path.Combine(_logs, "glider-20260101.log"), ["line one", "line two"]);
        File.WriteAllLines(Path.Combine(_logs, "host-20260101.log"), ["host line"]);

        await Make().HandleAsync(Request(4, "app.getLogs", "{\"maxLines\":50}"));

        var result = OnlyResponse().GetProperty("result");
        var text = result.GetProperty("text").GetString()!;
        Assert.Contains("line two", text);
        Assert.Contains("host line", text);
        Assert.Contains("IOException", text);
        Assert.EndsWith("glider-20260101.log", result.GetProperty("logFile").GetString());
    }

    [Fact]
    public async Task CopyLogsPutsTheSameTextOnTheClipboard()
    {
        File.WriteAllLines(Path.Combine(_logs, "glider-20260101.log"), ["alpha"]);

        await Make().HandleAsync(Request(5, "app.copyLogs"));

        Assert.Equal(JsonValueKind.Null, OnlyResponse().GetProperty("result").ValueKind);
        Assert.Contains("alpha", Assert.Single(_copied));
    }

    [Fact]
    public async Task QuitAndWindowButtonsStillWork()
    {
        var responder = Make();
        await responder.HandleAsync(Request(6, "window.close"));
        await responder.HandleAsync(Request(7, "window.minimize"));
        await responder.HandleAsync(Request(8, "app.quit"));

        Assert.Equal(["close", "minimize", "quit"], _actions);
        Assert.All(_transport.Sent, s => Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(s).RootElement.GetProperty("error").ValueKind));
    }

    [Fact]
    public async Task NonRequestsAndGarbageAreIgnored()
    {
        var responder = Make();
        await responder.HandleAsync("{\"type\":\"host\",\"op\":\"theme\"}");
        await responder.HandleAsync("not json");
        await responder.HandleAsync("{\"type\":\"request\",\"method\":\"x\"}");

        Assert.Empty(_transport.Sent);
    }
}
