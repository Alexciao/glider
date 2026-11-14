using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glider.Core.Contracts;

/// <summary>Marks an <see cref="IGliderRpc"/> method with its wire name, e.g. <c>game.install</c>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RpcMethodAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Declares a server-pushed event and its payload type. Applied to <see cref="IGliderRpc"/>.</summary>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = true)]
public sealed class RpcEventAttribute(string name, Type payload) : Attribute
{
    public string Name { get; } = name;
    public Type Payload { get; } = payload;
}

/// <summary>Serializes an enum as camelCase strings on the wire (and in the generated TS unions).</summary>
public sealed class CamelEnumConverter<TEnum>() : JsonStringEnumConverter<TEnum>(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    where TEnum : struct, Enum;

// ---------------------------------------------------------------------------------------------
// Wire envelopes. Web → host: RpcRequest. Host → web: RpcResponse | RpcEvent.
// Every message carries "type" so either side can switch on it cheaply.
// ---------------------------------------------------------------------------------------------

public sealed record RpcRequest(string Type, int Id, string Method, JsonElement? Params);

public sealed record RpcResponse(string Type, int Id, JsonElement? Result, GliderError? Error);

public sealed record RpcEvent(string Type, string Event, JsonElement Data);

/// <summary>Event names. Keep in sync with the [RpcEvent] attributes on <see cref="IGliderRpc"/>.</summary>
public static class RpcEvents
{
    /// <summary>Small app-wide <see cref="AppSnapshot"/>; coalesced, at most ~30/s. The UI replaces its store with it.</summary>
    public const string State = "state";

    /// <summary><see cref="LibraryDelta"/>: per-game upserts/removes with a monotonic revision.</summary>
    public const string Library = "library";

    /// <summary><see cref="JobProgress"/> for the running job; throttled to ~10 Hz.</summary>
    public const string Progress = "progress";

    /// <summary>Transient <see cref="Notice"/> (toast inside the app).</summary>
    public const string Notice = "notice";

    /// <summary><see cref="WindowInfo"/> whenever maximized/focus changes.</summary>
    public const string Window = "window";

    /// <summary><see cref="CaptionEvent"/> from the native Snap Layouts overlay over the HTML maximize button.</summary>
    public const string Caption = "caption";

    /// <summary><see cref="NavigateEvent"/>: show a route (glider:// links, tray, toasts).</summary>
    public const string Navigate = "navigate";

    /// <summary><see cref="StoreState"/> of the native Store view.</summary>
    public const string Store = "store";
}

/// <summary>Thrown by service code to return a structured, human-readable error to the UI.</summary>
public sealed class GliderException : Exception
{
    public GliderError Error { get; }

    public GliderException(GliderError error) : base(error.Message) => Error = error;

    public GliderException(GliderError error, Exception inner) : base(error.Message, inner) => Error = error;

    public GliderException() : this(Errors.Unknown()) { }

    public GliderException(string message) : this(Errors.Unknown(message)) { }

    public GliderException(string message, Exception inner) : this(Errors.Unknown(message), inner) { }
}
