# Performance summary

Measured numbers and method live in `docs/perf-host.md` (startup, idle memory, tray-hidden memory, WebView2 flags, publish size). This page is the short version plus the idle-wakeup audit.

## Headline (from `perf-host.md`)

- Startup to window ready: about 1.1-1.2 s median (self-contained, ReadyToRun, WebView2 environment started first, window cloaked until `app.ready`).
- Idle host memory: 3 MB private working set after the one-off post-startup trim (74 MB commit); hidden in the tray the WebView2 is suspended and a start-in-tray launch creates no WebView2 at all.
- Publish folder 154 MB, Setup.exe 90.7 MB (v0.1.0). Mostly the .NET + WPF runtime; WPF cannot be trimmed.

## Idle wakeup audit (v0.1.2)

| Source | Behaviour | Verdict |
|---|---|---|
| `ProcessWatcher` | Polls process names every 1.5 s only while a launch/run is in progress; the loop ends when the state returns to Idle. No handles held. | Fine. Nothing runs while the game is not launched or detected. |
| EGL watcher | `FileSystemWatcher` (event driven) plus a `BusyPollInterval` timeout, only while an operation is waiting for the Epic Games Launcher to go idle. | Fine. No timer when nothing is blocked. |
| Update timer (`UpdateChecker.RunPeriodicAsync`) | One `Task.Delay` per `UpdateCheckIntervalMinutes` (clamped 5 min - 24 h). | Fine. One wakeup per interval. |
| Self-update (`AppUpdater`) | One check 45 s after start, then a `PeriodicTimer` every 12 h; stops once an update is staged. Skipped for dev, portable and test runs. | Fine. |
| `FileLog` | Writer awaits a channel; no timer. One `FlushFileBuffers` per drained batch. | Fine. |

No wasteful polling was found, so nothing was changed. Not measured: CPU wakeups per second with an ETW trace; this is a code audit only.

## Not covered

Windows 10 numbers need real hardware; they are not part of v0.1.2. Download throughput (CDN ceiling, Legendary's pipeline, disk, priorities) is measured in `docs/download-perf.md` (v0.2).

## v0.2 startup targets (500 cached games)

Targets: interactive library with 500 cached games < 1.5 s from process start; host idle RAM < 100 MB (and the total with WebView2);
search/filter < 16 ms. Measured 2026-11-14, Windows 11 26300, WebView2 157, .NET 10.0.15, Release publish (self-contained, ReadyToRun), the
real React bundle (`pnpm build` → `wwwroot`), warm disk cache.

**Method.** `StartupBenchmarks.SeedDataDirForHostMeasurement` (`GLIDER_BENCH_SEED=<dir>`) writes a data dir with 500 synthetic games in
`cache/library.json` (561 KiB; 32-hex ids, art URLs, ~15 % installed, some DLC), a finished onboarding and schema 2; no Legendary folder, so the
cached library stays (`LibraryService`: "no metadata yet"). Then, per run on a fresh copy of that dir,
`Glider.exe --measure-startup=<file> --measure-delay=5 --data-dir=<dir>` (window revealed, 5 s settle, JSON, exit). "Interactive library" =
`revealed`: the UI calls `app.ready` only after `app.getSnapshot` **and** `library.get` were applied and one frame was painted twice
(`frontend/src/app/boot.ts`, `state/bridge.ts`). New marks: `rpc-ready` (dispatcher built) and `rpc-sent:app.getSnapshot` / `rpc-sent:library.get`
(response left Core); the host log also prints each one's time inside Core. `GliderRuntime` logs its phases (`runtime composed (ms since start)`).

**Caveat on the absolute numbers: the machine was not quiet.** Other agents were building and testing at the same time (CPU 76–100 % during
every run, 15 dotnet + 37 node processes), so `revealed` below is inflated by an unknown factor and the 1.5 s target could **not** be confirmed
here. What is robust under load is the time spent inside Core on the UI's critical path, which is what this change set reduces.

| | Before | After |
|---|---|---|
| `app.getSnapshot` inside Core (first call) | 145 ms | **2–4 ms** |
| `library.get` inside Core, 500 games, 561 KiB (first call), **with no Legendary metadata on disk** | 179 ms | **21–25 ms** (not the real case: see WP4 below for the same call with 500 real-sized metadata files on disk) |
| `rpc-ready` (Core composed + dispatcher built, ms since process start) | 0.69-1.5 s (6 runs, load 76-100 %) | 1.0-1.5 s (5 runs) + one 3.5 s outlier: **not improved**, load noise dominates |
| Core composition alone, one loaded run each (migrate / stores / legendary+queue / egl / service / started, cumulative ms) | 57 / 189 / 467 / 468 / 545 / 706 | 40 / 160 / 211 / 229 / 255 / 363 (a single sample each: only shows where the time goes, not a gain) |
| `revealed` (best / median of 6, **under 76–100 % CPU**) | 1.67 s / 2.4 s | 2.1 s / 3.6 s (heavier load: 100 % in 5 of 6 runs) |

What the numbers say: Core is composed on a pool thread in parallel with WebView2 and finishes before the page asks for anything
(`rpc-ready` 0.7 s clean / ~1.1 s loaded vs the first request at ≥ 1.3 s), so the long pole is the browser (environment → controller → navigate →
DOMContentLoaded → React boot). Core's remaining share is ~25 ms after the request arrives. A clean-machine `revealed` for the real UI still
has to be re-measured; the v0.1-era placeholder-UI figure was 1.15 s (`perf-host.md`), and the UI adds ~490 KB of JS to parse.

**Fixes for what blocked the targets**
- *First-use cost of the JSON contract* (≈ 480 ms of JIT in a cold test process, ~100–150 ms in the R2R host): the UI's first two requests paid it. `GliderService.WarmSerializers` (host only: `GliderServiceOptions.WarmSerializers`) serializes a throw-away snapshot and 64 games on a pool thread right after start, while WebView2 is still starting; it also warms the snapshot's sources (drives, EGL, queue).
- `RpcDispatcher.MethodNames` (reflection over `IGliderRpc` + metadata for every contract type) is touched on a pool thread at the very start of `HostRuntime`, not after Core is composed.
- Measured at the time (superseded by WP4 below): building 500 summaries from files took median **51 ms** for any change at all; `library.get` after warm-up serializes 561 KiB in ~6 ms, and the snapshot stays 1.6 KiB (asserted < 16 KiB). A pre-serialized JSON blob would save those 6 ms only; not worth a second code path.
- `cache/library.json` is still read synchronously in `StartAsync` (cold test process: 67–139 ms for 561 KiB; warm ~20 ms): it must be there before the first snapshot, and it overlaps window creation.

**Core benchmark**: see "Library cold start and incremental rebuild (WP4)" below; the 2026-11-14 numbers in this paragraph were measured with
Legendary's metadata folder deleted and are no longer the benchmark's configuration.

**Memory at idle** (real UI, 500 games, 5 s after reveal, after the one-off trim; 2 runs, same in the other four):

| | Host `Glider.exe` | WebView2 (6 processes) | Total |
|---|---|---|---|
| Private working set | **3 MB** | 91.5 MB | **94.5–94.8 MB** |
| Private bytes (commit) | **81–83 MB** | n/a | 273–333 MB |
| Working set (incl. shared images) | 8–9 MB | n/a | 329–335 MB |

Host < 100 MB: yes on every metric (private WS 3 MB, commit 81 MB). Total with WebView2 < 100 MB: yes on private working set (95 MB) **only with the
trim**; commit and plain working set are 3× that, as they are for any WebView2 app. The trim (compacting GC + `EmptyWorkingSet` 3 s after first reveal) already
existed (`MemoryTrimmer`); with 500 games the 561 KiB of summaries are ordinary heap and don't change the picture. Not measured: memory after scrolling a full grid.

**Search / filter < 16 ms**: the index answers any query in < 4 ms median (frontend perf test); the whole keystroke, to the last DOM mutation of the grid, is measured under WP4 below.

**Art cache** costs nothing at start: no file is touched until an `art.glider` request; a disk hit is `FileInfo` + one open (selftest: 200 + 15 KB JPEG
answered in 55 ms including the page's first fetch); a miss without catalog or network is a 404 in 0–2 ms (host log). A real download of a 300 px cover
from Epic's CDN is 20–35 KB (original 525 KB; `?w=300&resize=1&quality=medium`; verified with `curl`; the CDN always answers JPEG, even for `Accept: image/webp`).

To reproduce:
```powershell
$env:GLIDER_BENCH_SEED = "$env:TEMP\glider-perf-seed"; dotnet test --project tests/Glider.Core.Tests -c Release --filter-method "*SeedDataDir*"
dotnet publish src/Glider.App -c Release -o publish; Copy-Item -Recurse frontend\dist publish\wwwroot   # after `pnpm build`
Copy-Item -Recurse $env:GLIDER_BENCH_SEED $env:TEMP\run1
publish\Glider.exe --measure-startup=run1.json --measure-delay=5 --data-dir=$env:TEMP\run1
dotnet test --project tests/Glider.Core.Tests -c Release --filter-class "*StartupBenchmarks" --output detailed   # Core numbers
```

## Library cold start and incremental rebuild (WP4, `docs/review-v0.2-architecture.md` H4, M6, L13)

The numbers above were taken with Legendary's `metadata/` folder removed, so the first `library.get` never had a parse to wait for (H4). Since
WP4 the benchmark keeps real-shaped metadata on disk and the pre-warm running.

**What changed.**
- `library.get` (and `game.getDetail`) at cold start is the cached library, `cache/library.json`: it never parses Legendary's files and never waits
  on the pre-warm that does. The file-based rebuild runs in the background and arrives as one `library` delta (the UI already applies deltas in
  order). Only with no cache at all (first run after an upgrade) does it wait, up to 2 s, for the first read. Once the files were read, a foreground
  flush waits at most 500 ms for a rebuild that is already running.
- Rebuilds are incremental. `GameCatalog.Build` keeps a `CatalogMemo`: for every game it remembers the inputs its summary was computed from (catalog
  item, asset, install, Glider's record, runtime facts, art, DLC count, entitlements) and recomputes only games whose inputs changed. The reader
  re-parses `assets.json`, `installed.json` and `entitlements.json` only when their size or mtime changed (as it already did for each
  `metadata/<app>.json`). The entitlement-based "added" date is one index, not a scan per game; art picks run once per item.
- `cache/library.json` is written off the hot path, only when a summary changed since the last write, at most every 30 s (after the 2 s quiet delay),
  and once at shutdown. Before, every delta (placeholders every 3 s, playtime every 30 s) rewrote 561 KiB.
- Frontend: `LibraryIndex` is built on the previous index. Entries of unchanged games are reused (the store keeps their identity); when no sort
  position moved, only the changed entries are swapped into copies of the sorted arrays. The search box no longer re-renders the toolbar's selects and
  menus or the grid on every keystroke.

**Method.** `StartupBenchmarks` (Core, JIT-cold test process, Release, 2026-11-14, Windows 11, other agents building on the same machine). 500 games;
`metadata/*.json` written indented like Legendary (`json.dump(indent=2)`) and sized from the public catalog fixtures in
`docs/research/fixtures/legendary/catalog-v0.21.1` (75 % a 27 KB AAA item, 20 % a 2 KB one, 5 % a 77 KB one): **12.1 MiB**, plus one entitlement per game;
`cache/library.json` 561 KiB; the pre-warm is released right before `library.get`. "Before" is commit `d92f80b` with the same benchmark file; "after" is this change.
Three runs each; ranges are min–max of the three runs' figures (medians of five repetitions where it says median).

| Core, 500 games, 12.1 MiB of real-shaped metadata | Before | After |
|---|---|---|
| `library.get`, first call, pre-warm running | **2.8–3.7 s** (it parsed the 500 files itself) | **1.9–6.4 ms** |
| `StartAsync` (cache load + first snapshot + first publish) | 40–49 ms | 26–34 ms |
| Rebuild after any invalidation that changed nothing (job tick, settings write) | median 30–37 ms | median **2.0–2.4 ms** |
| Rebuild after one game's Glider record changed (favorite, playtime), `library.get` delta = 1 upsert | median 30–43 ms | median **1.9–4.0 ms** |
| Re-read of Legendary's files when none changed (every sync and job end) | median 24–47 ms | median **4.2–6.3 ms** |
| Re-read with one `metadata/<app>.json` rewritten | median 31–69 ms | median **11.8–13.8 ms** |
| `cache/library.json` writes after a rebuild that changed nothing | 0 (no delta) | 0 |
| `cache/library.json` writes while a game plays or art loads (a delta every 3–30 s) | one 561 KiB write per delta | at most one per 30 s |

The 3 s "before" is larger than the 266–678 ms of the review's probe (500 × 27 KB, warm cache): the benchmark writes its 12 MiB of files just before the
call, so Windows Defender is still scanning them. That is the cold-boot case the review describes (a Start-with-Windows launch with a freshly scanned
library drive), and the point of H4: this wait sat directly in front of the first paint.

The benchmark **fails above 50 ms for `library.get`** (no slack) and keeps generous guards (`LoadSlack` 3x) on the other Core numbers: `StartAsync`
< 1.2 s, first wire serialization < 300 ms (JIT-cold), snapshot < 16 KiB.

**Frontend** (600 games + DLC; mock backend `perf600`).

| | Before | After |
|---|---|---|
| `LibraryIndex` for a one-game delta (playtime, runState, job: nothing moves), vitest | 4.4 ms | **0.55 ms** |
| same, when a sort key moves (last played, size, title) | 4.4 ms | 1.8 ms |
| Library search keystroke: input event → last DOM mutation of the grid, production build, headless Chromium 1280×800, median of 4 runs × 54 keystrokes | 17.3 ms (17.0–17.6) | **11.6 ms** (8.5–12.7) |
| same, input event → next paint (2 frames) | 13.9–16.1 ms | 12.0–13.5 ms |

The remaining keystroke cost is mounting the visible covers (a result set of all-new tiles); it does not grow with the library. The grid is virtualized
and the query itself takes < 1 ms. In jsdom the cover is nearly all of a tile's cost (30 `GameCover`s alone: 27 ms, 30 whole tiles: 26 ms).

**Not done / why.** No pre-serialized `library.get` blob: warm serialization of 561 KiB is 3–8 ms and the first-call cost is JIT, which the host's
`WarmSerializers` already pays on a pool thread. The metadata parse itself (`JsonDocument` per file) is untouched: it now runs in the background and
only the first start after an upgrade (no cache) waits for it.

To reproduce:
```powershell
dotnet test --project tests/Glider.Core.Tests -c Release --filter-class "*StartupBenchmarks" --output detailed   # prints both benchmarks' numbers
cd frontend; pnpm vitest run src/state/libraryIndex.test.ts                                                       # index tests, incl. the delta timing
```
For the keystroke figure: `pnpm exec vite build`, `pnpm exec vite preview`, open `/?scenario=perf600&dev=0&art=none` in headless Chromium, go to the
Library, set the search box's value and dispatch `input`; watch the grid's `ul` with a `MutationObserver` (a throw-away script, not committed).
