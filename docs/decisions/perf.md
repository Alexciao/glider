# Performance decisions

- **Self-contained + ReadyToRun, no trimming**: WPF forbids trimming (NETSDK1168); R2R covers startup. Cost is the 154 MB publish / 90.7 MB installer; the only large cut would be a framework-dependent build, which needs the user to have .NET 10 and is a product decision, so it is not done in v0.1.2.
- **Post-startup working-set trim (3 s) and WebView2 suspend when hidden**: private working set 3 MB at idle; commit (74 MB) does not shrink and is accepted.
- **Event-driven over polling**: EGL uses `FileSystemWatcher` with a poll only as a fallback while blocked; the process watcher polls only while a game session is active.
- **Update checks are coarse timers**: the game update check uses the user setting (5 min - 24 h); the app self-update checks once after 45 s and then every 12 h, never on a tight loop.
- **FileLog flushes per drained batch** (writer thread, never blocks callers) and commits file metadata each time so on-disk size is truthful while the app runs.
