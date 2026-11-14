# Store tab (embedded Epic Games Store) and "Free this week": design for v0.2

Scope: embed `store.epicgames.com` in a second WebView2 inside the main window, signed in as the same user, with Store "Launch/Install"
buttons routed into Glider, newly acquired games appearing in the library, and a "Free this week" strip on Home fed by Epic's public
promotions feed. Nothing in this design automates a purchase or a claim: the user clicks every button on Epic's own page.

Provenance tags (same convention as `legendary.md`): **[code]** read in this repo, **[doc]** Microsoft/Epic documentation, **[src]** read in
third-party source (Legendary 0.21.1 tag, Heroic `89e06ad`, etc.), **[observed]** fetched live while writing this (public, anonymous
requests only), **[3p]** third-party reverse-engineering, **[inferred]** reasoning, **[unverified]** could not be checked without a signed-in
account or a GUI, and listed again in section 9 as a live-test checklist. No sign-in was made, no token was read, and no GUI was started.

---

## 0. Decisions in one table

| Question | Decision |
|---|---|
| Environment / user data folder | Reuse the **single** `CoreWebView2Environment` from `HostRuntime.EnvTask` (same options, same UDF `%LocalAppData%\Glider\webview2`). No change to `CreateEnvironmentOptions`. |
| Profile | Store view uses the **same profile as sign-in, `GliderEpic`** (`HostConstants.WebViewProfileEpic`). One "Epic web identity", one wipe point (`EpicLogin.ClearSessionAsync`). |
| Does launcher-mode login leave a web session? | **Probably yes while the browser process lives, probably not after a restart** [inferred, section 1.3]. Not provable without an account, so the Store must not depend on it: it **probes cookie names** and, if signed out, does an **exchange-code web SSO** (`/id/exchange`) seeded from `legendary get-token --json`. |
| User agent | **Default WebView2/Edge UA, no override.** Never the `EpicGamesLauncher/...` UA, never `window.ue`. |
| Deep links | Handle `com.epicgames.launcher://` in **both** `NavigationStarting` and `LaunchingExternalUriScheme` (and `NewWindowRequested`), cancel, parse with a strict grammar, emit a typed `storeIntent` event. |
| Placement | **Child-HWND WebView2 controller placed over an HTML placeholder** (bounds pushed by the frontend), plus a "covered" state with a bitmap snapshot for modals. Not a separate top-level window, not composition hosting. |
| Lifecycle | Created lazily on first Store tab activation. Hidden: `IsVisible=false` + `MemoryUsageTargetLevel=Low`; after ~60 s `TrySuspendAsync`; after ~20 min hidden (and no checkout in flight) `Close()` and recreate on demand. |
| New purchases | **Triggered, single-flight `library.refresh`** (Legendary `list --json`): after any checkout activity, when the Store tab is left, when the window is re-activated, plus a bounded poll while a checkout was seen. Never keyed on a "success URL" alone. |
| Free games | `GET store-site-backend-static-ipv4.ak.epicgames.com/freeGamesPromotions` from **Core** (not the page), parsed with a pure function, cached on disk, TTL 1 h clamped to the next promo boundary. Images through the existing `cache.glider` pipeline. |
| IPC isolation | The Store `CoreWebView2` is never given to `WebViewBridge`, has web messages and host objects off, no virtual-host mappings, no injected scripts, and the frontend can only name targets from a closed set, never a raw URL. |

---

## 1. Profile and cookie sharing

### 1.1 What the code does today [code]

* `HostRuntime` starts one environment at process start: `CoreWebView2Environment.CreateAsync(null, paths.WebView2, WebViewSetup.CreateEnvironmentOptions())`
  (`WebViewSetup.cs:33-37`). Options: `AdditionalBrowserArguments` (`--disable-features=msSmartScreenProtection,...`), `IsCustomCrashReportingEnabled=true`,
  `EnableTrackingPrevention=false`. Everything awaits `HostRuntime.EnvTask`.
* The main UI controller is created with `env.CreateCoreWebView2ControllerOptions()` with **no** `ProfileName` (so the UDF's default profile) and only
  `DefaultBackgroundColor` set (`MainWindow.cs:311-314`).
* The login window (`EpicLogin.CreateControllerAsync`, `EpicLogin.cs:422-435`) uses the **same `env`** (`_rt.EnvTask`) and
  `options.ProfileName = HostConstants.WebViewProfileEpic` = `"GliderEpic"`. The UA is applied per-WebView (`Settings.UserAgent` + one
  `Emulation.setUserAgentOverride`), so it does not leak to other WebViews in the profile; tracking prevention is **profile-wide**
  (`Profile.PreferredTrackingPreventionLevel = Balanced`, `EpicLogin.cs:145`).
* Sign-out wipes the profile: `EpicLogin.ClearSessionAsync` -> `Profile.ClearBrowsingDataAsync()` on a `GliderEpic` controller.

### 1.2 What is required by WebView2 [doc]

* All `CoreWebView2Environment` objects that point at one user data folder must be configured identically, otherwise the second one fails to create
  WebViews (different `Language`, browser arguments, etc.). Identically configured ones "represent the same user data folder and the same associated
  collection of processes" (process-model doc, "Multiple environment objects"). Glider has exactly one environment, so the Store view **must reuse
  `await _rt.EnvTask`**; never call `CoreWebView2Environment.CreateAsync` for it. After a browser-process crash, `RecreateEnvironment()` (already
  present) replaces it and the Store view must be torn down and re-created with the new one, exactly like `MainWindow.RecreateWebView`.
* Profiles are selected per controller through `CoreWebView2ControllerOptions.ProfileName`; a missing profile is created; the name is max 64 chars, from
  `[A-Za-z0-9#@$()+\-_~. ]`, case-insensitive, no leading space and no trailing space or dot (MultiProfile spec, `ProfileName`). `GliderEpic` is valid.
  Cookies, preferences and storage are separated per profile; "all the WebView2 controls that are associated with the same user profile share a single
  profile folder" (multi-profile doc). Profile-level settings (`PreferredTrackingPreventionLevel`, password autosave, general autofill) apply to **every**
  WebView2 of that profile.
* Renderer processes are not tied to one `CoreWebView2`; they can serve frames of several WebViews in the same UDF "depending on site isolation"
  (process-model doc). `app.glider` and `store.epicgames.com` are different sites, so the Store always gets its own renderer(s) whatever profile it uses;
  browser process, GPU and network services are shared (one process group per UDF). Whether renderers are additionally split per profile is not documented
  **[unverified]**; it does not change the plan.

### 1.3 Does the launcher-mode login leave the web session logged in? [inferred]

Not provable without an account, so the design assumes neither answer. The evidence:

* The login page runs on `www.epicgames.com/id/login`; to hand over an exchange code through `window.ue.signinprompt.requestexchangecodesignin`, the page has to
  obtain it from `POST /id/api/exchange/generate`, which needs the web session cookies (`EPIC_BEARER_TOKEN`, `XSRF-TOKEN`, `EPIC_SESSION_AP`), exactly the chain Legendary's
  `core.auth_sid` replays (`legendary/core.py:123-155`: `set-sid` -> `csrf` -> `exchange/generate`; its comment says the first call sets "EPIC_BEARER_TOKEN etc.")
  **[src]**. So at the moment the code is produced the profile very likely holds an authenticated web session on `.epicgames.com`.
* Glider then closes the window immediately and the browser process stays alive (the main UI WebView2 shares it), so in-memory cookies survive for the rest
  of the run. After a **restart**, only cookies with an expiry survive on disk; `EPIC_BEARER_TOKEN`/`EPIC_SESSION_AP` lifetimes are unknown (an anonymous
  `GET /id/api/redirect` sets `EPIC_SESSION_AP` with `HttpOnly; Secure`, `XSRF-TOKEN` session-scoped, and `EPIC_DEVICE` with 90 days **[observed]**).
  The Heroic precedent (`persist:epic` partition for login and Store, `WebView/index.tsx:692`) is a *web* login, not launcher mode, so it does not answer this.
* `store.epicgames.com` is a sibling of `www.epicgames.com`; the `.epicgames.com`-domain cookies apply to both, but the Store may additionally want its
  own first-party cookies that only a visit to the Store can set. [unverified]
* EGL itself (v2, per the third-party reverse-engineering [3p]) does **not** rely on cookies for its Store view: it injects `Authorization: Bearer` on
  the Store document request plus a `window.ue` bridge. We must not copy that (it needs the bearer token, which Glider never reads, and the page then expects a
  launcher host). So the Store runs in plain "browser mode" and needs web cookies.

**Probe, no secrets:** `CoreWebView2.CookieManager.GetCookiesAsync("https://www.epicgames.com/")` and `("https://store.epicgames.com/")`. Look only at
`Name`, `IsSession`, `Expires`; never copy or log `Value`. "Signed in" = a cookie named `EPIC_BEARER_TOKEN` is present and not expired. (`EPIC_BEARER_TOKEN` is
named in Legendary's source; other names, e.g. `EPIC_SSO_RM`, are [unverified]; the live checklist in section 9 records the real names once, then the probe pins them.)

Decision tree at first Store open in a run:

```
probe(GliderEpic) -> bearer cookie present?  -- yes --> navigate straight to the target URL
                                             -- no  --> SSO bootstrap (1.4), then target
login redirect seen later (id/login with redirectUrl) -> same SSO bootstrap once, then back to that redirectUrl
SSO failed twice  -> phase "signedOut": banner with [Sign in to Glider again] [Open in browser]
```

### 1.4 Fallback: exchange-code web SSO

**Endpoint.** `https://www.epicgames.com/id/exchange?exchangeCode=<code>&redirectUrl=<url-encoded target>` signs a browser in with an exchange code and
redirects. Evidence: EGL v2 uses it for the parental-consent page [3p: Zyhloh/Unofficial-EGL-Documentation `EpicGames/Web/Id/Exchange.md`, `Launcher/Auth/CabinedMode.md`],
and independent apps build the same URL for store/marketplace pages: `ahmed5939/penny-launcher src/kernel/core/epic-store.ts` (`openSignedIn`: mint code, open
`/id/exchange?exchangeCode=...&redirectUrl=<store.epicgames.com page>` in the system browser, restricted to `https://store.epicgames.com`),
`AchetaGames/Epic-Asset-Manager` (`...&redirectUrl=https%3A%2F%2Fwww.unrealengine.com%2Fmarketplace`) **[src]**. A bare anonymous request to the endpoint from `curl` is
a Cloudflare 403 **[observed]**, so only a real browser engine can complete it, which a WebView2 is.

**Getting a fresh code from Legendary 0.21.1 (verified in source at tag 0.21.1, `cli.py:2218-2240`, `api/egs.py:150-154`):**

* `legendary get-token --json` is a *hidden* subcommand (`_hidden_commands`, `cli.py:3125`; parser at `cli.py:2775`, `--bearer`/`--json` at `cli.py:3066-3069`).
  It calls `core.login(force_refresh=False)` (refreshes the OAuth session only if needed) and prints `json.dumps(egs.get_game_token())`, i.e. the body of
  `GET https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/exchange`: `{"expiresInSeconds": ~299, "code": "<32 hex>", "creatingClientId": "34a02cf8..."}` (shape
  per [3p] `AccountService/OAuth/Exchange.md`; consistent with `egs.py:150-154`). Without `--json` it logs `Exchange code: <code>` to stderr instead.
* The same call is what `legendary launch` makes for `-AUTH_PASSWORD` (`core.py:889-892`), so the endpoint is proven to work with Glider's session.
* **Never use `get-token --bearer`**: it prints the access token.
* The code is single-use and valid ~5 minutes; mint it immediately before navigating and discard it.
* **Client id.** The code is created by the launcher client `34a02cf8f4414e29b15921876da36f9a` (Legendary's login client, the same one EGL uses). EGL itself feeds such codes
  (no `consumingClientId`) to `/id/exchange` [3p]. Accepted for the Store: very likely (same flow EGL uses for the website; independent launchers use it for `store.epicgames.com`) but **[unverified live]**.
  If Epic's side ever rejects it, the symptom is the exchange page showing an error instead of redirecting; the app then drops to the `signedOut` banner.

**Core API** (not part of `IGliderRpc`, so the web cannot call it): `IStoreSession.MintWebExchangeCodeAsync(CancellationToken) -> string?`, implemented in `Glider.Core/Store/StoreSession.cs`
over `LegendaryCli`:

* Run `get-token --json` through the same runner as other commands, in "secret output" mode: stdout is not written to the command log, not kept in the
  ring buffer used by `app.getLogs`, and `SecretRedactor` gains a JSON rule for `"code":"<32 hex>"` (the existing `-AUTH_PASSWORD=` / `--token` rules are the precedent).
* Parse only `code`; validate `^[0-9a-f]{32}$`; return it by value; never put it in a record, an event, or an exception message.
* Single-flight and rate limit (one mint per 10 s; Epic rate-limits this call, 429 observed in EGL logs per [3p]); serialize with other Legendary calls (token refresh rotates `user.json`).

**Host side** (`StoreView.SignInAsync`): build the URL with a pure helper `StoreSso.Build(code, targetUrl)` which URL-encodes the target and **refuses any target whose host is not
`store.epicgames.com`** (or `www.epicgames.com`), then `web.Navigate(url)`. Hygiene:

* log with `SecretRedactor.UrlForLog` (the login window already logs only host+path) so the code never reaches the host log;
* set a flag so `NavigationStarting` for any `/id/exchange` URL that Glider did not start itself is cancelled (prevents Back from replaying a spent code);
* after the redirect lands, re-run the cookie probe; if still signed out, count a failure (max 2 per Store lifetime, then banner).

### 1.5 User agent

Use the WebView2 default UA (Edge/Chromium UA, brands include "Microsoft Edge WebView2", client hints as the runtime sends them). Do **not** set `EpicLauncherUserAgent`,
`LegendaryUserAgent`, or inject `window.ue`:

* Epic's pages switch into "launcher mode" when they see the launcher identity/bridge; in that mode the Store expects a host that implements
  `ue.webmarketplace.requestpurchase`, `ue.launcher.getaccesstoken`, `ue.common.getsignedinuser`, ... [3p `Launcher/Flows/Purchase.md`, `Launcher/IPC/StoreFab.md`]. Glider implements none, so Get/Buy
  would call a missing bridge. In plain browser mode the Store uses its normal web checkout (`#webPurchaseContainer` iframe, see 4.1), which needs nothing from the host.
* Heroic sets the EGL UA only on the login URL (`WebView/index.tsx:177-178,329-331`) and uses the default UA for store pages in the same partition **[src]**.
* Residual risk (R1): v0.1.5 found that `plain` WebView2 identity gets `captcha_invalid` at *login* MFA while launcher mode passes (`DECISIONS.md`, "launcher mode is the fix"). A purchase/claim
  "Place Order" step may use an invisible hCaptcha too; the free-games-claimer project documents hCaptcha at that step for automated clients [src: `vogler/free-games-claimer epic-games.js`, lines ~269-290].
  A *human* in a real browser engine normally passes it, and the earlier audit found WebView2 and Edge differ only by client-hint brand and `window.chrome.webview`. Mitigations, in order: (1) always show
  **Open in browser** in the Store toolbar (the user's default browser keeps its own session); (2) if captchas are rejected in practice, re-enable the v0.1.4 identity shim for the Store view only
  (`EdgeUserAgentMetadata` + `Emulation.setUserAgentOverride`, removed in commit `a629542`, introduced in `31669fd`); (3) never script the page to work around it.

### 1.6 Exactly what to change

| File | Change |
|---|---|
| `WebViewSetup.cs` | **Nothing** for the environment. Add `ConfigureStoreSettings(CoreWebView2Settings)` and `StoreNavigationPolicy` (section 6). |
| `HostConstants.cs` | Add `StoreOrigin = "https://store.epicgames.com/"`. Reuse `WebViewProfileEpic`. |
| `EpicLogin.cs` | Extract nothing. `ClearSessionAsync` stays the single wipe; `HostRuntime` calls `StoreView.DestroyAsync()` **before** it so no live Store controller holds the profile while it is cleared. |
| `EpicLogin.ConfigureAsync` | Unchanged. It sets `Profile.PreferredTrackingPreventionLevel = Balanced` for the whole profile; that is also the stock Edge default the Store expects. The Store must not change it. |
| `MainWindow.cs` | Own a `StoreView? _store`; forward `WM_SIZE`/`WM_DPICHANGED`/`WM_MOVE` to it; call it from `EnterLowMemory`, `LeaveLowMemory`, `SuspendWhenHiddenAsync`, `RecreateWebView`. |

---

## 2. Deep links (`com.epicgames.launcher://`) and checkout popups

### 2.1 How WebView2 surfaces them [doc]

* `CoreWebView2.LaunchingExternalUriScheme` (WebView2 1.0.1823.32+; the app ships 1.0.4314.35). Args: `Uri`, `InitiatingOrigin`, `IsUserInitiated`, `Cancel`, `GetDeferral()`. It is raised after
  `NavigationStarting` and before `NavigationCompleted` when the target scheme is registered with the OS; setting `Cancel=true` stops the launch **and** the built-in "Open this app?" dialog.
  `InitiatingOrigin` is the top-level document origin for a script-initiated navigation, the frame's origin for a child frame, empty for `Navigate()`.
  Some schemes are blocked by the runtime for safety and then the event does not fire.
* Because `NavigationStarting` is documented to precede it, a Glider-style guard that cancels in `NavigationStarting` (`WebViewSetup.GuardNavigation`) would swallow the custom-scheme navigation
  before the second event. Whether a custom-scheme **top-level** navigation raises `NavigationStarting` at all is not stated precisely **[unverified]**.
* Therefore the Store handler is registered in **all three** places and made idempotent (one `LauncherUriGate` with a 2 s same-URI dedupe):
  1. `NavigationStarting`: `Uri` scheme `com.epicgames.launcher` -> `e.Cancel = true` -> handle;
  2. `LaunchingExternalUriScheme`: `e.Cancel = true` -> handle (this also covers `<iframe src>` and `window.location =` from a subframe, where `InitiatingOrigin` shows who asked);
  3. `NewWindowRequested`: `e.Handled = true` -> handle if the target is such a URI (a `window.open('com.epicgames.launcher://...')`).
* If none of them fires and the OS handler is Epic Games Launcher, the user would get the default prompt and EGL would open. That is the failure mode the live checklist item L3 exists to exclude. Glider must **not** register the scheme itself (it would hijack EGL).

### 2.2 URL formats

Official documentation (dev.epicgames.com "Protocol Activation") gives:
`com.epicgames.launcher://apps/<SandboxID>%3A<CatalogID>%3A<ArtifactId>?action=launch&silent=true`, an install-directory variant `apps/D%3A%2FGames%2FMyGame?action=launch&silent=true`, and store pages
as `com.epicgames.launcher://store/product/<product slug>`. The deprecation of the old `apps/<ArtifactID>` form and the three-part form are confirmed by `ebkr/r2modmanPlus#1973`.
A widely shared real example is Fortnite: `com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=launch&silent=true`.
The third-party reverse-engineered grammar of the current launcher [3p `Launcher/DeepLinks.md`] is the most complete; it is what the parser implements:

| URI | Meaning | Glider action |
|---|---|---|
| `apps/<ns>%3A<catalogItemId>%3A<appName>` (default action = launch) | launch | resolve `appName`; installed -> launch; not installed -> open install sheet |
| `...?action=launch[&silent=true]` | launch | same |
| `...?action=install` (also `installer...`, `update`) | install / resume | open the game's install sheet (**never start a download silently**) |
| `...?action=verify` / `updatecheck` | verify / update check | navigate to the game page; the user presses the button |
| `...?action=uninstall` | uninstall confirm | **ignore** (log) |
| `apps/<bare appName>` | legacy launch | accepted, resolved by name |
| `apps/<url-encoded install dir>` | path form | **ignore** (never take paths from a web page) |
| `store/p/<slug>` and `store/product/<slug>` | store page | navigate the **Store view** to `https://store.epicgames.com/<locale>/p/<slug>` (slug-validated) |
| `store`, `store/<other>` | store home | Store home |
| `library`, `library?...` | library | switch to the Library page |
| anything else (`social`, `invite`, `settings`, `fab`, `ue`, `coderedemption`, ...) | n/a | ignore, log once |

What the **web store** itself emits in plain browser mode for its Install/Launch buttons is not documented anywhere I could reach; expect `com.epicgames.launcher://apps/...` (the form above) and, for
"Get the launcher" style buttons, an HTTPS download of the Epic installer. **[unverified]** Both are covered: the former by the parser, the latter by `DownloadStarting` -> cancel + an informational notice
("Glider replaces the Epic Games Launcher"; no installer download from the Store view).

### 2.3 Parsing and mapping to Glider app names

Pure, cross-platform, unit-tested type in Core: `Glider.Core.Store.LauncherUri.TryParse(string raw, out LauncherIntent intent)`.

* Reject: length > 2048; scheme other than `com.epicgames.launcher` (case-insensitive); userinfo, port, fragment; control chars; any embedded second scheme (`://` after the first) ([3p] EGL does the same: `too_long`, `bad_scheme`, `embedded_scheme`, `malformed`).
* Host (first element) case-insensitive: `apps`, `store`, `library`; everything else -> `Unknown`.
* `apps/<appRef>`: `Uri.UnescapeDataString` once (the `%3A` -> `:`); if it contains `:` it must split into **exactly three** parts `ns:catalogItemId:appName`, each matching
  `^[A-Za-z0-9._-]{1,128}$` (real namespaces include short ones such as `fn`; catalog ids are 32 hex; artifact ids are the Legendary **app name**). If it contains `/`, `\`, `..` or a drive colon (`D:/`) -> path form -> `Unknown`.
* **Glider app name = the third component** (= Legendary `app_name` = `GameRecord`/`GameState` key). Resolution order in Core (`StoreIntentResolver`): exact (OrdinalIgnoreCase) `appName` in the library;
  else `(namespace, catalogItemId)` against the cached Legendary metadata (`metadata.namespace`, `metadata.id`); else not found -> run one `library.refresh` (the item may have just been claimed) and retry once; else the intent is delivered with `resolved=false`
  and the UI says "That game is not in your library yet" with a Refresh action.
* Query: only `action` (whitelist `launch|install|installer*|update|verify|updatecheck|uninstall`) and `silent` are read. `arg`, `env.*`, `install`, `installtag`, `runningapptimeout` are **dropped** (a web page must not inject launch arguments or environment variables into a game process).
* Gesture rule: act only if `IsUserInitiated` (events 1-2) / `IsUserInitiated` (event 3) **and** the initiating origin is `https://store.epicgames.com` or `https://www.epicgames.com` (or empty for host-initiated). Otherwise show a non-blocking notice "The Store asked to open <Game>" with an Open button instead of acting.

### 2.4 Checkout and popups

* The Store's checkout is an **in-page iframe**, not a window: after the user clicks the purchase button the page creates `#webPurchaseContainer iframe` whose document is the Epic payment page (steps:
  "Place Order", EULA "I Accept", parental PIN, hCaptcha, then the text "Thanks for your order!") **[src: `vogler/free-games-claimer epic-games.js` ~lines 220-300, structure only; we do not drive it]**. The iframe host is an
  `*.ol.epicgames.com` payment site; in EGL the same checkout page is `https://launcher.store.epicgames.com/purchase?showNavigation=true&highlightColor=...&locale=...&offers=<qty>-<namespace>-<offerId>-<x>-<y>` [3p `EpicGamesStore/Purchase/Checkout.md`]. Frames do not go through `NavigationStarting`, so
  the top-level allowlist (6.2) does not affect them; `FrameNavigationStarting` is only used to block non-https and custom-scheme frame navigations and to detect the checkout host (4.1).
* Payment methods that open third-party pages (3-D Secure, PayPal, bank redirect): handle `NewWindowRequested` for the Store view like the login popups do (`EpicLogin.OnNewWindowRequested`): defer, create a native popup window that hosts the
  `CoreWebView2` handed back via `e.NewWindow` (so `window.opener`/`postMessage` keep working), `Handled=true`. Rules: **https only** (plus `about:blank`), **`IsUserInitiated` or opener inside a checkout session**, max 2 popups, popup is owned by the main window, same profile, same hardened settings, closes on `WindowCloseRequested`.
  Because the popup has no address bar, its **caption shows the host name and a padlock** (updated on every `NavigationCompleted`), so a phishing look-alike is visible to the user.
  Non-qualifying popups: ignore; a user-initiated link to a non-Epic host -> system browser (`WebViewSetup.OpenExternalSafe`).
* Top-level redirects away from Epic during checkout (some local payment methods): allowed only while a checkout session is active (section 4.1), otherwise cancelled (6.2). The toolbar always shows the current host and padlock.
  **[unverified]** which payment methods do this; see L6.

---

## 3. Lifecycle, cost, and placement

### 3.1 Lazy creation and suspension [doc/code]

* Create nothing at startup (the tray-start measurement in `docs/perf-host.md` shows 0 child processes until the window opens; the Store must not change that). First `store.open` (or a Store-bound deep link) creates the controller.
  Creation is async and takes the same order of magnitude as the login controller, ~0.1-0.8 s with a warm environment (`docs/perf-host.md`: env 192 ms, controller 761 ms cold) **[inferred for the second controller]**.
* States: `Creating -> Loading -> Ready`; `Ready -> Hidden` (tab left or window hidden) `-> Suspended` (idle) `-> Closed` (long idle); `Hidden/Suspended -> Ready` on show.
* Hidden: `controller.IsVisible=false` and `web.MemoryUsageTargetLevel = Low` right away (the doc recommends Low on inactive WebViews; it may drop cached data or swap memory to disk).
* Suspended: after 60 s hidden, `await web.TrySuspendAsync()`. Preconditions from the API reference: `IsVisible` must already be false (else `ERROR_INVALID_STATE`); it is best-effort ("Sleeping Tabs" conditions: playing media, etc. make it return `false`);
  the WebView resumes automatically when it becomes visible, `Navigate` also resumes it, so check `IsSuspended` before touching APIs that change state. `MainWindow.SuspendWhenHiddenAsync` is the existing pattern (`MainWindow.cs:500-528`); the Store is added to it.
  Do **not** suspend while a checkout session is active (4.1) or while a popup is open.
* Shown: `IsVisible=true` (auto-resume), `MemoryUsageTargetLevel=Normal`, `NotifyParentWindowPositionChanged()`, `MoveFocus(Programmatic)`.
* Closed after ~20 min hidden with no checkout: `Controller.Close()` (releases the renderer(s); cookies stay in the profile; cost of coming back is one page load). Keep only the last sanitized URL (origin+path) in memory and in `settings.json`
  (`storeLastPath`, path only, no query) so reopening resumes there.
* Window hidden to tray / minimized: `EnterLowMemory` hides and `Low`s the Store too; `SuspendWhenHiddenAsync` suspends it in the same pass (it must run before `MemoryTrimmer.Trim()`).
* Process failure: handle `ProcessFailed` on the Store web: renderer exit -> `Reload()`; `BrowserProcessExited` -> let `MainWindow.RecreateWebView` recreate the environment and then `StoreView.RecreateAsync()` (lazy, only if the tab is active).

### 3.2 Process sharing and expected RAM

* One browser process group per UDF, so the Store adds **renderers (and possibly utility processes), not a second browser/GPU process**. `docs/perf-host.md` baselines (Release, this dev machine): main UI idle 65 MB private WS over 6 processes; with the Epic login page also loaded and visible 144-152 MB, hidden + `Low` + suspended 91-101 MB.
* Expectation for the Store (**estimate, to be measured**, no GUI was run for this task): a store.epicgames.com page is a heavy React app with a video carousel and third-party scripts: roughly **+150-250 MB private WS while visible** (renderer for `store.epicgames.com`, plus separate renderers for the payment iframe and hCaptcha when a checkout is open),
  **+20-40 MB when hidden + suspended**, **0 when closed**. The self-test (`--selftest`) gets a `store.memory` step that opens the anonymous Store, records private WS per `msedgewebview2.exe` at visible / hidden / suspended / resumed / closed, and appends to `docs/perf-host.md`.
* Budget rule: the Store never exists while the window is hidden for more than the idle timer, so the "tray resident" cost stays at today's numbers.

### 3.3 Overlaying a native controller in the React layout

`MainWindow` already creates the UI controller directly on the main HWND (no WPF `WebView2` control, no `HwndHost`), so there is no airspace framework to fight; the Store controller is simply a **second child controller of the same parent HWND**. Options:

| Option | Verdict |
|---|---|
| **A. Second `CoreWebView2Controller` on the main HWND, bounds driven by an HTML placeholder** | **Recommended.** Same DPI/z-order/focus/DWM treatment as the existing view; no new top-level window; trivial to hide. Limits: rectangular, always above the main web view in its rectangle (airspace). |
| B. Composition controller + DirectComposition tree | Allows rounded clip and true z-interleaving, but needs manual mouse/keyboard/IME/accessibility forwarding and would also force the main view into composition to blend HTML above the Store. Large, risky; no benefit that A cannot approximate. |
| C. Separate owned top-level HWND/WPF window synced to the placeholder | Snap Layouts, minimize/restore, Alt-Tab, DWM shadow/Mica, z-order against other apps and focus all misbehave; rejected. |

Design for A:

* **Placeholder.** The Store page in React renders a toolbar (back, forward, reload, host + padlock, Free-games / Wishlist / Open in browser) and a `div[data-store-host]` that fills the rest. A `ResizeObserver` on that div plus `window.resize` and a `requestAnimationFrame`-coalesced layout hook sends `store.setBounds({ x, y, width, height })` in **CSS px relative to the main web view's client origin** (`getBoundingClientRect()`), at most once per frame, only when the rounded values change.
* **Conversion in the host.** CSS px -> physical px with the **host's** DPI, not `devicePixelRatio` (zoom is disabled, `IsZoomControlEnabled=false`, and the process is Per-Monitor-V2: `app.manifest`): `scale = GetDpiForWindow(hwnd)/96.0`; `Bounds = new Rectangle(round(x*scale), round(y*scale), round(w*scale), round(h*scale))`, clamped inside `GetClientRect`. Apply on `store.setBounds`, on `WM_SIZE`, and on `WM_DPICHANGED` (re-send after the DPI switch settles, like `LoginWindow` re-places once). Keep `ShouldDetectMonitorScaleChanges` at its default so the page re-renders crisp when the window crosses monitors.
* **Chrome that must stay on top.** `CaptionOverlays` (top resize strip, maximize hit-test overlay) are child HWNDs raised with `HWND_TOP`; the Store rectangle starts below the 40 px title bar, so it never overlaps them, and the left/right/bottom resize borders are outside the client area (`MainWindow.Chrome`). After creating the Store controller, call `_overlays.Raise()` once.
* **Rounded corners.** None: the Store panel is flush with the rail, title bar and window edges; Windows 11 rounds the *window* and DWM clips the children. (If a rounded card look is ever wanted: parent the controller to a `WS_CHILD|WS_CLIPCHILDREN` container HWND with `SetWindowRgn(CreateRoundRectRgn(...))`; the edge is not anti-aliased, so not recommended.)
* **Airspace with HTML that must appear above the Store.** Native controller wins in its rectangle, so anything React draws there is hidden. Rules:
  1. *Modal UI* (Dialog, CommandPalette, DropdownMenu/Select popovers launched from the Store page, window menus): the frontend sets `store.setCovered({ covered: true })` when any overlay mounts while `page === "store"`. The host does `await web.CapturePreviewAsync(Png, stream)` (a few tens of ms) into `cache/store-cover.png`, emits `store { coverUrl: "https://cache.glider/store-cover.png?v=N" }`, then `controller.IsVisible=false` (not suspended). The placeholder paints that image (dimmed under the scrim), so the modal looks like it sits over the live page. `covered=false` reverses it. Add `data-covers-native` to the shared overlay primitives (`ui/Dialog.tsx`, `ui/DropdownMenu.tsx`, `ui/Select.tsx`, `CommandPalette.tsx`) so one hook does this.
  2. *Toasts* (top-right, below the title bar) would sit under the Store, so while `page === "store"` the toast host renders into the Store toolbar's status slot instead (no cover). `ui/Toast.tsx` takes an `anchor` prop.
  3. *Tooltips* on the rail do not overlap the Store (rail is to its left).
* **Focus.** Click-through is native. When the user tabs out of the Store (`MoveFocusRequested`), call the main controller's `MoveFocus(Next/Previous)`; when the Store tab becomes active, `MoveFocus(Programmatic)` on the Store. Handle `AcceleratorKeyPressed` on the Store controller for Alt+Left/Alt+Right/F5/Ctrl+R (as `EpicLogin.AttachReloadSupport` does); keep other browser accelerators off in Release.

---

## 4. Detecting new purchases and claims

### 4.1 Signals (none of them automate anything)

Nothing is injected into Epic's page, so there is no DOM signal. Available, non-intrusive signals:

1. **Checkout activity** - a subframe navigation (`FrameNavigationStarting/Completed`) to an `*.ol.epicgames.com` host with path starting `/purchase`, or a top-level navigation whose path contains `/purchase` or `/checkout` on `store.epicgames.com`/`launcher.store.epicgames.com`. Sets `checkoutSession = { startedAt, lastActivity }`. [host name pattern from memory and EGL docs: **[unverified]**, L5 pins the real URLs]
2. **Leaving the Store tab** (`store.setVisible(false)`), **window re-activation** (`WM_ACTIVATE`) and **Store closed**, if `checkoutSession` existed since the last refresh.
3. **Navigation to a product page after checkout** (the page re-renders "In Library"): top-level `NavigationCompleted` while `checkoutSession` is live.
4. User presses **Refresh library** (always available, also in the command palette).

A "success URL" is deliberately not required: Epic's claim ends with an in-page "Thanks for your order!" without a navigation (free-games-claimer waits on that text, not a URL).

### 4.2 Refresh policy (Core, `library.refresh`)

* Implementation: `legendary list --json` (no `--force-refresh`). `list` always re-fetches the asset list for Windows and installed platforms (`core.get_game_and_dlc_list` -> `get_assets(update_assets=True)`, `core.py:523-535`), then fetches metadata only for apps whose asset/build changed or are new, so it is one cheap listing call plus one metadata call per new game **[src]**. Add `-T` only if non-installable titles should show (not for v0.2).
  It also refreshes the OAuth session if needed; run it through the same serialized Legendary queue.
* Diff the set of app names (and `(namespace, id)` pairs) against the previous snapshot; new entries produce `Notice { kind: "libraryAdded", appName, title }` ("Knightica was added to your library" with an Install action) and a `state` snapshot update.
* **Single-flight, min 10 s apart; backoff 30 s -> 5 min on errors/429; never while offline** (use the existing connectivity logic).
* **Bounded poll while a checkout session is live:** every 20 s for at most 5 minutes after `lastActivity`, stopping as soon as the diff finds something new, and only while the window is visible or was active in the last minute. Plus one unconditional refresh on triggers 2-4.
* A claim can lag the asset list by seconds; the poll covers it. Entitlements granted outside Glider (web, other devices) are picked up by trigger 2 or the next startup pre-warm.
* `LibraryRefreshResult { added: LibraryAdded[], total: int, at: string }` is returned to the caller (the UI uses it to dismiss the "Checking your library..." status).

---

## 5. Free games data

### 5.1 Endpoint and fixture [observed 2026-11-14 09:1x UTC]

`GET https://store-site-backend-static-ipv4.ak.epicgames.com/freeGamesPromotions?locale=en-US&country=US&allowCountries=US` - public, anonymous, `200 application/json; charset=utf-8`, ~49 KB.
Saved verbatim as `docs/research/fixtures/epic/freeGamesPromotions.json` (13 elements). The response carries **no `Cache-Control`, `ETag`, `Last-Modified`, `Expires` or `Vary`** headers, so there is nothing to revalidate against; caching is purely client-side.
`locale=it&country=IT&allowCountries=IT` returns the same offers priced in EUR with Italian number formatting (`fmtPrice.originalPrice = "13,49 €"`); omitting all three parameters also returns 200 (defaults to the service's own region), so always send them explicitly.

### 5.2 Shape (fields Glider uses)

```
data.Catalog.searchStore
  paging { count: 40, total: 13 }              // total <= count: one page holds everything; no pagination needed
  elements[]                                    // one per OFFER, not per game
    id                 offer id (32 hex)
    namespace          sandbox/namespace (32 hex)
    items[0].id        catalogItemId  (+ items[0].namespace)
    title, description, seller{id,name}
    offerType          BASE_GAME | BUNDLE | ADD_ON | ...   (fixture: BASE_GAME, BUNDLE, ADD_ON)
    status             ACTIVE
    isCodeRedemptionOnly  false
    effectiveDate, viewableDate, expiryDate     // listing lifetime, NOT the promo window
    keyImages[]        {type, url}
    productSlug        string | null   (can be "thems-fightin-herds/home" with a trailing "/home")
    urlSlug            slug OR a 32-hex id (often the id when productSlug is null)
    catalogNs.mappings[]   {pageSlug, pageType}   // pageType "productHome" = the product page slug
    offerMappings[]        {pageSlug, pageType}   // "productHome", or "offer" for add-ons
    categories[].path  contains "freegames" for every element in this feed
    price.totalPrice   {discountPrice, originalPrice (minor units), currencyCode, currencyInfo.decimals,
                        fmtPrice{originalPrice, discountPrice, intermediatePrice}}
    promotions         null | { promotionalOffers: PromoGroup[], upcomingPromotionalOffers: PromoGroup[] }
       PromoGroup      { promotionalOffers: [ { startDate, endDate, discountSetting{discountType, discountPercentage} } ] }
```

**Free now vs coming soon vs neither** (all three occur in the fixture):

| Case | `promotions` | Example in fixture |
|---|---|---|
| Free now | `promotionalOffers` has a group whose inner offer has `discountPercentage == 0` and `startDate <= now < endDate`; `price.totalPrice.discountPrice == 0`, `originalPrice > 0` | Knightica (2026-11-12T16:00Z -> 2026-11-19T16:00Z), `upcomingPromotionalOffers: []` |
| Coming soon (free) | `promotionalOffers: []`, `upcomingPromotionalOffers[].promotionalOffers[]` with `discountPercentage == 0` and `startDate > now` | Alien: Isolation and Mark of the Deep (2026-11-19T16:00Z -> 2026-11-26T16:00Z) |
| Upcoming **discount**, not free | same structure but `discountPercentage` of 20, 35, 55, 70 | Ghostrunner 2 (20), Monument Valley (35), LISA (55), TerraScape (70) |
| Past promo / just in the category | `promotions: null` | Them's Fightin' Herds, Eternal Threads, Luftrausers, ... |

`discountPercentage` is **the share of the price still paid** (`0` = 100% off, `20` = 20% of the price remains, i.e. a deep discount, hence the extra price check). Only `== 0` windows are giveaways.
Dates are ISO-8601 UTC. Epic's cadence is weekly (Thursday 16:00 UTC in the fixture) but the feed has also carried multi-week and daily windows during holiday events, so never assume 7 days: count down to the `endDate` of the active window, and switch the strip when `now >= endDate`.
An element may hold several windows (an active free window plus a later discount); pick the active 0% window for "now", else the earliest future 0% window for "soon".

**Images** (`keyImages[].type` counts in the fixture): `OfferImageWide` x13 (landscape, use for the strip card), `OfferImageTall` x13 (portrait poster, use for tall cards), `Thumbnail` x13 (square-ish fallback), `DieselStoreFrontWide`/`DieselStoreFrontTall` (alternate art, 1 element), `CodeRedemption_340x440`, and noise to ignore:
`featuredMedia` x103, `heroCarouselVideo` x16, `GalleryImage` x6. Preference: wide -> `OfferImageWide`, `DieselStoreFrontWide`, `Thumbnail`; tall -> `OfferImageTall`, `DieselStoreFrontTall`, `Thumbnail`. Source files are large PNGs (the 2560-wide Knightica `OfferImageWide` is **1.6 MB**) **[observed]**.
The CDN honours the store's resize parameters: `<url>?h=270&resize=1&w=480` returned a **57 KB JPEG** with 200 **[observed]**. Core downloads the resized variant once into `AppPaths.Cache` (existing key-art cache, served at `https://cache.glider/...`), so the UI needs no CSP/host change and no hotlinking. Image URLs must be `https` on `*.epicgames.com` (reject anything else).

### 5.3 Building the product URL

Slug resolution (first non-empty, valid `^[a-z0-9][a-z0-9-]{0,127}$` after cleaning):

1. `catalogNs.mappings[pageType=="productHome"].pageSlug`
2. `offerMappings[pageType=="productHome"].pageSlug`
3. `productSlug` with a trailing `/home` removed
4. `urlSlug`, only if it is **not** a 32-hex id (when it is the raw id the page does not exist)

For `ADD_ON` offers the `offerMappings` entry has `pageType:"offer"` (fixture: `lost-castle-lost-castle-the-old-ones-awaken-db1545`); link to the **base game's** `catalogNs` productHome page instead. This order is the one the community uses
(free-games-claimer comments recommend `catalogNs.mappings[0].pageSlug` over `urlSlug`, and a known case where `productSlug` was wrong; penny-launcher's `storeSlug` prefers `productSlug`, which is why mappings come first here) **[src]**.
URL: `https://store.epicgames.com/<locale>/p/<slug>` for games and add-ons; `https://store.epicgames.com/<locale>/bundles/<slug>` for `offerType == "BUNDLE"` **[unverified]** (fallback if it 404s: `/<locale>/free-games`). No slug (e.g. a "Mystery Game" placeholder during holiday events) -> not linkable, deep-link to `/<locale>/free-games`.
`<locale>` is the Store's path locale (`en-US`, `de`, `fr`, `it`, `es-ES`, `es-MX`, `pt-BR`, `pl`, `ru`, `tr`, `ja`, `ko`, `zh-CN`, `zh-Hant`, `th`, `ar`); unknown -> `en-US` (a locale-less `https://store.epicgames.com/p/<slug>` also resolves in a browser).

### 5.4 Locale, country, caching (Core `FreeGamesService`)

* `country`: ISO 3166-1 alpha-2 uppercase. Default from `RegionInfo.CurrentRegion.TwoLetterISORegionName`; overridable in Settings ("Store region"). `locale`: Glider's UI language mapped to the Store locales above. `allowCountries = country`. Note that **claiming** is decided by the *account's* region, not this parameter; the strip is informational, so a mismatch only changes prices/availability text.
* Key the cache by `country|locale`. Persist the last good response to `cache/free-games.<country>.<locale>.json` (atomic write) so the strip renders instantly at startup and works offline (mark `stale: true` when older than 24 h or when the fetch failed).
* TTL: refresh when older than **1 h**, but never later than **30 s after the nearest `startDate`/`endDate` boundary** in the cached data (so the strip flips right when Thursday 16:00 UTC passes), and at most once per 5 minutes. Triggers: app start (background, after first paint), Home page shown (if older than 15 min), `freeGames.get({force:true})` (manual refresh). Conditional requests are not available (no validators).
* Errors: HTTP/parse failure -> keep serving the cached snapshot, exponential backoff 1 -> 15 min, no user-facing error unless there is no cache at all (then the strip is simply absent). Use a plain `HttpClient` with a 10 s timeout and a generic `User-Agent: Glider`; no cookies, no auth, nothing about the user in the request.
* **In-library flag.** `FreeGameOffer.inLibrary` is computed in Core by matching `namespace` + `items[0].id` against the Legendary metadata of owned games (`metadata.namespace`, `metadata.id`, see `legendary.md` section 3); show "In your library" and do not offer a Store button for those.
* Everything from the feed is **untrusted text/URLs**: titles and descriptions render as text (never HTML), slugs and image URLs are validated as above, the parser is total (unknown/missing fields skip the element, never throw).

Fixture state when saved: **free now** Knightica; **free from 2026-11-19T16:00Z** Alien: Isolation and Mark of the Deep; others are future discounts or have no promotion. The parser test pins all three categories against this file.

### 5.5 Home strip behaviour

"Free this week" lists `now` offers first (with an end countdown), then `soon` (with the start date). The only CTA is **View in Store**, which calls `store.open({ target: { kind: "product", slug, bundle } })` and switches to the Store tab; Glider never claims.
If a game is already in the library it shows "In your library". The strip is hidden entirely when there is nothing to show (no cache and fetch failed).

---

## 6. Security

### 6.1 Isolation from Glider's IPC

* The Store `CoreWebView2` is **never** passed to `WebViewBridge.Attach` and `WebMessageReceived` is not subscribed on it. Settings: `IsWebMessageEnabled=false`, `AreHostObjectsAllowed=false`; never call `AddHostObjectToScript`, `AddScriptToExecuteOnDocumentCreated`, `ExecuteScriptAsync`, `PostWebMessage*` or `AddWebResourceRequestedFilter` on it. (Epic's page only has the standard, empty `window.chrome.webview`.)
* No `SetVirtualHostNameToFolderMapping` on it: `https://app.glider/` and `https://cache.glider/` do not exist for Epic's origin, so a page cannot even load Glider's UI or cache. (The mappings are per-`CoreWebView2`, `WebViewSetup.MapContent` is only called for the main view.)
* The existing main-view message check stays: `WebMessageReceived` accepts only `IsAppOrigin(e.Source)`. A Store page therefore has no channel into `RpcDispatcher`.
* The frontend -> host `store.*` RPCs accept **typed targets only**: `StoreTarget { kind: "home"|"freeGames"|"wishlist"|"browse"|"product"|"lastVisited", slug?: string, bundle?: bool }`. The host builds the URL (`StoreUrls.Build`, locale + validated slug). A raw URL is never accepted from the web UI, so XSS in Glider's React could not point the Store at a hostile origin or at `file:`/`javascript:`.
* Everything flowing the other way (`store` event) is sanitized: `url` is origin+path only (no query/fragment, which can hold codes), `title` is capped at 120 chars and rendered as text, no cookie or header data.
* No raw page-provided string ever reaches `Process.Start`: deep links become a `LauncherIntent` record; external opens go through `WebViewSetup.OpenExternalSafe` (http/https/mailto only).

### 6.2 Navigation allowlist (`StoreNavigationPolicy`, pure, unit-tested)

Top-level `NavigationStarting` on the Store view, in order:

1. scheme `com.epicgames.launcher` -> cancel, hand to `LauncherUriGate` (section 2).
2. `about:blank` -> allow. Any of `javascript:`, `data:`, `file:`, `blob:`, `ftp:`, `ws(s):`, custom schemes other than the one above -> **cancel**.
3. `https` + no userinfo + default port + host in the **Epic set** -> allow: `store.epicgames.com`, `launcher.store.epicgames.com`, `www.epicgames.com`, `epicgames.com`, `accounts.epicgames.com`, `help.epicgames.com`, `www.unrealengine.com` (matched exactly or as `*.epicgames.com` for hosts under `.epicgames.com`; the list is a constant in `HostConstants`, and every denied host is logged once, host only).
4. `/id/exchange` not started by Glider -> cancel (spent-code guard, 1.4).
5. Any other `https` host: if `checkoutSession` is live -> allow (payment redirects; toolbar shows host + padlock); else if `IsUserInitiated` -> cancel + open in the **system browser**; else cancel silently (an unrequested redirect away from Epic is never followed).
6. `http` -> cancel (upgrade is Epic's job; we never browse plaintext).

Also: `FrameNavigationStarting` -> cancel non-https/custom-scheme frame loads; `DownloadStarting` -> cancel; `PermissionRequested` -> deny all (camera, mic, geolocation, notifications, clipboard-read, ...); `NewWindowRequested` per 2.4; `ServerCertificateErrorDetected` -> `Cancel` (no click-through of TLS errors; show the error state); `IsBuiltInErrorPageEnabled=false` with our own error panel; `AreDevToolsEnabled`/`AreDefaultContextMenusEnabled`/`AreBrowserAcceleratorKeysEnabled = WebViewSetup.DeveloperMode`;
`IsPasswordAutosaveEnabled=false`, `IsGeneralAutofillEnabled=false` (no card or password data written into the Glider profile); `IsStatusBarEnabled=false`; `IsZoomControlEnabled=true` is fine for a browsing view (Ctrl+wheel) but not required.
InPrivate is **not** used (it would defeat the shared session).

### 6.3 Secrets

* Exchange codes: minted in Core, passed host-internally as a `string`, used once, never logged (`UrlForLog`), never serialized, never in an exception or event, never in the process command line (the stdout of `get-token --json` is in "secret output" mode). The 5-minute single-use lifetime bounds exposure.
* Cookie probe reads **names and flags only**.
* Sign-out order: `StoreView.DestroyAsync()` (close controller, drop snapshot file) -> `EpicLogin.ClearSessionAsync()` (clears `GliderEpic`) -> delete `cache/store-cover.png`. Account switch repeats it.
* The cover snapshot (`store-cover.png`) is a screenshot of the user's Store page (could show account name): it lives under `AppPaths.Cache`, is overwritten on each cover, deleted on uncover older than 1 min, on sign-out and at exit.

---

## 7. Implementation plan

Order matters: each step is independently testable; steps 1-3 need no GUI.

**Step 0 - live probe (needs the user, 10 minutes, no code to ship).** Behind a dev-only flag `Glider.exe --store-probe`, after a normal sign-in, log (cookie *names*, `IsSession`, `Expires` only) for `www.epicgames.com` and `store.epicgames.com` in `GliderEpic`, restart, log again, then open the anonymous Store and record the answers to L1-L6 in section 9. This pins the cookie name for the probe and decides whether SSO runs on every start.

**Step 1 - Core, pure logic + tests (no network).**
* `Glider.Core/Store/LauncherUri.cs`: `TryParse` -> `LauncherIntent { Kind: Launch|Install|Verify|UpdateCheck|StorePage|Library|Unknown, Namespace?, CatalogItemId?, AppName?, StoreSlug? }`; `[Theory]` tests from the table in 2.2 plus abuse cases (path form, extra schemes, `%00`, over-long, uppercase scheme, double-encoding).
* `Glider.Core/Store/StoreUrls.cs`: `StoreTarget` -> URL (locale, slug validation, bundle path), `StoreSso.Build(code, target)` (host check), `StoreRoutes` constants.
* `Glider.Core/Store/FreeGamesParser.cs`: `Parse(JsonElement, DateTimeOffset now, string locale) -> FreeGamesSnapshot` implementing 5.2-5.3 (total, no throws); tests run against `docs/research/fixtures/epic/freeGamesPromotions.json` copied/linked into `tests/Glider.Core.Tests/Fixtures` with fixed `now` values: before 11-12 (nothing), 11-14 (Knightica now; Alien/Mark soon), 11-19T16:00Z (Alien/Mark now, Knightica gone), plus hand-built edge cases (two windows, 20 % window, null promotions, no slug, 32-hex urlSlug, ADD_ON, non-https image).
* `StoreNavigationPolicy` (host project, compiled into the tests by a link like `ChromeLogic.cs`): table-driven tests for 6.2.

**Step 2 - Core services.**
* `FreeGamesService` (`Glider.Core/Store/`): fetch, disk cache, TTL/boundary scheduling (5.4), image download through the existing key-art cache, `inLibrary` flag; `IClock` and `HttpMessageHandler` injected for tests.
* `IStoreSession` / `StoreSession` (1.4) over `LegendaryCli.GetTokenJsonAsync` (new wrapper, secret-output mode) + `SecretRedactor` rule + a fake-Legendary test that asserts the code never appears in logs.
* `LibraryRefresher` (4.2) over `legendary list --json` (new typed wrapper; Core's library model is Fortnite-only today, so this arrives together with the v0.2 library work) with diff, single-flight, backoff; `StoreIntentResolver` (2.3).
* Wire into `Composition`/`GliderService`; `Prewarm` triggers `FreeGamesService.RefreshAsync` after first paint.

**Step 3 - Contract.** Add the records, RPC methods and events below to `Contracts/`, run `dotnet run --project tools/Glider.ContractGen`, extend the mock backend in `frontend/src/ipc` (`store.*` no-ops, a free-games scenario built from the fixture).

**Step 4 - Host (`Glider.App`).**
* `StoreView.cs` (new, ~400 lines): lazy `EnsureCreatedAsync()` (awaits `_rt.EnvTask`, `CreateCoreWebView2ControllerOptions()` with `ProfileName = HostConstants.WebViewProfileEpic`, `DefaultBackgroundColor` = theme background, `IsVisible=false` until the first `setBounds`), `ConfigureStoreSettings`, navigation policy hooks, `LauncherUriGate`, popup windows (reuse the `ChromeWindow` base by promoting `EpicLogin.ChromeWindow/PopupWindow` to an `internal` shared file `NativeChromeWindow.cs`), cookie probe, SSO bootstrap (1.4), checkout-session tracker (4.1), state machine + timers (3.1), `CapturePreviewAsync` cover (3.3), `StoreState` event publisher (throttled, sanitized).
* `MainWindow`: create `StoreView` lazily on `store.open`; forward size/DPI/move; add to `EnterLowMemory`/`LeaveLowMemory`/`SuspendWhenHiddenAsync`/`RecreateWebView`; `Dispose` on quit.
* `WindowsHostPlatform` gains an `IStoreHost` implementation delegating to `MainWindow.Store`; `GliderService` forwards the `store.*` RPCs through it (same pattern as `window.minimize`).
* `SelfTest`: `store.smoke` (anonymous Store loads, bounds follow the placeholder at 100/150/200 % DPI, cover/uncover, suspend/resume, deep-link events with a synthetic page served via `NavigateToString`), `store.memory` (3.2). `--selftest` is a GUI run, so it is done by the user or in a dedicated session.

**Step 5 - Frontend.**
* `state/ui.ts`: `Page = "home" | "library" | "store" | "downloads" | "settings"` (Home arrives with v0.2); Rail gets a Store item (keep the Store mounted but hidden after first visit so state is preserved; the *native* part is what the host suspends).
* `features/store/StoreTab.tsx`: toolbar + `div[data-store-host]` + states `creating | loading | ready | signedOut | error | offline`; `useNativeBounds(ref)` hook (ResizeObserver, rAF-coalesced, rounds, de-duplicates); `useCoverNative()` used by Dialog/Dropdown/Select/CommandPalette; toast anchoring (3.3).
* `features/home/FreeThisWeek.tsx`: strip with countdown (re-render each minute, switch at boundary), uses `freeGames` event + `freeGames.get`.
* Handlers: `storeIntent` -> `launch` (installed -> `game.launch`) / install sheet / navigate to game page / library; `notice kind=libraryAdded` toast with Install.
* Command palette: "Open Epic Store", "Free games this week", "Refresh library".
* Tests: `features.test.tsx` cases for the bounds hook (mock `ResizeObserver`), cover toggling, the strip's now/soon rendering from the fixture, intent routing.

**Step 6 - Docs/decisions.** Add the decisions in section 0 to `DECISIONS.md`; update `ARCHITECTURE.md` (diagram: second WebView2 child) and `docs/perf-host.md` with measured Store memory; add the security notes (6.x) to `docs/security-review.md`.

### 7.1 Exact RPC surface

Methods (`[RpcMethod("...")]` on `IGliderRpc`; records in `Models.cs`; camelCase wire format as elsewhere):

| Method | Request -> Response | Notes |
|---|---|---|
| `store.open` | `StoreOpenRequest { target: StoreTarget }` -> `StoreState` | Creates lazily, navigates; `lastVisited` resumes. |
| `store.setBounds` | `StoreBounds { x, y, width, height }` (CSS px, main-view client coords) -> `void` | Frontend coalesces to 1/frame; ignored if no Store. |
| `store.setVisible` | `{ visible: bool }` -> `void` | Tab active/inactive; drives `IsVisible`, memory level, suspend timer. |
| `store.setCovered` | `{ covered: bool }` -> `void` | Snapshot + hide / restore (3.3). |
| `store.navigate` | `{ action: "back"\|"forward"\|"reload"\|"stop"\|"home" }` -> `StoreState` | No URLs. |
| `store.openInBrowser` | `{ signedIn: bool }` -> `void` | Opens the current sanitized URL in the default browser. `signedIn=true` mints an exchange code and uses `/id/exchange` (user-initiated, v0.2.x), default `false`. |
| `store.getState` | -> `StoreState` | For late subscribers. |
| `library.refresh` | `{ reason: "manual"\|"checkout"\|"storeLeft"\|"activated"\|"startup" }` -> `LibraryRefreshResult` | Single-flight (4.2). |
| `freeGames.get` | `{ force?: bool }` -> `FreeGamesSnapshot` | Serves cache; `force` bypasses the 5-minute floor only. |

Records:

```
StoreTarget      { kind: "home"|"freeGames"|"wishlist"|"browse"|"product"|"lastVisited", slug: string|null, bundle: bool }
StoreState       { phase: "idle"|"creating"|"loading"|"ready"|"hidden"|"suspended"|"signedOut"|"error",
                   url: string|null,            // origin+path
                   host: string|null, secure: bool, title: string|null,
                   canGoBack: bool, canGoForward: bool, isLoading: bool,
                   covered: bool, coverUrl: string|null, checkoutActive: bool,
                   error: GliderError|null }
StoreIntent      { kind: "launch"|"install"|"verify"|"updateCheck"|"library"|"unknown",
                   appName: string|null, title: string|null, resolved: bool, installed: bool|null }
LibraryRefreshResult { added: LibraryAdded[], total: int, at: string }
LibraryAdded     { appName: string, title: string, namespace: string, catalogItemId: string }
FreeGameOffer    { offerId, namespace, catalogItemId, title, offerType, seller: string|null,
                   status: "now"|"soon", startsAt: string, endsAt: string,
                   wideArtUrl: string|null, tallArtUrl: string|null,        // https://cache.glider/...
                   regularPrice: string|null,                                // fmtPrice.originalPrice
                   storeSlug: string|null, storeKind: "product"|"bundle"|"none",
                   inLibrary: bool }
FreeGamesSnapshot { country, locale, fetchedAt, stale: bool, now: FreeGameOffer[], soon: FreeGameOffer[] }
```

Events (`RpcEvents` constants + `[RpcEvent]` attributes): `store` -> `StoreState` (throttled to ~10/s, only on change); `storeIntent` -> `StoreIntent`; `freeGames` -> `FreeGamesSnapshot` (on refresh); library additions reuse `notice` with a new `NoticeKind.LibraryAdded` and the regular `state` snapshot.
Host-only (not on `IGliderRpc`, no web access): `IStoreHost` (`EnsureOpen`, `SetBounds`, `SetVisible`, `SetCovered`, `Navigate`, `Destroy`) for Core -> host calls and `IStoreSession.MintWebExchangeCodeAsync` for host -> Core.

---

## 8. Alternatives considered

* **Use the launcher-mode UA/bridge for the Store and implement `ue.webmarketplace.*`:** would give EGL-style quick purchase, but needs Epic's order APIs with the bearer token (never read by Glider), reimplements private bridges, and any bridge-contract change breaks it. Rejected.
* **Inject `Authorization: Bearer` on the Store document like EGL v2** [3p]: requires the access token in the host. Rejected for the same reason.
* **Separate profile `GliderStore` seeded only by SSO:** cleaner data separation but two wipe points, one more thing to desync on account switch, and no benefit if the launcher login already leaves a session. Revisit only if the probe shows the shared profile causing trouble (e.g. login-window tracking-prevention changes affecting the Store).
* **System browser for the Store (as penny-launcher does):** simplest, but it is not an in-app experience and Home deep links cannot target a tab. It remains the always-available escape hatch (Open in browser).

---

## 9. Live-test checklist (everything marked [unverified])

Run once with a real account and a GUI session (not possible in this research task). Record results back into this file.

| # | Question | How | If the answer is "no" |
|---|---|---|---|
| L1 | After a normal Glider sign-in, which cookie names/lifetimes does `GliderEpic` hold for `.epicgames.com` and `store.epicgames.com` (names, `IsSession`, `Expires`), before and after an app restart? | `--store-probe` | Always run SSO at first open (already the fallback). |
| L2 | Does `/id/exchange?exchangeCode=<get-token code>&redirectUrl=https://store.epicgames.com/en-US/` leave the Store signed in, in a WebView2 with the default UA, and does the exchange URL stay out of history? | Manual, host log (`UrlForLog`) | Fall back to `signedOut` banner; consider `consumingClientId`/`sid` variants (`auth --sid` path in Legendary `core.auth_sid`). |
| L3 | Which of `NavigationStarting` / `LaunchingExternalUriScheme` / `NewWindowRequested` fires for a user click on the Store's Install/Launch buttons, with which URI shape, and is EGL's own prompt suppressed? | Log URIs (scheme+host+action only) with `HostLog` | Adjust the gate; make sure `Cancel=true` in the event that fires. |
| L4 | Does the web Store in plain browser mode show Install/Launch for owned games, or only "Get the launcher"/an HTTPS installer? | Visual | Keep `DownloadStarting` cancel + notice; Glider-side "Install" buttons come from the library, not the Store. |
| L5 | Real checkout URLs: iframe host/path, top-level `/purchase` path, any URL change after "Thanks for your order!"; does a free claim ever navigate? | Log navigation hosts+paths during a manual claim of a free game | Pin 4.1 signal 1 to the real values; otherwise rely on triggers 2-4 only. |
| L6 | Do payment methods (PayPal, 3-D Secure, bank redirects) use top-level redirects or popups, and does hCaptcha accept the WebView2 identity at "Place Order"? | Manual, with one paid test if the user agrees | Extend the allowlist / use `Open in browser`; enable the Edge-identity shim for the Store (1.5). |
| L7 | Memory: private WS per state (3.2). | `store.memory` self-test | Shorten the close timer; drop `featuredMedia` autoplay is not controllable, so rely on suspend/close. |
| L8 | `store/p/<slug>` and `/bundles/<slug>` URL forms, `/wishlist` path. | Visual | Fix `StoreUrls`. |
| L9 | Does `legendary list --json` list a just-claimed free game within seconds? | Claim a free game, run `list` every 5 s | Increase the poll window; add the entitlements path. |

---

## Sources

Microsoft (WebView2):
* Support multiple profiles under a single user data folder: https://learn.microsoft.com/microsoft-edge/webview2/concepts/multi-profile-support
* Multi-profile spec (`ProfileName` rules, `CoreWebView2ControllerOptions`): https://github.com/MicrosoftEdge/WebView2Feedback/blob/main/specs/MultiProfile.md
* Process model for WebView2 apps (environments, UDF, renderers): https://learn.microsoft.com/microsoft-edge/webview2/concepts/process-model
* Performance (`MemoryUsageTargetLevel`, `TrySuspend`/`Resume`): https://learn.microsoft.com/microsoft-edge/webview2/concepts/performance
* `CoreWebView2.LaunchingExternalUriScheme`: https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2.launchingexternalurischeme ; Win32 args reference https://github.com/MicrosoftDocs/webview2-win32-reference/blob/live/1-0-2535-41/icorewebview2launchingexternalurischemeeventargs.md
* `TrySuspend`/`Resume`/`IsSuspended` reference: https://github.com/MicrosoftDocs/webview2-win32-reference/blob/live/1-0-2535-41/icorewebview2_3.md
* `NewWindowRequested` args: https://github.com/MicrosoftDocs/webview2-win32-reference/blob/live/1-0-2535-41/icorewebview2newwindowrequestedeventargs.md
* `CoreWebView2Controller3` (`BoundsMode`, `RasterizationScale`, `ShouldDetectMonitorScaleChanges`): https://github.com/MicrosoftDocs/webview2-win32-reference/blob/live/1-0-2535-41/icorewebview2controller3.md

Epic:
* Protocol Activation (official): https://dev.epicgames.com/docs/epic-games-store/protocol-activation
* Free games feed (public): https://store-site-backend-static-ipv4.ak.epicgames.com/freeGamesPromotions?locale=en-US&country=US&allowCountries=US
* `GET https://www.epicgames.com/id/api/redirect` (anonymous probe of the web-session JSON shape: `redirectUrl`, `authorizationCode`, `exchangeCode`, `sid`, all `null` when signed out) - observed.

Third-party source and reverse-engineering:
* Legendary 0.21.1: `legendary/cli.py` (`get_token` 2218-2240, `_hidden_commands` 3125, `list_games` ~216-225), `core.py` (`auth_sid` 123-155, `get_game_and_dlc_list` 523-563, `get_launch_parameters` ~889), `api/egs.py` (`get_game_token` 150-154): https://github.com/legendary-gl/legendary/tree/0.21.1
* Heroic `89e06ad` `src/frontend/screens/WebView/index.tsx` (UA only for login URL, `persist:epic`): https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher
* Unofficial EGL documentation (EGL v2 reverse-engineering: `Launcher/DeepLinks.md`, `EpicGames/Web/Id/Exchange.md`, `EpicGames/AccountService/OAuth/Exchange.md`, `Launcher/Flows/Purchase.md`, `Launcher/IPC/StoreFab.md`, `EpicGamesStore/Purchase/Checkout.md`): https://github.com/Zyhloh/Unofficial-EGL-Documentation
* penny-launcher `src/kernel/core/epic-store.ts` and `features/library/free-games.ts` (exchange-code store sign-in, free-games parsing): https://github.com/ahmed5939/penny-launcher
* Epic-Asset-Manager (`/id/exchange` with `unrealengine.com` redirect): https://github.com/AchetaGames/Epic-Asset-Manager
* free-games-claimer `epic-games.js` (structure of the web checkout: `#webPurchaseContainer iframe`, hCaptcha, "Thanks for your order!"; read for structure only): https://github.com/vogler/free-games-claimer
* r2modman launch-URI change: https://github.com/ebkr/r2modmanPlus/issues/1973

Repo: `DECISIONS.md` (login entries), `docs/research/legendary.md`, `docs/research/heroic.md`, `docs/perf-host.md`, `src/Glider.App/{WebViewSetup,HostRuntime,EpicLogin,MainWindow,CaptionOverlays}.cs`.
