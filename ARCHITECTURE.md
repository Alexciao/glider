# Glider architecture

Glider replaces the Epic Games Launcher for the whole Epic library. It is a thin native Windows host around a web UI,
and it drives the bundled **Legendary** CLI (v0.21.1) as the Epic protocol engine. The binding design is
[`docs/v0.2-design.md`](docs/v0.2-design.md); this file maps it onto the code.

```
┌──────────────────────────── Glider.App (net10.0-windows, WPF) ───────────────────────────┐
│  MainWindow (frameless, Mica)  ── WebView2 ── https://app.glider/  (frontend/dist, local)   │
│  LoginWindow (WebView2, launcher mode → exchange code)                                     │
│  Tray (Close <game> per session / Play <last played>) · single instance · toasts · HKCU Run│
│  WebViewBridge: WebMessageReceived ⇄ RpcDispatcher ; PostWebMessageAsJson ⇐ events       │
└──────────────────────────────────────────┬─────────────────────────────────────────────────┘
                                           │ IGliderRpc (C# interface = the IPC contract, v2)
┌──────────────────────────── Glider.Core (net10.0, no UI deps) ───────────────────────────┐
│  Services/GliderService : IGliderRpc — facade; StateHub (small AppSnapshot)               │
│  Library/   LegendaryLibraryReader → GameCatalog (pure) → LibraryService (map + deltas)    │
│  Jobs/      writer lane (JobQueue), phases/segments, progress tracker, policy seams        │
│  Legendary/ process runner + AuthGate, typed CLI, parser, error classifier                 │
│  Game/      GameProfiles (quirks), SessionTracker (process tree), launch, install, EAC     │
│  Egl/       scan every .item, busy, drift, write-back with backups                         │
│  Infrastructure/ stores (settings, games, collections, onboarding), Migrations, atomic IO  │
│  Overlay/ Saves/  EOS overlay, save sync                                                   │
│  Store/     links + deep-link parser, URLs, nav policy, free games, StoreService           │
└──────────────────────────────────────────┬─────────────────────────────────────────────────┘
                                           │ child processes, env LEGENDARY_CONFIG_PATH=%LocalAppData%\Glider\legendary
                                    legendary.exe (bundled, SHA-256 pinned)
```

## Repository layout

| Path | What |
|---|---|
| `src/Glider.Core` | All logic. Cross-platform class library, unit-testable on any OS. |
| `src/Glider.Core/Contracts` | **The IPC contract**: `IGliderRpc`, DTO records, errors, dispatcher, JSON context. |
| `src/Glider.App` | WPF + WebView2 host. Only Windows/UI glue lives here. |
| `tests/Glider.Core.Tests` | xUnit v3: parsers (real fixtures), catalog (real catalog JSON), lanes, EGL scenarios, migration (real v0.1 data shapes), end to end. |
| `tests/FakeLegendary` | A scriptable legendary.exe (multi-app: `app=` on download/verify/installed) for runner and end-to-end tests. |
| `tools/Glider.ContractGen` | Generates `frontend/src/ipc/contract.gen.ts` from the C# contract. |
| `frontend` | React 19 + TS + Vite + Tailwind v4 + Radix + Motion + Zustand (pnpm). |
| `docs/research` | Phase-0 findings (Legendary internals incl. `legendary-v0.2.md`, Heroic, host/packaging, store). |

## Core modules

| Module | Responsibility |
|---|---|
| `Library/LegendaryLibrary.cs` | Reads `metadata/*.json`, `assets.json`, `installed.json`, `entitlements.json` directly (never spawns Legendary). Each parse retried 3× / 100 ms; a file that stays torn keeps its previous value; missing = unknown. Unchanged metadata files (size + mtime) are not re-parsed. |
| `Library/GameCatalog.cs` | Pure mapping to `GameSummary`: kind (`mainGameItem`, `addons/launchable`), hidden DLC and mods excluded, entitlement-only DLC, cloud saves, offline default, third party, grant date, art (`DieselGameBoxTall`/`DieselGameBox` with fallbacks + placeholder colour), update = asset build ≠ installed version. Also `Diff`. |
| `Library/LibraryService.cs` | One immutable `ImmutableDictionary<app, GameSummary>` + revision. Any change → one coalesced (50 ms) `LibraryDelta` with only the changed summaries; `library.get` flushes then returns everything (`Reset`). Cold start from `cache/library.json`. Background `list -T --json` / `list-installed --check-updates` on the metadata lane, lazy update-size dry runs, broken-install detection, `GameDetail`. |
| `Services/StateHub.cs` | Small `AppSnapshot` (app, auth, connectivity, library status, queue, sessions, storage, EGL, overlay, collections, settings, onboarding), coalesced 33 ms; forwards `progress`; holds `HubState` with per-game runtime maps (sessions, sizes, update sizes, broken). |
| `Jobs/JobQueue.cs` | **Writer lane**: one job at a time, user-ordered, persisted (`state/jobs.json` schema 2), crash-resumed. Idempotent per (AppName, Kind); a paused job blocks only its own game (and its DLC); reorder / move-to-top / priority / pause-all / resume-all / clear-finished; "installed data lock" refusals retried every 5 s; WhilePlaying holds via `IQueuePolicy`; every Legendary run is spawned with the `IResourcePolicy` profile for the effective `DownloadMode`. |
| `Jobs/JobPlan.cs`, `JobProgressTracker.cs` | Planned phases per kind, the whole-job bar split into per-step segments (monotonic), honest groups per phase (Verify / Network+Disk / Disk), averages and peaks, ≈1 Hz `JobSample`s per job (≤ 600) for `jobs.getHistory`; `WriterActivity` (pre-download phase) for the metadata lane. |
| `Legendary/AuthGate.cs` + runner | One Legendary process inside `login()` at a time; launch jumps the wait list; released at the first post-login line, at exit or after 45 s. |
| `Services/ServiceContext.cs` (`MetadataLane`) | Metadata lane: one online library command at a time, never during the writer's pre-download phase. |
| `Game/LaunchService.cs` | **Launch lane**: `launch <app> --json --skip-version-check [--offline]`, refused only when that game or its base game has a writer job; prepared launch on hover; EAC; per-session playtime into `games.json`. |
| `Game/SessionTracker.cs` | Per-game sessions: spawned PID + descendants (one Toolhelp snapshot per poll, only while sessions live) + the profile's tracked names; ends when nothing tracked remains for 5 s. |
| `Game/GameProfiles.cs` | Per-game quirks keyed by AppName; Fortnite is entry #1 (tracked stubs incl. `FortniteBootstrapper`, liveness exe, EAC setup + product id, quick-scan folders). Generic EAC/BattlEye detection from the install folder. Nothing else may branch on a game. |
| `Game/InstallService.cs` | Plan (catalog FolderName, DLC options), install (+ one writer job per DLC, base with `--skip-dlcs`), update, verify→repair→verify, move, uninstall, import of EGL installs / located folders, folder validation (app from `.egstore`). |
| `Services/JobHooks.cs` | Per-game gating (waits while that game runs, auth, EGL busy) and bookkeeping (install records, EGL write-back keyed by app, import outcomes and follow-up verify, notices with AppName/JobId). |
| `Services/EglCoordinator.cs` | Contract `EglState` from the scan + library (status, drift, title, art) and startup drift reconciliation per EGL-shared game; `egl.checkReadiness`. |
| `Overlay/` | `OverlayService` (state from files + `IOverlayRegistry`, install/update as an `Overlay` writer job, HKCU on/off, inline remove, periodic check), `OverlayFiles`, `OverlayRegistry` (HKCU seam + Windows impl). |
| `Game/PrerequisiteService.cs`, `ThirdPartyService.cs`, `GameStateFiles.cs` | Elevated once-only prerequisites (`state/prereqs.json`); Ubisoft/EA activation and launcher hand-off (`state/thirdparty.json`); tiny lazy JSON state. |
| `Links/`, `Shortcuts/`, `Tray/` | Strict `glider://` / Epic link parsers, link routing, per-user protocol registration (Epic handler backed up and restored), per-game `.lnk` shortcuts, tray view-model. Registry and shell links are host-implemented interfaces. Details: `docs/shell-integration.md`. |
| `Services/UpdateService.cs` | Per-game auto-update policy (Default → Settings): Always queues (game closed, idle when asked, retried by a timer that only exists while something waits), Ask = one notice per version with `FixAction.Update` + toast (ledger in `cache/update-notices.json`), Never = badge only; `library.updateAll`; periodic checks on the configured interval; `QueuePolicy` (WhilePlaying, idle full speed, effective mode + reason). |
| `Art/` | `ArtCache` behind `https://art.glider/{app}/{tall, tall-thumb, wide or logo}?v={hash}`: lazy download of Epic's picture resized by the CDN, `cache/art/<app>/<variant>-<hash>.jpg`, de-duplicated, 6 parallel, LRU cap, offline = 404, v0.1 files adopted, cover colour via the host's `IImageProcessor` (WPF). The host side is `Glider.App/ArtHandler.cs` (`WebResourceRequested`). |
| `Infrastructure/Migrations` | Versioned migrations (v0.1 → 2), run by `GliderRuntime` before any store loads; `state/schema.json` written last. |

## IPC

- Web → host: `{ type: "request", id, method, params }`. Host → web: `{ type: "response", id, result, error }`
  or `{ type: "event", event, data }`. Transport: WebView2 `postMessage` / `PostWebMessageAsJson`.
- `IGliderRpc` methods carry `[RpcMethod("ns.name")]`; events are declared with `[RpcEvent]`.
  `RpcDispatcher` routes by name using the source-generated `GliderJsonContext`.
- Events: `state` (small `AppSnapshot`, coalesced), `library` (`LibraryDelta`, revisioned, never coalesced by the bridge),
  `progress` (`JobProgress`, ≤10 Hz), `notice`, `window`, `caption`, `navigate`, `store`.
- The UI calls `library.get` once (reset), applies deltas in order, and calls it again on a revision gap.
- **Change the contract only in C#**, then run `dotnet run --project tools/Glider.ContractGen`.

## Store tab (embedded Epic Store)

`Glider.App/StoreView` owns a second `CoreWebView2Controller` on the main HWND (same environment + `GliderEpic` profile as sign-in, default UA, no bridge), placed over the Store page's HTML placeholder (`store.setBounds`, CSS px × host DPI). It is created lazily, hidden + low-memory when the tab is left, suspended after 60 s and closed after ~20 min idle. Core (`Store/`) decides everything that matters: `StoreUrls` (typed target → URL), `StoreNavigationPolicy` (allowlist), `EpicLauncherUri` (the one strict `com.epicgames.launcher://` parser, `Links/`; also feeds the OS protocol handler via `EpicLinkMapper`), `StoreIntentRouter` (link → game page / launch), `CheckoutRefresher` (library refresh after a checkout), `FreeGamesService` (public promotions feed, disk cache). The host talks to Core through `IStoreHost` only. Design and live-test checklist: `docs/research/store-webview.md` (§9).

## Lanes (who may run what, L§1.8)

| Lane | Where | Concurrency |
|---|---|---|
| Writer | `JobQueue` | 1 job; install/update/repair/verify/move/uninstall/import |
| Metadata | `MetadataLane` | 1; `list -T`, `list-installed --check-updates`, `info`, update dry runs; waits for the writer's pre-download phase |
| Launch | `LaunchService` | unlimited; never queued behind the writer; jumps the auth gate |
| Read | `LegendaryLibraryReader`, `LegendaryFiles` | in-process |

Saves (S lane): `Saves/SaveService` — one sync at a time, exclusive per game with writer jobs and with the game running; details in [`docs/cloud-saves.md`](docs/cloud-saves.md).

## Data locations (all per-user, `%LocalAppData%\Glider\`)

| Path | Content |
|---|---|
| `legendary\` | Legendary's isolated config (`LEGENDARY_CONFIG_PATH`): user.json, installed.json, metadata, manifests. Glider only reads it. |
| `state\saves.json` | cloud-save ledger: per game the last synced file list (hashes, mtimes) and cloud manifests |
| `backups\saves\<app>\<ts>\` | local save copies taken before every overwrite and after uploads |
| `state\schema.json` | `{schemaVersion: 2, migratedFrom, migratedAt}` (migration commit marker) |
| `state\settings.json`, `games.json`, `collections.json`, `jobs.json`, `onboarding.json` | schema 2 stores (`games.json` = per-app install record, favorite, hidden, playtime, settings, shortcuts) |
| `cache\library.json`, `cache\installed-size.json`, `cache\update-notices.json`, `cache\art\<app>\`, `cache\art\placeholders.json` | caches (safe to delete) |
| `backups\migration\v0.1-<ts>\` | byte copies of the v0.1 files before migrating |
| `backups\egl\<ts>\` | copies of every EGL file before Glider writes it |
| `logs\`, `webview2\` | rolling logs, WebView2 user data |

## Threading & performance rules

- The UI thread only renders. Every Legendary call is async with a `CancellationToken`.
- Legendary output is read with async stream reads; progress lines are parsed from spans; UI progress ≤10 Hz.
- Startup: the first snapshot and the cached library are published before any Legendary process or file parse; pre-warm
  (version, offline account, library files, running games, EGL scan + drift, job restore, auth warm-up, library sync) runs in
  the background.
- The snapshot never carries per-game lists (except the queue and EGL installs); 500 games rebuild in well under a second.

## EGL coexistence (summary)

One-time `legendary import` per game (one Import job each, zero re-download), then drift checks per EGL-shared game on
startup (no continuous `egl-sync`). "Keep Epic Games Launcher in sync" writes version changes back to EGL's `.item`
exactly as Legendary's EGL export does, keyed by app, with a backup and an atomic write. Write operations wait while EGL is
busy with that folder. Details: `docs/egl.md`.
