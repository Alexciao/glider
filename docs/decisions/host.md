# Host (Glider.App) decisions and the contract the frontend / Core must follow

One line of reasoning per decision; the constants live in `src/Glider.App/HostConstants.cs` (and `Glider.Core.WebHost`).

## Decisions

- **Code-only WPF window + raw `CoreWebView2Controller` (no XAML, no WPF `WebView2` control)**: no BAML, no `HwndHost`/airspace plumbing, and the controller can be created the moment the HWND exists (measured: 330 ms earlier "ready" than starting it after `Show()`).
- **Window starts *cloaked* (`DWMWA_CLOAK`), is shown natively, and is uncloaked on `app.ready`** (3 s safety timeout): a cloaked window is "visible" to WebView2 so the page paints, yet DWM draws nothing. Verified: `app.ready` arrives while cloaked and the frame captured just before uncloak is never white (`#202020` dark / `#F3F3F3` light).
- **Frame = `WS_OVERLAPPEDWINDOW` + `WM_NCCALCSIZE` that only removes the caption** (Windows Terminal's approach): Snap, Win+arrows, shadow, rounded corners and the invisible 7 px left/right/bottom resize borders stay native. Verified by hit-test probes (HTLEFT/HTRIGHT/HTBOTTOM/HTBOTTOMLEFT outside the visible frame).
- **Top-edge resize and Snap Layouts both use tiny child HWNDs over the page** (the WebView2 HWND covers the client area, so the top-level never sees those hits). They are `WS_EX_LAYERED` with alpha 1/255: still hit-testable but invisible. A plain never-painted child window renders as an opaque **white rectangle** (found by screenshot; fixed).
- **Maximized: client rect = work area exactly; 2 px reserved next to an auto-hide taskbar.**
- **Cache served on its own virtual host `cache.glider`**: `WebResourceRequested` is never raised for virtual-host requests (verified, even with a `*` filter), so `https://app.glider/cache/…` cannot be served by a handler next to the folder mapping. Same-origin policy is not an issue: `<img>`, `fetch` and canvas read-back from `app.glider` all work (verified).
- **Missing WebView2 runtime ⇒ native message box + download link, then exit**; WebView2 is also reported when the app starts in the tray.
- **Start-in-tray (`--background` or `startMinimized`) creates no WebView2 at all**: the host is ~3 MB private working set with zero browser processes until the user opens the window (verified: 0 child processes, then 1 browser group after activation).
- **Hidden to tray**: `MemoryUsageTargetLevel=Low` + `IsVisible=false` immediately, `TrySuspendAsync` + GC compaction + `EmptyWorkingSet` after 8 s; everything reverses on restore. Outbound messages are held (coalesced) while suspended.
- **One compacting GC + `EmptyWorkingSet` 3 s after first reveal**: host working set 115 MB → ~9 MB (see `docs/perf-host.md`); pages fault back on demand.
- **Tray = `Shell_NotifyIcon` v4 + native popup menu (dark-aware via uxtheme ordinals 135/136, best effort)**; H.NotifyIcon rejected (extra dependency + WPF visual tree for a 150-line message window). Toasts are NIF_INFO balloons.
- **Epic login = second window, same `CoreWebView2Environment`, profile `GliderEpic`**; the code is read with `ExecuteScriptAsync("document.body.innerText")` on the `…/id/api/redirect` page. Sign-out wipes that profile (`ClearBrowsingDataAsync`). The window stays hidden until a real login page has loaded, so an existing Epic session does not flash a window.
- **Host log is separate** (`logs\host-yyyyMMdd.log`) so it never contends with Core's rolling log.
- **`--selftest[=dir]` is shipped on purpose**: a bounded, self-terminating harness (hit-test probes, PrintWindow + screen captures, cache serving, maximize, suspend, login smoke test). `GLIDER_SELFTEST_HOVER=1` additionally moves the cursor once onto the maximize button to capture the Snap Layouts flyout.

## Constants (frontend ⇄ host)

| Name | Value | Meaning |
|---|---|---|
| App origin | `https://app.glider/` (page: `index.html?theme=dark\|light&backdrop=mica\|solid`) | UI served from `wwwroot` (Debug fallback: repo `frontend/dist`; last resort: built-in placeholder) |
| Cache URLs | `https://cache.glider/<relative path under AppPaths.Cache>` | Core builds them with `Glider.Core.WebHost.CacheUrl("art/hero.jpg")` |
| Caption button size | **46 × 40 CSS px** each, top-right, order **min, max, close** | |
| **Maximize-button overlay rect** | `left = windowWidth − 92`, `top = 0`, `width = 46`, `height = 40` (CSS px; scaled by DPI). I.e. right edge is **46 px** from the window's right edge (the close button's width) | Hit-tests as `HTMAXBUTTON` (Snap Layouts) and toggles maximize on click |
| Top resize strip | full width × **4 CSS px** at the very top (hidden when maximized) | Do not put clickable things in the top 4 px |
| Dark / light base | `#202020` / `#F3F3F3` (opaque) | Window + WebView2 background when `backdrop=solid`; with `mica` the page must keep `html, body { background: transparent }` |

## Messages beyond `IGliderRpc` (all optional; unknown ones are ignored)

Web → host (via `chrome.webview.postMessage`; **`"type"` must be the first key**):

- `{"type":"host","op":"chrome","maxRightInset":46,"width":46,"height":40}`: move/resize the maximize overlay if the layout differs from the constants above.
- `{"type":"host","op":"theme","theme":"dark"|"light"|"system"}`: tell the host when the user changes the theme setting (updates DWM dark mode, window/WebView2 background). System theme changes are tracked by the host itself.

Host → web (same envelope as other events):

- `{"type":"event","event":"caption","data":{"button":"max","state":"hover"|"leave"|"down"}}`: the overlay swallows the mouse, so the HTML maximize button gets no `:hover`/`:active`; style it from this event. Clicks are handled by the host (it calls toggle-maximize and the `window` event follows).
- `window` (`WindowInfo`) is pushed on maximize/restore and focus changes, coalesced.

Startup contract: the frontend calls `app.ready` once it has painted. The host also reads `theme` from `state\settings.json` (`"theme"`, `"startMinimized"`) before Core is up.

## Art (v0.2)
- **`https://art.glider/…` is intercepted with `WebResourceRequested` (filter `https://art.glider/*`, all contexts), deliberately not mapped as a virtual host** (mapped hosts never raise the event). The handler (`ArtHandler`) answers from Core's `ArtCache`; responses carry `Cache-Control: public, max-age=31536000, immutable` (the URL holds the source hash), `X-Content-Type-Options: nosniff` and `Access-Control-Allow-Origin: https://app.glider`. Verified by `--selftest` (`GLIDER_SELFTEST_ONLY=art`): `<img>` loads, `fetch` 200 with the immutable header, unknown game/variant 404, malformed or foreign URLs 400.
- `GLIDER_SELFTEST_ONLY=cache,art` limits `--selftest` to those steps (no Epic login smoke test).
