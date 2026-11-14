# Glider v0.2: architecture and performance review

Reviewed commit `80d1a8d` (branch `v0.2`, 2026-11-14), the whole repository: Core (`src/Glider.Core`, ~31k lines), host
(`src/Glider.App`), frontend (`frontend/src`) and tests. I read the design docs first (`README.md`, `ARCHITECTURE.md`,
`docs/v0.2-design.md`, `DECISIONS.md`, `docs/perf.md`, `docs/download-perf.md`) and then checked each requirement against the code.
Severity is judged against the product requirements in the brief: launch never waits behind a download, the PC stays usable,
fast downloads, cold start under 1.5 s with 500 games, search under 16 ms, idle host RAM under 100 MB, a lazy Store that is
suspended when hidden, background incremental refresh, no user data loss, and no RPC that can hang the UI.

**Summary.** The lane model, the process runner and the persistence primitives are well built (see "What's solid"). No finding
is Critical: nothing silently destroys user data in normal use, and no UI-thread path blocks on Core. There are seven High
findings:

- every quit hangs for 3 s on a sync-over-async deadlock;
- once Core decides it is offline it stays offline;
- one game's gate holds the whole writer lane;
- cold start parses Legendary's metadata on the `library.get` path, and the benchmark skips that path;
- the Store view is never suspended in the tray;
- user data sits in the folder the uninstaller deletes;
- interactive metadata RPCs queue behind background syncs and hit the UI's 30 s timeout.

All are fixable without redesigning anything.

## Measurements

| What | Result |
|---|---|
| `dotnet test -c Release` (whole suite, parallel) | 1430 tests: 1425 passed, 1 skipped, **4 failed**: `CdnProbeTests.AStalledDownloadRestartsOnceOnTheNextCdn`, `CdnProbeTests.AUserChosenCdnSkipsTheProbe`, `SpeedLimitTests.ThrottlesRealWorkerProcesses…`, `EndToEndTests.PauseKillsTheDownloadAndResume…`. All 24 tests of those three classes pass when run alone (24 s), so these are load-sensitive flakes (2 s stall timeouts around real child processes), not regressions. See L9. |
| `StartupBenchmarks` (4 runs) | `StartAsync` 47–52 ms, plus one first-run outlier of **1409 ms** that still passed, because the floor is 10× (`LoadSlack`, 4 s). `library.get` 14–18 ms; snapshot 1.6 KiB, first serialization 146–181 ms (JIT-cold, pre-warmed in the host); rebuild of 501 unchanged games, median **31–79 ms**. |
| Cold `library.get` **with Legendary's metadata on disk** (temporary probe, not committed: 500 × a real 27 KB catalog item, warm OS cache) | **266–678 ms** over 6 runs, against 14–18 ms in the shipped benchmark, which deletes `legendary/metadata` before measuring (`StartupBenchmarks.cs:61`). See H4. |
| `PerfBenchmarks` | Progress parsing: 751k download lines/s and 1.47M verify lines/s, 0.02–0.04 bytes allocated per line. Play → process start: 1.68 s cold, **0.8 ms prepared**. |
| Shutdown probe (headless `Dispatcher`, the same pattern as `HostApp.Run` → `Composition.Dispose`) | After `app.Run()` the thread's context is still `DispatcherSynchronizationContext` and the dispatcher has shut down. `DisposeAsync().AsTask().Wait(3 s)` returned **false after 2998 ms**, and the dispose chain never resumed. See H1. |
| Frontend (`pnpm test`, `typecheck`, `lint`, Node 24) | 32 files, **512 tests pass**; typecheck and lint clean. `LibraryIndex` performance test: median query < 4 ms, worst < 16 ms for 600 games plus DLC. The grid is virtualized and search uses `useDeferredValue`. Note: the Node 20 on `PATH` cannot start vitest (undici `bind` error); README asks for Node 24+. |
| Not measured | Host GUI startup and RAM (the brief rules out GUI runs; `docs/perf.md` numbers were taken under 76–100 % CPU), real Epic account, Windows 10. |

---

## Critical

None found. A Critical finding would be silent loss of saves or installs in normal use, or a UI that freezes. EGL writes,
cloud-save overwrites and `jobs.json`/`games.json` writes are all backed up or atomic, and the UI thread never waits on Core.
The data-loss risks that do exist (H6, M1) need an uninstall or a failed migration to trigger.

## High

### H1. Every quit hangs for 3 s; the Core runtime is never fully disposed, and a quick relaunch is swallowed
- **Where:** `src/Glider.App/Program.cs:152` (a `DispatcherSynchronizationContext` installed on the main thread), `Program.cs:237–241`
  (`app.Run()` → `DisposeRpc`), `src/Glider.App/Composition.cs:26` (`Runtime?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3))`),
  `src/Glider.Core/Services/GliderRuntime.cs:151` → `src/Glider.Core/Jobs/JobQueue.cs:545` (`await _shutdown.CancelAsync()` without `ConfigureAwait(false)`).
- **Scenario:** the user picks Quit. `app.Run()` returns on the STA thread, whose current context is still the dispatcher
  context, and that dispatcher has already shut down. `GliderRuntime.DisposeAsync` reaches `JobQueue.DisposeAsync`, where
  `CancelAsync()` completes asynchronously because the loop's `_signal.WaitAsync(ct)` registered a callback. Its continuation is
  posted to the dead dispatcher and never runs. `.Wait(3 s)` times out (reproduced: false after 2998 ms). `_egl.Dispose()`,
  `_legendary.Dispose()`, the stores and `FileLog.DisposeAsync()` never run, so the last log lines and the final `jobs.json`
  persist are lost. The named mutex and the activation pipe stay alive for those 3 s. A user who relaunches Glider in that window
  signals the dying instance (`SingleInstance.SignalFirst`), whose `Listen` callback does `Dispatcher.BeginInvoke` on the dead
  dispatcher. The second instance exits with code 0 and nothing opens. Windows logoff/shutdown and Velopack's apply-on-exit wait the same 3 s.
- **Fix:** run disposal off the UI context: `Task.Run(() => Runtime.DisposeAsync().AsTask()).Wait(5 s)`, or
  `SynchronizationContext.SetSynchronizationContext(null)` before disposing. Also add `ConfigureAwait(false)` throughout
  `JobQueue`/`GliderRuntime` disposal. Release the single-instance mutex, or stop `Listen`, **before** disposing Core, so a relaunch
  waits for the mutex instead of signalling a dying process. Add a host test that measures quit-to-exit time.

### H2. Once Core decides it is offline it never checks again: update checks, auto-updates and cloud-save uploads stop
- **Where:** `src/Glider.Core/Services/UpdateService.cs:306–310` (offline → set `_lastCheck` and `continue`, with no probe);
  connectivity flips to `Online` only in `LibraryService.SyncAsync` (`:591`) and `AuthService` (`:65`, `:130`);
  `src/Glider.Core/Saves/SaveService.cs:354–363` (offline → `Skip`, retryable); `LaunchService.DecideOffline` (`:159–168`).
- **Scenario:** a laptop starts Glider with Windows before Wi-Fi is up, or a sync fails once with a network error. Pre-warm's
  `WarmUpAsync` gets a `Network` error and sets `Connectivity = Offline`. From then on the periodic loop only reschedules itself
  every 5 min and never runs anything that could set `Online` again:
  - no update checks, so no auto-updates;
  - `LaunchMode.Auto` starts every `CanRunOffline` game with `--offline` (no exchange code, no Epic services) on a working network;
  - every post-exit cloud-save upload is skipped. The retries at 30 s, 2 min and 5 min all `Skip` again, and the session ends
    with no notice.

  If the user then plays on a second PC, they play on a stale cloud save. The 3-way check turns this into a conflict, not a
  silent overwrite, but the user has to choose a side. Recovery needs a manual "Refresh library" or a new sign-in.
- **Fix:** make offline a state that is re-probed: a `ConnectivityMonitor` that listens to `NetworkChange.NetworkAvailabilityChanged`
  and probes Epic with backoff (a cheap HEAD request, or `status --json` on the metadata lane). When it flips back to `Online`, kick
  the update loop and replay pending post-exit save syncs. In `UpdateService`, try the sync while offline at the minimum interval
  instead of skipping it.

### H3. A job waiting for its own game (running, EGL busy, save sync) holds the only writer slot, so every other game's download stalls
- **Where:** `src/Glider.Core/Jobs/JobQueue.cs:740` (`BeforeRunAsync` runs *after* `Claim` has set `_running`);
  `src/Glider.Core/Services/JobHooks.cs:45–53` (`while (IsGameRunning(app)) Delay(1.5 s)`), `:61–69` (`WaitUntilNotBusyAsync`, no cap), `:44` (`WhenSavesIdle`).
- **Scenario:** Fortnite is shared with EGL, and EGL starts its own update of Fortnite. Glider's auto-update, or the user, queues
  Glider's Fortnite update, which reaches the head of the queue and sits in "Epic Games Launcher is updating…" for the length of
  EGL's update. Meanwhile the user queues a 60 GB install of another game, which waits behind it for the whole time. The same
  happens when a game was started outside Glider (shortcut, EGL, Steam) after its update was queued, or with
  `WhilePlaying = Throttle`. This breaks the rule the queue was designed around ("a paused job blocks only its own game"), and the
  user sees no reason for the second game not starting.
- **Fix:** move the gates out of the running slot. Before `Claim`, ask a non-blocking `IJobGate.ReasonNotToStart(job)` (game
  running, EGL busy with that folder, saves busy). Skip jobs that have a reason, leaving them `Waiting`/`Held` with
  `BlockedReason` set, and re-signal the loop on session end, EGL idle and saves idle (these events already exist).
  `BeforeRunAsync` then only re-checks to close the race, and puts the job back if a gate closed in the meantime.

### H4. Cold start parses Legendary's metadata on the `library.get` path, and the 500-game benchmark skips that path
- **Where:** `src/Glider.Core/Library/LibraryService.cs:454–456` (`GetAsync` → `FlushAsync()`), `:318` (`if (reread || !FilesRead)` → full
  `_reader.ReadAsync`); pre-warm's `ReloadAsync` holds `_rebuild` during the parse; `tests/.../StartupBenchmarks.cs:61` deletes `metadata/`.
- **Scenario:** startup invalidations leave `_pendingRebuild = true`. The UI's first `library.get`, a non-background flush, either
  does the first parse itself or waits on the `_rebuild` semaphore that pre-warm holds while it parses 500 `metadata/*.json` files.
  With 500 real-sized items (27 KB each) the first `library.get` took **266–678 ms** even with a warm OS cache, which is 20–40×
  the documented 21–25 ms. On a cold boot (Start with Windows, a SATA or HDD library drive, Defender scanning new handles) this
  parse sits directly in front of the first paint, so the "interactive library in under 1.5 s" target is not shown to hold, and the
  benchmark that backs it never exercises this path. `game.getDetail` has the same coupling.
- **Fix:** `GetAsync` must never wait on a file parse. While `!_filesRead`, return `_published` (the cache) at once; the parse's
  delta follows, and the UI already applies deltas in order. Only flush a pending cheap rebuild (no re-read), with a time box.
  Rewrite the benchmark to keep real-shaped metadata on disk, assert `library.get` < 50 ms with pre-warm running, and drop
  `LoadSlack` to 2–3× for the Core-only numbers.

### H5. The Store WebView2 is never suspended or closed while Glider sits in the tray with the Store tab open
- **Where:** `src/Glider.App/StoreView.cs:209–222` (`OnWindowHidden` hides the view, sets Low memory and starts `_suspendTimer`, but not
  `_closeTimer`), `:332` (`SuspendAsync` returns early when `_wantVisible`); `MainWindow.SuspendWhenHiddenAsync` → `StoreView.SuspendNowAsync` hits the same guard.
- **Scenario:** the user is on the Store tab and closes the window to the tray. The page is suspended, so it never sends
  `store.hide`, and `_wantVisible` stays true. The main UI is suspended after 8 s, but Epic's Store renderer (carousels, video,
  analytics timers; typically 100–300 MB) keeps running indefinitely at Low memory and is never closed by the 20-minute timer.
  This directly breaks "Store WebView2 lazy and suspended when hidden".
- **Fix:** treat "main window hidden" as not wanting visibility. Add `_windowHidden` and suspend when `_wantVisible && !_windowHidden`
  is false, start `_closeTimer` from `OnWindowHidden`, and on `OnWindowShown` restore only if `_wantVisible`. Add a self-test step:
  Store open → hide to tray → after 70 s, `IsSuspended == true`.

### H6. Irreplaceable user data lives in Velopack's install root, which uninstall deletes
- **Where:** `src/Glider.Core/Abstractions.cs:21–23` (`AppPaths.ForCurrentUser` = `%LocalAppData%\Glider`), `build/build.ps1:84`
  (`--packId Glider`, the same folder); already tracked as open in `todo.md:44`.
- **Scenario:** in v0.1 this folder held caches and Legendary's config. v0.2 added `backups\saves\<app>\` (the **only** local copies
  of saves Glider overwrote), `backups\egl\` (pre-write copies of EGL manifests), `backups\migration\`, `state\saves.json` (the
  3-way ledger) and `state\protocol-backup.json`. A user who uninstalls Glider to reinstall, or to go back to EGL, loses their save
  backups and the EGL backups, which are exactly the files Glider promised as the safety net. Legendary's auth and `installed.json`
  go too, so every game has to be re-imported.
- **Fix:** move the data root out of the Velopack folder (for example `%LocalAppData%\GliderData`, or `%AppData%\Glider` for state
  and backups) with a one-time move: schema 3, copy then verify then switch, keeping a pointer file in the old root. Until then,
  at least put backups outside it, and have `OnBeforeUninstallFastCallback` move `backups\` and `state\` aside.

### H7. Interactive metadata calls (`game.planInstall` → `legendary info`) queue behind background syncs and time out in the UI
- **Where:** `src/Glider.Core/Services/ServiceContext.cs:200–222` (`MetadataLane`: one `SemaphoreSlim`, no priority, and every
  entrant waits for the writer's pre-download phase); `src/Glider.Core/Game/InstallService.cs:58`, `:86`;
  `LibraryService.MeasureUpdateSizes` (`:614–674`, one dry run per updatable game, each taking the lane separately);
  `frontend/src/ipc/client.ts:44` (30 s timeout; `planInstall` is not in `NO_TIMEOUT`).
- **Scenario:** first run after sign-in, with 500 owned games. Pre-warm runs `list-installed --check-updates`, then
  `list -T --json` (minutes for a big first sync, within the 5-minute `InfoTimeout`), then N update-size dry runs. The user opens
  Install on a game. `PlanAsync` → `GetInfoAsync` waits for the lane, or for a writer job's login and manifest download. After
  30 s the UI shows "Glider's backend didn't respond in time" on the most important dialog, while Core keeps working.
  `game.getDetail` → `RequestUpdateSize` adds still more background work to the same lane.
- **Fix:** give the lane two priorities (as `AuthGate` already does). `RunInteractiveAsync` jumps the wait list and skips the
  pre-download wait for read-only `info`. Background callers keep `RunAsync`. `PlanAsync` should answer from cached `info` or the
  catalog at once (sizes from `metadata` and `assets`) and publish exact sizes later, instead of holding the RPC open.

## Medium

### M1. A failed v0.1 migration lets the job queue overwrite v0.1 `jobs.json` before the retry
- **Where:** `src/Glider.Core/Services/GliderRuntime.cs:66` (it continues after `MigrationStatus.Failed`); `JobQueue.cs:483–487` (v1 entries lack
  `appName` and are dropped as "unusable"), `:520` (`RestoreAsync` always persists).
- **Scenario:** migration fails after the `backup` or `games` step (an antivirus lock past the 3.5 s retries, or a full disk). Core
  starts anyway, `RestoreAsync` reads the v1 `jobs.json` with the v2 reader, drops every entry, and writes an empty schema-2 file.
  On the next start `HasV1Data` no longer sees v1 jobs, so the interrupted Fortnite update or install is gone. The bytes survive
  only in `backups\migration\…`, which nothing reads back. `MigrationTests.AFailedRunLeavesV01Files…` re-runs the migrator
  directly and never starts the runtime in between, so it misses this.
- **Fix:** in `RestoreAsync`, never persist over a file it could not fully interpret (older schema, or dropped entries). Copy it to
  `jobs.json.unreadable-<ts>` first, or skip the write until something changes. Add a test that fails the migration after
  `games`, starts the runtime, and then retries.

### M2. One throwing subscriber wedges the writer lane for the rest of the session; pause and cancel then never return
- **Where:** `JobQueue.cs:785–870`: `tracker.Flush()`, `Changed?.Invoke()` (`:869`) and `running.Done.TrySetResult()` (`:870`) are outside any
  `try/finally`; `StopAsync` (`:593–605`) awaits `Done` with no timeout; `LoopAsync` (`:621–632`) has no catch, and `EnsureLoop` uses
  `_loop ??=` (`:616`), so a faulted loop is never restarted.
- **Scenario:** any `Changed` or `ProgressChanged` handler throws: the host tap, a future subscriber, or an `ObjectDisposedException`
  during shutdown. `_running` stays set forever, nothing else is ever claimed, and `jobs.pause`/`jobs.cancel` hang inside Core.
  The UI gives up after 30 s; the Core task leaks.
- **Fix:** put the tail of `RunAsync` in `try/finally` that always clears `_running` and completes `Done`. Wrap every event invocation
  so a handler's exception is logged and swallowed. Catch and log in `LoopAsync` and restart it (`if (_loop is { IsCompleted: true })`).
  Time-box `StopAsync` (for example 30 s, then log and force-clear).

### M3. If Core composition fails, the UI's requests are never answered and the user gets no explanation
- **Where:** `src/Glider.App/HostRuntime.cs:30–37` (`ContinueWith(..., OnlyOnRanToCompletion)`, so the dispatcher task is cancelled when
  composition faults); `WebViewBridge.cs:190–205` (logs and drops the request); `Program.cs:171` (only logs).
- **Scenario:** `GliderRuntime.StartAsync` throws (an unwritable data dir, or an exception in a store loader or a service
  constructor). The window is revealed by the 3 s safety timer, and the page shows "connecting" for 30 s, then
  "failed" with a Retry that can never succeed. There is no error text and no way to copy logs from the UI.
- **Fix:** when `RpcTask` faults, install a fallback dispatcher that answers every request with a `startupFailed` `GliderError`
  (message, log path, `FixAction.CopyLogs`), and have the boot screen show it. Keep `app.getLogs`/`app.copyLogs` host-served so they
  still work.

### M4. While the main WebView2 is suspended, library deltas and notices pile up without limit and then replay as a render storm
- **Where:** `src/Glider.App/WebViewBridge.cs:69–100` (only `state`, `progress` and `window` coalesce; `RemoveAll` is O(n) per `Send`),
  `:136` (`RemoveAt(0)` is O(n) per item).
- **Scenario:** with "minimize to tray while playing", the page is suspended about 8 s after the game starts. Playtime is flushed
  every 30 s, each flush changes `GameSummary.PlaytimeSeconds`, and each change queues a `library` delta: about 120 per hour, plus
  notices and placeholders. After a 6-hour session the restore replays about 700 messages. Each one copies the 500-entry `Map`
  (`applyLibraryDelta`), invalidates the `LibraryIndex` and re-renders, so the window stutters on restore. Over days in the tray the
  queue only grows.
- **Fix:** while held, fold `library` deltas per `appName` (latest summary wins, remove beats upsert), or drop them and post one
  synthetic "library changed" event that makes the UI call `library.get`. Cap queued notices at about 20. Use a `Queue<T>` or a
  ring buffer instead of `List.RemoveAt(0)`.

### M5. After a renderer crash or reload, old responses reach the new page and resolve the wrong promises
- **Where:** `src/Glider.App/MainWindow.cs:393` (`_web.Reload()` on `RenderProcessExited`/`Unresponsive`, bridge still attached),
  `:406–424` (recreate); `frontend/src/ipc/client.ts:47` (`nextId = 1` on every page load).
- **Scenario:** the old page had `game.launch` (no timeout) in flight as id 7 when the renderer died. The reloaded page sends
  `library.get` as id 7. The old `LaunchResult` arrives and resolves the new `library.get`, and `store.apply(full)` throws on
  `upserts` being undefined, so boot fails. Any queued events for the old page also land in the new one.
- **Fix:** make ids unique per page load (start at a random value below 2^30, since `RpcDispatcher` reads ids with `GetInt32`), or
  add a session nonce that the host echoes. In the host, drop queued messages on `NavigationStarting`.

### M6. The library rebuild is all-or-nothing: 500 summaries are rebuilt for any change, and 561 KiB is rewritten after every delta
- **Where:** `src/Glider.Core/Services/GliderService.cs:193–199` (every hub mutation, job change, `games.json` write, settings change and
  placeholder publish triggers `Library.Invalidate()`); `LibraryService.cs:345` (`GameCatalog.Build` over every game), `:364`/`:691–703`
  (whole-library cache write, 2 s debounce); `frontend/src/state/libraryIndex.ts:217–233` (full index rebuild per `Map` identity).
- **Scenario:** each rebuild costs a measured 31–79 ms of CPU for 500 games. While art loads, placeholders publish up to every 3 s,
  and while a game runs playtime writes every 30 s. Each writes the whole 561 KiB cache file and rebuilds the UI index. The
  requirement says "incremental"; today only the wire delta is incremental. The cost grows linearly with library size and
  competes with the game during play.
- **Fix:** track dirty app names (session, job, game entry, placeholder and size changes already know their app) and rebuild only
  those summaries, keeping a full rebuild for file re-reads. Write the cache only on `revision % N`, on a 30 s timer, or at shutdown.
  In the UI, patch `LibraryIndex` for small deltas (re-sort only the changed entries) or debounce the rebuild with `startTransition`.

### M7. The mock backend and Core disagree on several RPCs the UI depends on
- **Where:** `frontend/src/ipc/mock/backend.ts` against Core:
  - `game.modify` (`backend.ts:1525`): the mock creates a working `modify` job with components. Core throws `NotAvailableYet` for any
    component change (`InstallService.cs:189–192`), and `JobKind.Modify` has no executor (`JobQueue.cs:943–944`).
  - `queue.pauseAll` (`:1709`): the mock turns the running job back to `queued` and leaves the others alone. Core sets every pausable
    job to `Paused` (`JobQueue.cs:368–398`), so per-job Resume buttons and labels differ.
  - `jobs.dismiss` (`:1684`): the mock throws for unfinished jobs; Core silently ignores them (`JobQueue.cs:318–334`).
  - `jobs.cancel` (`:1678`): the mock refuses a running uninstall; Core kills Legendary mid-delete (M8).
  - `game.launch` (`:1085`): the mock fails fast with `offline` for `mode=online` while offline, and with `offline` for
    `!canRunOffline`; Core tries anyway and fails later with a classifier error.
- **Scenario:** a feature works in `pnpm dev` and in the screenshots but fails or looks different in the real app. Component
  changes in "Modify install" are the visible case.
- **Fix:** hide component editing until Core implements it (or implement it in Core), and match the mock to Core for the other four.
  Add a small contract-conformance suite: the same scripted RPC sequences run against the mock (vitest) and against `GliderService`
  with fakes (xUnit), comparing status transitions and error codes.

### M8. Core lets the UI cancel a running uninstall, which kills Legendary halfway through deleting the game
- **Where:** `JobQueue.cs:278–316` (`CancelAsync` has no per-kind guard; `JobPlan` has `CanPause` but no `CanCancel`); only
  `ActiveJobCard.tsx:197` hides Cancel. `GameActions.tsx:75/159` offer cancel for "the game's job".
- **Scenario:** cancelling an uninstall with `DeleteFiles` from the game page menu leaves a half-deleted folder still listed in
  `installed.json`, so the game shows as Broken. A cross-volume move is safe because its copy is discarded; uninstall is not.
- **Fix:** add `JobPlan.CanCancel(kind, step)` (false for a running `Uninstall`, and for `Move` once the metadata step has started),
  enforce it in `CancelAsync` with a clear error, surface it as `JobInfo.Cancellable`, and have the UI read that field.

## Low

- **L1. Launch waits behind another game's prepare.** `LaunchService.cs:211` awaits the in-flight `_preparing` whatever game it is
  for: hover game B, click game A, and A waits for B's `launch --json` (1.5–3 s, up to `QuickTimeout`). *Fix:* join only when the key
  matches.
- **L2. Launch can wait up to 45 s at the auth gate.** `AuthGate.cs:11`, `LegendaryCli.cs:666–673`: an online launch waits for a login
  already in progress (metadata sync, writer), up to `MaxHold` when Epic is slow. *Fix:* keep the gate, but give priority entrants a
  shorter cap, or release early on the first byte of the HTTP response (fork), and keep relying on the prepared launch.
- **L3. One cloud-save lane for all games.** `SaveService.cs:41`, `:374`: the pre-launch sync of game A waits behind game B's post-exit
  upload, inside A's 60 s budget. *Fix:* a per-game lock plus a small global concurrency limit (2).
- **L4. Uncached EGL scan on hot paths.** `EglService.cs:423–424`: while EGL runs, `IsBusyWith` re-reads and parses every `.item` on
  every launch, every Play hover (`Prepare` → `CanLaunch`) and every job start. *Fix:* cache `GameItems()` behind a
  `FileSystemWatcher` or mtime check.
- **L5. Session watch loop can miss a restart.** `LaunchService.cs:396–420` / `SessionTracker.cs:279–286`: a session begun between the
  final `Tick()` returning false and the loop task completing is never polled, so it sits in "Launching" and holds the queue.
  *Fix:* loop while `AnyActive`, re-checked under `_gate` after the last tick, or restart the loop if `AnyActive` after it completes.
- **L6. Shutdown ordering.** `GliderService.cs:305–331` cancels but never awaits `WarmupTask`, `_periodic`, the reconcile task,
  update sizes or the size meter before disposing the semaphores and timers they use (`MetadataLane._gate.Release()` then throws
  `ObjectDisposedException`). `GliderRuntime.cs:148–160` disposes the service *before* the job queue, so a finishing hook can run
  against disposed state. *Fix:* await background tasks with a timeout, then dispose; dispose the queue first.
- **L7. Playtime lost at quit.** Quitting while a game runs loses up to 30 s of playtime: `LaunchService` has no dispose-time flush.
- **L8. Unbounded migration backups.** The migration creates a new backup folder on every failed start (`Migrator.cs:198–231`).
  *Fix:* reuse the newest backup whose hashes match.
- **L9. Flaky timing tests.** Four tests use 2 s stall windows around real child processes and fail under full parallel load
  (see Measurements). *Fix:* `[Collection]` them serially or derive their timeouts from a calibrated tick; this is also a CI risk.
- **L10. Fire-and-forget persistence.** `PersistInBackground` (`JobQueue.cs:1368`) catches only IO, unauthorized-access and disposed
  exceptions (`:1387`); anything else is an unobserved task exception. *Fix:* catch all and log.
- **L11. Enqueue before restore.** An `Enqueue` before `RestoreAsync` (a click during the EGL scan) bypasses the (app, kind) check
  against restored jobs, so duplicates are possible. *Fix:* refuse or merge on restore.
- **L12. Two defaults for auto workers.** `GliderServiceOptions.cs:65` defaults `AutoWorkers` to `min(2×CPU, 16)` while `AutoTune` uses
  16/12. The runtime overrides it, but tests and other hosts show the wrong value. *Fix:* default it from `AutoTune.ForCurrentMachine()`.
- **L13. Stale docs.** `README.md` still describes the v0.1 Fortnite-only launcher; `docs/perf.md` quotes `library.get` numbers
  measured without metadata (H4).

---

## What's solid

- **The writer lane's data model.** Claim and running state change in one critical section, so pause and cancel never see a job
  in between. Jobs are idempotent per (app, kind). A paused job blocks only its own game group. Jobs are persisted and resumed
  after a crash, with the resume step and baseline kept, and history is bounded (20 finished jobs, ≤ 600 samples each).
  `JsonFileStore` writes in order, coalesces saves and keeps a `.bak`, on top of `AtomicFile` (flush to disk, then
  `File.Replace`, with antivirus retries).
- **Process runner.**
  - Children run in a kill-on-close Job Object, with a tree kill on cancel, stall and timeout.
  - Pipe draining after exit is bounded, with straggler handling.
  - Parsing is span-based and allocation-free (0.02 B per line), and an unbounded channel keeps a slow consumer from ever
    blocking Legendary's stderr.
  - Every command has a timeout or a stall watchdog.
  - The exe hash is checked whenever the file's length or modification time changes.
  - The auth gate has a priority queue and releases on the first post-login line.
- **The launch lane.** It is never exclusive with the writer and never takes the install lock. A prepared launch takes 0.8 ms
  against 1.7 s cold. Refusal is scoped to that game or its base game, and session tracking follows the process tree, not just a
  PID.
- **The wire.** The snapshot is 1.6 KiB (asserted < 16 KiB). Library deltas are revisioned with gap recovery (`LibrarySync`
  buffers during reloads). The diff relies on record equality (`GameSummary` has no reference-typed lists, so it is correct). The
  bridge coalesces `state` and `progress` and posts in 6 ms batches; Core caps progress at 10 Hz.
- **Layering.** No game-specific branch outside `GameProfiles` (checked for `"Fortnite"`; v0.1 paths go through
  `GameProfiles.FortniteAppName`). Core references no host types; registry, shell, WebView2 and the Store are behind interfaces.
  The security boundaries (`StoreNavigationPolicy`, `EpicLauncherUri`, `GliderLinkParser`) are pure and tested in Core. One
  `installed.json` parser is shared by the CLI wrapper and the library reader.
- **Data safety, where it is used.** EGL writes are backed up, atomic, rolled back on failure and pruned without touching the
  current backup set. Cloud saves use a 3-way ledger, a verified backup before every overwrite, and upload verification by
  re-listing.
- **Frontend performance.** The grid is virtualized, search is deferred, the index query is far under budget (median < 4 ms for
  600 games), and selectors are narrow (`useGame`). Tests, typecheck and lint are green.
- **Idle behaviour.** No polling at idle: session tracking runs only while sessions exist, update and overlay checks wake once per
  interval, free games follow the promotion schedule, the main WebView2 is suspended in the tray, and "start in tray" creates no
  WebView2 at all.

---

## Fix plan: independent work packages

Each package owns the files listed, and no file appears in two packages, so separate agents can work in parallel. Shared
contract files (`Models.cs`, `IGliderRpc.cs`) only need the additive changes noted; regenerate the TypeScript with
`dotnet run --project tools/Glider.ContractGen` and rebase on each other's generated file.

| # | Package | Findings | Owns (files) | Priority |
|---|---|---|---|---|
| WP1 | **Host shutdown, startup failure, bridge hygiene** | H1, M3, M4, M5 | `src/Glider.App/Composition.cs`, `Program.cs`, `HostRuntime.cs`, `WebViewBridge.cs`, `SingleInstance.cs`, `MainWindow.cs`; `frontend/src/ipc/client.ts` (+ test); `frontend/src/app/boot.ts` | 1 |
| WP2 | **Connectivity recovery** | H2 | `src/Glider.Core/Services/UpdateService.cs`, `src/Glider.Core/Auth/AuthService.cs`, new `src/Glider.Core/Services/ConnectivityMonitor.cs`, `src/Glider.Core/Saves/SaveService.cs` (the post-exit replay only) | 1 |
| WP3 | **Writer-lane robustness** | H3, M1, M2, M8, L10, L11 | `src/Glider.Core/Jobs/JobQueue.cs`, `Jobs/JobPlan.cs`, new `Jobs/JobGate.cs` (non-blocking gate seam), `src/Glider.Core/Services/JobHooks.cs`; additive `JobInfo.Cancellable` in `Models.cs`; `frontend/src/features/downloads/*`, `features/game/GameActions.tsx` (read `cancellable`) | 1 |
| WP4 | **Library cold start and incremental rebuild** | H4, M6, L13 (perf.md) | `src/Glider.Core/Library/LibraryService.cs`, `Library/GameCatalog.cs`, `tests/Glider.Core.Tests/Benchmarks/StartupBenchmarks.cs`, `frontend/src/state/libraryIndex.ts`, `docs/perf.md` | 2 |
| WP5 | **Metadata lane priority** | H7 | `src/Glider.Core/Services/ServiceContext.cs` (move `MetadataLane` to its own `Services/MetadataLane.cs`; add `RunInteractiveAsync`, leave `RunAsync` as the background path so `LibraryService` is untouched), `src/Glider.Core/Game/InstallService.cs` (`PlanAsync`/`GetInfoAsync` only) | 2 |
| WP6 | **Store view lifecycle** | H5 | `src/Glider.App/StoreView.cs`, `src/Glider.App/SelfTest.cs` (add the hidden-Store step) | 2 |
| WP7 | **Data location** (needs a product decision first) | H6, L8 | `src/Glider.Core/Abstractions.cs` (`AppPaths.ForCurrentUser`), `src/Glider.Core/Infrastructure/Migrations/*` (schema 3 move + backup reuse), `src/Glider.App/WindowsShellIntegration.cs` (uninstall hook), `build/*` | 3 (design now, ship before v0.2 GA) |
| WP8 | **Mock ↔ Core parity** | M7 | `frontend/src/ipc/mock/*`, the install/modify dialog under `frontend/src/features/dialogs/` (hide components), new conformance tests (`frontend/src/ipc/mock/conformance.test.ts`, `tests/Glider.Core.Tests/Contracts/ConformanceTests.cs`) | 3 |
| WP9 | **Core hygiene** | L1, L3, L4, L5, L6, L7, L12 | `src/Glider.Core/Game/LaunchService.cs`, `Game/SessionTracker.cs`, `src/Glider.Core/Egl/EglService.cs`, `src/Glider.Core/Services/GliderService.cs`, `Services/GliderRuntime.cs`, `Services/GliderServiceOptions.cs`, `src/Glider.Core/Saves/SaveService.cs` (the lane only, coordinated with WP2) | 4 |
| WP10 | **Test reliability and docs** | L9, L13 (README) | `tests/Glider.Core.Tests/Legendary/CdnProbeTests.cs`, `Legendary/SpeedLimitTests.cs`, `EndToEnd/*`, `README.md` | 4 |

**Order and dependencies.** WP1, WP2 and WP3 are independent and fix the user-visible Highs: run them first, in parallel. WP4,
WP5 and WP6 come next, also in parallel. WP7 needs a decision on the new data root before code; after that it is self-contained,
because the host keeps calling `AppPaths.ForCurrentUser()`. WP8 should land after WP3, because the mock's cancel and pause
semantics should copy Core's new `Cancellable` field. WP9 and WP10 can run at any time; WP9's `SaveService` change is limited to
the lane field and must not touch the post-exit code owned by WP2.

**Definition of done:**
- **Per package:** the finding's scenario is reproduced as a failing test first (xUnit for Core, vitest for the UI, a self-test step
  for the host); `dotnet test` and `pnpm test`/`typecheck`/`lint` pass.
- **WP1:** quit-to-exit time is measured and under 1 s.
- **WP4:** the startup benchmark runs with real-shaped metadata and fails above 50 ms for `library.get`.
