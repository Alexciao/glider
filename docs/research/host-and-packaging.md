# Glider host & packaging research

Scope: thin WPF shell (.NET 10, C#) hosting WebView2 that renders a local React UI, packaged with Velopack.
Researched 2026-11-12 on Windows 11 build 26300, .NET SDK 10.0.404, WebView2 Runtime 157.0.4341.46.

**Evidence labels used below**

- **[E]** = verified empirically this session with a throwaway spike app (not committed, not in the repo).
- **[D]** = read in official docs, specs, or source this session.
- **[K]** = standard API knowledge, **not** re-verified this session. Treat as "check before relying on it".

---

## 0. Versions (NuGet flat-container API, queried 2026-11-12) [D]

| Package | Latest stable | Published | Latest prerelease / notes |
|---|---|---|---|
| Microsoft.Web.WebView2 | **1.0.4314.35** | 2026-10-23 | 1.0.4365-prerelease (2026-11-02). Release notes call 4314.35 "for Runtime 156". |
| Velopack | **1.2.161** | 2026-09-29 | 1.3.39-g8d2844f (2026-10-17) |
| vpk (dotnet tool) | **1.2.161** | same as Velopack | Docs: keep the vpk version equal to the Velopack package version. |
| H.NotifyIcon.Wpf | **2.4.1** | not fetched | 2.5.0-beta.3 (2026-09-12). The 2.4.1 nuspec has a `net10.0-windows7.0` group and depends on H.NotifyIcon 2.4.1. |
| Microsoft.Toolkit.Uwp.Notifications | **7.1.3** | 2022-11-01 | Stale (4 years). Nuspec groups seen: netstandard1.4, UAP10.0.16299, .NETCoreApp3.1 (depends on Microsoft.Windows.SDK.Contracts). The net5+/net6+ groups were not inspected. |
| CommunityToolkit.WinUI.Notifications | **7.1.2** | 2021-11-18 | Stale, WinUI-oriented. Same code lineage as the Uwp package. |

Sources:

- <https://api.nuget.org/v3-flatcontainer/microsoft.web.webview2/index.json>
- <https://api.nuget.org/v3-flatcontainer/velopack/index.json>
- <https://api.nuget.org/v3-flatcontainer/vpk/index.json>
- <https://api.nuget.org/v3-flatcontainer/h.notifyicon.wpf/index.json>
- <https://api.nuget.org/v3-flatcontainer/microsoft.toolkit.uwp.notifications/index.json>
- <https://api.nuget.org/v3-flatcontainer/communitytoolkit.winui.notifications/index.json>

---

## 1. Frameless WPF + HTML title bar

### 1.1 What WebView2 itself gives you

| Capability | Status | Source |
|---|---|---|
| `CoreWebView2Settings.IsNonClientRegionSupportEnabled` (CSS `app-region: drag / no-drag`) | Introduced in SDK **1.0.2420.47**. Default `false`. Takes effect after the **next navigation**. | [D] <https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2settings9> |
| It works in **WPF** (HWND-hosted `WebView2` control) | **[E] Yes.** With `WindowChrome{CaptionHeight=0}` and a `-webkit-app-region:drag` div, a real mouse drag from (500,120) to (800,220) moved the window from (100,100) to (400,220), exactly +300,+120. The spec's .NET sample is also a WPF `WebView2` control. | [E]; [D] <https://github.com/MicrosoftEdge/WebView2Feedback/blob/main/specs/IsNonClientRegionSupportEnabled.md> |
| Behaviour of drag regions | Treated like the title bar: drag moves the window, right-click shows the system menu, double-click maximizes/restores. Right-click and double-click not tested. Only the top-level document is supported. | [D] same spec |
| Enum `CoreWebView2NonClientRegionKind` | `Nowhere 0`, `Client 1`, `Caption 2`, `Minimize 8`, `Maximize 9`, `Close 20` (Min/Max/Close added in SDK 1.0.2903.40). **`app-region` only produces `Caption`.** Min/Max/Close are part of the *composition-controller* hit-testing API (`ICoreWebView2CompositionController4::GetNonClientRegionAtPoint`) and the experimental Window Controls Overlay. The doc for `GetNonClientRegionAtPoint` lists only CAPTION/CLIENT/NOWHERE results. | [D] <https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2nonclientregionkind>, <https://github.com/MicrosoftDocs/webview2-win32-reference/blob/live/1-0-4314-35/icorewebview2compositioncontroller4.md> |
| Window Controls Overlay API (`CoreWebView2.WindowControlsOverlay`, WebView2 renders caption buttons) | **Prerelease only** (first in 1.0.3415-prerelease). Not found in the stable release notes up to 4314.35, which are sparse. Do not depend on it. | [D] release-notes/sdk/1-0-3415-prerelease.md |
| WPF `WebView2CompositionControl` (visual hosting, solves airspace) | Exists. It captures the browser with a `GraphicsCaptureSession`, so **lower frame rates and no DRM video**. Not recommended for this app. | [D] <https://github.com/MicrosoftEdge/WebView2Feedback/blob/main/specs/WPF_WebView2CompositionControl.md> |

### 1.2 Snap Layouts on an HTML maximize button: what actually works [E]

The WebView2 HWND covers the whole client area, so the top-level window cannot see the hover.
Two designs were tested, each hovering over the top-right 46×32 px box:

| Design | Result |
|---|---|
| A. Handle `WM_NCHITTEST` in the **top-level** WPF window and return `HTMAXBUTTON` for the button rect | The top-level did receive `WM_NCHITTEST` there and returned `HTMAXBUTTON`, but **no Snap flyout appeared** in the screenshot. |
| B. A small **child HWND overlay** (`HwndSource` with `WS_CHILD\|WS_VISIBLE`, parent = main window, z-order on top of the WebView2 HWND) whose own WndProc returns `HTMAXBUTTON` for `WM_NCHITTEST` | **The Windows 11 Snap Layouts flyout appeared** (screenshot confirmed). The overlay received 26 hit-tests and 12 `WM_NCMOUSEMOVE`. |

**Recommendation (most robust for WPF): design B.** One small overlay child HWND per system-button area, or a single overlay over the max button.

- Do not use the WebView2 composition-control route (see 1.1).
- WebView2 Min/Max/Close region kinds are not available to HTML in the stable HWND-hosted path.

Consequences of design B:

1. The overlay swallows the mouse, so the HTML button gets no `:hover` or `:active`. Post `WM_NCMOUSEMOVE` / `WM_NCMOUSELEAVE` / `WM_NCLBUTTONDOWN/UP` to JS over the message bridge and style the button from state.
2. Handle the click yourself (see the snippet).
3. Min and close can be plain HTML buttons with `app-region: no-drag`; only max needs the overlay (min and close have no flyout).

**Overlay snippet** (worked as shown for hover; click handling is [K], untested):

```csharp
// after Web.EnsureCoreWebView2Async(...) so the overlay is above the WebView2 child HWND
var p = new HwndSourceParameters("MaxOverlay", 46, 32) {
    ParentWindow = new WindowInteropHelper(window).Handle,
    WindowStyle  = 0x40000000 | 0x10000000,           // WS_CHILD | WS_VISIBLE
    PositionX    = clientWidthPx - 46, PositionY = 0  // device pixels
    // do NOT set UsesPerPixelTransparency: it threw Win32Exception creating the child
};
var ov = new HwndSource(p);
ov.RootVisual = new Border { Background = Brushes.Transparent };   // I used a visible color while testing
ov.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) => {
    switch (msg) {
        case 0x0084: handled = true; return (IntPtr)9;                  // WM_NCHITTEST -> HTMAXBUTTON
        case 0x00A1: handled = true; return IntPtr.Zero;                // WM_NCLBUTTONDOWN  [K]
        case 0x00A2: handled = true;                                    // WM_NCLBUTTONUP    [K]
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;
            return IntPtr.Zero;
    }
    return IntPtr.Zero;
});
SetWindowPos(ov.Handle, IntPtr.Zero /*HWND_TOP*/, x, 0, 46, 32, 0x0040 /*SWP_SHOWWINDOW*/);
```

Reposition the overlay on size change and on DPI change.

### 1.3 Window chrome, resize borders, maximize inset

- **[E]** This combination renders a frameless window with DWM rounded corners, and drag works:
  `WindowChrome { CaptionHeight = 0, ResizeBorderThickness = 6, GlassFrameThickness = 0, UseAeroCaptionButtons = false }`.
  Do not set `AllowsTransparency`.
- **Untested [K]:**
  - Resize via `WindowChrome`'s 6 px border. The WebView2 HWND fills the client area and will cover the border. Inset the WebView2 by 6 px (Mica shows through, so it looks fine), or reuse the overlay trick (child HWNDs returning `HTLEFT/HTTOP/...`).
  - `WindowChrome` normally handles the maximized-window inset (the ~8 px overhang). Verify on maximize, especially on a second monitor and with the taskbar auto-hidden.
- **Fallback if you must write the WndProc yourself [K]** (the maximize inset is the part `WindowChrome` normally does):

```csharp
const int WM_NCCALCSIZE = 0x83, WM_NCHITTEST = 0x84, WM_GETMINMAXINFO = 0x24;
// WM_NCCALCSIZE (wParam != 0): client area = whole window, except when maximized:
//   inset rgrc[0] by GetSystemMetrics(SM_CXSIZEFRAME) + SM_CXPADDEDBORDER (and the same for Y),
//   i.e. what DefWindowProc would add. Return IntPtr.Zero, handled = true.
// WM_GETMINMAXINFO: set ptMaxPosition / ptMaxSize from MonitorFromWindow + GetMonitorInfo().rcWork
//   (needed if you ever use WindowStyle=None, or the window covers the taskbar).
// WM_NCHITTEST on the top-level: return HTLEFT/HTRIGHT/HTTOP/HTTOPLEFT/... within a 6 px band,
//   otherwise HTCLIENT. Be aware the WebView2 child HWND will eat hits over the client area.
```

---

## 2. Mica

### 2.1 Recipe that worked [E] (build 26300, WPF + HWND-hosted `WebView2`)

```csharp
[DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
[DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
// XAML: Window Background="Transparent"  (AllowsTransparency stays false)
SourceInitialized += (_, _) => {
    var h = new WindowInteropHelper(this).Handle;
    HwndSource.FromHwnd(h).CompositionTarget.BackgroundColor = Colors.Transparent;  // default is black
    int dark = 1;     DwmSetWindowAttribute(h, 20, ref dark, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE
    int backdrop = 2; DwmSetWindowAttribute(h, 38, ref backdrop, 4); // DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_MAINWINDOW (HRESULT 0)
    var m = new MARGINS { l = -1, r = -1, t = -1, b = -1 };
    DwmExtendFrameIntoClientArea(h, ref m);
};
// after EnsureCoreWebView2Async:
Web.DefaultBackgroundColor = System.Drawing.Color.Transparent;   // page CSS: html,body{background:transparent}
```

**Does Mica show through the HWND-hosted WebView2? Yes** [E]. Screenshot pixel evidence:

| Case | Pixels |
|---|---|
| `DWMSBT_MAINWINDOW (2)` | Transparent HTML area is uniform `(32,32,32)`. An HTML bar `rgba(255,0,0,.35)` composites to `(110,21,21)`, which equals 0.35·red + 0.65·32 (so true alpha blending against the backdrop). |
| `DWMSBT_NONE (1)` | The same HTML area showed the **desktop behind the window** (T3 window colors `(17,20,24)`), so the WebView2 is genuinely see-through, not painting its own opaque color. |
| `DWMSBT_TRANSIENTWINDOW (3, acrylic)` | Uniform `(84,84,84)`, a different blurred material. |

The three backdrop types give different results, so the transparency is real.

**Honest caveat:**

- On this machine's dark neutral wallpaper, Mica is almost exactly `#202020`, the same value as the dark fallback color.
- I cannot prove from pixel values alone that it is *wallpaper-tinted* Mica rather than the solid fallback. Eyeball it on a machine with a colorful wallpaper.
- A first batch of backdrop comparisons ran a stale binary and was **discarded**. The numbers above are from the corrected re-run.

### 2.2 Requirements, build checks, fallback [D]/[K]

| Item | Value | Source |
|---|---|---|
| `DWMWA_SYSTEMBACKDROP_TYPE` (38) | Windows 11 **build 22621+** | [D] <https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute> |
| `DWMWA_USE_IMMERSIVE_DARK_MODE` (20) | build 22000+ per the same page | [D] |
| Older Win11 (22000–22620) | Undocumented attr `1029` (`DWMWA_MICA_EFFECT`) = 1. [K] The page I read does not list it. | [K] |
| Windows 10 / failure | `DwmSetWindowAttribute` fails or no-ops. Fall back to an opaque theme background (set `Window.Background` and the HTML body to the theme color). Gate the transparent-everything CSS on a `hasMica` flag sent to JS. | [K] |
| Dark-mode switching | Re-call attr 20 on `SystemEvents.UserPreferenceChanged` / theme change, and tell JS. | [K] |

Gate by `Environment.OSVersion.Version.Build >= 22621`.

**Mica Alt** is `DWMSBT_TABBEDWINDOW = 4`. It rendered uniform `(32,32,32)` too, but that test ran on the stale binary.

Microsoft's own Mica-for-Win32 article uses the Windows App SDK `MicaController` (a different mechanism). The DWM attribute route above is what was tested.

### 2.3 Gotchas

- `AllowsTransparency=true` is not needed and would push WPF into layered-window mode (slower, breaks the HWND child). Keep it false.
- The WPF airspace limitation remains: nothing WPF-drawn will appear above the WebView2. The overlay design in 1.2 works around that with a child HWND, not a WPF element.
- Set `DefaultBackgroundColor` early to avoid a flash, via `CoreWebView2ControllerOptions.DefaultBackgroundColor` (a spec exists because setting the property after creation applies "too late") or the `WEBVIEW2_DEFAULT_BACKGROUND_COLOR` env var. [D] <https://github.com/MicrosoftEdge/WebView2Feedback/blob/main/specs/CoreWebView2ControllerOptions.DefaultBackgroundColor.md>. In my spike I set it after `EnsureCoreWebView2Async` and did not measure any flash.

---

## 3. Startup speed and memory

### 3.1 Measured [E]

Spike: JIT build (not ReadyToRun), framework-dependent, WebView2 environment created in `Loaded` (not parallelized), warm disk cache, local `NavigateToString`.
Times are from process start:

| Milestone | ms |
|---|---|
| Window `ContentRendered` | ~905 |
| `CoreWebView2` ready | ~1051 |
| `NavigationCompleted` | ~1143 |

Memory, steady state, trivial page:

| Process set | Working set | Private |
|---|---|---|
| WPF host process | **108–111 MB** | ~61 MB |
| WebView2 child processes (6) | ~307 MB working set total (working sets double-count shared pages) | 3 + 7 + 12 + 20 + 36 + 69 = **~147 MB** |

Plan on roughly 200 MB private for host + browser with a lightweight page, and more with React.
The WPF host dominates what you control. Self-contained R2R and `TieredPGO` were not measured on startup, only on size.

### 3.2 Recommendations ([K] unless noted; not benchmarked here)

1. In `Main`/`App` (after `VelopackApp.Build().Run()`), start `CoreWebView2Environment.CreateAsync(null, userDataFolder, options)` immediately, before creating the window, and `await` it in `Loaded`.
   Pass the environment to `EnsureCoreWebView2Async(env)`.
   Use `%LocalAppData%\Glider\WebView2` as the user data folder. Never use the install dir, since Velopack replaces `current\`.
2. Virtual host mapping, serving local files over HTTPS-like origin without `file://` limits:
   `CoreWebView2.SetVirtualHostNameToFolderMapping("app.glider", appFolder, CoreWebView2HostResourceAccessKind.Allow)` then `Navigate("https://app.glider/index.html")`.
3. Avoid the white flash:
   - transparent or theme-colored `DefaultBackgroundColor` via controller options (see 2.3);
   - `Window.Background` matching the theme;
   - keep the window hidden or at opacity 0 until `NavigationCompleted` or the first paint message from JS, then show.
4. `CoreWebView2EnvironmentOptions.AdditionalBrowserArguments`: start conservative, for example `--disable-features=msSmartScreenProtection` and `--disable-background-networking`. Measure RAM/CPU before and after, and keep only flags with measurable gain. Unsupported flags can break on runtime updates.
5. Hidden or tray: `CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low` while hidden, `Normal` on show.
   For deep idle, set `IsVisible = false` (or hide the control) and call `CoreWebView2.TrySuspendAsync()`; call `Resume()` on show.
   (API behaviour recalled from the WebView2 API reference; not re-fetched.)
6. Host publish settings. Measured for size only (section 8); startup gain not measured:
   - `PublishReadyToRun=true` (self-contained win-x64)
   - `TieredPGO` (already default in recent .NET)
   - `InvariantGlobalization=true` if no culture-specific formatting is needed
   - `SatelliteResourceLanguages=en`
   - `DebugType=none`
7. **Trimming is not viable for WPF [E]:** `PublishTrimmed=true` fails the build with **NETSDK1168** ("trimming is not supported or recommended for WPF").
   Native AOT is also not available for WPF.

---

## 4. JSON message bridge

- .NET to JS: `webView.CoreWebView2.PostWebMessageAsJson(json)`. JS: `window.chrome.webview.addEventListener('message', e => e.data)`.
- JS to .NET: `window.chrome.webview.postMessage(obj)`. .NET: `CoreWebView2.WebMessageReceived` with `e.WebMessageAsJson` (or `TryGetWebMessageAsString`).
- **[E]** `chrome.webview.postMessage({hello:1})` from a page loaded with `NavigateToString` fired `WebMessageReceived`, and `e.WebMessageAsJson` was `{"hello":1}`.
- Threading [K]: all `CoreWebView2` members are UI-thread only (STA), and WebView2 events arrive on the UI thread. Marshal from background threads with `Dispatcher.InvokeAsync`.
  For heavy bulk data (game-library lists, images), serialize on a background thread, then post once. Prefer fewer, larger messages. Use `PostSharedBufferToScript` for large binary data.
- Throughput was not measured. Define envelopes `{id, type, payload}` with a request/response correlation id, and keep payloads small (paginate).

---

## 5. Native bits

### 5.1 Tray icon

| Option | Notes |
|---|---|
| **H.NotifyIcon.Wpf 2.4.1** | WPF-native (`TaskbarIcon`), has a `net10.0-windows7.0` target, supports modern context menus and balloon. Extra dependency (H.NotifyIcon). |
| **`System.Windows.Forms.NotifyIcon`** with `<UseWindowsForms>true` | Zero 3rd-party dependency and trivial. Pulls WinForms into the process and the self-contained publish (the extra size/RAM was **not measured**). The menu is the old GDI `ContextMenuStrip`. |

Recommendation: prefer **H.NotifyIcon.Wpf** if the tray menu is small and the theme matters.
Choose WinForms `NotifyIcon` only if every MB of the publish matters. Measure `UseWindowsForms` before deciding.

### 5.2 Toast notifications from an unpackaged app

- **AUMID: Velopack handles it. [E][D]**
  - Velopack stamps the Start-menu shortcut with an AUMID, default **`velopack.{packId}`**.
  - Installing the spike (`Spike.Test`) gave `Get-StartApps` → `AppID : velopack.Spike.Test`.
  - Override at pack time with `vpk pack --aumid "<AUMID>"`, or at runtime with `VelopackApp.Build().SetAppUserModelId("...")`.
  - Docs: `integrating/overview` ("By default this is read from the package manifest, falling back to `velopack.{AppId}`"), and the vpk `--aumid` flag.
  - Source: `src/bins/src/windows/shortcuts.rs` uses `PKEY_AppUserModel_ID`.
- Toast packages [K]:
  - `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 provides `ToastNotificationManagerCompat` / `ToastContentBuilder`, and works for unpackaged apps.
  - Call `SetCurrentProcessExplicitAppUserModelID` with the **same** AUMID early in `Main`.
  - It needs a TFM of at least `net10.0-windows10.0.17763.0`.
- **Size trap [E]:** that Windows-version TFM makes the SDK add `Microsoft.Windows.SDK.NET.dll` (**52.6 MB**, uncompressed).
  - Self-contained Setup.exe grew from **75.8 MB to 91.2 MB (+15.4 MB)**. See section 8.
  - This is the single biggest avoidable cost found.
- Avoid the size hit by keeping the main app on plain `net10.0-windows` and sending toasts without the full WinRT projection. Options (none tested):
  - a tiny sidecar console exe built with the Windows-version TFM;
  - raw COM interop for `IToastNotificationManagerStatics`;
  - NotifyIcon balloon tips as the minimal fallback.
- Activation when the app is not running requires the Compat COM-activator registration [K], which writes under `HKCU\Software\Classes\CLSID`. Verify behaviour across Velopack updates.

### 5.3 Single instance (named mutex + pipe) [K]

```csharp
// in Main, right after VelopackApp.Build().Run()
using var mutex = new Mutex(true, @"Local\Glider.SingleInstance", out bool first);
if (!first) {
    using var c = new NamedPipeClientStream(".", "Glider.Activate", PipeDirection.Out);
    c.Connect(1000); c.WriteByte(1);     // optionally pass args: "--uri ..."
    return;
}
_ = Task.Run(async () => {
    while (true) {
        await using var s = new NamedPipeServerStream("Glider.Activate", PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await s.WaitForConnectionAsync();
        app.Dispatcher.Invoke(ShowAndActivateMainWindow);
    }
});
```

Keep the mutex object alive for the whole process.

### 5.4 Run at login (HKCU Run) with Velopack's stable path

- **[E] Install layout** (Setup.exe, per-user):
  ```
  %LocalAppData%\<packId>\
      current\            <- replaced on every update (contains Spike.exe etc.)
      packages\
      Update.exe
      <packTitle>.exe     <- execution stub, e.g. "Spike Test.exe"
  ```
- **The stub is named after `--packTitle`, not `--mainExe`** [E]. The log said `Extracting stub 'Spike Test_ExecutionStub.exe' to ...\Spike.Test\Spike Test.exe`.
  The stub is the stable path that survives updates; the Velopack docs say shortcuts and launchers should point at it.
- So the Run value is `"%LocalAppData%\<packId>\<packTitle>.exe" --background`.
  Build it from `VelopackLocator.Current.RootAppDir` plus the title. Do not use `Environment.ProcessPath` (it is inside `current\`).
- Alternative for fixed autostart: `vpk pack --shortcuts Desktop,StartMenuRoot,Startup` creates a Startup-folder shortcut at install, but it is not user-toggleable at runtime. Prefer the Run key for an in-app toggle.
- `--shortcuts` values (docs): `Desktop`, `StartMenuRoot`, `StartMenu` (needs `--packAuthors`), `Startup`, `None`. Default `Desktop,StartMenuRoot`.

### 5.5 Window placement persistence [K]

```csharp
[DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, out WINDOWPLACEMENT p);
[DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
// Save on Closing: GetWindowPlacement -> JSON in %AppData%\Glider\window.json
// Restore in SourceInitialized (HWND exists, window not yet shown): SetWindowPlacement,
// after validating the rect is still on a connected monitor.
```

Store settings under `%AppData%\Glider` or `%LocalAppData%\Glider`, **never** in `current\`.

---

## 6. Second WebView2 window for Epic login [K]

- Same `CoreWebView2Environment` object: one browser process group, shared cookies, and the lowest RAM.
  A separate profile (`CoreWebView2ControllerOptions.ProfileName`, or `IsInPrivateModeEnabled`) isolates cookies. Use that only if the main UI must not share the Epic session.
- For `https://www.epicgames.com/id/api/redirect?clientId=...&responseType=code`, two ways to read the JSON:
  1. `CoreWebView2.WebResourceResponseReceived`, filter `e.Request.Uri`, then `await e.Response.GetContentAsync()` (a `Stream`; it may be unavailable for some redirect responses).
     Add the filter with `AddWebResourceRequestedFilter` as needed.
  2. More robust: after login, `Navigate` the window to that URL and, on `NavigationCompleted`, read `await ExecuteScriptAsync("document.body.innerText")` (JSON is rendered as text).
- `NavigationStarting` is the place to detect redirect targets and cancel (`e.Cancel = true`) once you have the code.
- **None of this was run against Epic** (no credentials, and it is outside the spike). Validate response-body access on the real endpoint.

---

## 7. Velopack packaging

### 7.1 Verified flow [E][D]

```powershell
dotnet publish -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true `
    -p:SatelliteResourceLanguages=en -p:DebugType=none -o publish
dotnet tool install -g vpk --version 1.2.161          # or: dnx vpk@1.2.161 ...
vpk pack --packId Glider --packVersion 1.0.0 --packDir publish --mainExe Glider.exe `
         --packTitle "Glider" --packAuthors "..." --icon app.ico --framework webview2
```

- Docs (bootstrapping): `--framework webview2` is the valid value for the Edge WebView2 runtime; comma-separated with others.
  For a self-contained build do **not** add a `net…-desktop` framework.
- [E] `--framework webview2` appears in the package manifest as a runtime dependency. At install, Setup logged
  `WebView2 version: 157.0.4341.46 … Microsoft Edge WebView2 Runtime is already installed`.
  **The download-and-install path (runtime missing) was not exercised.**
- **Output file names** [E] (default channel `win`):
  - `<id>-win-Setup.exe`
  - `<id>-<ver>-full.nupkg`
  - `<id>-win-Portable.zip`
  - `releases.win.json`, `assets.win.json`, and a legacy `RELEASES`
  - A `-delta.nupkg` appears when a previous full package is present in the output dir (this is why CI runs `vpk download github` first).
- **Setup.exe behaviour** [E]:
  - One-click, per-user, installs to `%LocalAppData%\<packId>`.
  - Creates a Desktop shortcut (OneDrive-redirected Desktop was found and used) and a Start Menu root shortcut.
  - `--silent` shows nothing and does **not** launch the app.
  - Runs the app's hook (`--veloapp-install 1.0.0`) and then writes the HKCU Uninstall key (`HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\<packId>`, `UninstallString: "<root>\Update.exe" --uninstall`).
  - Install took ~15 s on this machine; the shortcut search of the OneDrive Desktop and Start Menu dominated (~4 s + ~11 s per log timestamps).
- **`VelopackApp.Build().Run()` placement for WPF** [D]. The docs recommend a custom `Main`, first line, with `App.xaml` as `Page` and `StartupObject` set. This is exactly what the spike used and the install/uninstall hooks worked:

```xml
<PropertyGroup><StartupObject>Glider.Program</StartupObject></PropertyGroup>
<ItemGroup><ApplicationDefinition Remove="App.xaml"/><Page Include="App.xaml"/></ItemGroup>
```
```csharp
[STAThread] public static void Main(string[] a) {
    VelopackApp.Build().Run();            // must be first; exits inside Run() for hook invocations
    var app = new App(); app.InitializeComponent(); app.Run();
}
```

- **Uninstall** [E]:
  - `Update.exe uninstall --silent` (or the Apps & Features entry) ran the `--veloapp-uninstall` hook, removed both shortcuts, the HKCU Uninstall key and the temp dir.
  - Deleting `%LocalAppData%\<packId>` is scheduled via `cmd /C choice … rmdir`. When I invoked it from a shell, `Update.exe` was still locked and the folder remained until removed manually. This may be an artifact of how I invoked it.
- **Updates** [D]: the whole `current\` is replaced. Keep data out of it (docs: "Preserving Files").

### 7.2 Self-update (later) [D]

```csharp
var mgr = new UpdateManager(new GithubSource("https://github.com/<you>/glider", accessToken: null, prerelease: false));
var info = await mgr.CheckForUpdatesAsync();
if (info != null) {
    await mgr.DownloadUpdatesAsync(info);
    mgr.ApplyUpdatesAndRestart(info);   // or WaitExitThenApplyUpdates to apply after the user quits
}
```

- Unauthenticated GitHub source is limited to 60 requests/hour per IP.
- `SetAutoApplyOnStartup(false)` disables the apply-on-next-launch default.

### 7.3 GitHub Actions (adapted from docs.velopack.io; vpk pinned) [D]

```yaml
name: release
on: { push: { tags: ['v*'] } }
permissions: { contents: write }
jobs:
  release:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 10.0.x }
      - name: Publish
        run: dotnet publish src/Glider/Glider.csproj -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -p:SatelliteResourceLanguages=en -p:DebugType=none -o publish
      - name: Pack and upload with Velopack
        shell: bash
        run: |
          VER="${GITHUB_REF_NAME#v}"
          dotnet tool install -g vpk --version 1.2.161
          vpk download github --repoUrl https://github.com/${{ github.repository }} --token ${{ secrets.GITHUB_TOKEN }}
          vpk pack -u Glider -v "$VER" -p publish -e Glider.exe --packTitle Glider --icon app.ico -f webview2
          vpk upload github --repoUrl https://github.com/${{ github.repository }} --token ${{ secrets.GITHUB_TOKEN }} --publish --releaseName "Glider $VER" --tag "v$VER"
```

- `vpk upload github` flags (docs): `--publish` (default is a draft), `--pre`, `--merge`, `--tag`, `--releaseName`, `--targetCommitish`, `--token`.
- `vpk upload github` vs `gh release create`:
  - vpk knows the exact set of files (`*.nupkg`, `releases.win.json`, `assets.win.json`, Setup, Portable) and sets the release correctly.
  - With `gh`, upload **all** of `Releases\*` yourself, or `GithubSource` will not find the feed.
  - Prefer `vpk upload github`.

---

## 8. Installer size (measured [E]; hit your "< 60 MB" goal?)

All variants: win-x64, same spike (WPF + WebView2 + Velopack), vpk 1.2.161, `--framework webview2` unless stated.

| Variant | Publish dir | Setup.exe |
|---|---|---|
| Self-contained + R2R, TFM `net10.0-windows10.0.17763.0` | 196 MB | **91.2 MB** |
| Self-contained + R2R, TFM `net10.0-windows` | 144 MB | **75.8 MB** |
| + `SatelliteResourceLanguages=en`, no pdb/xml/createdump | 136 MB | **72.2 MB** (full.nupkg 64.7 MB; Setup stub adds ~7.5 MB) |
| Self-contained single-file, compressed (`EnableCompressionInSingleFile`) | 73 MB (exe 66.8 MB) | not packed; Velopack re-zips, little to gain, slower startup |
| `PublishTrimmed` | build fails: **NETSDK1168** (WPF unsupported) | n/a |
| Framework-dependent (`--framework net10.0-x64-desktop,webview2`) | 2.4 MB | **10.3 MB** (Setup detected the installed .NET 10 Desktop runtime; downloading it when missing was not exercised) |

Biggest assemblies in the self-contained publish: `Microsoft.Windows.SDK.NET.dll` 52.6 MB (only with the `windows10.0.x` TFM), `System.Private.CoreLib` 16 MB, `PresentationFramework` 15.8 MB, `PresentationCore` 8.3 MB, `System.Private.Xml` 7.8 MB, `D3DCompiler_47_cor3` 4.7 MB.

**Conclusion:** a self-contained WPF installer cannot reach **< 60 MB** with the knobs tested (best **72.2 MB**).

Options:

1. Accept ~72 MB. It is a one-time download, because Velopack delta updates are small afterwards (`--delta` default `BestSpeed`).
2. Use a framework-dependent build: ~10 MB Setup that bootstraps the .NET 10 Desktop runtime plus WebView2 on first install. Cost: a runtime download and possible elevation, and it was not tested here.
3. Untested ideas for the last ~12 MB:
   - manually pruning unused framework assemblies (risky);
   - dropping `D3DCompiler_47_cor3` if no WPF shader effects are used;
   - replacing the WPF host with a Win32/AOT host. This is a different project.

**Avoid the Windows SDK TFM** (`windows10.0.x`) unless needed: it alone costs ~15 MB of installer.

---

## 9. Verified vs docs: summary

| Topic | [E] verified empirically | [D] docs / source | [K] not verified |
|---|---|---|---|
| app-region drag in WPF | Yes (+300,+120 move) | API / spec | Right-click menu, double-click maximize |
| Snap Layouts on HTML max button | Yes, child overlay HWND returning `HTMAXBUTTON`; top-level-only hook failed | Snap approach docs not re-fetched | Click-to-maximize handling |
| Mica through transparent WebView2 | Yes (backdrop none/Mica/acrylic give different results; alpha composites) | Build numbers 22000/22621 | Attr 1029 fallback; Win10 fallback; wallpaper-tinted Mica not visually proven |
| Startup timing | ~0.9 s window, ~1.14 s navigation complete (JIT, unoptimized) | n/a | R2R / parallel env gains, white-flash behaviour |
| Memory | Host 108–111 MB WS; WebView2 ~147 MB private | n/a | n/a |
| Bridge | `postMessage` to `WebMessageReceived` JSON works | n/a | Throughput, size limits |
| Tray / toast / single instance / Run key / placement | Run-key stub path and AUMID default verified | Velopack docs / source | Toolkit activation, tray RAM cost, pipes, placement code |
| Epic login capture | none | none | all |
| Velopack install / uninstall / names / hooks | Yes (silent install, uninstall, files, AUMID, shortcuts, registry) | CLI + docs | WebView2 bootstrap download path; runtime bootstrap for FDD |
| Installer size | Yes (table in section 8) | n/a | n/a |
| Trimming | NETSDK1168 confirmed | n/a | n/a |

## 10. Sources

- WebView2 settings (Win32 ref): <https://learn.microsoft.com/en-us/microsoft-edge/webview2/reference/win32/icorewebview2settings9>
- WebView2 non-client regions spec: <https://github.com/MicrosoftEdge/WebView2Feedback/blob/main/specs/IsNonClientRegionSupportEnabled.md>
- `CoreWebView2NonClientRegionKind` enum: <https://learn.microsoft.com/en-us/dotnet/api/microsoft.web.webview2.core.corewebview2nonclientregionkind>
- Composition controller non-client API: <https://github.com/MicrosoftDocs/webview2-win32-reference/blob/live/1-0-4314-35/icorewebview2compositioncontroller4.md>
- WebView2 SDK release notes (1.0.2420.47, 1.0.2903.40, 1.0.3415-prerelease, 4314.35): <https://github.com/MicrosoftDocs/edge-developer/tree/main/microsoft-edge/webview2/release-notes/sdk>
- WPF `WebView2CompositionControl` spec: <https://github.com/MicrosoftEdge/WebView2Feedback/blob/main/specs/WPF_WebView2CompositionControl.md>
- `ControllerOptions.DefaultBackgroundColor` spec: <https://github.com/MicrosoftEdge/WebView2Feedback/blob/main/specs/CoreWebView2ControllerOptions.DefaultBackgroundColor.md>
- DWM window attributes: <https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute>
- Mica in Win32 (`MicaController`): <https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/ui/apply-mica-win32>
- Velopack docs: <https://docs.velopack.io/> (source: <https://github.com/velopack/velopack.docs>): `getting-started/csharp`, `packaging/bootstrapping`, `packaging/operating-systems/windows`, `integrating/overview`, `integrating/shortcuts`, `integrating/uninstalling`, `integrating/update-sources`, `distributing/github-actions`, `reference/cli`
- Velopack source: <https://github.com/velopack/velopack/blob/develop/src/bins/src/windows/shortcuts.rs>, `src/lib-csharp/NuGet/PackageManifest.cs`, `src/lib-csharp/Locators/WindowsVelopackLocator.cs`
- NuGet flat-container API: see section 0.
