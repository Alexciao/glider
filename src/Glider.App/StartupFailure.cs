using System.Globalization;
using System.Text.Json;
using Glider.Core.Contracts;
using Glider.Core.Infrastructure;

namespace Glider.App;

/// <summary>
/// Stands in for Core's RPC dispatcher when composition failed, so the UI is not left waiting 30 s on requests nobody will answer.
/// Every request gets a <c>startupFailed</c>-style <see cref="GliderError"/> (what went wrong, where the logs are). The calls that
/// still make sense without Core are served by the host: <c>app.getLogs</c>, <c>app.copyLogs</c> and the quit/window buttons.
/// </summary>
internal sealed class StartupFailedResponder
{
    private const int DefaultLogLines = 400;

    private readonly IRpcTransport _transport;
    private readonly Exception _failure;
    private readonly string _logDirectory;
    private readonly Action<string> _copyToClipboard;
    private readonly IReadOnlyDictionary<string, Action> _hostActions;
    private readonly GliderError _error;

    /// <param name="hostActions">Host-served methods that answer with a null result after running (e.g. <c>app.quit</c>, <c>window.close</c>).</param>
    public StartupFailedResponder(IRpcTransport transport, Exception failure, string logDirectory, Action<string> copyToClipboard, IReadOnlyDictionary<string, Action> hostActions)
    {
        _transport = transport;
        _failure = failure;
        _logDirectory = logDirectory;
        _copyToClipboard = copyToClipboard;
        _hostActions = hostActions;
        _error = new GliderError(
            ErrorCode.Unknown,
            "Glider couldn't start",
            "Glider's background service failed to start: " + SecretRedactor.Redact(failure.Message),
            FixAction.OpenLogs,
            $"{failure.GetType().Name}; logs: {logDirectory}");
    }

    /// <summary>Never throws; answers every request that carries an id, ignores everything else.</summary>
    public Task HandleAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "request" || !root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
            {
                return Task.CompletedTask;
            }

            var method = root.TryGetProperty("method", out var m) ? m.GetString() ?? "" : "";
            switch (method)
            {
                case "app.getLogs":
                    var lines = root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("maxLines", out var ml) && ml.TryGetInt32(out var n)
                        ? Math.Clamp(n, 1, 10_000)
                        : DefaultLogLines;
                    var result = JsonSerializer.Serialize(new LogsResult(LogText(lines), NewestLog("glider-*.log") ?? ""), GliderJsonContext.Default.LogsResult);
                    _transport.Send(Success(id, result));
                    break;
                case "app.copyLogs":
                    _copyToClipboard(LogText(2000));
                    _transport.Send(Success(id, "null"));
                    break;
                default:
                    if (_hostActions.TryGetValue(method, out var action))
                    {
                        action();
                        _transport.Send(Success(id, "null"));
                    }
                    else
                    {
                        _transport.Send($"{{\"type\":\"response\",\"id\":{id.ToString(CultureInfo.InvariantCulture)},\"result\":null,\"error\":{JsonSerializer.Serialize(_error, GliderJsonContext.Default.GliderError)}}}");
                    }

                    break;
            }
        }
        catch (JsonException)
        {
            // not ours to answer: no id to answer to
        }

        return Task.CompletedTask;
    }

    private static string Success(int id, string resultJson) =>
        $"{{\"type\":\"response\",\"id\":{id.ToString(CultureInfo.InvariantCulture)},\"result\":{resultJson},\"error\":null}}";

    private string LogText(int lines)
    {
        var text = $"Glider failed to start{Environment.NewLine}{_failure}{Environment.NewLine}";
        foreach (var pattern in new[] { "glider-*.log", "host-*.log" })
        {
            if (NewestLog(pattern) is { } file)
            {
                text += $"{Environment.NewLine}--- {Path.GetFileName(file)} ---{Environment.NewLine}{Tail(file, lines)}";
            }
        }

        return SecretRedactor.Redact(text);
    }

    private string? NewestLog(string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(_logDirectory, pattern).OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Tail(string file, int maxLines)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var queue = new Queue<string>();
            while (sr.ReadLine() is { } line)
            {
                queue.Enqueue(line);
                if (queue.Count > maxLines)
                {
                    queue.Dequeue();
                }
            }

            return string.Join(Environment.NewLine, queue) + Environment.NewLine;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
