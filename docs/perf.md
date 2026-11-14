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
| `library.get` inside Core, 500 games, 561 KiB (first call) | 179 ms | **21–25 ms** |
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
- Left as is, measured: building 500 summaries from files (`GameCatalog.Build` + `Diff`) takes median **51 ms** (46–106 ms under load), `library.get` after warm-up serializes 561 KiB in ~6 ms, and the snapshot stays 1.6 KiB (asserted < 16 KiB). A pre-serialized JSON blob would save those 6 ms only; not worth a second code path.
- `cache/library.json` is still read synchronously in `StartAsync` (cold test process: 67–139 ms for 561 KiB; warm ~20 ms): it must be there before the first snapshot, and it overlaps window creation.

**Core benchmark** (`StartupBenchmarks`, test process = JIT-cold, so an upper bound): 500 games, `cache/library.json` 561 KiB; write-side serialize 5–6 ms;
`StartAsync` (cache load + first snapshot + first library publish) 67–139 ms; `library.get` 4–128 ms (first call, depends on load); wire JSON 561 KiB
in 63–83 ms cold and **5.7 ms warm**; rebuild of 501 games without file changes: median 51 ms. Asserted floors: start < 400 ms, `library.get` < 100 ms.

**Memory at idle** (real UI, 500 games, 5 s after reveal, after the one-off trim; 2 runs, same in the other four):

| | Host `Glider.exe` | WebView2 (6 processes) | Total |
|---|---|---|---|
| Private working set | **3 MB** | 91.5 MB | **94.5–94.8 MB** |
| Private bytes (commit) | **81–83 MB** | n/a | 273–333 MB |
| Working set (incl. shared images) | 8–9 MB | n/a | 329–335 MB |

Host < 100 MB: yes on every metric (private WS 3 MB, commit 81 MB). Total with WebView2 < 100 MB: yes on private working set (95 MB) **only with the
trim**; commit and plain working set are 3× that, as they are for any WebView2 app. The trim (compacting GC + `EmptyWorkingSet` 3 s after first reveal) already
existed (`MemoryTrimmer`); with 500 games the 561 KiB of summaries are ordinary heap and don't change the picture. Not measured: memory after scrolling a full grid.

**Search / filter < 16 ms**: not measured here (frontend index, measured by the UI side); cite their number from the frontend perf tests.

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
