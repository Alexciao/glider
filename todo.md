# TODO

Work interrupted by the budget stop on 2026-11-12, plus issues found since. Grouped by area, roughly in priority order.

## Design / UX (Fable review found these; no fixes landed yet)
- [ ] **Hero legibility fails AA in 20/60 measured cases** (worst 2.09:1), clustered in error states and at 960×600 where the text rises above the scrim; default mock art fails once (4.05:1). Run `frontend/e2e/hero-legibility.spec.ts` with `HERO_ENFORCE=1` until it passes on `?art=busy|bright|white`.
- [ ] **Installing state repeats itself**: "Installing" ×3, percent ×2, speed ×2, and the primary button is a status label, not an action. Pick one source of truth on Library; show the title-bar pill only off-Library.
- [ ] **Percent is per phase** (Verifying/Moving restart at 0 by design, see `JobProgressTracker.cs`): label the number by phase instead of "Installing N%". The mock wrongly resets on Finalizing; the backend doesn't.
- [ ] **"Ready to play" choreography**: two-frame smear, ~45 px title jump, "···" button collides with the shrinking primary, ~220 ms dead dot; sheen invisible on the white button, ring too faint. Re-tune with `frontend/e2e/tools/film.mjs`.
- [ ] **Toasts cover the Library facts row**, and some duplicate on-screen state ("Signed out", "Fortnite is ready to play" during onboarding). Define an error/notice hierarchy; de-duplicate inline callout + toast.
- [ ] Settings: "Auto (1024 MB)" wraps inside its segment.
- [ ] Onboarding: the error callout is cut off by the sticky action bar.
- [ ] Light theme: cards render darker than the page; the no-art hero is a dark slab.
- [ ] Library feels sparse for returning users (last played / update notes / quick maintenance).
- [ ] Settings is a plain list: better grouping/scannability (search optional).
- [ ] Login window: show a themed loading state while Epic's page loads.
- [ ] Write `docs/design-critique.md`; re-shoot and commit the dark + light 1280×800 screenshot sets with before/after pairs.

## Contract changes requested by the frontend
- [ ] `GameState.LastPlayedAt` (`DateTimeOffset?`) for a "last played" fact.
- [ ] `Notice.JobId` (`string?`) so a toast can be suppressed when its job is already visible.
- [ ] `InstallCandidate.Reason` (`string?`): the UI currently guesses the stale/invalid reason from the flags.
- [ ] `AppInfo.AvailableCdns` (`Host`, `Label`); the CDN dropdown is a hard-coded guess.
- [ ] `[RpcMethod("game.close")] Task CloseGame()` for `FixAction.CloseGame` (today the button means "I've closed it" and retries).
- [ ] Document semantics: `JobProgress.Percent`/`EtaSeconds`/`ElapsedSeconds` (per phase vs whole job) and `GameState.DownloadSizeBytes` (update size when installed with an update available, full size otherwise).

## Performance / architecture (review stopped after 5 of 7 items)
- [ ] **Installer size** (91 MB `Setup.exe`, target < 60 MB): start with ~6 MB of debugger/diagnostics DLLs (safe), then unused WPF/BCL assemblies (needs runtime verification), R2R only for our assemblies, vpk compression.
- [ ] Idle CPU/polling audit (process watcher, EGL watcher, update timer) while idle and hidden.
- [ ] Architecture review: UI-thread marshalling for host calls from background threads (HideToTray/ShowToast/RestoreWindow), disposal, cancellation, "no RPC can hang".
- [ ] Verify the Epic exchange-code lifetime (assumed 300 s; prefetch TTL is 90 s).
- [ ] Measure download throughput against Epic's CDN with a real account; benchmark the AutoTune defaults.
- [ ] Write `docs/perf.md` (method + before/after tables) and `docs/decisions/perf.md`.

## Security / robustness (review stopped early)
- [ ] **WebView2 crash recovery**: after a browser-process crash the window is rebuilt on the dead environment; a second crash within 10 s leaves it blank. Recreate the environment; needs a GUI test.
- [ ] Single-instance edge cases with `--data-dir` and multiple sessions.
- [ ] Not yet reviewed: art cache, update checker, process watcher, full error-mapping coverage, end-to-end disk-full and network-loss behaviour, the `cache.glider` mapping.
- [ ] Verify the Easy Anti-Cheat setup signer against a real Fortnite install (allow-list: "Epic Games", "Easy Anti-Cheat" substring of the certificate subject).
- [ ] Accepted risks to revisit: Job Object assignment race at Legendary start, sign-in code visible on Legendary's command line, unrestricted login-window navigation, no per-request RPC timeout on the host side.

## Found after v0.1.0
- [x] **Epic login window was blank** (controller created under a never-shown parent). Fixed in v0.1.1: the window is shown first and the controller's visibility/bounds are re-asserted; the self-test now screenshots the login page.
- [ ] Glider's data dir (`%LocalAppData%\Glider`) is also Velopack's install root, so uninstalling Glider deletes Legendary data, logs and EGL backups. Decide whether to move data to its own folder (with migration).
- [ ] The Core log was 0 bytes in an installed run while the app was open: confirm `FileLog` flushes promptly (or on a timer).
- [ ] Windows 10 (solid backdrop) has never been tested on a real machine.
- [ ] Never run against a real Epic account: everything in the README's manual checklist.

## Later
- [ ] Code-sign the installer (removes the SmartScreen warning).
- [ ] Enable Velopack self-updates from GitHub Releases (`UpdateManager` + `GithubSource`).
