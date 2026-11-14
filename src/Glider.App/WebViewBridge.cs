using System.Windows.Threading;
using Glider.Core.Contracts;
using Microsoft.Web.WebView2.Core;

namespace Glider.App;

/// <summary>
/// The host end of the IPC bridge. Outbound: <see cref="Send"/> may be called from any thread; messages are queued,
/// folded by <see cref="BridgeQueue"/> (latest-wins per event kind; while the page is suspended also library deltas, capped notices) and
/// posted from the UI thread in time-boxed batches so a flood never blocks rendering. Inbound: <see cref="HandleIncoming"/> routes UI → host messages.
/// </summary>
internal sealed class WebViewBridge : IRpcTransport
{
    private static readonly TimeSpan BatchBudget = TimeSpan.FromMilliseconds(6);

    private readonly Dispatcher _ui;
    private readonly object _gate = new();
    private readonly BridgeQueue _queue = new();
    private bool _scheduled;
    private CoreWebView2? _web;

    public WebViewBridge(Dispatcher ui) => _ui = ui;

    /// <summary>Raised on the UI thread for the first "app.ready" request (before it is dispatched to Core).</summary>
    public event Action? ReadyReceived;

    /// <summary>Raised on the UI thread for {"type":"host",...} messages (host-only chrome/theme control).</summary>
    public event Action<System.Text.Json.JsonElement>? HostMessageReceived;

    /// <summary>Set once the RPC dispatcher (or, when Core failed to start, its stand-in) exists; requests arriving earlier wait for it.</summary>
    public Task<Func<string, Task>>? Dispatcher { get; set; }

    public void Attach(CoreWebView2 web)
    {
        lock (_gate)
        {
            _web = web;
        }

        Schedule();
    }

    public void Detach()
    {
        lock (_gate)
        {
            _web = null;
        }
    }

    /// <summary>
    /// A new page document is about to replace the current one (renderer crash/reload, new controller): whatever is still queued was
    /// meant for the old page, and the new one loads its state fresh, so it is dropped.
    /// </summary>
    public void BeginNewPage()
    {
        lock (_gate)
        {
            _queue.Clear();
        }
    }

    /// <summary>While held (WebView2 suspended / not yet navigated) messages accumulate (folded, see <see cref="BridgeQueue"/>) instead of being posted.</summary>
    public void Hold(bool hold)
    {
        lock (_gate)
        {
            _queue.Held = hold;
        }

        if (!hold)
        {
            Schedule();
        }
    }

    public void Send(string json)
    {
        lock (_gate)
        {
            _queue.Enqueue(json);
        }

        Schedule();
    }

    private void Schedule()
    {
        lock (_gate)
        {
            if (_scheduled || _queue.Count == 0 || _web is null || _queue.Held)
            {
                return;
            }

            _scheduled = true;
        }

        _ui.BeginInvoke(DispatcherPriority.Normal, Pump);
    }

    private void Pump()
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            string json;
            CoreWebView2? web;
            lock (_gate)
            {
                web = _web;
                if (web is null || _queue.Held || !_queue.TryDequeue(out json!))
                {
                    _scheduled = false;
                    return;
                }
            }

            try
            {
                web.PostWebMessageAsJson(json);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
            {
                HostLog.Warn("PostWebMessageAsJson failed (webview closing?)", ex);
            }

            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) > BatchBudget)
            {
                lock (_gate)
                {
                    _scheduled = _queue.Count > 0;
                }

                if (_scheduled)
                {
                    _ui.BeginInvoke(DispatcherPriority.Background, Pump);
                }

                return;
            }
        }
    }

    /// <summary>UI → host. Runs on the UI thread; Core work is moved off it immediately.</summary>
    public void HandleIncoming(string json)
    {
        // "type" is always the first key in messages built by the frontend client (and by JSON.stringify of its literals).
        if (json.StartsWith("{\"type\":\"host\"", StringComparison.Ordinal))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                HostMessageReceived?.Invoke(doc.RootElement.Clone());
            }
            catch (System.Text.Json.JsonException ex)
            {
                HostLog.Warn("Bad host message", ex);
            }

            return;
        }

        if (json.Contains("\"method\":\"app.ready\"", StringComparison.Ordinal))
        {
            ReadyReceived?.Invoke();
        }

        var pending = Dispatcher;
        _ = Task.Run(async () =>
        {
            try
            {
                if (pending is null)
                {
                    HostLog.Warn("Message arrived before the RPC dispatcher was created; dropped");
                    return;
                }

                var handle = await pending.ConfigureAwait(false);
                await handle(json).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HostLog.Error("RPC dispatch failed", ex);
            }
        });
    }
}

/// <summary>Breaks the construction cycle dispatcher → service → event sink → dispatcher.</summary>
internal sealed class ForwardingEventSink : IEventSink
{
    private volatile IEventSink? _target;

    public void Attach(IEventSink target) => _target = target;

    /// <summary>Host-side observers (the tray) see every event as it is published, on the publisher's thread: keep handlers tiny.</summary>
    public event Action<string, object?>? Tapped;

    public void Publish<T>(string eventName, T data)
    {
        Tapped?.Invoke(eventName, data);
        _target?.Publish(eventName, data);
    }
}
