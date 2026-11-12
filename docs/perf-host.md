# Host performance (Glider.App)

Measured 2026-11-12 on the dev machine: Windows 11 build 26300, WebView2 Runtime 157.0.4341.46, .NET 10.0.15, Release publish
(self-contained win-x64, ReadyToRun), warm disk cache, UI = the built-in placeholder page (the real React bundle will add its own
parse/render time and a few MB to the renderer). Numbers come from `Glider.exe --measure-startup=<file> --measure-delay=5` (milestones in ms since the OS created the process;
memory sampled 5 s after the window was revealed). Three metrics are reported because they answer different questions:

- **private working set**: resident pages not shared with other processes (Task Manager's "Memory"),
- **private bytes**: committed private memory,
- **working set**: all resident pages including shared framework images (what older tools show; inflated by shared R2R images).

Small samples (3–6 runs per variant, medians); treat differences under ~100 ms or ~3 MB as noise.

## Startup (median of 6 runs, ms since process start)

| Milestone | ms |
|---|---|
| `Main` entered | 83 |
| `VelopackApp.Run()` returned | 110 |
| WebView2 environment ready | 192 |
| Window HWND created | 421 |
| Tray icon added | 708 |
| `CoreWebView2Controller` ready | 761 |
| DOMContentLoaded / NavigationCompleted | 897 / 957 |
| **`app.ready` received = window revealed** | **1152** (min 1003, max 1394) |

Fresh WebView2 profile (empty user-data folder, OS file cache still warm): ready at 1010 ms, so profile creation is not the bottleneck.

What helped (same machine, 4 runs each):

| Change | `app.ready` |
|---|---|
| Controller created after `Show()` | 1444 |
| **Controller created before `Show()`** (browser spins up while WPF spends ~200 ms in `Show()`) | **1115** |

What did not matter, so it was not changed: concurrent vs non-concurrent GC (1176 vs 1115 ms, memory identical), `TieredPGO` on/off (identical), `GCgen0MaxBudget=16 MB` (no memory effect). Workstation non-concurrent GC is kept as the safe default (no background GC thread).

## Memory at idle (placeholder UI, window visible, 5 s after reveal)

| | Host (`Glider.exe`) | WebView2 (6 processes) | Total |
|---|---|---|---|
| Private working set, **after the one-off post-startup trim** (shipped) | **3 MB** | 65 MB | **68 MB** |
| Private working set, without the trim | 31 MB | 62 MB | 94 MB |
| Working set, shipped / without trim | 9 / 115 MB | n/a | 318 MB total WS (shipped) |
| Private bytes (commit), unchanged by trimming | 74 MB | n/a | ~220 MB |

Target was "host < 80 MB": met on private working set (3 MB) and on private bytes (74 MB); the plain working set is only
under 80 MB because of the trim (115 MB without it, mostly shared framework images). **The 74 MB commit is the figure that did not move** (the CLR/WPF native heaps are not returned by `EmptyWorkingSet`).
The trim (one compacting GC + `EmptyWorkingSet` 3 s after the first reveal) costs nothing measurable at startup; pages fault back in as needed.

### Hidden in the tray (self-test, 11 s hidden)

| State | Host private WS | WebView2 private WS |
|---|---|---|
| Visible | 15–22 MB | 144–152 MB |
| Hidden ≥ 8 s: `IsVisible=false`, `MemoryUsageTargetLevel=Low`, `TrySuspendAsync` = true, GC + `EmptyWorkingSet` | **3–6 MB** | **91–101 MB** |
| After restore (`Resume`) | 9–10 MB | 92–102 MB |

(WebView2 numbers are larger here than at idle because the self-test had also loaded the Epic login page in the same browser process group.)

### Start in tray (`--background` / `startMinimized`)

No WebView2 is created until the window is first opened: **0 child processes, host working set 4.6 MB, private bytes 67 MB** 7 s after launch (the same one-off trim runs 3 s after the tray icon is added).

### WebView2 browser arguments (3 runs each)

| Arguments | `app.ready` | WebView2 private WS | Processes |
|---|---|---|---|
| none | 1404 | 65 MB | 6 |
| **`--disable-features=msSmartScreenProtection,msEdgeShopping,msEdgeCollections,msUndersideButton,msEdgeUpsell`** (shipped) | 1211 | 62 MB | 6 |
| shipped + `--in-process-gpu` | 950 | 60 MB | 5 |
| shipped + `--disable-background-networking --disable-component-update --disable-sync --disable-breakpad --js-flags=--max-lazy` | 1099 | 63 MB | 6 |

`--in-process-gpu` removes a process (−2.6 MB working set, −18 MB commit) but a GPU crash would take the whole browser process with it, for a few MB, so it is **not** used. The extra flags in the last row gained nothing measurable.
`GLIDER_WV2_ARGS` overrides the argument string for experiments.

## Publish size

Self-contained, ReadyToRun, `SatelliteResourceLanguages=en`, `DebugType=none`, no Windows SDK projection, no WinForms, unused WebView2 wrapper assemblies pruned: publish folder **136 MB** (Setup.exe size is measured by the packaging workstream; the spike's equivalent was 72 MB). `Glider.dll` is 0.4 MB; the size is the .NET + WPF runtime (trimming is unsupported for WPF, NETSDK1168).

## How to reproduce

```powershell
dotnet publish src/Glider.App -c Release -o publish
publish\Glider.exe --measure-startup=startup.json --measure-delay=5   # uses %TEMP%\glider-host-test as its data dir; exits by itself
publish\Glider.exe --selftest=outdir                                   # bounded verification run, writes selftest.json + PNGs
```
