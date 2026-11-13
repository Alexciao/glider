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

CDN throughput benchmark and Windows 10 numbers need real hardware and accounts; they are not part of v0.1.2.
