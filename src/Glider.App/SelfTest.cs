using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glider.App.Native;

namespace Glider.App;

/// <summary>
/// <c>--selftest[=dir]</c>: a bounded, self-terminating verification run (hit-test probes, PrintWindow screenshots, cache serving,
/// maximize, hide/suspend memory, Epic login window smoke test). Writes selftest.json + PNGs to the directory, then quits.
/// Never sends synthetic keyboard input; the cursor is only moved when GLIDER_SELFTEST_HOVER=1 (Snap Layouts check).
/// </summary>
internal static partial class SelfTest
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint hdc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleBitmap(nint hdc, int w, int h);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint hdc, nint obj);
    [LibraryImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint hdc);
    [LibraryImport("gdi32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static partial bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [LibraryImport("gdi32.dll")] private static unsafe partial int GetDIBits(nint hdc, nint bmp, uint start, uint lines, byte* bits, BITMAPINFOHEADER* bi, uint usage);

    private static readonly Dictionary<string, object?> Report = [];
    private static readonly Dictionary<string, JsonElement> JsMessages = [];

    public static void Begin(HostRuntime rt, MainWindow win, string dir)
    {
        rt.Bridge.HostMessageReceived += m =>
        {
            if (m.TryGetProperty("op", out var op) && op.GetString() == "selftest" && m.TryGetProperty("key", out var k) && m.TryGetProperty("value", out var v))
            {
                lock (JsMessages)
                {
                    JsMessages[k.GetString()!] = v.Clone();
                }
            }
        };
        Directory.CreateDirectory(dir);
        win.BeforeReveal = () =>
        {
            Screenshot(win, Path.Combine(dir, "first-frame.png"), "firstFrame");
            Win32.GetWindowRect(win.Hwnd, out var wr);
            Report["firstFrame.cloaked"] = Win32.DwmGetWindowAttribute(win.Hwnd, Win32.DWMWA_CLOAKED, out var c, 4) == 0 ? c : -1;
        };
        _ = Task.Run(() => RunAsync(rt, win, dir));
    }

    private static async Task RunAsync(HostRuntime rt, MainWindow win, string dir)
    {
        Directory.CreateDirectory(dir);
        try
        {
            for (var i = 0; i < 200 && !StartupTimeline.Has("revealed"); i++)
            {
                await Task.Delay(50);
            }

            await Task.Delay(1500);
            await Step("state", () => Ui(rt, () => StateAndHitTests(win)));
            await Step("pixels", () => Ui(rt, () => Screenshot(win, Path.Combine(dir, "normal.png"), "normal")));
            await Step("cache", () => CacheTest(rt, win));
            await Step("art", () => ArtTest(rt, win));
            await Step("maximize", () => MaximizeTest(rt, win, dir));
            await Step("hover", () => HoverTest(rt, win, dir));
            await Step("toast", () => Ui(rt, () => rt.Platform.ShowToast("Glider self-test", "Host toast via Shell_NotifyIcon (safe to ignore).")));
            await Step("placement", () => Ui(rt, () => PlacementTest(rt, win)));
            await Step("store", () => StoreTest(rt, win, dir));
            await Step("store-hidden", () => StoreHiddenTest(rt, win));
            await Step("login", () => LoginTest(rt, dir));
            await Step("close", () => CloseTest(rt, win));
            await Step("hide-suspend", () => HideSuspendTest(rt, win));
        }
        catch (Exception ex)
        {
            Report["fatal"] = ex.ToString();
        }
        finally
        {
            File.WriteAllText(Path.Combine(dir, "selftest.json"), JsonSerializer.Serialize(Report, new JsonSerializerOptions { WriteIndented = true }));
            _ = rt.Ui.BeginInvoke(win.QuitNow);
        }
    }

    private static readonly HashSet<string>? Only = Environment.GetEnvironmentVariable("GLIDER_SELFTEST_ONLY") is { Length: > 0 } list
        ? [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
        : null;

    private static async Task Step(string name, Func<Task> body)
    {
        if (Only is not null && !Only.Contains(name))
        {
            return; // GLIDER_SELFTEST_ONLY=art,cache runs just those steps
        }

        try
        {
            await body();
        }
        catch (Exception ex)
        {
            Report[name + ".error"] = ex.ToString();
        }
    }

    private static Task Ui(HostRuntime rt, Action a) => rt.Ui.InvokeAsync(a).Task;

    // ------------------------------------------------------------------ geometry + hit tests

    private static string Who(MainWindow win, nint h) =>
        h == win.Hwnd ? "top-level" : h == win.Overlays?.MaxHandle ? "max-overlay" : h == win.Overlays?.TopHandle ? "top-overlay" : h == 0 ? "none" : "child";

    /// <summary>Emulates the system's hit-test walk: deepest window under the point, then up through HTTRANSPARENT answers.</summary>
    private static string HitAt(MainWindow win, int x, int y)
    {
        var h = Win32.WindowFromPoint(new POINT { X = x, Y = y });
        var guard = 0;
        while (h != 0 && guard++ < 6)
        {
            var r = (int)Win32.SendMessage(h, Win32.WM_NCHITTEST, 0, Win32.MakeLParam(x, y));
            if (r != Win32.HTTRANSPARENT)
            {
                return $"{Who(win, h)}:{r}";
            }

            h = h == win.Hwnd ? 0 : win.Hwnd;
        }

        return "none";
    }

    private static void StateAndHitTests(MainWindow win)
    {
        var h = win.Hwnd;
        Win32.GetWindowRect(h, out var wr);
        Win32.GetClientRect(h, out var cr);
        var origin = new POINT();
        Win32.ClientToScreen(h, ref origin);
        Win32.DwmGetWindowAttributeRect(h, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out var ext, Marshal.SizeOf<RECT>());
        Win32.DwmGetWindowAttribute(h, Win32.DWMWA_CLOAKED, out var cloaked, 4);
        var dpi = Win32.GetDpiForWindow(h);
        var s = dpi / 96.0;

        Report["windowRect"] = $"{wr.Left},{wr.Top},{wr.Right},{wr.Bottom}";
        Report["visibleFrameBounds"] = $"{ext.Left},{ext.Top},{ext.Right},{ext.Bottom}";
        Report["clientScreenRect"] = $"{origin.X},{origin.Y},{origin.X + cr.Width},{origin.Y + cr.Height}";
        Report["dpi"] = dpi;
        Report["cloakedAfterReveal"] = cloaked;
        Report["mica"] = win.IsMicaActive;
        Report["revealed"] = win.IsRevealed;
        var maxCx = origin.X + cr.Width - (int)((HostConstants.MaxButtonRightInset + HostConstants.CaptionButtonWidth / 2) * s);
        var midY = wr.Top + wr.Height / 2;
        var midX = wr.Left + wr.Width / 2;
        Report["hit.leftOutside"] = HitAt(win, ext.Left - 3, midY);
        Report["hit.rightOutside"] = HitAt(win, ext.Right + 3, midY);
        Report["hit.bottomOutside"] = HitAt(win, midX, ext.Bottom + 3);
        Report["hit.bottomLeftCorner"] = HitAt(win, ext.Left - 3, ext.Bottom + 3);
        Report["hit.topBand1px"] = HitAt(win, midX, wr.Top);
        Report["hit.topStrip"] = HitAt(win, midX, origin.Y + 2);
        Report["hit.topLeftStrip"] = HitAt(win, origin.X + 4, origin.Y + 2);
        Report["hit.maxButtonCenter"] = HitAt(win, maxCx, origin.Y + (int)(20 * s));
        Report["hit.closeButtonCenter"] = HitAt(win, origin.X + cr.Width - (int)(23 * s), origin.Y + (int)(20 * s));
        Report["hit.dragRegion"] = HitAt(win, origin.X + 300, origin.Y + (int)(20 * s));
        Report["hit.content"] = HitAt(win, midX, midY);
    }

    // ------------------------------------------------------------------ pixels

    private static unsafe byte[] Capture(Func<nint, bool> draw, int w, int h)
    {
        var screen = Win32.GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var bmp = CreateCompatibleBitmap(screen, w, h);
        var old = SelectObject(mem, bmp);
        var pixels = new byte[w * h * 4];
        try
        {
            draw(mem);
            var bi = new BITMAPINFOHEADER { Size = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), Width = w, Height = -h, Planes = 1, BitCount = 32 };
            fixed (byte* p = pixels)
            {
                _ = GetDIBits(mem, bmp, 0, (uint)h, p, &bi, 0);
            }
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            _ = Win32.ReleaseDC(0, screen);
        }

        return pixels;
    }

    private static void SavePng(string path, byte[] bgra, int w, int h)
    {
        for (var i = 3; i < bgra.Length; i += 4)
        {
            bgra[i] = 255;
        }

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4)));
        using var f = File.Create(path);
        enc.Save(f);
    }

    private static string Px(byte[] bgra, int w, int x, int y)
    {
        var i = (y * w + x) * 4;
        return $"{bgra[i + 2]},{bgra[i + 1]},{bgra[i]}";
    }

    private static void Screenshot(MainWindow win, string path, string label)
    {
        Win32.GetWindowRect(win.Hwnd, out var wr);
        var w = wr.Width;
        var h = wr.Height;
        var px = Capture(dc => Win32.PrintWindow(win.Hwnd, dc, 2 /* PW_RENDERFULLCONTENT */), w, h);
        SavePng(path, px, w, h);
        var s = Win32.GetDpiForWindow(win.Hwnd) / 96.0;
        // pixel just left of / inside the max button overlay vs. the page's own caption background
        var inset = (int)((HostConstants.MaxButtonRightInset + HostConstants.CaptionButtonWidth / 2) * s);
        var bx = w - inset - (w - Math.Min(w, w)); // window-relative x of overlay centre (client == window except 1px/8px frame)
        Report[label + ".px.maxButtonCenter"] = Px(px, w, Math.Clamp(bx, 0, w - 1), (int)(20 * s));
        Report[label + ".px.beforeMaxButton"] = Px(px, w, Math.Clamp(bx - (int)(120 * s), 0, w - 1), (int)(20 * s));
        Report[label + ".px.contentBackground"] = Px(px, w, w / 2, h - 30);
        Report[label + ".px.topEdgeRow1"] = Px(px, w, w / 2, 1);
        Report[label + ".size"] = $"{w}x{h}";

        // What DWM actually composed on screen for this window (needs the window to be unobscured), for comparison with PrintWindow.
        var screenPx = Capture(dc => { var sdc = Win32.GetDC(0); var ok = BitBlt(dc, 0, 0, w, h, sdc, wr.Left, wr.Top, 0x00CC0020 | 0x40000000); _ = Win32.ReleaseDC(0, sdc); return ok; }, w, h);
        SavePng(path.Replace(".png", "-screen.png"), screenPx, w, h);
        Report[label + ".screen.px.maxButtonCenter"] = Px(screenPx, w, Math.Clamp(bx, 0, w - 1), (int)(20 * s));
        Report[label + ".screen.px.beforeMaxButton"] = Px(screenPx, w, Math.Clamp(bx - (int)(120 * s), 0, w - 1), (int)(20 * s));
        Report[label + ".screen.px.topEdgeRow1"] = Px(screenPx, w, w / 2, 1);
    }

    // ------------------------------------------------------------------ cache + navigation

    private static async Task CacheTest(HostRuntime rt, MainWindow win)
    {
        Directory.CreateDirectory(Path.Combine(rt.Paths.Cache, "art"));
        await File.WriteAllTextAsync(Path.Combine(rt.Paths.Cache, "art", "selftest.txt"), "hello-cache");
        await File.WriteAllTextAsync(Path.Combine(rt.Paths.State, "secret-selftest.txt"), "must-not-leak");
        // 1x1 PNG so <img> loading can be checked as well as fetch (CORS) and a canvas read-back (tainting).
        await File.WriteAllBytesAsync(Path.Combine(rt.Paths.Cache, "art", "px.png"), Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));
        const string js = """
            (async()=>{const out={};
              for (const u of ['https://cache.glider/art/selftest.txt','https://cache.glider/nope.txt','https://cache.glider/..%2Fstate%2Fsecret-selftest.txt','https://cache.glider/%2e%2e/state/secret-selftest.txt','/zzz/nope.txt','/index.html']) {
                try { const r = await fetch(u); out[u] = r.status + ':' + (await r.text()).slice(0,24).replace(/\s+/g,' '); } catch (e) { out[u] = 'ERR ' + e; }
              }
              out.img = await new Promise(res => { const i = new Image(); i.onload = () => { try { const c = document.createElement('canvas'); c.width = c.height = 1; const x = c.getContext('2d'); x.drawImage(i,0,0); x.getImageData(0,0,1,1); res('loaded, canvas readable'); } catch (e) { res('loaded, canvas tainted: ' + e.name); } }; i.onerror = () => res('img error'); i.src = 'https://cache.glider/art/px.png'; });
              chrome.webview.postMessage({type:'host',op:'selftest',key:'fetch',value:out});
            })()
            """;
        await rt.Ui.InvokeAsync(() => win.Web!.ExecuteScriptAsync(js)).Task.Unwrap();
        for (var i = 0; i < 40; i++)
        {
            lock (JsMessages)
            {
                if (JsMessages.TryGetValue("fetch", out var v))
                {
                    Report["cache.fetch"] = JsonSerializer.Deserialize<Dictionary<string, string>>(v.GetRawText());
                    break;
                }
            }

            await Task.Delay(100);
        }
    }

    // ------------------------------------------------------------------ art.glider (lazy art cache served by WebResourceRequested)

    /// <summary>
    /// Seeds one cached picture (no catalog, no network), then loads it the way the UI does (&lt;img&gt;, fetch, canvas read-back), plus a
    /// miss, a malformed URL and a traversal attempt. Also exercises the WPF image processor (cover colour, downscale) on a real JPEG.
    /// </summary>
    private static async Task ArtTest(HostRuntime rt, MainWindow win)
    {
        const string source = "https://cdn1.epicgames.com/selftest/source.jpg";
        var v = Glider.Core.Art.ArtUrls.HashOf(source);
        var jpeg = MakeJpeg(800, 1200, 200, 40, 40);
        var dir = Path.Combine(rt.Paths.Cache, "art", "SelfTestApp");
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, $"tall-thumb-{v}.jpg"), jpeg);

        var processor = new WpfImageProcessor();
        var facts = processor.Analyze(jpeg);
        Report["art.processor.facts"] = facts is null ? "null" : $"{facts.Width}x{facts.Height} {facts.DominantColor}";
        var small = processor.Downscale(jpeg, 300);
        var smallFacts = small is null ? null : processor.Analyze(small);
        Report["art.processor.downscale"] = smallFacts is null ? "null" : $"{smallFacts.Width}x{smallFacts.Height}, {small!.Length} bytes (from {jpeg.Length})";
        Report["art.processor.garbage"] = processor.Analyze("not an image"u8) is null ? "null (ok)" : "decoded?!";

        var good = $"https://art.glider/SelfTestApp/tall-thumb?v={v}";
        var js = $$"""
            (async()=>{const out={};
              const t0 = performance.now();
              try { const r = await fetch('{{good}}'); const b = await r.blob(); out.hit = r.status + ' ' + r.headers.get('content-type') + ' ' + b.size + ' cc=' + r.headers.get('cache-control'); } catch (e) { out.hit = 'ERR ' + e; }
              out.hitMs = Math.round(performance.now() - t0);
              out.img = await new Promise(res => { const i = new Image(); i.onload = () => { try { const c = document.createElement('canvas'); c.width = c.height = 1; const x = c.getContext('2d'); x.drawImage(i,0,0,1,1); const d = x.getImageData(0,0,1,1).data; res('loaded ' + i.naturalWidth + 'x' + i.naturalHeight + ' px=' + d[0] + ',' + d[1] + ',' + d[2]); } catch (e) { res('loaded ' + i.naturalWidth + 'x' + i.naturalHeight + ', canvas tainted: ' + e.name); } }; i.onerror = () => res('img error'); i.src = '{{good}}'; });
              const t1 = performance.now();
              for (const [k, u] of Object.entries({missGame:'https://art.glider/NoSuchGame/tall?v=0123456789', missVariant:'https://art.glider/SelfTestApp/wide', badKind:'https://art.glider/SelfTestApp/huge', escaped:'https://art.glider/..%2Fstate/tall', otherHost:'https://art.glider.example/SelfTestApp/tall'})) {
                try { const r = await fetch(u); out[k] = r.status; } catch (e) { out[k] = 'ERR'; }
              }
              out.missesMs = Math.round(performance.now() - t1);
              chrome.webview.postMessage({type:'host',op:'selftest',key:'art',value:out});
            })()
            """;
        await rt.Ui.InvokeAsync(() => win.Web!.ExecuteScriptAsync(js)).Task.Unwrap();
        for (var i = 0; i < 100; i++)
        {
            lock (JsMessages)
            {
                if (JsMessages.TryGetValue("art", out var value))
                {
                    Report["art.page"] = JsonSerializer.Deserialize<Dictionary<string, object>>(value.GetRawText());
                    break;
                }
            }

            await Task.Delay(100);
        }
    }

    private static byte[] MakeJpeg(int w, int h, byte r, byte g, byte b)
    {
        var px = new byte[w * h * 4];
        for (var i = 0; i < px.Length; i += 4)
        {
            (px[i], px[i + 1], px[i + 2], px[i + 3]) = (b, g, r, 255);
        }

        var enc = new JpegBitmapEncoder { QualityLevel = 85 };
        enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4)));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ maximize + overlay click

    private static async Task MaximizeTest(HostRuntime rt, MainWindow win, string dir)
    {
        await Ui(rt, win.ToggleMaximize);
        await Task.Delay(900);
        await Ui(rt, () =>
        {
            Win32.GetWindowRect(win.Hwnd, out var wr);
            Win32.GetClientRect(win.Hwnd, out var cr);
            var origin = new POINT();
            Win32.ClientToScreen(win.Hwnd, ref origin);
            var mon = Win32.MonitorFromWindow(win.Hwnd, Win32.MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { Size = (uint)Marshal.SizeOf<MONITORINFO>() };
            Win32.GetMonitorInfo(mon, ref mi);
            Report["max.zoomed"] = Win32.IsZoomed(win.Hwnd);
            Report["max.windowRect"] = $"{wr.Left},{wr.Top},{wr.Right},{wr.Bottom}";
            Report["max.clientScreenRect"] = $"{origin.X},{origin.Y},{origin.X + cr.Width},{origin.Y + cr.Height}";
            Report["max.workArea"] = $"{mi.Work.Left},{mi.Work.Top},{mi.Work.Right},{mi.Work.Bottom}";
            Report["max.clientEqualsWorkArea"] = origin.X == mi.Work.Left && origin.Y == mi.Work.Top && origin.X + cr.Width == mi.Work.Right && origin.Y + cr.Height == mi.Work.Bottom;
            Report["max.info"] = $"{win.Info}";
            Screenshot(win, Path.Combine(dir, "maximized.png"), "max");
        });

        // click the max overlay the way the system would deliver it (NC messages) → must restore
        await Ui(rt, () =>
        {
            var ov = win.Overlays!.MaxHandle;
            Win32.PostMessage(ov, Win32.WM_NCLBUTTONDOWN, Win32.HTMAXBUTTON, 0);
            Win32.PostMessage(ov, Win32.WM_NCLBUTTONUP, Win32.HTMAXBUTTON, 0);
        });
        await Task.Delay(900);
        await Ui(rt, () =>
        {
            Report["max.afterOverlayClick.zoomed"] = Win32.IsZoomed(win.Hwnd);
            Report["max.afterOverlayClick.info"] = $"{win.Info}";
            Screenshot(win, Path.Combine(dir, "restored.png"), "restored");
        });
    }

    // ------------------------------------------------------------------ Snap Layouts (needs real hover)

    private static async Task HoverTest(HostRuntime rt, MainWindow win, string dir)
    {
        if (Environment.GetEnvironmentVariable("GLIDER_SELFTEST_HOVER") != "1")
        {
            Report["hover"] = "skipped (set GLIDER_SELFTEST_HOVER=1 to move the cursor over the maximize button once)";
            return;
        }

        POINT saved = default;
        POINT target = default;
        await Ui(rt, () =>
        {
            Win32.SetForegroundWindow(win.Hwnd);
            Win32.GetCursorPos(out saved);
            var origin = new POINT();
            Win32.ClientToScreen(win.Hwnd, ref origin);
            Win32.GetClientRect(win.Hwnd, out var cr);
            var s = Win32.GetDpiForWindow(win.Hwnd) / 96.0;
            target = new POINT { X = origin.X + cr.Width - (int)((HostConstants.MaxButtonRightInset + HostConstants.CaptionButtonWidth / 2) * s), Y = origin.Y + (int)(20 * s) };
            Win32.SetCursorPos(target.X - 30, target.Y);
        });
        await Task.Delay(250);
        Win32.SetCursorPos(target.X, target.Y);
        await Task.Delay(1800); // flyout shows after a short hover delay
        var px = Capture(dc => BitBlt(dc, 0, 0, 760, 420, Win32.GetDC(0), target.X - 380, Math.Max(0, target.Y - 20), 0x00CC0020 | 0x40000000), 760, 420);
        SavePng(Path.Combine(dir, "hover-snap.png"), px, 760, 420);
        Report["hover.caption"] = StartupTimeline.Has("revealed");
        Win32.SetCursorPos(saved.X, saved.Y);
        Report["hover"] = "captured hover-snap.png (cursor restored)";
    }

    // ------------------------------------------------------------------ placement, login, suspend

    private static void PlacementTest(HostRuntime rt, MainWindow win)
    {
        WindowPlacementStore.Save(rt.Paths, win.Hwnd);
        var path = Path.Combine(rt.Paths.State, "window.json");
        Report["placement.file"] = File.Exists(path) ? File.ReadAllText(path) : "MISSING";
    }

    /// <summary>
    /// The embedded Store, anonymously (no sign-in, no exchange code): opens the public Store home over a placeholder rectangle, then
    /// covers/uncovers it (snapshot), hides it, and closes it. Reports the phase after each step and the native bounds.
    /// </summary>
    private static async Task StoreTest(HostRuntime rt, MainWindow win, string dir)
    {
        var store = rt.Platform.StoreHost ?? throw new InvalidOperationException("no store host");
        var bounds = new Glider.Core.Contracts.StoreBounds(72, 40, 800, 520);
        var state = await store.ShowAsync(Glider.Core.Store.StoreUrls.Home("en-US"), bounds);
        for (var i = 0; i < 40 && state.Phase is Glider.Core.Contracts.StorePhase.Loading or Glider.Core.Contracts.StorePhase.Creating; i++)
        {
            await Task.Delay(500);
            state = store.State;
        }

        Report["store.afterShow"] = $"{state.Phase} host={state.Host} visible={state.Visible} title={state.Title}";
        await Task.Delay(1500);
        if (rt.Platform.StoreIfCreated is { } view)
        {
            await rt.Ui.InvokeAsync(() => Report["store.nativeBounds"] = view.DescribeBoundsForTests());
        }

        await rt.Ui.InvokeAsync(() => Screenshot(win, Path.Combine(dir, "store.png"), "store"));
        var covered = await store.SetCoveredAsync(true);
        Report["store.cover"] = $"covered={covered.Covered} visible={covered.Visible} snapshot={covered.CoverUrl is not null && File.Exists(Path.Combine(rt.Paths.Cache, "store-cover.png"))}";
        var uncovered = await store.SetCoveredAsync(false);
        Report["store.uncover"] = $"covered={uncovered.Covered} visible={uncovered.Visible}";
        var hidden = await store.HideAsync();
        Report["store.hide"] = $"{hidden.Phase} visible={hidden.Visible}";
        await store.DestroyAsync();
        Report["store.destroy"] = $"{store.State.Phase} created={store.State.Created} snapshotLeft={File.Exists(Path.Combine(rt.Paths.Cache, "store-cover.png"))}";
    }

    /// <summary>
    /// The Store open on its tab while the window sits in the tray (review H5): the page must end up suspended, and come back live when
    /// the window does. Anonymous (the public Store home, no sign-in); bounded: ≤ ~50 s even when nothing works. Two paths: the real
    /// tray hide (MainWindow suspends everything ~8 s after hiding) and the Store's own suspend timer, shortened from 60 s to 3 s.
    /// </summary>
    private static async Task StoreHiddenTest(HostRuntime rt, MainWindow win)
    {
        var store = rt.Platform.StoreHost ?? throw new InvalidOperationException("no store host");
        var state = await store.ShowAsync(Glider.Core.Store.StoreUrls.Home("en-US"), new Glider.Core.Contracts.StoreBounds(72, 40, 800, 520));
        for (var i = 0; i < 40 && state.Phase is Glider.Core.Contracts.StorePhase.Loading or Glider.Core.Contracts.StorePhase.Creating; i++)
        {
            await Task.Delay(500);
            state = store.State;
        }

        var view = rt.Platform.StoreIfCreated ?? throw new InvalidOperationException($"the Store view was not created (phase {state.Phase})");
        Report["storeHidden.beforeHide"] = $"{state.Phase} visible={state.Visible}";

        // A: window to the tray with the Store tab still "active".
        await Ui(rt, win.HideToTray);
        var started = DateTime.UtcNow;
        var suspended = await WaitFor(rt, () => view.IsSuspendedForTests, TimeSpan.FromSeconds(20));
        Report["storeHidden.trayPhase"] = $"{store.State.Phase} visible={store.State.Visible}";
        Report["storeHidden.suspendedInTray"] = suspended ? $"yes after {(DateTime.UtcNow - started).TotalSeconds:0.#} s" : "NO within 20 s";
        await Ui(rt, win.RestoreFromTray);
        await Task.Delay(1200);
        var resumed = false;
        await Ui(rt, () => resumed = !view.IsSuspendedForTests);
        Report["storeHidden.resumedOnRestore"] = resumed ? $"yes, {store.State.Phase} visible={store.State.Visible}" : "NO, still suspended";
        if (!suspended || !resumed)
        {
            throw new InvalidOperationException($"tray hide: suspended={suspended}, resumed={resumed}");
        }

        // B: the Store's own timer (a real tray hide is also suspended by MainWindow after ~8 s, which would mask it).
        await Ui(rt, () => view.SuspendDelay = TimeSpan.FromSeconds(3));
        try
        {
            await Ui(rt, view.OnWindowHidden);
            started = DateTime.UtcNow;
            suspended = await WaitFor(rt, () => view.IsSuspendedForTests, TimeSpan.FromSeconds(12));
            Report["storeHidden.suspendTimer"] = suspended ? $"suspended after {(DateTime.UtcNow - started).TotalSeconds:0.#} s (timer set to 3 s)" : "NO within 12 s";
            await Ui(rt, view.OnWindowShown);
            await Task.Delay(800);
            await Ui(rt, () => resumed = !view.IsSuspendedForTests);
            Report["storeHidden.timerResumed"] = resumed;
            if (!suspended || !resumed)
            {
                throw new InvalidOperationException($"suspend timer: suspended={suspended}, resumed={resumed}");
            }
        }
        finally
        {
            await Ui(rt, () => view.SuspendDelay = Glider.Core.Store.StoreLifecycle.SuspendAfter);
            await store.DestroyAsync();
        }
    }

    private static async Task<bool> WaitFor(HostRuntime rt, Func<bool> condition, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var ok = false;
            await Ui(rt, () => ok = condition());
            if (ok)
            {
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }

    private static async Task LoginTest(HostRuntime rt, string dir)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(24));
        var task = await rt.Ui.InvokeAsync(() => rt.Platform.AcquireEpicExchangeCodeAsync(cts.Token)).Task;
        // What the user actually sees: screen pixels of the login window after the page had time to load.
        await Task.Delay(TimeSpan.FromSeconds(9));
        await rt.Ui.InvokeAsync(() =>
        {
            var hwnd = LoginWindowHwnd();
            if (hwnd == 0 || !Win32.GetWindowRect(hwnd, out var wr))
            {
                Report["login.screenshot"] = "no window";
                return;
            }

            int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
            var px = Capture(dc => { var sdc = Win32.GetDC(0); var ok = BitBlt(dc, 0, 0, w, h, sdc, wr.Left, wr.Top, 0x00CC0020 | 0x40000000); _ = Win32.ReleaseDC(0, sdc); return ok; }, w, h);
            SavePng(Path.Combine(dir, "login-screen.png"), px, w, h); // may show whatever covers the window
            // PrintWindow does not depend on occlusion (the window may sit behind the user's foreground app).
            px = Capture(dc => Win32.PrintWindow(hwnd, dc, 2 /* PW_RENDERFULLCONTENT */), w, h);
            SavePng(Path.Combine(dir, "login.png"), px, w, h);
            var distinct = new HashSet<int>();
            for (var i = 0; i + 3 < px.Length && distinct.Count < 64; i += 4 * 97)
            {
                distinct.Add(px[i] | (px[i + 1] << 8) | (px[i + 2] << 16));
            }

            Report["login.screenshot"] = $"{w}x{h}, distinct colors sampled: {distinct.Count} (blank window ≈ 1–3)";
        });
        // Popups for social logins: the login page calls window.open (about:blank allowed, javascript: blocked).
        Report["login.popup"] = await rt.Platform.LoginPopupSmokeAsync();
        try
        {
            var code = await task;
            Report["login.result"] = code ?? "null (window closed)";
        }
        catch (OperationCanceledException)
        {
            Report["login.result"] = "cancelled by token after 24s (window opened and closed cleanly)";
        }

        await rt.Platform.ClearLoginSessionAsync();
        Report["login.clearSession"] = "ok";
    }

    /// <summary>Alt+F4 / system close must go through rpc.Close (→ hide to tray with the placeholder's default CloseToTray=true), not kill the app.</summary>
    private static nint LoginWindowHwnd() => EpicLogin.LastHwndForTests;

    private static async Task CloseTest(HostRuntime rt, MainWindow win)
    {
        await Ui(rt, () => Win32.PostMessage(win.Hwnd, Win32.WM_CLOSE, 0, 0));
        await Task.Delay(800);
        bool hidden = false;
        await Ui(rt, () => hidden = !win.IsVisible);
        Report["close.WM_CLOSE.hiddenToTray"] = hidden;
        await Ui(rt, win.RestoreFromTray);
        await Task.Delay(500);
    }

    private static async Task HideSuspendTest(HostRuntime rt, MainWindow win)
    {
        var before = StartupMeasure.PrivateWorkingSetTotalsMb();
        await Ui(rt, win.HideToTray);
        await Task.Delay(11_000);
        var hidden = StartupMeasure.PrivateWorkingSetTotalsMb();
        bool? suspended = null;
        await Ui(rt, () => suspended = win.Web?.IsSuspended);
        await Ui(rt, win.RestoreFromTray);
        await Task.Delay(1200);
        var after = StartupMeasure.PrivateWorkingSetTotalsMb();
        bool? resumed = null;
        bool visible = false;
        await Ui(rt, () =>
        {
            resumed = win.Web?.IsSuspended;
            visible = win.IsVisible;
        });
        Report["hide.beforeMb"] = $"host {before.HostMb} + webview2 {before.WebViewMb}";
        Report["hide.hiddenSuspendedMb"] = $"host {hidden.HostMb} + webview2 {hidden.WebViewMb}";
        Report["hide.afterRestoreMb"] = $"host {after.HostMb} + webview2 {after.WebViewMb}";
        Report["hide.isSuspendedWhileHidden"] = suspended;
        Report["hide.isSuspendedAfterRestore"] = resumed;
        Report["hide.visibleAfterRestore"] = visible;
    }
}
