# Glider architecture

Glider replaces the Epic Games Launcher for one game, Fortnite. It is a thin native Windows host around
a web UI, and it drives the bundled **Legendary** CLI (v0.21.1) as the Epic protocol engine.

```
┌──────────────────────────── Glider.App (net10.0-windows, WPF) ───────────────────────────┐
│  MainWindow (frameless, Mica)  ── WebView2 ── https://app.glider/  (frontend/dist, local)   │
│  LoginWindow (WebView2 → Epic login, captures authorizationCode)                          │
│  Tray · single instance (mutex + pipe) · toasts · HKCU Run · window placement             │
│  WebViewBridge: WebMessageReceived ⇄ RpcDispatcher ; PostWebMessageAsJson ⇐ events       │
│  WindowsHostPlatform : IHostPlatform  (dialogs, UAC, shell, clipboard, toasts)             │
└──────────────────────────────────────────┬─────────────────────────────────────────────────┘
                                           │ IGliderRpc (C# interface = the IPC contract)
┌──────────────────────────── Glider.Core (net10.0, no UI deps) ───────────────────────────┐
│  Services/GliderService : IGliderRpc  — facade; StateHub builds + publishes AppSnapshot    │
│  Legendary/  process runner, typed CLI wrappers, span-based progress parser, error mapper  │
│  Jobs/       persistent serial queue: install/update/repair/verify/move/uninstall/import   │
│  Egl/        detect EGL installs, adopt, drift checks, manifest write-back with backups    │
│  Game/       folder validation, EAC check, launch, process watch                           │
│  Auth/  Settings/  Infrastructure/ (paths, atomic files, logging)                          │
└──────────────────────────────────────────┬─────────────────────────────────────────────────┘
                                           │ child process, env LEGENDARY_CONFIG_PATH=%LocalAppData%\Glider\legendary
                                    legendary.exe (bundled, SHA-256 pinned)
```

## Repository layout

| Path | What |
|---|---|
| `src/Glider.Core` | All logic. Cross-platform class library, unit-testable on any OS. |
| `src/Glider.Core/Contracts` | **The IPC contract**: `IGliderRpc`, DTO records, errors, dispatcher, JSON context. |
| `src/Glider.App` | WPF + WebView2 host. Only Windows/UI glue lives here. |
| `tests/Glider.Core.Tests` | xUnit v3 tests: parsers (real fixtures), job queue (fake Legendary), EGL scenarios. |
| `tools/Glider.ContractGen` | Generates `frontend/src/ipc/contract.gen.ts` from the C# contract. |
| `frontend` | React 19 + TS + Vite + Tailwind v4 + Radix + Motion + Zustand (pnpm). |
| `frontend/src/ipc` | Typed RPC client, WebView2 transport, mock backend (scenarios). |
| `build` | Scripts: fetch + verify Legendary, publish, Velopack pack. |
| `docs/research` | Phase-0 findings (Legendary internals, Heroic patterns, host/packaging). |

## IPC

- Web → host: `{ type: "request", id, method, params }`. Host → web: `{ type: "response", id, result, error }`
  or `{ type: "event", event, data }`. Transport: WebView2 `postMessage` / `PostWebMessageAsJson`.
- `IGliderRpc` methods carry `[RpcMethod("ns.name")]`; events are declared with `[RpcEvent]`.
  `RpcDispatcher` routes by name using the source-generated `GliderJsonContext`.
- Events: `state` (full `AppSnapshot`, coalesced), `progress` (`JobProgress`, ≤10 Hz), `notice`, `window`.
- The UI keeps one Zustand store that mirrors the latest snapshot; it never derives backend state itself.
- **Change the contract only in C#**, then run `dotnet run --project tools/Glider.ContractGen`.

## Data locations (all per-user)

| Path | Content |
|---|---|
| `%LocalAppData%\Glider\legendary\` | Legendary's isolated config (`LEGENDARY_CONFIG_PATH`): user.json, installed.json, metadata, manifests |
| `%LocalAppData%\Glider\state\` | `settings.json`, `jobs.json` (persisted queue), `game.json` (our install record + EGL link) |
| `%LocalAppData%\Glider\cache\` | Key art and metadata cache, served to the UI at `https://app.glider/cache/…` |
| `%LocalAppData%\Glider\logs\` | Rolling logs (`glider-YYYYMMDD.log`) |
| `%LocalAppData%\Glider\backups\egl\` | Timestamped copies of any EGL file before Glider writes it |
| `%LocalAppData%\Glider\webview2\` | WebView2 user data folder |

## Threading & performance rules

- The UI thread only renders. Every Legendary call is async with a `CancellationToken`.
- Legendary output is read with async stream reads into `System.Threading.Channels`; progress lines are
  parsed from spans without per-line allocations where practical; UI progress is throttled to ~10 Hz.
- Startup: the WebView2 environment is created in parallel with the window; the window is shown
  when the UI reports `app.ready` (no white flash). Pre-warm (auth refresh, metadata, update check,
  EGL drift check) runs in the background after first paint.

## EGL coexistence (summary)

One-time `legendary import` of an EGL install (no re-download), then our own drift checks on startup
(no continuous `egl-sync`). The opt-in-by-default "Keep Epic Games Launcher in sync" setting writes
version changes back to EGL's `.item` manifest exactly as Legendary's EGL export does, with a backup and
an atomic write. Write operations are blocked while EGL is busy with the game. Details: `docs/egl.md`.
