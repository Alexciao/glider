# TODO

Work interrupted by the budget stop on 2026-11-12, plus issues found since. Grouped by area, roughly in priority order.

## Design / UX (Fable review found these; no fixes landed yet)
- [ ] **Hero legibility fails AA in 20/60 measured cases** (worst 2.09:1), clustered in error states and at 960×600 where the text rises above the scrim; default mock art fails once (4.05:1). Run `frontend/e2e/hero-legibility.spec.ts` with `HERO_ENFORCE=1` until it passes on `?art=busy|bright|white`. Not fixed: needs real-pixel contrast work in HeroArt/scrim; out of cheap scope.
- [ ] **Installing state repeats itself**: "Installing" ×3, percent ×2, speed ×2, and the primary button is a status label, not an action. Pick one source of truth on Library; show the title-bar pill only off-Library. Not fixed: status line, button and pill each carry a distinct piece; removing one is a design call.
- [x] **Percent is per phase** (Verifying/Moving restart at 0 by design, see `JobProgressTracker.cs`): label the number by phase instead of "Installing N%". The mock wrongly resets on Finalizing; the backend doesn't. Fixed in v0.1.2: the label shows the phase and N%; backend percent semantics unchanged.
- [ ] **"Ready to play" choreography**: two-frame smear, ~45 px title jump, "···" button collides with the shrinking primary, ~220 ms dead dot; sheen invisible on the white button, ring too faint. Re-tune with `frontend/e2e/tools/film.mjs`. Not fixed: out of the listed scope.
- [ ] **Toasts cover the Library facts row**, and some duplicate on-screen state ("Signed out", "Fortnite is ready to play" during onboarding). Define an error/notice hierarchy; de-duplicate inline callout + toast. Partly: toasts moved top-right in v0.1.2; de-duplication not fixed (needs Notice.JobId suppression).
- [x] Settings: "Auto (1024 MB)" wraps inside its segment. Fixed in v0.1.2: wider segmented control, nowrap.
- [x] Onboarding: the error callout is cut off by the sticky action bar. Fixed in v0.1.2: error scrolls into view with a scroll margin.
- [ ] Light theme: cards render darker than the page; the no-art hero is a dark slab. Partly: light cards fixed in v0.1.2; the no-art hero dark slab is not fixed (design decision needed).
- [ ] Library feels sparse for returning users (last played / update notes / quick maintenance). Partly: last played and playtime fixed in v0.1.2; update notes and quick maintenance not fixed (out of scope).
- [ ] Settings is a plain list: better grouping/scannability (search optional). Not fixed: out of the listed scope.
- [x] Login window: show a themed loading state while Epic's page loads. Fixed in v0.1.2: themed loading state until first navigation, with a 10 s safety reveal.
- [ ] Write `docs/design-critique.md`; re-shoot and commit the dark + light 1280×800 screenshot sets with before/after pairs. Not fixed: deferred to the final screenshot pass.

## Contract changes requested by the frontend
- [x] `GameState.LastPlayedAt` (`DateTimeOffset?`) for a "last played" fact. Fixed in v0.1.2: LastPlayedAt added; contract regenerated.
- [ ] `Notice.JobId` (`string?`) so a toast can be suppressed when its job is already visible. Not fixed: out of the listed scope.
- [x] `InstallCandidate.Reason` (`string?`): the UI currently guesses the stale/invalid reason from the flags. Fixed in v0.1.2: InstallCandidate.Reason added; frontend candidateIssue not switched yet.
- [ ] `AppInfo.AvailableCdns` (`Host`, `Label`); the CDN dropdown is a hard-coded guess. Not fixed: Legendary exposes no CDN list; needs a hard-coded list and Settings wiring.
- [x] `[RpcMethod("game.close")] Task CloseGame()` for `FixAction.CloseGame` (today the button means "I've closed it" and retries). Fixed in v0.1.2: game.close does a graceful close, then kill.
- [ ] Document semantics: `JobProgress.Percent`/`EtaSeconds`/`ElapsedSeconds` (per phase vs whole job) and `GameState.DownloadSizeBytes` (update size when installed with an update available, full size otherwise). Not fixed: no semantics doc written; DownloadSizeBytes changed in v0.1.2 (update delta).

## Performance / architecture (review stopped after 5 of 7 items)
- [ ] **Installer size** (91 MB `Setup.exe`, target < 60 MB): start with ~6 MB of debugger/diagnostics DLLs (safe), then unused WPF/BCL assemblies (needs runtime verification), R2R only for our assemblies, vpk compression. Not fixed: WPF and the runtime cannot be trimmed; needs a framework-dependent build (product decision).
- [x] Idle CPU/polling audit (process watcher, EGL watcher, update timer) while idle and hidden. Fixed in v0.1.2: audited, no wasteful polling found; see docs/perf.md.
- [ ] Architecture review: UI-thread marshalling for host calls from background threads (HideToTray/ShowToast/RestoreWindow), disposal, cancellation, "no RPC can hang". Not fixed: needs a real account and machine; scheduled for v0.2.
- [ ] Verify the Epic exchange-code lifetime (assumed 300 s; prefetch TTL is 90 s). Not fixed: needs a real account and machine.
- [ ] Measure download throughput against Epic's CDN with a real account; benchmark the AutoTune defaults. Not fixed: needs a real Epic account.
- [x] Write `docs/perf.md` (method + before/after tables) and `docs/decisions/perf.md`. Fixed in v0.1.2: both files written.

## Security / robustness (review stopped early)
- [x] **WebView2 crash recovery**: after a browser-process crash the window is rebuilt on the dead environment; a second crash within 10 s leaves it blank. Recreate the environment; needs a GUI test. Fixed in v0.1.2 in code (environment recreated); not yet tested against a real browser-process crash.
- [ ] Single-instance edge cases with `--data-dir` and multiple sessions. Not fixed: no bug found; mutex and pipe names already hash the data dir.
- [ ] Not yet reviewed: art cache, update checker, process watcher, full error-mapping coverage, end-to-end disk-full and network-loss behaviour, the `cache.glider` mapping. Partly: update checker reviewed in v0.1.2; error mapping, disk-full and network-loss not reviewed.
- [ ] Verify the Easy Anti-Cheat setup signer against a real Fortnite install (allow-list: "Epic Games", "Easy Anti-Cheat" substring of the certificate subject). Not fixed: needs a real Fortnite install.
- [ ] Accepted risks to revisit: Job Object assignment race at Legendary start, sign-in code visible on Legendary's command line, unrestricted login-window navigation, no per-request RPC timeout on the host side. Not fixed: accepted risks, out of scope.

## Found after v0.1.0
- [x] **Epic login window was blank** (controller created under a never-shown parent). Fixed in v0.1.1: the window is shown first and the controller's visibility/bounds are re-asserted; the self-test now screenshots the login page.
- [x] Glider's data dir (`%LocalAppData%\Glider`) is also Velopack's install root, so uninstalling Glider deletes Legendary data, logs and EGL backups. Fixed in v0.2 (review H6): the data lives in `%LocalAppData%\GliderData`, moved once by the schema-3 data-root move (`DataRootMover`).
- [x] The Core log was 0 bytes in an installed run while the app was open: confirm `FileLog` flushes promptly (or on a timer). Fixed in v0.1.2: FileLog flushes per batch (FlushFileBuffers); test added.
- [ ] Windows 10 (solid backdrop) has never been tested on a real machine. Not fixed: needs a manual test on real Windows 10 hardware.
- [ ] Never run against a real Epic account: everything in the README's manual checklist. Not fixed: needs a real account and machine.

## Later
- [ ] Code-sign the installer (removes the SmartScreen warning). Not fixed: no certificate available.
- [x] Enable Velopack self-updates from GitHub Releases (`UpdateManager` + `GithubSource`). Fixed in v0.1.2: AppUpdater checks GitHub Releases; untested against a real release.
