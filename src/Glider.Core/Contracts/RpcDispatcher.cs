using System.Collections.Frozen;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Glider.Core.Contracts;

/// <summary>Sends serialized JSON messages to the UI. The host marshals to the UI thread.</summary>
public interface IRpcTransport
{
    void Send(string json);
}

/// <summary>Pushes server events to the UI.</summary>
public interface IEventSink
{
    void Publish<T>(string eventName, T data);
}

/// <summary>
/// Routes JSON-RPC requests from the UI to an <see cref="IGliderRpc"/> implementation and serializes
/// responses/events. Method table is built once from the interface's [RpcMethod] attributes.
/// </summary>
public sealed class RpcDispatcher : IEventSink
{
    private sealed record Route(MethodInfo Method, JsonTypeInfo? ParamType, JsonTypeInfo? ResultType, PropertyInfo? ResultProperty);

    private static readonly FrozenDictionary<string, Route> Routes = BuildRoutes();

    private readonly IGliderRpc _impl;
    private readonly IRpcTransport _transport;
    private readonly Action<string, Exception>? _onError;
    private readonly Action<string, double, int>? _onHandled;

    /// <param name="onHandled">Diagnostics: (method, milliseconds from receipt to the response being handed to the transport, response characters).</param>
    public RpcDispatcher(IGliderRpc impl, IRpcTransport transport, Action<string, Exception>? onError = null, Action<string, double, int>? onHandled = null)
    {
        _impl = impl;
        _transport = transport;
        _onError = onError;
        _onHandled = onHandled;
    }

    public static IReadOnlyCollection<string> MethodNames => Routes.Keys;

    private static FrozenDictionary<string, Route> BuildRoutes()
    {
        var ctx = GliderJsonContext.Default;
        var map = new Dictionary<string, Route>(StringComparer.Ordinal);
        foreach (var m in typeof(IGliderRpc).GetMethods())
        {
            var attr = m.GetCustomAttribute<RpcMethodAttribute>()
                ?? throw new InvalidOperationException($"{m.Name} lacks [RpcMethod]");
            var ps = m.GetParameters();
            if (ps.Length > 1)
            {
                throw new InvalidOperationException($"{m.Name}: RPC methods take at most one parameter");
            }

            JsonTypeInfo? paramInfo = ps.Length == 1
                ? ctx.GetTypeInfo(ps[0].ParameterType) ?? throw new InvalidOperationException($"{ps[0].ParameterType} missing from GliderJsonContext")
                : null;

            JsonTypeInfo? resultInfo = null;
            PropertyInfo? resultProp = null;
            if (m.ReturnType.IsGenericType && m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var t = m.ReturnType.GetGenericArguments()[0];
                resultInfo = ctx.GetTypeInfo(t) ?? throw new InvalidOperationException($"{t} missing from GliderJsonContext");
                resultProp = m.ReturnType.GetProperty(nameof(Task<int>.Result));
            }
            else if (m.ReturnType != typeof(Task))
            {
                throw new InvalidOperationException($"{m.Name} must return Task or Task<T>");
            }

            map.Add(attr.Name, new Route(m, paramInfo, resultInfo, resultProp));
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>Handle one raw message from the UI. Never throws; always answers requests that carry an id.</summary>
    public async Task HandleAsync(string json)
    {
        int id = -1;
        string method = "?";
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != "request")
            {
                return;
            }

            id = root.GetProperty("id").GetInt32();
            method = root.GetProperty("method").GetString() ?? "";
            if (!Routes.TryGetValue(method, out var route))
            {
                SendError(id, Errors.InvalidRequest($"Unknown method '{method}'"));
                return;
            }

            object?[] args = [];
            if (route.ParamType is not null)
            {
                if (!root.TryGetProperty("params", out var p) || p.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    SendError(id, Errors.InvalidRequest($"'{method}' requires params"));
                    return;
                }

                args = [p.Deserialize(route.ParamType)];
            }

            var task = (Task)route.Method.Invoke(_impl, args)!;
            await task.ConfigureAwait(false);

            string resultJson = "null";
            if (route.ResultType is not null)
            {
                var result = route.ResultProperty!.GetValue(task);
                resultJson = JsonSerializer.Serialize(result, route.ResultType);
            }

            var response = $"{{\"type\":\"response\",\"id\":{id},\"result\":{resultJson},\"error\":null}}";
            _transport.Send(response);
            _onHandled?.Invoke(method, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds, response.Length);
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
            var error = inner switch
            {
                GliderException ge => ge.Error,
                OperationCanceledException => Errors.Cancelled(),
                JsonException je => Errors.InvalidRequest(je.Message),
                // The UI gets the message only (no stack trace, redacted); the full exception goes to onError's log.
                _ => Errors.Unknown(Infrastructure.SecretRedactor.Redact($"{inner.GetType().Name}: {inner.Message}")),
            };
            if (inner is not GliderException and not OperationCanceledException)
            {
                _onError?.Invoke(method, inner);
            }

            if (id >= 0)
            {
                SendError(id, error);
            }
        }
    }

    private void SendError(int id, GliderError error)
    {
        var e = JsonSerializer.Serialize(error, GliderJsonContext.Default.GliderError);
        _transport.Send($"{{\"type\":\"response\",\"id\":{id},\"result\":null,\"error\":{e}}}");
    }

    public void Publish<T>(string eventName, T data)
    {
        var info = (JsonTypeInfo<T>)GliderJsonContext.Default.GetTypeInfo(typeof(T))!;
        var payload = JsonSerializer.Serialize(data, info);
        _transport.Send($"{{\"type\":\"event\",\"event\":\"{eventName}\",\"data\":{payload}}}");
    }
}
