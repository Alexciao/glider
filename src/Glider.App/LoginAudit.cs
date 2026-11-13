using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using Glider.App.Native;
using Microsoft.Web.WebView2.Core;
using DrawingColor = System.Drawing.Color;

namespace Glider.App;

/// <summary>
/// <c>--login-audit=&lt;dir&gt;</c>: diagnostic for "Epic's captcha rejects the embedded login". Loads Epic's login page in (1) a plain
/// WebView2 like the 0.1.3 window, (2) the login window's current configuration and (3) a real Microsoft Edge started with a throwaway
/// profile, dumps the browser fingerprint and the request headers Epic receives, and writes the differences. No credentials are
/// involved; the page is only loaded, never interacted with. Writes audit-*.json and audit-diff.txt, then quits.
/// </summary>
internal static class LoginAudit
{
    private const string PageUrl = "https://www.epicgames.com/id/login";

    private const string Probe = """
        (async () => {
          const o = {};
          const t = (k, f) => { try { o[k] = f(); } catch (e) { o[k] = 'ERR ' + e.message; } };
          const frame = document.createElement('iframe'); frame.src = 'https://www.hcaptcha.com/'; frame.style.display = 'none'; document.body.appendChild(frame);
          await new Promise(r => setTimeout(r, 4000));
          t('webdriver', () => String(navigator.webdriver));
          t('userAgent', () => navigator.userAgent);
          t('platform', () => navigator.platform);
          t('vendor', () => navigator.vendor);
          t('language', () => navigator.language);
          t('languages', () => navigator.languages.join(','));
          t('plugins', () => navigator.plugins.length);
          t('mimeTypes', () => navigator.mimeTypes.length);
          t('pdfViewerEnabled', () => String(navigator.pdfViewerEnabled));
          t('hardwareConcurrency', () => navigator.hardwareConcurrency);
          t('deviceMemory', () => navigator.deviceMemory);
          t('maxTouchPoints', () => navigator.maxTouchPoints);
          t('cookieEnabled', () => String(navigator.cookieEnabled));
          t('chromeKeys', () => window.chrome ? Object.getOwnPropertyNames(window.chrome).sort().join(',') : 'undefined');
          t('chromeWebview', () => String(!!(window.chrome && window.chrome.webview)));
          t('chromeRuntime', () => String(!!(window.chrome && window.chrome.runtime)));
          t('uaBrands', () => JSON.stringify(navigator.userAgentData.brands));
          t('uaMobile', () => String(navigator.userAgentData.mobile));
          t('uaPlatform', () => navigator.userAgentData.platform);
          try {
            const h = await navigator.userAgentData.getHighEntropyValues(['architecture','bitness','model','platformVersion','fullVersionList','uaFullVersion','wow64','formFactors']);
            o.uaHigh = JSON.stringify(h);
          } catch (e) { o.uaHigh = 'ERR ' + e.message; }
          t('inner', () => innerWidth + 'x' + innerHeight);
          t('outer', () => outerWidth + 'x' + outerHeight);
          t('screen', () => screen.width + 'x' + screen.height + ' avail ' + screen.availWidth + 'x' + screen.availHeight + ' depth ' + screen.colorDepth);
          t('devicePixelRatio', () => devicePixelRatio);
          t('visibility', () => document.visibilityState);
          t('hasFocus', () => String(document.hasFocus()));
          t('timezone', () => Intl.DateTimeFormat().resolvedOptions().timeZone);
          t('notification', () => typeof Notification === 'undefined' ? 'undefined' : Notification.permission);
          t('webgl', () => {
            const gl = document.createElement('canvas').getContext('webgl');
            if (!gl) return 'none';
            const e = gl.getExtension('WEBGL_debug_renderer_info');
            return e ? gl.getParameter(e.UNMASKED_VENDOR_WEBGL) + ' | ' + gl.getParameter(e.UNMASKED_RENDERER_WEBGL) : 'no debug info';
          });
          t('localStorage', () => { localStorage.setItem('x', '1'); const v = localStorage.getItem('x'); localStorage.removeItem('x'); return v; });
          t('indexedDB', () => String(!!window.indexedDB));
          t('cookieWrite', () => { document.cookie = 'glider_audit=1; SameSite=None; Secure; path=/'; const ok = document.cookie.includes('glider_audit=1'); document.cookie = 'glider_audit=; Max-Age=0; path=/'; return String(ok); });
          t('hasStorageAccess', () => typeof document.hasStorageAccess);
          t('automationGlobals', () => Object.getOwnPropertyNames(window).filter(k => /cdc_|webdriver|__nightmare|_phantom|callPhantom|__selenium/i.test(k)).join(',') || 'none');
          t('permissionsQueryNotification', () => 'n/a');
          return JSON.stringify(o);
        })()
        """;

    private static readonly string[] HeaderNames =
    [
        "user-agent", "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform", "accept-language", "accept", "accept-encoding",
        "sec-fetch-site", "sec-fetch-mode", "sec-fetch-dest", "upgrade-insecure-requests", "cache-control",
    ];

    public static void Begin(HostRuntime rt, MainWindow win, string dir)
    {
        Directory.CreateDirectory(dir);
        _ = Task.Run(async () =>
        {
            var all = new Dictionary<string, SortedDictionary<string, string>>();
            try
            {
                var env = await rt.EnvTask;
                foreach (var (name, fixedMode) in new[] { ("webview2-raw", false), ("webview2-login", true) })
                {
                    all[name] = await Guard(name, () => rt.Ui.InvokeAsync(() => WebView2PassAsync(env, name, fixedMode)).Task.Unwrap());
                }

                all["edge"] = await Guard("edge", () => EdgePassAsync());
            }
            catch (Exception ex)
            {
                all["fatal"] = new SortedDictionary<string, string> { ["error"] = ex.ToString() };
            }
            finally
            {
                foreach (var (name, values) in all)
                {
                    File.WriteAllText(Path.Combine(dir, $"audit-{name}.json"), JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
                }

                File.WriteAllText(Path.Combine(dir, "audit-diff.txt"), Diff(all));
                _ = rt.Ui.BeginInvoke(win.QuitNow);
            }
        });
    }

    private static async Task<SortedDictionary<string, string>> Guard(string name, Func<Task<SortedDictionary<string, string>>> pass)
    {
        try
        {
            return await pass();
        }
        catch (Exception ex)
        {
            return new SortedDictionary<string, string> { ["pass.error"] = $"{name}: {ex.Message}" };
        }
    }

    private static string Diff(Dictionary<string, SortedDictionary<string, string>> all)
    {
        var sb = new StringBuilder();
        if (!all.TryGetValue("edge", out var edge))
        {
            return "no edge pass\n";
        }

        foreach (var name in all.Keys.Where(k => k.StartsWith("webview2", StringComparison.Ordinal)))
        {
            sb.AppendLine($"== {name} vs edge (differences only) ==");
            foreach (var key in all[name].Keys.Union(edge.Keys).Order())
            {
                var a = all[name].GetValueOrDefault(key, "<missing>");
                var b = edge.GetValueOrDefault(key, "<missing>");
                if (a != b)
                {
                    sb.AppendLine($"{key}\n    {name}: {a}\n    edge: {b}");
                }
            }
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------- WebView2

    private static async Task<SortedDictionary<string, string>> WebView2PassAsync(CoreWebView2Environment env, string profile, bool login)
    {
        var window = new Window { Title = "Glider login audit", Width = 540, Height = 820, Left = 80, Top = 40, ShowInTaskbar = false };
        window.Show();
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        try
        {
            var options = env.CreateCoreWebView2ControllerOptions();
            options.ProfileName = login ? HostConstants.WebViewProfileEpic + "Audit" : "GliderAuditRaw";
            var controller = await env.CreateCoreWebView2ControllerAsync(hwnd, options);
            controller.DefaultBackgroundColor = DrawingColor.FromArgb(unchecked((int)ThemeState.BackgroundArgb));
            Win32.GetClientRect(hwnd, out var rc);
            controller.Bounds = new System.Drawing.Rectangle(0, 0, rc.Width, rc.Height);
            controller.IsVisible = true;
            var web = controller.CoreWebView2;
            if (login)
            {
                await EpicLogin.ConfigureAsync(controller);
            }

            var result = new SortedDictionary<string, string>();
            await web.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
            web.GetDevToolsProtocolEventReceiver("Network.requestWillBeSentExtraInfo").DevToolsProtocolEventReceived += (_, e) => CaptureHeaders(result, e.ParameterObjectAsJson);
            var loaded = new TaskCompletionSource();
            web.NavigationCompleted += (_, _) => loaded.TrySetResult();
            web.Navigate(PageUrl);
            await Task.WhenAny(loaded.Task, Task.Delay(30_000));
            await Task.Delay(6000); // let Epic's scripts (and its captcha loader) run
            // ExecuteScriptAsync does not await promises; Runtime.evaluate does (same call as the Edge pass).
            var eval = await web.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = Probe, awaitPromise = true, returnByValue = true }));
            MergeEvaluate(result, eval);
            result["page.url"] = UrlPath(web.Source);
            controller.Close();
            return result;
        }
        finally
        {
            window.Close();
        }
    }

    // ---------------------------------------------------------------- real Edge

    private static async Task<SortedDictionary<string, string>> EdgePassAsync()
    {
        var exe = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
        }.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("msedge.exe not found");
        var profile = Path.Combine(Path.GetTempPath(), "glider-audit-edge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(profile);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in new[] { "--user-data-dir=" + profile, "--remote-debugging-port=0", "--no-first-run", "--no-default-browser-check", "--window-size=540,820", "--window-position=700,40", "about:blank" })
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Edge did not start");
        try
        {
            var portFile = Path.Combine(profile, "DevToolsActivePort");
            for (var i = 0; i < 150 && !File.Exists(portFile); i++)
            {
                await Task.Delay(100);
            }

            var lines = (await ReadShared(portFile)).Split('\n', StringSplitOptions.TrimEntries);
            var port = int.Parse(lines[0]);
            using var http = new HttpClient();
            string? wsUrl = null;
            for (var i = 0; i < 50 && wsUrl is null; i++)
            {
                using var doc = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/list"));
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    if (t.GetProperty("type").GetString() == "page")
                    {
                        wsUrl = t.GetProperty("webSocketDebuggerUrl").GetString();
                        break;
                    }
                }

                await Task.Delay(200);
            }

            var result = new SortedDictionary<string, string>();
            await using var cdp = await Cdp.ConnectAsync(wsUrl ?? throw new InvalidOperationException("no Edge tab"));
            cdp.OnEvent += (method, json) =>
            {
                if (method == "Network.requestWillBeSentExtraInfo")
                {
                    CaptureHeaders(result, json);
                }
            };
            await cdp.CallAsync("Network.enable", "{}");
            await cdp.CallAsync("Page.enable", "{}");
            await cdp.CallAsync("Page.navigate", $$"""{"url":"{{PageUrl}}"}""");
            await Task.Delay(12_000);
            var eval = await cdp.CallAsync("Runtime.evaluate", JsonSerializer.Serialize(new { expression = Probe, awaitPromise = true, returnByValue = true }));
            MergeEvaluate(result, eval);

            try
            {
                await cdp.CallAsync("Browser.close", "{}");
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
            {
            }

            return result;
        }
        finally
        {
            // By PID only: the user's own Edge may be running.
            try
            {
                if (!process.WaitForExit(3000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch (InvalidOperationException)
            {
            }

            await Task.Delay(500);
            try
            {
                Directory.Delete(profile, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<string> ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        return await sr.ReadToEndAsync();
    }

    // ---------------------------------------------------------------- shared

    private static string UrlPath(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : url;

    private static void MergeEvaluate(SortedDictionary<string, string> into, string evaluateResultJson)
    {
        using var outer = JsonDocument.Parse(evaluateResultJson);
        var inner = outer.RootElement.GetProperty("result").GetProperty("value").GetString() ?? "{}";
        using var doc = JsonDocument.Parse(inner);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            into["js." + p.Name] = p.Value.ToString();
        }
    }

    /// <summary>First main-document request for Epic's login page: the headers a server sees (client hints included).</summary>
    private static void CaptureHeaders(SortedDictionary<string, string> into, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("headers", out var headers))
        {
            return;
        }

        var map = headers.EnumerateObject().ToDictionary(p => p.Name.ToLowerInvariant(), p => p.Value.GetString() ?? "");
        if (map.GetValueOrDefault(":authority", "").EndsWith("hcaptcha.com", StringComparison.Ordinal) && map.GetValueOrDefault(":path") == "/" && !into.ContainsKey("ifr.user-agent"))
        {
            // a cross-site iframe (what Epic's captcha is): does it send the same client hints as the top page?
            into["ifr.user-agent"] = map.GetValueOrDefault("user-agent", "<absent>");
            into["ifr.sec-ch-ua"] = map.GetValueOrDefault("sec-ch-ua", "<absent>");
            into["ifr.accept-language"] = map.GetValueOrDefault("accept-language", "<absent>");
            return;
        }

        if (!map.TryGetValue(":path", out var path) || !path.StartsWith("/id/login", StringComparison.Ordinal) || into.ContainsKey("hdr.user-agent"))
        {
            return;
        }

        foreach (var name in HeaderNames)
        {
            into["hdr." + name] = map.GetValueOrDefault(name, "<absent>");
        }

        into["hdr.names"] = string.Join(",", map.Keys.Where(k => !k.StartsWith(':')).Order());
    }

    private sealed class Cdp : IAsyncDisposable
    {
        private readonly ClientWebSocket _ws = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = new();
        private int _id;
        private Task _pump = Task.CompletedTask;

        public event Action<string, string>? OnEvent;

        public static async Task<Cdp> ConnectAsync(string url)
        {
            var c = new Cdp();
            await c._ws.ConnectAsync(new Uri(url), CancellationToken.None);
            c._pump = Task.Run(c.PumpAsync);
            return c;
        }

        public async Task<string> CallAsync(string method, string paramsJson)
        {
            var id = Interlocked.Increment(ref _id);
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            await _ws.SendAsync(Encoding.UTF8.GetBytes($$"""{"id":{{id}},"method":"{{method}}","params":{{paramsJson}}}"""), WebSocketMessageType.Text, true, CancellationToken.None);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }

        private async Task PumpAsync()
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (_ws.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await _ws.ReceiveAsync(buffer, CancellationToken.None);
                        ms.Write(buffer, 0, r.Count);
                    }
                    while (!r.EndOfMessage);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    using var doc = JsonDocument.Parse(ms.ToArray());
                    var root = doc.RootElement;
                    if (root.TryGetProperty("id", out var id) && _pending.TryRemove(id.GetInt32(), out var tcs))
                    {
                        tcs.TrySetResult(root.TryGetProperty("result", out var res) ? res.GetRawText() : "{}");
                    }
                    else if (root.TryGetProperty("method", out var m))
                    {
                        OnEvent?.Invoke(m.GetString() ?? "", root.TryGetProperty("params", out var p) ? p.GetRawText() : "{}");
                    }
                }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or JsonException)
            {
            }
            finally
            {
                foreach (var tcs in _pending.Values)
                {
                    tcs.TrySetCanceled();
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
                }
            }
            catch (WebSocketException)
            {
            }

            await Task.WhenAny(_pump, Task.Delay(1000));
            _ws.Dispose();
        }
    }
}
