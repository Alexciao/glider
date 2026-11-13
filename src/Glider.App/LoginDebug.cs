using System.Globalization;
using System.Text.Json;
using System.Windows.Threading;
using Glider.Core.Contracts;
using Microsoft.Web.WebView2.Core;

namespace Glider.App;

/// <summary>
/// <c>Glider.exe --login-debug=&lt;dir&gt; [--variant=current|plain|inprivate|noua]</c>: opens the real Epic login window and writes a
/// flushed, line-by-line trail to <c>&lt;dir&gt;/login-debug-&lt;variant&gt;.log</c> (+ <c>shots/</c>) while a person types their own credentials
/// into it. The log holds navigations, the status/path/duration of every response from Epic and its captcha providers, error bodies
/// of failed Epic calls (masked, 600 chars), the NAMES of request headers and JSON keys of Epic API POSTs, page console output and
/// exceptions, and every 2 s the page's visibility/focus and visible error text. Never values, credentials or the authorization code.
/// </summary>
internal sealed class LoginDebug
{
    private static readonly string[] Hosts = ["epicgames.com", "hcaptcha.com", "arkoselabs.com", "funcaptcha.com"];

    private sealed class Req(string url, string method, double start)
    {
        public string Url { get; } = url;
        public string Method { get; } = method;
        public double Start { get; } = start;
        public int Status { get; set; }
        public string Mime { get; set; } = "";
    }

    private readonly string _dir;
    private readonly string _logPath;
    private readonly object _gate = new();
    private readonly Dictionary<string, Req> _reqs = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CoreWebView2? _web;
    private string _lastError = "";
    private int _shots;
    private bool _snapshotting;

    public LoginDebug(string dir, LoginVariant variant, bool noCdp)
    {
        _dir = dir;
        Variant = variant;
        NoCdp = noCdp;
        Directory.CreateDirectory(Path.Combine(dir, "shots"));
        _logPath = Path.Combine(dir, $"login-debug-{variant.ToString().ToLowerInvariant()}{(noCdp ? "-nocdp" : "")}.log");
        _timer.Tick += async (_, _) => await SnapshotAsync();
    }

    public LoginVariant Variant { get; }

    /// <summary>Managed WebView2 events only: no DevTools Network/Runtime/Log session (anti-bot scripts can detect an attached one).</summary>
    public bool NoCdp { get; }

    public void Log(string line)
    {
        try
        {
            lock (_gate)
            {
                File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static void Begin(HostRuntime rt, MainWindow win, string dir, LoginVariant variant, bool noCdp)
    {
        var debug = new LoginDebug(dir, variant, noCdp);
        EpicLogin.Debug = debug;
        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
            try
            {
                // The environment is an STA COM object: read it on the UI thread.
                var version = await rt.Ui.InvokeAsync(async () => (await rt.EnvTask).BrowserVersionString).Task.Unwrap();
                debug.Log($"login-debug start variant={variant} noCdp={noCdp} webview2={version}");
                var code = await rt.Platform.AcquireEpicAuthorizationCodeAsync(cts.Token);
                debug.Log(code is null ? "window closed by user" : $"redirect reached, code length {code.Length}");
            }
            catch (OperationCanceledException)
            {
                debug.Log("hard timeout (15 min), closing");
            }
            catch (GliderException ex)
            {
                debug.Log("login failed: " + LoginDebugRedaction.MaskText(ex.Error.Detail ?? ex.Error.Message, 300));
            }
            catch (Exception ex)
            {
                debug.Log("unexpected: " + LoginDebugRedaction.MaskText(ex.Message, 300));
            }
            finally
            {
                await rt.Ui.InvokeAsync(debug._timer.Stop).Task;
                _ = rt.Ui.BeginInvoke(win.QuitNow);
            }
        });
    }

    /// <summary>UI thread, before the first navigation.</summary>
    public async Task AttachAsync(CoreWebView2 web)
    {
        _web = web;
        Log($"userAgent={LoginDebugRedaction.MaskText(web.Settings.UserAgent, 300)}");
        web.NavigationStarting += (_, e) => Log($"NAV start {LoginDebugRedaction.UrlNoQuery(e.Uri)} user={e.IsUserInitiated} redirect={e.IsRedirected}");
        web.NavigationCompleted += (_, e) => Log($"NAV done {LoginDebugRedaction.UrlNoQuery(web.Source)} success={e.IsSuccess} error={e.WebErrorStatus} http={e.HttpStatusCode}");
        web.FrameNavigationStarting += (_, e) => Log($"FRAME start {LoginDebugRedaction.UrlNoQuery(e.Uri)}");
        web.FrameNavigationCompleted += (_, e) => Log($"FRAME done success={e.IsSuccess} error={e.WebErrorStatus} http={e.HttpStatusCode}");
        web.ProcessFailed += (_, e) => Log($"PROCESS FAILED kind={e.ProcessFailedKind} reason={e.Reason} exit={e.ExitCode}");

        if (NoCdp)
        {
            AttachManaged(web);
            _timer.Start();
            return;
        }

        On(web, "Network.requestWillBeSent", OnRequestWillBeSent);
        On(web, "Network.requestWillBeSentExtraInfo", OnRequestExtraInfo);
        On(web, "Network.responseReceived", OnResponseReceived);
        On(web, "Network.loadingFinished", OnLoadingFinished);
        On(web, "Network.loadingFailed", OnLoadingFailed);
        On(web, "Runtime.consoleAPICalled", OnConsole);
        On(web, "Runtime.exceptionThrown", OnException);
        On(web, "Log.entryAdded", OnLogEntry);
        foreach (var domain in new[] { "Network", "Runtime", "Log" })
        {
            await web.CallDevToolsProtocolMethodAsync(domain + ".enable", "{}");
        }

        _timer.Start();
    }

    /// <summary>
    /// --no-cdp: what the managed API can see after the fact (WebResourceResponseReceived carries the request as sent, so header NAMES
    /// and POST key names are available without a request filter or any DevTools session). No timings, console or exceptions.
    /// </summary>
    private void AttachManaged(CoreWebView2 web)
    {
        Log("no-cdp: managed events only; no console/exception capture, no durations");
        web.WebResourceResponseReceived += async (_, e) =>
        {
            try
            {
                var url = e.Request.Uri;
                if (!IsInteresting(url))
                {
                    return;
                }

                var status = e.Response.StatusCode;
                var method = e.Request.Method;
                var req = new Req(url, method, 0) { Status = status };
                var mime = e.Response.Headers.Contains("Content-Type") ? e.Response.Headers.GetHeader("Content-Type") : "";
                Log($"RES {status} {method} {LoginDebugRedaction.UrlNoQuery(url)} mime={mime}");
                if (IsEpicApiPost(req))
                {
                    var names = e.Request.Headers.Select(h => h.Key.ToLowerInvariant()).Order();
                    var keys = "?";
                    if (e.Request.Content is { } content)
                    {
                        using var ms = new MemoryStream();
                        await content.CopyToAsync(ms);
                        keys = string.Join(",", LoginDebugRedaction.BodyKeyNames(System.Text.Encoding.UTF8.GetString(ms.ToArray())));
                    }

                    var contentType = e.Request.Headers.Contains("Content-Type") ? e.Request.Headers.GetHeader("Content-Type") : "";
                    Log($"REQ POST {LoginDebugRedaction.UrlNoQuery(url)} content-type={contentType} bodyKeys=[{keys}] headerNames=[{string.Join(",", names)}]");
                }

                if (status >= 400 && IsEpic(req))
                {
                    var stream = await e.Response.GetContentAsync();
                    if (stream is not null)
                    {
                        using var sr = new StreamReader(stream);
                        Log($"BODY {status} {LoginDebugRedaction.UrlNoQuery(url)}: {LoginDebugRedaction.RedactBody(await sr.ReadToEndAsync())}");
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException or ObjectDisposedException)
            {
                Log($"(debug: could not read a response: {ex.GetType().Name})");
            }
        };
    }

    private void On(CoreWebView2 web, string name, Action<JsonElement> handler) =>
        web.GetDevToolsProtocolEventReceiver(name).DevToolsProtocolEventReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
                handler(doc.RootElement);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                Log($"(debug: could not read {name}: {ex.GetType().Name})");
            }
        };

    private static bool IsInteresting(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && Hosts.Any(h => u.Host.EndsWith(h, StringComparison.OrdinalIgnoreCase));

    private static bool IsEpicApiPost(Req r) =>
        r.Method == "POST" && Uri.TryCreate(r.Url, UriKind.Absolute, out var u) && u.Host.EndsWith("epicgames.com", StringComparison.OrdinalIgnoreCase)
        && u.AbsolutePath.StartsWith("/id/api/", StringComparison.Ordinal);

    private static bool IsEpic(Req r) => Uri.TryCreate(r.Url, UriKind.Absolute, out var u) && u.Host.EndsWith("epicgames.com", StringComparison.OrdinalIgnoreCase);

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private void OnRequestWillBeSent(JsonElement p)
    {
        var id = Str(p, "requestId");
        var request = p.GetProperty("request");
        var url = Str(request, "url");
        if (p.TryGetProperty("redirectResponse", out var redirect) && _reqs.Remove(id, out var previous))
        {
            var from = Str(redirect, "url") is { Length: > 0 } ru ? ru : previous.Url;
            Log($"RES {LoginDebugRedaction.UrlNoQuery(from)} status={redirect.GetProperty("status").GetInt32()} (redirect)");
        }

        if (!IsInteresting(url))
        {
            return;
        }

        var req = new Req(url, Str(request, "method"), p.GetProperty("timestamp").GetDouble());
        _reqs[id] = req;
        if (!IsEpicApiPost(req))
        {
            return;
        }

        var contentType = "";
        if (request.TryGetProperty("headers", out var headers))
        {
            contentType = headers.EnumerateObject().FirstOrDefault(h => h.Name.Equals("content-type", StringComparison.OrdinalIgnoreCase)).Value.ToString();
        }

        var post = Str(request, "postData");
        if (post.Length > 0)
        {
            Log($"REQ POST {LoginDebugRedaction.UrlNoQuery(url)} content-type={contentType} bodyKeys=[{string.Join(",", LoginDebugRedaction.BodyKeyNames(post))}]");
        }
        else if (request.TryGetProperty("hasPostData", out var has) && has.ValueKind == JsonValueKind.True)
        {
            _ = LogPostBodyAsync(id, url, contentType);
        }
        else
        {
            Log($"REQ POST {LoginDebugRedaction.UrlNoQuery(url)} content-type={contentType} bodyKeys=[]");
        }
    }

    private async Task LogPostBodyAsync(string id, string url, string contentType)
    {
        var keys = "?";
        try
        {
            var r = await _web!.CallDevToolsProtocolMethodAsync("Network.getRequestPostData", JsonSerializer.Serialize(new { requestId = id }));
            using var doc = JsonDocument.Parse(r);
            keys = string.Join(",", LoginDebugRedaction.BodyKeyNames(Str(doc.RootElement, "postData")));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }

        Log($"REQ POST {LoginDebugRedaction.UrlNoQuery(url)} content-type={contentType} bodyKeys=[{keys}]");
    }

    /// <summary>The full set of header names the browser sent (cookies/authorization included as NAMES only).</summary>
    private void OnRequestExtraInfo(JsonElement p)
    {
        if (_reqs.TryGetValue(Str(p, "requestId"), out var req) && IsEpicApiPost(req) && p.TryGetProperty("headers", out var headers))
        {
            Log($"REQ-HEADERS {LoginDebugRedaction.UrlNoQuery(req.Url)} names=[{string.Join(",", headers.EnumerateObject().Select(h => h.Name.ToLowerInvariant()).Where(n => !n.StartsWith(':')).Order())}]");
        }
    }

    private void OnResponseReceived(JsonElement p)
    {
        if (_reqs.TryGetValue(Str(p, "requestId"), out var req))
        {
            var response = p.GetProperty("response");
            req.Status = response.GetProperty("status").GetInt32();
            req.Mime = Str(response, "mimeType");
        }
    }

    private void OnLoadingFinished(JsonElement p)
    {
        var id = Str(p, "requestId");
        if (!_reqs.Remove(id, out var req))
        {
            return;
        }

        var ms = (p.GetProperty("timestamp").GetDouble() - req.Start) * 1000;
        Log($"RES {req.Status} {req.Method} {LoginDebugRedaction.UrlNoQuery(req.Url)} mime={req.Mime} dur={ms:0}ms");
        if (req.Status >= 400 && IsEpic(req))
        {
            _ = LogErrorBodyAsync(id, req);
        }
    }

    private async Task LogErrorBodyAsync(string id, Req req)
    {
        string body;
        try
        {
            var r = await _web!.CallDevToolsProtocolMethodAsync("Network.getResponseBody", JsonSerializer.Serialize(new { requestId = id }));
            using var doc = JsonDocument.Parse(r);
            body = doc.RootElement.TryGetProperty("base64Encoded", out var b64) && b64.ValueKind == JsonValueKind.True
                ? "(binary body)"
                : LoginDebugRedaction.RedactBody(Str(doc.RootElement, "body"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            body = "(body not available)";
        }

        Log($"BODY {req.Status} {LoginDebugRedaction.UrlNoQuery(req.Url)}: {body}");
    }

    private void OnLoadingFailed(JsonElement p)
    {
        if (_reqs.Remove(Str(p, "requestId"), out var req))
        {
            Log($"FAIL {req.Method} {LoginDebugRedaction.UrlNoQuery(req.Url)} error={Str(p, "errorText")} blocked={Str(p, "blockedReason")} canceled={p.TryGetProperty("canceled", out var c) && c.ValueKind == JsonValueKind.True}");
        }
    }

    private void OnConsole(JsonElement p)
    {
        var parts = p.TryGetProperty("args", out var args)
            ? args.EnumerateArray().Select(a => a.TryGetProperty("value", out var v) ? v.ToString() : Str(a, "description"))
            : [];
        Log($"CONSOLE {Str(p, "type")} {LoginDebugRedaction.MaskText(string.Join(" ", parts), 300)}");
    }

    private void OnException(JsonElement p)
    {
        var d = p.GetProperty("exceptionDetails");
        var description = d.TryGetProperty("exception", out var ex) ? Str(ex, "description") : "";
        Log($"EXCEPTION {LoginDebugRedaction.MaskText(Str(d, "text") + " " + description, 300)} at {LoginDebugRedaction.UrlNoQuery(Str(d, "url"))}:{(d.TryGetProperty("lineNumber", out var l) ? l.GetInt32() : 0)}");
    }

    private void OnLogEntry(JsonElement p)
    {
        var e = p.GetProperty("entry");
        Log($"LOGENTRY {Str(e, "level")} {Str(e, "source")} {LoginDebugRedaction.MaskText(Str(e, "text"), 300)} {LoginDebugRedaction.UrlNoQuery(Str(e, "url"))}");
    }

    private const string SnapshotScript = """
        (() => {
          const seen = [];
          for (const el of document.querySelectorAll('[role=alert], .error, [class*=error], [class*=Error]')) {
            if (!el.getClientRects().length) continue;
            const t = (el.innerText || '').trim().slice(0, 300);
            if (t && !seen.includes(t)) seen.push(t);
          }
          return JSON.stringify({ vis: document.visibilityState, focus: document.hasFocus(), err: seen.join(' || ').slice(0, 600) });
        })()
        """;

    private async Task SnapshotAsync()
    {
        if (_snapshotting || _web is not { } web)
        {
            return;
        }

        _snapshotting = true;
        try
        {
            var raw = await web.ExecuteScriptAsync(SnapshotScript);
            var json = JsonSerializer.Deserialize<string>(raw);
            if (json is null)
            {
                return;
            }

            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var err = LoginDebugRedaction.MaskText(Str(r, "err"), 600);
            Log($"SNAP url={LoginDebugRedaction.UrlNoQuery(web.Source)} visibility={Str(r, "vis")} focus={r.GetProperty("focus").GetBoolean()} error=\"{err}\"");
            if (err.Length > 0 && err != _lastError)
            {
                await ShotAsync(web);
            }

            _lastError = err;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
        }
        finally
        {
            _snapshotting = false;
        }
    }

    private async Task ShotAsync(CoreWebView2 web)
    {
        var path = Path.Combine(_dir, "shots", $"{++_shots:000}-{DateTime.Now:HHmmss}.png");
        await using var fs = File.Create(path);
        await web.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, fs);
        Log($"SHOT {Path.GetFileName(path)} (shows what is typed in the page)");
    }
}
