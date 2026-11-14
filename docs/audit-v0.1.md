# Glider v0.1.5 audit (input for the v0.2 multi-game refactor)

Audited 2026-11-14 at `021fdd9` (v0.1.5, 132 commits). Read: ARCHITECTURE.md, DECISIONS.md, todo.md, README.md,
docs/*.md, docs/decisions/*, docs/research/*, git log, and the code below. Paths are repo-relative; `file:line`
points at the current tree. "Real" means observed against Epic/Fortnite on this machine (from the read-only logs
under `%LocalAppData%\Glider\logs`); "tested" means unit/e2e tests with fakes.

## 0. Headline findings

1. **Everything is keyed to one app** through `FortniteLayout` (`src/Glider.Core/Abstractions.cs:288-299`),
   `ServiceContext.App` (`src/Glider.Core/Services/ServiceContext.cs:8`) and `JobQueueOptions.AppName`
   (`src/Glider.Core/Jobs/JobQueue.cs:18`). The contract has a singular `AppSnapshot.Game`, singular `game.*` RPCs with
   no app id, and `JobRequest`/`JobInfo` carry no app name. On disk there is one `state/game.json`.
2. **Download modes (Background/Balanced/Full), IO/memory priority, adaptive idle→full and a speed limit do not
   exist.** What exists: a Job Object with a *below-normal CPU priority-class limit* for download/verify runs
   (`src/Glider.Core/Legendary/NativeMethods.cs:103-122`), static AutoTune workers/cache, and three Settings overrides
   (workers, shared memory, CDN). No test asserts the priority at all.
3. **Real-world launch tracking is broken on current Fortnite (43.10)**: Legendary resolved `FortniteBootstrapper.exe`,
   which is not in `ProcessWatcher.LauncherNames` (`src/Glider.Core/Game/ProcessWatcher.cs:16-21`); the log shows
   `started FortniteBootstrapper.exe … eacInstalledNow=False` then `WRN [launch] The game process never appeared`
   (2026-11-14 10:04:40 → 10:06:09). `playtimeSeconds` is still 0 in the real `game.json`.
4. **Real coverage so far** (from this machine's logs): sign-in via `auth --token` (launcher-mode window), adopt via
   `import` of a located folder, Verify → `repair` (35.5 GB in 87 min, `--max-workers 16 --max-shared-memory 3072`) →
   verify passed, launch command resolution ×3. **Never run for real**: fresh install, update, move, uninstall,
   pause/resume/crash-resume, EGL drift/write-back (`backups/egl` is empty), EAC setup (service was already present).
5. **Full-snapshot pushes will not scale to 300–500 games** (§5.2); the job queue's per-kind idempotency
   (`JobQueue.cs:111`) would hand game B the job of game A once jobs carry multiple apps.
6. **CI is green on GitHub; locally everything passes except `pnpm test` under the machine's default Node 20.20**
   (vitest workers crash in undici 8, which needs Node >= 22.19; `frontend/package.json` has no `engines` guard). With Node 24.22.2 it passes (187 tests).

## 1. What exists

### 1.1 Core (`src/Glider.Core`, net10.0, no UI deps)
| Module | One line |
|---|---|
| `Abstractions.cs` | Seams: `AppPaths`, `IHostPlatform`, `ILegendary`, `GameRecord`, `IEglService`, `IJobQueue`, `IJobHooks`, `FortniteLayout`. |
| `Contracts/` | IPC contract: `IGliderRpc` (37 methods + 5 events, `IGliderRpc.cs:9-76`), DTOs (`Models.cs`), `Errors.cs` (all user copy), `RpcDispatcher`, `GliderJson` source-gen context. |
| `Auth/AuthService.cs`, `EpicSignIn.cs` | Exchange-code sign-in (`legendary auth --token … --disable-webview`), offline/online status, sign-out. |
| `Legendary/LegendaryCli.cs` | Typed `ILegendary` over legendary.exe: argv recipes, MemoryError retry, repair-and-update fallback, update dry run, move (copy + `move --skip-move`). |
| `Legendary/LegendaryProcessRunner.cs` | Spawns legendary in a Job Object, global exclusive lock for mutating commands, stall watchdog, PipeReader line pump, tail + facts. |
| `Legendary/ProgressParser.cs`, `LegendaryOutputFacts.cs`, `ErrorClassifier.cs` | Span-based progress parsing, whole-run facts (write failures, SDL failure…), output→`GliderError` mapping (exit codes ignored). |
| `Legendary/LegendaryFiles.cs`, `LegendaryConfigFile.cs` | Direct reads of `installed.json`/`user.json`/SDL cache; merge-edit of `config.ini` `[Legendary]` keys. |
| `Jobs/JobQueue.cs` | Serial persistent queue (`state/jobs.json`): install/update/repair/verify(+repair)/move/uninstall/import, pause=kill, resume=rerun, crash-resume. |
| `Jobs/JobProgressTracker.cs`, `ProgressSmoother.cs`, `ProgressThrottle.cs` | Monotonic per-phase percent, EMA speeds/ETA, ≤10 Hz events. |
| `Egl/EglService.cs` (+ `EglManifest`, `PyJson`, `EglFileTransaction`, `LauncherInstalledDat`, `InstallLocator`, `FortniteVersion`) | Detect EGL installs, busy detection, drift, write-back with backups/atomic/rollback, quick drive scan. |
| `Game/InstallService.cs` | Plan/install/validate/adopt/update/verify/move/uninstall RPC logic, "one job at a time" gate. |
| `Game/LaunchService.cs`, `ProcessWatcher.cs`, `EacService.cs`, `PlaytimeTracker.cs` | Launch (prefetched `launch --json`, Glider spawns the exe), EAC check/setup, name-polling run state, local playtime. |
| `Game/FolderInspector.cs`, `Authenticode.cs` | Locate a game folder from any nearby folder; signer check before running EAC elevated. |
| `Services/GliderService.cs` | `IGliderRpc` facade; every method wrapped by `Rpc(...)` error hygiene. |
| `Services/StateHub.cs` | Builds `AppSnapshot`, coalesced (33 ms trailing) `state` pushes; forwards `progress`/`notice`. |
| `Services/Prewarm.cs`, `UpdateChecker.cs`, `JobHooks.cs`, `InstalledSizeMeter.cs`, `FileStores.cs`, `Seams.cs`, `Diagnostics.cs` | Startup sequence, update checks/auto-update, post-job record/EGL/notices, size walk, art cache/onboarding stores, process/disk/idle seams, log header. |
| `Infrastructure/` | `AtomicFile`, `JsonFileStore` (ordered saves + `.bak`), `SettingsStore`, `GameRecordStore`, `FileLog`, `AutoTune`, `DeleteGuard`, `SecretRedactor`. |

### 1.2 App (`src/Glider.App`, WPF + raw CoreWebView2Controller)
`Program.cs` (args: `--selftest`, `--login-audit`, `--login-debug`, `--data-dir`, `--background`), `Composition.cs`
(wires `GliderRuntime` + `WindowsIdleSignal`), `MainWindow*.cs` (frameless, Mica, cloaked until `app.ready`),
`CaptionOverlays.cs`/`ChromeLogic.cs` (Snap Layouts + resize strip HWNDs), `WebViewSetup.cs`/`WebViewBridge.cs`
(virtual hosts `app.glider`/`cache.glider`, message pump), `EpicLogin.cs` (launcher-mode login + popups),
`LoginAudit.cs`/`LoginDebug*.cs` (diagnostics), `WindowsHostPlatform.cs` (dialogs, UAC, clipboard, toasts), `TrayIcon.cs`,
`SingleInstance.cs`, `StartupRegistration.cs` (HKCU Run), `WindowPlacementStore.cs`, `AppUpdater.cs` (Velopack/GitHub),
`MemoryTrimmer.cs`, `StartupMeasure*.cs`, `SelfTest.cs`, `HostSettingsPeek.cs` (reads theme before Core starts).

### 1.3 Frontend (`frontend/src`, React 19 + Zustand + Motion)
`ipc/` (`client.ts`, `rpc.ts`, generated `contract.gen.ts` 432 lines, `mock/` backend 1074 lines + `sim.ts` + 12 scenarios),
`state/` (`store.ts` snapshot mirror with structural sharing, `bridge.ts` events→stores, `derive.ts` hero model and copy
tables, `actions.ts` RPC calls + optimistic patches, `progress.ts` 10 Hz store + sparkline history, `ui.ts`, `notices.ts`),
`app/` (Shell, Rail, TitleBar, ActivityPill, CommandPalette, Notices, Splash, boot), `features/` (library, downloads,
onboarding, settings, dialogs, common), `ui/` (primitives), `dev/` (DevSwitcher, stress data). 9 vitest files, 6 Playwright specs.

### 1.4 How jobs, state and Legendary invocation work
- **RPC → job**: e.g. `game.update` → `InstallService.UpdateAsync` → refuses with `Busy` if any job is active
  (`InstallService.cs:296`) → `IJobQueue.Enqueue(JobRequest)` (idempotent per *kind*, `JobQueue.cs:111`) → `jobs.json`
  persisted → `LoopAsync`/`Claim` runs jobs strictly in order; a paused job blocks everything behind it (`JobQueue.cs:439`).
- **Execution**: `ExecuteAsync` (`JobQueue.cs:584-613`) maps kinds to `ILegendary` calls with the hard-wired app name;
  `IJobHooks.BeforeRunAsync` gates (EGL busy → `Waiting` + `BlockedReason`, game running), `OnSucceeded/OnFailed`
  update `game.json`, EGL write-back and notices (`JobHooks.cs`).
- **Legendary invocation**: `LegendaryProcessRunner` spawns with `LEGENDARY_CONFIG_PATH=%LocalAppData%\Glider\legendary`,
  cwd = config dir, stdin closed, Job Object KILL_ON_JOB_CLOSE (+ below-normal priority limit when `LowPriority`),
  `Exclusive` commands serialized by one global lock, stall watchdog 90 s (`LegendaryCli.cs:11`), outcomes from parsed
  output only. Install argv: `-y install|update|repair <app> [--base-path p] --skip-sdl --skip-dlcs [--update-only]
  [--repair-and-update] [--max-workers n] [--max-shared-memory MiB] [--preferred-cdn host]` (`LegendaryCli.cs:568-613`).
- **State**: `StateHub.BuildSnapshot` (`StateHub.cs:198-223`) composes `HubState` (auth, EGL scan, latest version,
  sizes, run state, art), `SettingsStore.Current`, `GameRecordStore.Current` and the whole `IJobQueue.Snapshot()` (up to
  20 finished jobs, `JobQueue.cs:34`) and pushes it whole on every change, trailing-edge coalesced (≤~30/s). `progress`
  is a separate ≤10 Hz event. The UI replaces its snapshot with structural sharing (`frontend/src/state/store.ts:33-37`).

## 2. Fortnite-specific code inventory

Verdict key: **G** = becomes generic code (driven by Legendary metadata / app id), **P** = per-game profile/quirks entry,
**D** = delete or replace.

### 2.1 Core
| Where | What | Verdict |
|---|---|---|
| `Abstractions.cs:288-299` `FortniteLayout` | `AppName`/`Title` = "Fortnite", shipping exe `FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe`, process name, `LooksLikeInstall` | G: app/title from metadata, exe from `installed.json executable`; P: optional "liveness exe" + "looks like install" probe |
| `ServiceContext.cs:8` `App = FortniteLayout.AppName`; `:38` running = shipping process found; `:129` broken = shipping exe missing | single app + exe-based checks | G (per `GameRecord`), P for the probe exe |
| `JobQueue.cs:18` `JobQueueOptions.AppName`; `:586,:620,:684,:761,:857,:862,:892` uses it; `:689` comment `<base>\Fortnite` | queue is one-app | G: app id on `JobRequest`/`PersistedJob` |
| `JobQueue.cs:892` game folder = `basePath + AppName` | install folder assumed = app name | G: catalog `FolderName` (already used in `InstallService.cs:21`, `JobHooks.cs:44`) |
| `StateHub.cs:249-250` `GameState(FortniteLayout.AppName, FortniteLayout.Title, …)` | one GameState | G |
| `InstallService.cs:21` FolderName fallback "Fortnite"; `:198,:207,:247,:252` copy | | G (copy uses title) |
| `Game/FolderInspector.cs:21,31,47,59-72` | "Fortnite"/`FortniteGame` hints, child named `Fortnite`, 4-level walk-up from `Win64` | G walk-up + P layout hints |
| `Game/EacService.cs:17-19,101` | services `EasyAntiCheat_EOS`/`EasyAntiCheat`, setup `FortniteGame\…\EasyAntiCheat_EOS_Setup.exe`, product id `prod-fn`, `Settings.json` lookup | G: EAC/EOS prerequisite from manifest `prereq_info`; P: setup path + product id per game |
| `Game/ProcessWatcher.cs:16-21,80,98` | stub names `FortniteLauncher`, `…_EAC_EOS`, `…_EAC`, `…_BE`; polls shipping name | G: process-tree tracking of the spawned PID; P: stub/real exe names (add `FortniteBootstrapper`) |
| `Game/LaunchService.cs:131,186-188,240` | skip-version-check needs `LooksLikeInstall`; Corrupted if shipping exe missing; `CloseGame` kills Fortnite names | G + P |
| `Egl/EglService.cs:33-34` busy reasons "…Fortnite…"; `:196-199` `FortniteItems` (AppName==Fortnite, DLC excluded); `:220` `.dat` filter; `:327` mancpn AppName; `:693` DisplayName fallback | EGL scan is one-app | G: scan all items keyed by AppName (bulk import) |
| `Egl/LauncherInstalledDat.cs:101-110` patch only the Fortnite entry | | G (match AppName + location) |
| `Egl/InstallLocator.cs:12-17,32-40` quick-scan folders `\Fortnite`, `\Epic Games\Fortnite`… | | P (per-game folder names) or G (scan `*\Epic Games\*` for `.egstore`) |
| `Egl/FortniteVersion.cs` | CL-based version ordering of `++Fortnite+Release-x.y-CL-n` | G: generic "CL if present else component compare"; Fortnite format is just a test case |
| `Legendary/LegendaryCli.cs:332` SDL error copy; `:439` move precheck `LooksLikeInstall`; `:484` marker text | | G |
| `Legendary/LegendaryCli.cs:579-583` always `--skip-sdl --skip-dlcs` | SDL/install tags only exist for a few titles; DLCs matter for other games | P (SDL policy, tag choice) + G (DLC handling) |
| `Legendary/LegendaryConfigFile.cs:8` keeps `[Fortnite] install_tags` | must keep every `[<app>]` section | G |
| `Legendary/ErrorClassifier.cs:113,118,131,168,247` | "Fortnite" in copy | G (title param) |
| `Contracts/Errors.cs:36,42,46,51,56,60,63,66,69,87` | user copy names Fortnite | G (title param) |
| `Contracts/Models.cs:184` `ExecutableFound` doc = Fortnite shipping exe; `:297` example path | | G |
| `Services/JobHooks.cs:56,72-73,96,145,166` notices "Fortnite is ready to play/up to date/moved…" | | G |
| `Services/UpdateChecker.cs:5,43,226` `info Fortnite --json`, notice title | | G (and a different mechanism, §5.6) |
| `Services/FileStores.cs` `HttpArtCache` (one `art.json`, Diesel key-image types) | | G per app |
| `Auth`, `Infrastructure`, `Legendary/LegendaryProcessRunner` | game-agnostic | keep |

### 2.2 Contract (`Contracts/Models.cs`, `IGliderRpc.cs`)
- `AppSnapshot.Game: GameState` (singular, `Models.cs:18-25`); `GameState` mixes catalog data (title, art, latest
  version, sizes), install state and run state (`Models.cs:68-90`).
- `game.*` RPCs take no app id (`IGliderRpc.cs:42-59`); `InstallRequest(BasePath)`, `AdoptRequest(CandidateId, Path,
  Verify)`, `MoveRequest`, `UninstallRequest` are app-less (`Models.cs:292-339`).
- `JobInfo` has no `AppName` (`Models.cs:126-139`); `EglState.SyncAvailable/ManifestPath` describe "the" install
  (`:164-176`); `InstallCandidate` has no `AppName` (`:178-190`); `Settings.LaunchArgs`, `EglSync`,
  `MinimizeToTrayWhilePlaying` are global (`:200-219`).
- `InstallPlan.GamePath` example `D:\Games\Fortnite`; `LaunchResult.EacInstalled`; `FixAction.CloseGame` assumes one game.

### 2.3 App
- Nothing names Fortnite except the login window's `EpicGamesLauncher/…` UA (generic) and tray/toast text coming from
  Core notices. Single-instance/pipe has no protocol handling (`com.epicgames.launcher://` is a v0.2 item).

### 2.4 Frontend (all **G** unless noted)
- Copy: `app/CommandPalette.tsx:76-81` ("Play/Update/Install/Repair Fortnite"); `state/derive.ts:117-125` ("Starting
  Fortnite", "Fortnite is running/closing"); `features/onboarding/Onboarding.tsx:82,190,198-199,232,235,282,289,297,311,
  335-338`; `features/settings/Settings.tsx:176,249,287,306,354,379-380,395,407`; `features/downloads/Downloads.tsx:127-135`;
  `features/downloads/ActiveJobCard.tsx:38`; `features/dialogs/Dialogs.tsx:42,185`; `features/library/LibraryParts.tsx:172-177`;
  `features/common/InstallPlanner.tsx:55,113`; `features/settings/AboutPanel.tsx:42` (trademark line, keep/expand).
- Logic: `Dialogs.tsx:188` move target = `joinPath(base, "Fortnite")` (hard-coded folder name, should come from Core);
  `derive.ts` `deriveHero(s)` reads `s.game` (one hero); `state/format.ts` `prettyVersion` ("first dotted number",
  tuned to Fortnite build strings); every page selects `s.game`.
- Mock: `ipc/mock/constants.ts:11-34,63-67` (Fortnite versions/paths/reasons), `ipc/mock/scenarios.ts:38-51`
  (12 single-game scenarios), `ipc/mock/backend.ts:818-990` (single-game handlers), `ipc/mock/errors.ts`,
  `ipc/mock/logs.ts`, `dev/stress.ts` (worst-case data around one game).

## 3. Spec gaps in v0.1 itself

### 3.a Download modes and resource priority — **missing** (one partial piece present but untested)
| Item | Status | Evidence |
|---|---|---|
| Background / Balanced / Full speed modes | **missing** | no setting, no contract field, no UI; `Settings` has only workers/shared memory/CDN (`Models.cs:200-205`) |
| CPU priority on Legendary's whole tree | **present, untested** | Job Object `JOB_OBJECT_LIMIT_PRIORITY_CLASS` = BELOW_NORMAL (`NativeMethods.cs:115-119`), applied when `LowPriority` (`LegendaryProcessRunner.cs:267,279`); fallback `PriorityClass=BelowNormal` if job assignment fails (`:431-436`). Set for install/update/repair (`LegendaryCli.cs:301`), verify (`:370`), update dry run (`:357`); **not** for import (`:382-386`), move copy, uninstall, info, launch. No test references `LowPriority`/`BelowNormal`. Always on; there is no "full speed" escape. |
| IO priority (very low / low) | **missing** | no `NtSetInformationProcess(ProcessIoPriority)` or background mode anywhere |
| Memory priority | **missing** | no `SetProcessInformation(ProcessMemoryPriority)`; no EcoQoS/power throttling |
| Worker caps per mode | **missing** | `AutoTune.Compute` is static per machine: workers `clamp(2×CPU, 6, 16)` capped 4/8 under 4/8 GiB (`AutoTune.cs:35-45`); user override via `Settings.DownloadWorkers` |
| Shared memory per mode | **missing** | AutoTune 1/2/3/4 GiB by RAM (`AutoTune.cs:51`); MemoryError retry (`LegendaryCli.cs:310-320`); user override `Settings.SharedMemoryMb` |
| Adaptive "idle → full speed" | **missing** | `IIdleSignal` (`Services/Seams.cs:52`, Win32 `GetLastInputInfo` in `App/Composition.cs:36-60`) is used only to gate auto-update start (`UpdateChecker.cs:242`); nothing changes a running download |
| Speed limit | **missing** | Legendary 0.21.1 has no rate-limit flag (`docs/research/legendary.md` §8). Options for v0.2: Job Object `JobObjectNetRateControlInformation` (Win10 1607+, verify it works unelevated for the PyInstaller tree), or worker-count throttling with restart |
| Changing mode on a running job | **missing** | would need kill+resume (pause semantics) or live job-object limit changes (priority/CPU-rate/net-rate are settable on a live job) |

### 3.b Disk-bound update optimizations — **mostly missing**
- Flags actually passed (`LegendaryCli.cs:568-613`): `-y`, `--base-path` (fresh install), `--skip-sdl`, `--skip-dlcs`,
  `--update-only` (update), `--repair-and-update` (retry only), `--max-workers`, `--max-shared-memory`,
  `--preferred-cdn` (only if the value starts with a letter/digit). Real run: `-y repair Fortnite --skip-sdl --skip-dlcs
  --max-workers 16 --max-shared-memory 3072`.
- Not used: `--enable-reordering` (lower RAM need), `--disable-delta-manifests` / `--disable-patching` (delta + patching are
  Legendary's defaults, so they are *on*; nothing chooses them per disk type), `--dl-timeout`, `--bind`, `--no-https`,
  `-A` API timeout. Legendary has no read/write buffer knobs; verify is single-threaded SHA-1 with 1 MiB reads.
- No disk-type detection (HDD vs SSD) and no write-bound tuning; `Settings.PreferredCdn` options are a hard-coded
  frontend list (`Settings.tsx:241`, todo: `AppInfo.AvailableCdns` not done).
- Real datapoint: the repair downloaded 35,483 MiB in 5,238 s (~6.8 MiB/s), with long stretches where chunks advanced at
  "0.0 MiB/s" network (23:40-23:47) — consistent with a disk- or patch-bound tail; never profiled.
- Update size: dry run `update --skip-sdl --skip-dlcs --update-only` without `-y`, stopped at the summary
  (`LegendaryCli.cs:350-361`), tested (`DownloadSizeTests`, `UpdateDryRunTests`), not run for real (no update yet).

### 3.c Core flows
| Flow | Status | Evidence |
|---|---|---|
| Sign-in (launcher-mode window → exchange code → `auth --token`) | **works for real, tested** | `EpicSignInTests` (2), `LoginDebugRedactionTests`; real `auth --token *** --disable-webview` + `[auth] signed in` 2026-11-13 22:17:35. Social-login popups: manual only. Token refresh `status --json` ran for real once. |
| Sign-out (`auth --delete` + profile wipe) | present, tested with fakes | `ServiceTests`; not run for real |
| Fresh install | present, tested with fakes | `EndToEndTests.SignInThenInstallWithProgressEndsReady`, `JobQueueTests`; **never run for real** (needs ~50+ GB) |
| Adopt (EGL candidate or located folder → `import`) | **works for real (located folder), tested** | real `import Fortnite D:\EpicLibrary\Fortnite` ("`.egstore` folder exists but manifest file is missing") then auto Verify; EGL-candidate adoption only in tests (`EndToEndTests.AdoptingAnEglInstall…`, `EglOtherDriveTests`) |
| Verify → repair → verify | **works for real, tested** | real: 190 corrupted + 247 missing → repair 87 min → verify passed; `VerifyRepairTests` (6) |
| Update | present, tested | `UpdateCheckTests`, `GameTests`; never run for real; launch-time "out of date" does not flip `UpdateAvailable` (DECISIONS) |
| Pause / resume | present, tested with fakes | `JobQueueTests.PauseKillsLegendary…`, `EndToEndTests.PauseKillsTheDownload…` (FakeLegendary process); never for real |
| Cancel (fresh install deletes resume file + created folder; update keeps it) | present, tested | `JobQueueTests:476,517` |
| Crash resume | present, tested | `JobQueueTests.ARunningJobResumesAfterRestart`, `RestoreKeepsPausedJobsPaused…`; never for real |
| Stall watchdog / disk full / locked files | present, tested with fixtures | `JobQueueTests.StallFails…`, `ErrorClassifierTests`; disk-full/network-loss end-to-end not reviewed (todo) |
| Move (cross-volume copy + `move --skip-move`) | present, tested | `GameTests`; never for real |
| Uninstall (Glider-only / delete files) | present, tested | `EndToEndTests.RemoveFromGliderOnly…`; never for real |
| EGL detection + quick scan | present, tested | `EglDetectionTests`, `EglOtherDriveTests`, `EglHostileInputTests` |
| EGL drift (EglNewer → re-import, EglMissing → unlink, Unreconcilable → verify) | present, tested | `EglDriftTests`, `EndToEndTests.EglNewerVersion…`; never against a real EGL update |
| EGL write-back (`.item`, `.manifest`, `.mancpn`, `.dat` patch, backups, rollback) | present, tested with Legendary-generated goldens | `EglWriteBackTests` (12); `backups/egl` empty here → never ran for real |
| EGL busy wait | present, tested | `EglBusyTests`, `EndToEndTests.JobWaitsWhileEglIsBusy…` |
| Launch (prefetched `launch --json`, Glider spawns) | **command works for real; tracking fails** | 3 real `launch --json`; `FortniteBootstrapper.exe` started; watcher never saw `FortniteClient-Win64-Shipping` within the window → `LaunchFailed` notice; playtime 0. `LaunchTests` (17) all use fake process names. |
| EAC check / setup | check works for real (service present), setup **never run** | `EacService.cs:26-35` registry check returned installed; signer allow-list unverified (todo) |
| Playtime | present, tested, broken in practice by the watcher | `PlaytimeTests` (4) |
| Auto-update when idle | present, tested | `UpdateChecker.cs:242`; never for real |
| Self-update (Velopack) | present, untested against a real release | `AppUpdater.cs` |

## 4. v0.1 user data inventory (`%LocalAppData%\Glider\`)

The data root **is also Velopack's install root** (`Glider.exe`, `Update.exe`, `current\`, `packages\` live beside the
data on this machine), so uninstalling Glider deletes Legendary auth, logs and EGL backups (todo, DECISIONS). The
v0.2 migration is the moment to move data out.

| Path | Writer | Schema version | Notes |
|---|---|---|---|
| `state\settings.json` | `SettingsStore` via `JsonFileStore<Settings>` (`SettingsStore.cs:33,45`) | **none**; loaded field by field over defaults | `.bak` kept. **Absent on this machine** (settings never changed). |
| `state\game.json` | `GameRecordStore` (`GameRecordStore.cs:17`), `EngineJsonContext` camelCase | **none** | `.bak` kept. Single record. |
| `state\jobs.json` | `JobQueue` (`JobQueue.cs:83`) | `version: 1` (`PersistedJobs.cs:9`) | `.bak` kept; written on every state change, progress ≤ every 10 s |
| `state\onboarding.json` | `FileOnboardingStore` (`FileStores.cs:34-52`) | none | `{"completed": bool}`; **absent here** (an installed record counts as onboarded) |
| `state\window.json` | `WindowPlacementStore` (App) | `version: 1` | `{version, maximized, left, top, right, bottom}` (physical px) |
| `cache\art.json` + `cache\art\<sha256(url)[:16]>.<ext>` | `HttpArtCache` (`FileStores.cs:57-170`) | none | `{heroUrl, tallUrl, logoUrl}` as `https://cache.glider/art/…` |
| `cache\installed-size.json` | `InstalledSizeMeter` (`InstalledSizeMeter.cs:27,194`) | none | `{path, version, bytes, measuredAt}` (one entry) |
| `legendary\` | legendary.exe (`LEGENDARY_CONFIG_PATH`) | Legendary's | `user.json` (**tokens, never read here**), `installed.json`, `config.ini`, `metadata\`, `manifests\`, `tmp\` (resume/`.repair` files), `assets.json`, `entitlements.json`, `aliases.json`, `version.json` |
| `logs\glider-YYYYMMDD.log`, `logs\host-YYYYMMDD.log` | `FileLog` (14-day retention), `HostLog` | n/a | redacted, but contain account display name and paths |
| `backups\egl\<yyyyMMdd-HHmmss-fff>\…` + `backup-index.txt` | `EglFileTransaction` (`EglFileTransaction.cs:88,116`) | n/a | newest 20 sets; empty here |
| `webview2\EBWebView\` (`Default` + `GliderEpic` profile) | WebView2 | n/a | 92 MB here; `GliderEpic` holds Epic login cookies (sensitive) |
| `placeholder\index.html` | `WebViewSetup.cs:67-75` | n/a | rewritten every start |

**Exact current schemas (C# records; JSON is camelCase, enums camelCase strings, nullable fields always present):**
```
GameRecord (game.json, Abstractions.cs:197-208)
  installPath: string; version: string; installSizeBytes: long; installedAt: DateTimeOffset;
  adoptedFromEgl: bool; eglInstallationGuid: string?; needsVerification: bool;
  playtimeSeconds: long = 0; lastPlayedAt: DateTimeOffset? = null
Settings (settings.json, Models.cs:200-237; defaults in parentheses)
  downloadWorkers: int? (null=auto); sharedMemoryMb: int? (null=auto); preferredCdn: string? (null);
  autoUpdate: bool (true); autoUpdateOnlyWhenIdle: bool (true); updateCheckIntervalMinutes: int (30, clamped 5..1440);
  startWithWindows: bool (false); startMinimized: bool (false); closeToTray: bool (true);
  minimizeToTrayWhilePlaying: bool (true); launchArgs: string ("", ≤4096); logLevel: debug|info|warning|error (info);
  theme: system|dark|light (system); eglSync: bool (true); notifications: bool (true); defaultInstallBase: string? (null)
PersistedJobFile (jobs.json, PersistedJobs.cs:7-80)
  version: int = 1; jobs: [ { info: JobInfo; request: JobRequest; step: "Main"|"Verify"|"Repair" (PascalCase!);
    createdGameFolder: bool; gameFolder: string?; autoRepairAttempted: bool; verifyChecked: int; verifyBad: int;
    baseline: { totalDownloadBytes: long?; totalWriteBytes: long?; percent: double; elapsedSeconds: double; phase: JobPhase? } } ]
  JobInfo = { id, kind, status, progress: JobProgress?, error: GliderError?, createdAt, finishedAt?, targetPath?,
              fromVersion?, toVersion?, blockedReason?, verify: {filesChecked, filesBad, filesFixed, verified}? }
  JobRequest = { kind, targetPath?, uninstallMode?, fromVersion?, toVersion? }   // no appName
```
`JobStep` uses `JsonStringEnumConverter` (PascalCase values) while every other enum is camelCase — a v0.2 reader must accept both.

**Real files vs code (read-only look, `user.json` not opened):**
- `game.json` matches `GameRecord` exactly; `+` is written as `\u002B` (System.Text.Json default encoder; harmless);
  `installSizeBytes` = 160,618,483,626 (Legendary `install_size` = full manifest, not the installed tags) while
  `installed-size.json` measured 52,376,063,424 bytes — the record's size is not a usable on-disk size.
- `adoptedFromEgl: false`, `eglInstallationGuid: null` for an install that *was* adopted (via located folder; EGL had no
  `.item`), so "adopted" cannot be inferred from this field.
- `jobs.json`: 1 completed Verify with `info.verify` filled; `percent` serialized as an integer; matches the records.
- `art.json`, `installed-size.json` (and `onboarding.json` when written) start with a **UTF-8 BOM**
  (`SmallFiles.WriteAtomicAsync` uses `Encoding.UTF8`, `FileStores.cs:16`); `game.json`/`jobs.json` have none.
- `legendary\config.ini` has `[Legendary] disable_update_check/disable_update_notice/egl_sync=false` and
  `[Fortnite] install_tags = ,Startup,…,GFP_SaveTheWorldRoot,…,Langru,` (written by Legendary's repair). The real repair
  also printed `Deleting now untagged files` — tag handling after `import` needs care per game in v0.2.

**Migration needs:** add `schemaVersion` to every Glider file; convert `game.json` → `games\<appName>.json` (or one
`library.json` keyed by app); add `appName` to persisted jobs (all v0.1 jobs are `Fortnite`); move per-game art/size
caches under `cache\games\<appName>\`; keep `legendary\` intact (it is already multi-game); decide data root vs Velopack root.

## 5. What must be refactored for a multi-game library

### 5.1 Contract
- Split `AppSnapshot` into a small **app snapshot** (app info, auth, settings, onboarding, EGL summary, job summary,
  library *revision*) and per-game data fetched/pushed separately: `library.list` (paged/virtualized summaries: appName,
  title, art, installStatus, updateAvailable, sizes, playtime), `game.get(appName)` for detail pages, and a
  `games` delta event (`{revision, upserts[], removes[]}`) instead of whole-state replacement.
- Every `game.*` request gains `AppName`; `JobInfo`/`JobRequest`/`Notice` gain `AppName` (and `Notice.JobId`, todo).
- Separate catalog (owned games, art, versions), install record, and runtime (RunState) — today all in `GameState`.
- Per-game settings (`LaunchArgs`, EGL sync, SDL/tag choice, cloud-save path, overlay) move out of global `Settings`.
- `InstallCandidate` gains `AppName`; `EglState` becomes per-install sync info.

### 5.2 StateHub / snapshot size
- Today a push = whole snapshot incl. up to 20+ jobs, rebuilt and serialized on every `Invalidate` (`StateHub.cs:132-182`).
  At ~0.8-1 KB per `GameState`, 400 games ≈ 300-400 KB per push at up to ~30 pushes/s, plus structural-sharing diffing of
  400 entries in the UI on every job state change. Keep the coalesced push for the small app snapshot; push game changes as
  deltas keyed by appName with a revision; keep `progress` separate (already is).
- `HubState` holds one `LatestVersion`/`Art`/`InstalledSize`/`RunState` (`StateHub.cs:6-24`): becomes per-game maps
  (immutable dictionaries) so `Mutate` stays atomic.

### 5.3 JobQueue
- Serial and single-app. Keep **serial for Legendary-mutating work** (Legendary holds `installed.json.lock` for
  install/import/move/uninstall; one config dir = one installer at a time), but:
  - key idempotency by (appName, kind) — today `JobQueue.cs:111` dedups by kind only (**multi-game bug**);
  - a paused job must block only jobs of the same game (`Claim`, `JobQueue.cs:439`; `BlockedByPause`);
  - user reordering / "move to top", per-game cancel, history cap per game or global with paging (`KeepFinishedJobs=20`);
  - `JobHooks` and `InstallService.cs:296` "refuse if any job is active" become per-game rules;
  - `CreatedGameFolder` uses `basePath + AppName` (`JobQueue.cs:892`) instead of the catalog folder name.
- Risky: crash-resume of a queue that now contains several games' resume files (`legendary\tmp\<app>.resume`) and the
  cancel-cleanup logic that deletes folders (`JobQueue.cs:857-862`) — needs per-app tests.

### 5.4 LaunchService / ProcessWatcher / EAC
- One watcher, polling *names* of Fortnite processes every 1.5 s; one `RunningSince`; one playtime tracker.
  Multi-game: per-session tracking of the spawned PID's tree (Job Object per game session, or toolhelp descendants) with
  per-game quirks for launcher stubs that re-parent (Fortnite's bootstrapper/EAC stubs). Current real failure (§0.3) shows
  the name list is already stale.
- EAC/EOS: generic from `installed.json prereq_info` + per-game product id; EOS overlay (`legendary eos-overlay`) is new.
- `MinimizeToTrayWhilePlaying`, auto-update-on-close and `CloseGame` must become session-scoped.

### 5.5 EGL module
- Filters to Fortnite in every read path (`EglService.cs:196-199,220,373,391,542,621,738`). Bulk import = enumerate all
  `.item`s (minus DLC via `MainGameAppName`), map to owned apps, import each (serially, under the exclusive lock), then
  drift-check per game. Write-back and busy detection are already path-keyed and mostly generic; `DisplayName` fallback
  and `.dat` patching need the app name.

### 5.6 InstallService / UpdateChecker
- `UpdateChecker` runs `info Fortnite --json` (spawns PyInstaller, downloads the manifest) per interval
  (`UpdateChecker.cs:43`). For hundreds of games use `legendary list --json` (assets with versions, one login) or
  `list-installed --check-updates` (TSV only, `docs/research/legendary.md` line 30) for installed games; fetch manifests
  only for detail pages / planned installs. The update dry run is per game and should run lazily.
- `InstallService` plan/validate/adopt assume one layout; plan needs per-game install tags/SDL and DLC choices.

### 5.7 Frontend
- `store.ts` holds one `AppSnapshot`; `derive.ts`/`deriveHero` and every page read `s.game`. Needed: a normalized
  `games` map + `libraryOrder`, selectors by appName, virtualized grid/list, detail route per game, a queue view with
  multiple games, per-game hero derivation, and `actions.ts` calls with `appName`.
- Mock backend: rebuild around N games (≥ 400 for perf), with scenarios per game state and the real verify→repair→verify
  flow (today the mock enqueues `repair` when broken, `backend.ts:986`, while Core always enqueues `verify`).

### 5.8 Risky parts (in order)
1. Snapshot→delta protocol change touches every frontend selector and the mock at once.
2. JobQueue keying, pause blocking and persisted-job migration (data loss or wrong-game operations).
3. Process tracking redesign (EAC games, stubs, games that restart themselves).
4. EGL bulk import performance and correctness across many `.item`s (and never tested against real EGL write-back).
5. Install tags/SDL generalization: Legendary deletes "untagged" files after install/repair; wrong tags delete data.
6. Data-root move away from Velopack's folder while Legendary's auth lives inside it.

## 6. Known UI bugs and inconsistencies (checklist)

From todo.md / design-critique.md / design-review.md (still open):
- [ ] Hero text below AA in 20/60 cases (reproduced today, §7): worst "Update available" 2.34:1, "Details" 2.95:1,
      "Downloading" 4.01:1, "Not enough disk space" 4.24:1, mostly light theme / 960×600 / bright art.
- [ ] Installing state repeats itself ("Installing" ×3, percent ×2, speed ×2; the primary button is a status label).
- [ ] "Ready to play" choreography: two-frame smear, ~45 px title jump, More button collides with the shrinking Play button, dead dot.
- [ ] Toast vs inline duplicates ("Signed out", "Fortnite is ready to play" during onboarding); needs `Notice.JobId`.
- [ ] Light theme no-art hero is a dark slab; Library sparse (no update notes / maintenance); Settings is a flat list.
- [ ] `InstallCandidate.Reason` exists but the UI still guesses (`derive.ts:263` `candidateIssue`).
- [ ] CDN dropdown is a hard-coded guess; no verify-result screenshot state in the mock.

Found in this audit (frontend reading):
- [ ] **Verify job running its repair step**: `ActiveJobCard` title "Verifying Fortnite" and subtitle "Checking every
      file against Epic's manifest" (`ActiveJobCard.tsx:67-68,79`) while the badge says "Downloading"; `hasBytes` excludes
      `verify` (`:55`) so a 35 GB repair shows no bytes and no sparkline; percent restarts three times in one job.
- [ ] Hero "working" state repeats the verb: label `JOB_VERB` + status text `JOB_VERB` + sub `PHASE_LABEL`
      ("Verifying · Verifying · Verifying files") (`derive.ts:168-178`).
- [ ] Phase shown twice on the card when not downloading: badge `PHASE_LABEL` (`ActiveJobCard.tsx:84`) and "Phase" metric (`:147`).
- [ ] Pause button is offered for running Move/Import jobs (`ActiveJobCard.tsx:97` excludes only uninstall) but Core
      refuses (`JobQueue.cs:964` `CanPause` = install/update/repair/verify) → error toast.
- [ ] Play/"…" split button: Play width morphs with a spring while More is static (collision above); `joined` is
      `game.installed !== null` (`Library.tsx:106`) and More hides when not installed (`LibraryParts.tsx:132`) — consistent
      today, but the pair is not one component, so emphasis/size can drift (two tone helpers in `PrimaryAction.tsx:65-74`).
- [ ] Wording for the same action differs: menu "Verify and repair", palette "Repair Fortnite", hero "Repair", running
      "Verifying", history "Verified" (`derive.ts:6-46`, `CommandPalette.tsx:81`, `LibraryParts.tsx`); uninstall: menu
      "Uninstall…", dialog "Remove from Glider"/"Uninstall and delete files", running "Removing", history "Uninstalled",
      notice "Fortnite removed from Glider"; import: "Use this install" / "Linking your install…" / running "Importing" /
      history "Linked existing install of" / contract "Adopt".
- [ ] A11y labels differ: `JobLine` progress label is the raw kind (`` `${job.kind} progress` ``, `LibraryParts.tsx:30`) vs
      `ActiveJobCard` "Installing progress"; `ProgressAnnouncer` hard-codes "Fortnite" (`ActiveJobCard.tsx:38`).
- [ ] Move dialog shows the destination as `base\Fortnite` (`Dialogs.tsx:188`) regardless of the real folder name.
- [ ] Mock/Core divergence: mock creates `repair` jobs for broken installs (`backend.ts:986`); Core never enqueues `Repair` from the UI.
- [ ] "Install size" for an adopted/imported install is the manifest total (160 GB here) wherever `installed.installSizeBytes` is shown.

## 7. CI status and local run results

**GitHub** (`gh run list --branch main --limit 3`): all `success` — 43508157126 "docs: v0.1.5 release notes" 4m58s,
43498069990 "docs: v0.1.4 release notes" 5m06s, 43493217141 "test(launch): ignore leading Idle states…" 4m54s
(2026-11-13). Tag runs `ci`/`release` for v0.1.4 and v0.1.5 also succeeded.

**Local** (Windows 11, .NET SDK 10.0.404, pnpm 10.28.2; same commands as `.github/workflows/ci.yml`):
| Step | Result | Duration |
|---|---|---|
| `dotnet build Glider.slnx -c Release -warnaserror` | pass, 0 warnings | 13 s |
| `dotnet test --project tests/Glider.Core.Tests -c Release` | pass, **487/487** (MTP) | 18 s (tests 15.0 s) |
| `dotnet run --project tools/Glider.ContractGen -c Release -- --check` | pass (contract up to date) | 3 s |
| `pnpm install --frozen-lockfile` | pass | 16 s |
| `pnpm lint` | pass | 13 s |
| `pnpm typecheck` | pass | 7 s |
| `pnpm test` with system Node **20.20.0** | **fail**: "Failed to start forks worker … TypeError: Cannot read properties of undefined (reading 'bind')" in `undici@8.12.4/lib/interceptor/decompress.js:78`; 0 tests ran | 3 s |
| `pnpm test` with Node **24.22.2** (fnm; CI uses 24) | pass, **187/187** in 9 files | 54 s (vitest 31.9 s) |
| `dotnet run build/FetchLegendary.cs` | pass (SHA-256 verified) | 5 s |
| `build/smoke-legendary.ps1` | pass (`legendary version "0.21.1"`, `status --offline --json` → not logged in) | 4 s |
| Package (Velopack) | skipped (optional) | — |

Diagnosis of the Node 20 failure: environment, not code. The transitive `undici@8.12.4` (pulled in by the test
environment) declares `"node": ">=22.19.0"` and uses APIs missing in Node 20; `frontend/package.json` itself has no
`engines` field (README says Node 24+), so pnpm installs without warning. The default `node` on PATH is
`C:\Program Files\nodejs` 20.20.0 while fnm has 22/24/25 installed. Fix locally: `fnm use 24`; worth adding
`"engines": {"node": ">=24"}` + `engine-strict` in v0.2 so this fails fast.

**Playwright** (headless Chromium vs the mock, Node 24; specs that write into `docs/screenshots` were redirected to the
scratchpad with `SHOT_OUT` or skipped, so the repo stayed clean):
| Spec | Result | Duration |
|---|---|---|
| `a11y.spec.ts` + `focus.spec.ts` | pass, 140/140 (axe: 0 serious/critical) | 96 s |
| `hero-legibility.spec.ts` with `HERO_ENFORCE=1` | **20 failed / 40 passed** — the known open AA issue (todo.md); passes without the flag | 44 s |
| `surfaces.spec.ts` (`SHOT_OUT` = scratchpad) | pass, 30/30 | 20 s |
| `screenshots.spec.ts`, `break-ui.spec.ts` | not run (write committed screenshot sets; break-ui has no output override) | — |
