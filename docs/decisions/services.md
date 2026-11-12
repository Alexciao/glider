# Service layer decisions

One line of reasoning per decision (`src/Glider.Core/Services`, `Auth`, `Game`).

## Wiring & state
- **No changes to `Abstractions.cs` or `Contracts/`**: everything extra (process lookup/spawn, service query, disk info, idle signal, art cache, onboarding store) is a service-layer seam in `Services/Seams.cs`, passed through optional `GliderServiceOptions` with real defaults.
- **`DeferredJobHooks` / `DeferredEventSink`**: the queue needs hooks and the dispatcher needs the service, so the host breaks both construction cycles with settable proxies.
- **Snapshot = cached hub state + store `Current` values + `IJobQueue.Snapshot()`**: `GetSnapshot` never spawns or does I/O beyond memory.
- **`state` coalescing is trailing-edge over 33 ms via `TimeProvider` timers**: the first change opens a window and the publish carries the latest state, so it never exceeds ~30/s. `StartAsync` publishes once immediately for first paint.
- **`progress` is forwarded unthrottled**: the queue already throttles it to ~10 Hz.
- **`InstallStatus.Broken` is computed when the record changes and at startup** (a single `File.Exists`), not on every snapshot.
- **Onboarding flag lives in `state/onboarding.json`, not Settings**: "Reset settings" mustn't replay onboarding. An existing install also counts as onboarded. Step = Welcome (signed out) / ChooseInstall (signed in) / Done.

## Startup
- **Pre-warm order**: legendary version → `status --offline` → EGL scan + drift → online refresh → `info` → restore jobs → Warm → evaluate updates. Each step is best-effort and logged.
- **The drift-triggered verify and auto-update are queued only after `RestoreAsync`**, so the persisted queue is loaded before anything is added.
- **EglNewer → `import` only when no job is active and EGL isn't busy**, otherwise retry next start, so it never races Legendary's lock. EglMissing → unlink (`EglInstallationGuid = null`).
- **Periodic checks re-scan EGL too** and clamp the interval to 5–1440 min.

## Auth
- **Network failures on refresh keep the offline answer; AuthExpired/AuthFailed/NotSignedIn mark `Expired`**.
- **`AvatarUrl` is always null**: Epic has no public avatar endpoint.
- **Never `auth --import`**: it spends EGL's refresh token and logs EGL out.
- **Sign-out is refused while a network job is *running*** (not queued/paused).

## Install / adopt
- **Default base = `<fixed drive with most free space>\Games`, not `%ProgramFiles%\Epic Games`**: Legendary runs unelevated and can't create folders under Program Files.
- **Space margin = max(2 GiB, 3 %) on top of the install size**.
- **`PlanInstall` works signed-out/offline with cached or zero sizes** (empty version) instead of failing. `Install` itself requires sign-in.
- **`ValidateFolder` accepts the folder, its `<FolderName>` child, or its `Fortnite` child**. The version comes from EGL's `.item` (exact) and falls back to `Engine\Build\Build.version` (`BranchName-CL-<Changelist>`, display only and never compared).
- **Adopt runs `import` inline (it's fast, and the result needs the new GameState), refusing with `Busy` if any job is active**.
- **Auto verify policy: skip only if the EGL item is complete, not flagged for validation, and its version == latest**. Every other case, plus `Background`/`Full`, queues a Verify. `Full` is enforced by launch refusing while any job is queued.
- **One job at a time from the RPC side**: game operations refuse with `Busy` if any job is active, except a repeat of the same kind, which returns the existing job (idempotent).
- **Uninstall/Move/Update/Verify refuse while the game runs**. Hooks also refuse every write job (all but Import) at run time.
- **Moving a shared install with sync off clears the EGL link** and shows a warning notice. EGL's item is left untouched.

## Jobs
- **Toast = `ShowToast` when `Settings.Notifications` and the window isn't focused** (`IHostPlatform` exposes focus, not visibility). The in-app `notice` is always sent.
- **The EGL write-back runs after Install/Update/Repair only if sync is on and the folder has an EGL item** (record guid or `FindByLocation`). A failure is a warning notice, not a job failure.
- **A successful Verify clears `NeedsVerification` in our record.** Install/Update/Repair take Legendary's flag as-is.

## Launch
- **EAC: registry check for the `EasyAntiCheat_EOS` / `EasyAntiCheat` service** (`HKLM\SYSTEM\CurrentControlSet\Services\…`), with no NuGet and no `sc` spawn. The result is cached per session once positive.
- **EAC setup = `FortniteGame\Binaries\Win64\EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe install <productid>`**. The product id comes from the adjacent `Settings.json`, falling back to `prod-fn`, and an EAC entry in `manifest.prerequisites` overrides both. Confidence is high for the path and `prod-fn` (Epic's Fortnite help article) and medium for the Settings.json lookup (easy.ac's generic instructions).
- **A missing EAC setup exe logs a warning and launches anyway**: the game's own bootstrapper reports EAC problems.
- **`LaunchResult.EacInstalled` = "Glider ran the EAC setup during this launch"**, so the UI can say so.
- **`skipVersionCheck` = we checked within 15 min and saw no update**. Otherwise Legendary checks and refuses out-of-date builds.
- **Process watch = name polling every 1.5 s with a `Tick()` state machine**: Launching lasts while a stub (FortniteLauncher / `_EAC_EOS` / `_EAC` / `_BE`) is alive, until Shipping appears, or until 120 s. A stub that exits without Shipping gets a 10 s grace before LaunchFailed. Shipping missing on 2 polls gives Closing → Idle.
- **The window is hidden to tray only if *we* launched and the setting is on**, and restored only if we hid it. A game already running at startup is tracked but the window isn't hidden.
- **Game closed → try the auto-update** (the natural idle moment).

## RPC hygiene
- **Every method body goes through `Rpc(...)`**: `GliderException` passes through, cancellation becomes `Cancelled`, argument/format/key errors become `InvalidRequest`, and anything else is logged and becomes `Unknown`.
- **`OpenExternal` accepts only http(s). `OpenPath` opens any existing folder but only files under Glider's data root** (shell-opening an arbitrary file could execute it).
- **Logs are prefixed with a diagnostics header (versions, OS, settings, EGL, record, jobs — no account id or tokens)** and the tail is regex-redacted for `-AUTH_PASSWORD=`, `access_token`, `refresh_token`, `authorizationCode`, `exchange_code`, `"code"`.
- **License texts load from `<exe>\licenses\<id>.txt`** when the build ships them. Otherwise `Text` is null and the UI links to `Url`.
