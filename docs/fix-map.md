# Fix map

Maps the issues in the fix brief to the code that owns them. Paths are relative to the repo root. Line numbers are approximate (read at HEAD `834540a`).

## Legendary CLI wrapper and test fakes

- Interface: `src/Glider.Core/Abstractions.cs:140` `ILegendary` (`GetGameInfoAsync` line ~155, `GetInstalledAsync`, `InstallAsync`, `VerifyAsync`, `ImportAsync`, `MoveAsync`, `LaunchAsync`).
- Production impl: `src/Glider.Core/Legendary/LegendaryCli.cs:53` `LegendaryCli : ILegendary`. Builds argv (`InstallArguments` line 550) and calls `RunAsync`.
- Process runner: `src/Glider.Core/Legendary/LegendaryProcessRunner.cs:87` (`RunAsync` line 117). Runs legendary in a kill-on-close Job Object. Output parsed by `ProgressParser.cs`, `LegendaryOutputFacts.cs`, `ErrorClassifier.cs`.
- Exe location: `src/Glider.Core/Abstractions.cs:22` (`AppContext.BaseDirectory/legendary/legendary.exe`); pinned hash in `build/legendary.json`, `LegendaryCli.PinnedSha256`.
- File readers: `src/Glider.Core/Legendary/LegendaryFiles.cs` (`InstalledJson` line 11, `Metadata` line 17, `ManifestsDir` line 25).
- Test fakes:
  - In-memory: `tests/Glider.Core.Tests/Services/Fakes.cs:160` `FakeLegendary : ILegendary` (fields `Info`, `Installed`). Used by `Services/Rig.cs:17`.
  - Real exe: `tests/FakeLegendary/Program.cs` (script directives: `download`, `verify`, `installed`, `err`, `out`). Used by `tests/Glider.Core.Tests/TestSupport/TestSupport.cs:250` `FakeLegendaryEnv` and `Legendary/LegendaryProcessRunnerTests.cs`.
  - Fixtures: `tests/Glider.Core.Tests/Fixtures/legendary/` (`synthetic-*.txt`, `synthetic-installed.json`, `synthetic-metadata-Fortnite.json`). Generator: `generate_synthetic.py`.

## A1. UI spacing/alignment, Settings width jump, three-dot vs Play

- Settings width jump (scrollbar appears/disappears on section switch):
  - `frontend/src/features/common/Page.tsx:5` `PageScroll` (`h-full overflow-y-auto`, no `scrollbar-gutter`).
  - `frontend/src/features/settings/Settings.tsx:505` `Settings()`, nav+content grid line ~515.
  - Global scrollbar styling: `frontend/src/styles/index.css:79-80`.
- Three-dot vs Play mismatch:
  - Play: `frontend/src/features/library/PrimaryAction.tsx:81` `PrimaryAction`, class at ~line 102 (`h-12 min-w-[132px] px-6 rounded`, default radius, `bg-primary`).
  - Three-dot: `frontend/src/features/library/LibraryParts.tsx:92` `MoreMenu`, trigger at ~line 98 (`size-12 rounded-[14px] bg-surface-3`). Height matches, radius does not.
  - Layout: `frontend/src/features/library/Library.tsx:106-107` (renders `PrimaryAction` then `MoreMenu`).
- Design tokens: `frontend/src/styles/tokens.css` (radius scale lines 49-54; `--titlebar-height`/`--sidebar-width` 67-68; type line-heights 21-39; surface/border colours on `:root` ~81-91 dark, ~148-157 light, Tailwind mapping ~206-221).
- Button sizes: `frontend/src/ui/Button.tsx` (size map lines 23-26, icon sizes line 64).
- Menu: `frontend/src/ui/DropdownMenu.tsx`.
- Tests: `frontend/src/features/features.test.tsx`, `frontend/e2e/screenshots.spec.ts`, `frontend/e2e/focus.spec.ts`, `frontend/e2e/a11y.spec.ts`.

## A2. Playtime on the launch/game metadata row

- Display row: `frontend/src/features/library/LibraryParts.tsx:43` `Fact`, `:53` `Facts` (rows: Version ~64, On disk ~65, Location ~68, EGL status ~73; not-installed branch ~83-86). Rendered from `Library.tsx:117`.
- Existing session tracking: only live, not persisted.
  - `src/Glider.Core/Contracts/Models.cs:80` `GameState.RunningSince` (for a "playing for" display).
  - `src/Glider.Core/Game/LaunchService.cs:306` `SetRun` sets `RunningSince`; `:257` starts `ProcessWatcher.RunAsync`.
  - `src/Glider.Core/Game/ProcessWatcher.cs:43` (transition event `(state, runningSince, launchFailed)`), `:160` `RunAsync`.
- No playtime total or last-played field exists. `GameRecordStore.cs` (`state/game.json`) has no such field. Legendary's `installed.json` / metadata carry no playtime in the fixtures.
- Contract gap (todo): `GameState.LastPlayedAt` (not in `Models.cs` or `contract.gen.ts`).
- Frontend for "last played": `frontend/src/state/derive.ts` (hero/status), `frontend/src/state/format.ts`.

## A3. Download progress bar, speed, paused state

- Bar component: `frontend/src/ui/ProgressBar.tsx:15` `ProgressBarProps`, `:28` `ProgressBar({ value, tone, size, label })` (`value: null` = indeterminate).
- Library card: `frontend/src/features/library/LibraryParts.tsx:21` `JobLine` (`paused` at line 24; bar line 28; "Paused at" line 37).
- Downloads page: `frontend/src/features/downloads/ActiveJobCard.tsx:43` `ActiveJobCard` (`paused` line 49; bar line 135; speed metrics lines 141-146; sparkline 154-165). Pause/resume buttons lines 97-104.
- Hero percent: `frontend/src/features/library/PrimaryAction.tsx:30` `JobPercent`, `:42` `JobFill`.
- Progress state: `frontend/src/state/progress.ts`, `frontend/src/state/store.ts` (`ingestProgress`), `frontend/src/state/format.ts` (`formatSpeed`, `formatEta`).
- Source of speed and paused:
  - Speeds: `JobProgress` (`src/Glider.Core/Contracts/Models.cs:134`; `DownloadBytesPerSecond`, `DiskBytesPerSecond`). Produced by `src/Glider.Core/Jobs/ProgressSmoother.cs`, `src/Glider.Core/Jobs/JobProgressTracker.cs:95` (zeroes on Finalizing), `src/Glider.Core/Legendary/ProgressParser.cs`.
  - Paused: `JobInfo.Status` (`Models.cs:120`), set by `src/Glider.Core/Jobs/JobQueue.cs` (pause/resume).
- Tests: `tests/Glider.Core.Tests/Jobs/ProgressTests.cs` (`ProgressSmootherTests`, `ProgressThrottleTests`), `frontend/src/state/store.test.ts`, `frontend/src/state/format.test.ts`.

## A4. "On disk" size label

- UI label: `frontend/src/features/library/LibraryParts.tsx:65-66` (`<Fact label="On disk">` with `formatBytes(installed.installSizeBytes)`). Also used in `frontend/src/features/dialogs/Dialogs.tsx:150, 189, 228-229` (uninstall and move).
- Backend value:
  - `src/Glider.Core/Contracts/Models.cs:89` `InstalledGame.InstallSizeBytes`.
  - Set at adopt time: `src/Glider.Core/Game/InstallService.cs:104` `AdoptAsync`, `:144-146` (`new GameRecord(..., installed.InstallSizeBytes, ...)`).
  - Source: `LegendaryCli.GetInstalledAsync` (`LegendaryCli.cs:241`) reading `installed.json` via `LegendaryFiles.ParseInstalled`.
  - Surfaced: `src/Glider.Core/Services/StateHub.cs:222` `BuildGame`, `:235` `record.InstallSizeBytes`.
- Data type: `frontend/src/ipc/contract.gen.ts` (generated; do not hand-edit).
- Tests: `tests/Glider.Core.Tests/Legendary/ParsingTests.cs`, `tests/Glider.Core.Tests/Services/GameTests.cs`, `frontend/src/features/features.test.tsx`.

## B5. Mica, title bar, window chrome, theme

- WPF/host window: `src/Glider.App/MainWindow.cs` (`IsMicaActive` line 81; `DefaultBackgroundColor` lines 310 and 436; `BuildStartUrl` line 349; `ToggleMaximize` line 220; `MinimizeWindow` line 218; `Close` path lines 237-275).
- Chrome and backdrop: `src/Glider.App/MainWindow.Chrome.cs`.
  - `ApplyBackdrop` lines 154-190 (`DWMWA_USE_IMMERSIVE_DARK_MODE` line 163; `DWMWA_SYSTEMBACKDROP_TYPE` line 169; `ActiveBackdrop` line 185).
  - Theme change via `ImmersiveColorSet` line 60; theme message line 201.
- Custom caption buttons and hit-testing: `src/Glider.App/CaptionOverlays.cs` (`CaptionOverlays` line 24; `WM_NCHITTEST` lines 122 and 171). Button size from `src/Glider.App/HostConstants.cs:19-20`.
- Native interop: `src/Glider.App/Native/Win32.cs` (`DWMWA_*` constants lines 282-289; `WM_NCHITTEST` line 187; `DwmSetWindowAttribute` line 500).
- Support: `src/Glider.App/NativeWindow.cs` (not read in detail for this map), `src/Glider.App/HostEnvironment.cs:13-16` (`SupportsMica`, `GLIDER_FORCE_SOLID=1`, Build >= 22621).
- WebView2 init and background: `src/Glider.App/WebViewSetup.cs` (`WebViewSetup.BuildStartUrl`, called from `MainWindow.cs:349`; environment setup not read in detail), `src/Glider.App/WebViewBridge.cs`.
- Theme: `src/Glider.App/Theme.cs:5-38` (`ThemeChoice`, `ThemeState`, `Parse`, `IsDark` from `AppsUseLightTheme`); `src/Glider.App/Program.cs:114-115` (theme read at startup via `HostSettingsPeek.cs:12`).
- Login window (also WebView2 + theme colours): `src/Glider.App/EpicLogin.cs:141-221`.
- Frontend chrome: `frontend/src/app/TitleBar.tsx` (`CaptionButtons` line 72, `TitleBar` line 137, `ActivityPill` line 146), `frontend/src/app/ActivityPill.tsx`.
- Tests: `src/Glider.App/SelfTest.cs` (`Report["mica"]` line 153; maximize line 280; close line 403; WM_NCHITTEST probe line 124). `frontend/e2e/focus.spec.ts`. Host has no unit-test project.

## C6. Epic Games Launcher (EGL) detection, import, write-back

- Service: `src/Glider.Core/Egl/EglService.cs:29` `EglService`.
  - `ReadItems` line 84 (parses `.item` manifests; `ItemFile` line 70).
  - `ScanAsync` line 214 / `Scan` line 217 (launcher running, items, `LauncherInstalled.dat`).
  - `CheckDrift` line 520. `WriteBackAsync` line 562 / `WriteBackCoreAsync` line 575 / `ApplyExport` line 647.
  - `UpdateLocationAsync` line 703 (`StageLauncherInstalled` line 752).
  - `IsLauncherRunning` line 336; `WaitUntilNotBusyAsync` line 429.
- Manifest: `src/Glider.Core/Egl/EglManifest.cs:13` `EglManifest` (`Known` keys line 60; `FromJson` line 99), `EglJson` line 200.
- LauncherInstalled.dat: `src/Glider.Core/Egl/LauncherInstalledDat.cs:24` (`Parse` line 35, `Patch` line 105).
- Transactions and backups: `src/Glider.Core/Egl/EglFileTransaction.cs:14` (`CommitAsync` line 39, `CreateBackupsAsync` 84, `Rollback` 151, `WriteAtomic` 207).
- Paths and version: `src/Glider.Core/Egl/EglPath.cs:7`; `src/Glider.Core/Egl/FortniteVersion.cs:20-35`; `src/Glider.Core/Egl/PyJson.cs:72` (Python-compatible JSON writer).
- Options and process probe: `src/Glider.Core/Egl/EglServiceOptions.cs:6, 33`.
- Locate existing install / import via Legendary:
  - `src/Glider.Core/Game/InstallService.cs:91` (validate/adopt section), `:104` `AdoptAsync`, `:161` return.
  - `src/Glider.Core/Legendary/LegendaryCli.cs:362` `ImportAsync` (`import` command).
  - `src/Glider.Core/Services/GliderService.cs:303` `Adopt` RPC.
- Frontend: `frontend/src/features/dialogs/Dialogs.tsx:33` (adopt with `verify`); `frontend/src/features/onboarding/Onboarding.tsx:172, 224, 280`; `frontend/src/features/settings/Settings.tsx:366` `Egl` section (candidate list 418-431; `candidateStatus` in `frontend/src/state/derive.ts`); `frontend/src/features/library/LibraryParts.tsx:132` `ExistingInstallHint`.
- Tests: `tests/Glider.Core.Tests/Egl/` (`EglDetectionTests.cs` 13 facts, `EglWriteBackTests.cs` 12, `EglBusyTests.cs` 9, `EglDriftTests.cs` 8, `EglHostileInputTests.cs` 3, helper `EglTestTree.cs`).
- Fixtures: `tests/Glider.Core.Tests/Fixtures/Egl/` (`fortnite-egl.item`, `edge-cases.item`, `LauncherInstalled.dat`, `*.golden.item`, `fortnite.golden.mancpn`). Generators: `tools/egl-golden/make_fixtures.py`, `make_goldens.py`. Design notes: `docs/egl.md`, `docs/decisions/egl.md`.

## D7. Update detection (installed vs latest) and scheduling

- Checker: `src/Glider.Core/Services/UpdateChecker.cs:6` `UpdateChecker`.
  - `InfoFreshFor` = 10 min (line 8). `GetInfoAsync` line 18; `FetchAsync` line 41 (calls `ILegendary.GetGameInfoAsync`, sets `LastUpdateCheck` line 51, `DownloadSizeBytes` line 49).
  - `CheckAsync` line 81 (sets `CheckingForUpdates`). `Result` line 121 (`InstalledVersion` from `g.Installed?.Version`).
  - `Evaluate` line 128 (notify once per version). `TryAutoUpdate` line 147 (enqueues `JobKind.Update`).
  - `RunPeriodicAsync` line 167 (interval from `Settings.UpdateCheckIntervalMinutes`, clamped 5..1440 at line 171).
  - `CheckedRecentlyWithoutUpdate` line 186.
- Scheduling and wiring:
  - `src/Glider.Core/Services/GliderService.cs:62` (`new UpdateChecker`), `:129` (`WarmupTask` runs `Prewarm`).
  - `src/Glider.Core/Services/Prewarm.cs:24-28` (`GetVersionAsync` at startup).
  - `src/Glider.Core/Services/GliderServiceOptions.cs:44` `RecentUpdateCheck` = 15 min.
  - Validation: `GliderService.cs:447`.
- Installed version: `LegendaryCli.GetInstalledAsync` (`LegendaryCli.cs:241`), `installed.json`.
- Latest version and metadata cache: `LegendaryCli.GetGameInfoAsync` (`LegendaryCli.cs:202`, metadata read at 221 from `LegendaryFiles.Metadata`, cache under `<config>/metadata/`).
- State model: `src/Glider.Core/Contracts/Models.cs:68` `GameState` (`LatestVersion` 74, `UpdateAvailable` 75, `CheckingForUpdates` 76, `LastUpdateCheck` 77).
- Frontend: `frontend/src/state/actions.ts:127` (`checkForUpdates` toast "up to date"); `frontend/src/state/derive.ts:214-221` (Update hero); `frontend/src/features/onboarding/Onboarding.tsx:172, 330`; `frontend/src/features/downloads/Downloads.tsx:133`.
- Tests: `tests/Glider.Core.Tests/Services/GameTests.cs:19, 68` (`FakeLegendary.MakeInfo`), `tests/Glider.Core.Tests/Services/ServiceTests.cs`, `tests/Glider.Core.Tests/Services/StartupTests.cs:111`, `frontend/src/state/derive.test.ts:9, 70`.

## D8. Verify and repair

- Backend: `src/Glider.Core/Legendary/LegendaryCli.cs:349` `VerifyAsync` (`["verify", appName]`, error via `ErrorClassifier.Classify` line 358).
- Repair: `LegendaryCli.cs:271` `InstallAsync` with `InstallMode.Repair` (kind mapping 286-288; manifest-missing fallback 322-325); arguments in `InstallArguments` line 550 (`repair` at 555, `repairAndUpdate` at 572).
- Jobs: `src/Glider.Core/Game/InstallService.cs` (job-backed verify/repair), `src/Glider.Core/Jobs/JobQueue.cs`, `src/Glider.Core/Jobs/JobProgressTracker.cs` (phases: verifying, per-phase percent).
- Models: `src/Glider.Core/Contracts/Models.cs` (`JobKind` includes `Verify`, `Repair`; `FixAction.Repair` line 259).
- Frontend trigger:
  - `frontend/src/state/actions.ts:107` `verify()` (RPC `game.verify`); `:268-270, :295-296` (repair/verify fix routes).
  - `frontend/src/features/library/LibraryParts.tsx:110` (More menu "Verify and repair").
  - `frontend/src/features/settings/Settings.tsx:344-348` (Verify files button).
  - `frontend/src/app/CommandPalette.tsx:81, 87`.
  - Hero repair: `frontend/src/state/derive.ts:237-242`; `frontend/src/features/library/PrimaryAction.tsx:17`.
- Result reporting: `frontend/src/features/downloads/ActiveJobCard.tsx:55, 67` (verify/repair presentation); `frontend/src/features/downloads/Downloads.tsx`; toasts via `Notice` (`Models.cs:267`); `frontend/src/state/derive.ts:9-42` (labels "Verified", "Repaired").
- Tests: `tests/Glider.Core.Tests/Legendary/LegendaryCliTests.cs:23-55` (install/repair recipes); fixtures `tests/Glider.Core.Tests/Fixtures/legendary/synthetic-verify-*.stdout.txt` / `.stderr.txt` (ok, failed, largefile, locked) and `synthetic-repair.*`; `tests/Glider.Core.Tests/Services/GameTests.cs`; `frontend/src/features/features.test.tsx:76-82` (launch failure with Repair); `frontend/src/state/derive.test.ts:48-51`.

## D9. Installed-size and download-size computation and caching

- Sizes from manifest: `src/Glider.Core/Legendary/LegendaryCli.cs:202` `GetGameInfoAsync`, `:221-226` (metadata, tags, `Sizes`); `:665` `Sizes(manifest, tags)`; `:672-683` (`download_size`, `disk_size`, `tag_download_size`, `tag_disk_size`, capped by tag sums); `:642` `ChosenInstallTagsAsync` (installed tags or untagged).
- Download-size from run output: `src/Glider.Core/Legendary/LegendaryOutputFacts.cs:61, 190` (MiB parsed); `LegendaryCli.cs:345` (`LegendaryOutcome` with sizes); `src/Glider.Core/Legendary/LegendaryProcessRunner.cs:582` (`LegendaryRunSizes`); `src/Glider.Core/Abstractions.cs:84` (`LegendaryGameInfo.DownloadSizeBytes`), `:134` (`LegendaryOutcome`).
- Caching: in memory only (`GameState.DownloadSizeBytes`/`InstallSizeBytes`, `Models.cs:83-84`; `StateHub.cs:255-256`); refreshed by `UpdateChecker.cs:49`; `UpdateChecker.InfoFreshFor` 10 min. Used by `src/Glider.Core/Game/InstallService.cs:51`.
- Frontend: `frontend/src/state/derive.ts:221, 253`; `frontend/src/features/common/InstallPlanner.tsx:63, 85-86`; `frontend/src/features/onboarding/Onboarding.tsx:291, 331`; `frontend/src/features/dialogs/Dialogs.tsx:189`.
- Tests: `tests/Glider.Core.Tests/Legendary/LegendaryCliTests.cs:37` (download size from output), `tests/Glider.Core.Tests/Legendary/ErrorClassifierTests.cs:121`, `tests/Glider.Core.Tests/Legendary/ParsingTests.cs`, fixtures `synthetic-metadata-Fortnite.json` (`disk_size`, `download_size`, tag sizes).

## Test commands

Frontend (run from `frontend/`):
- `pnpm test` (vitest; single file: `pnpm vitest run src/features/features.test.tsx`)
- `pnpm typecheck`, `pnpm lint`
- `pnpm screenshots` (Playwright: screenshots, break-ui), `pnpm a11y` (a11y, focus)
- Hero contrast: `HERO_ENFORCE=1 npx playwright test e2e/hero-legibility.spec.ts`

.NET (run from repo root):
- `dotnet build Glider.slnx -c Release -warnaserror`
- `dotnet test --project tests/Glider.Core.Tests -c Release`
- Filter: `dotnet test --project tests/Glider.Core.Tests -c Release --filter "FullyQualifiedName~Egl"`
- Contract drift check: `dotnet run --project tools/Glider.ContractGen -c Release -- --check` (regenerate `frontend/src/ipc/contract.gen.ts` after changing `Models.cs` or `Rpc.cs`)
- Host smoke test: `src/Glider.App/SelfTest.cs` (Mica, maximize, close). Invocation switch not verified; check `src/Glider.App/Program.cs` and `build/build.ps1`.
- Real Legendary smoke (needs the downloaded exe): `build/smoke-legendary.ps1`.

## Triage

Each open item in `todo.md` assigned to one group. A = frontend polish (issues 1-4), B = Mica/title bar and window host, C = EGL, D = Legendary state/updates/verify/sizes, E = fits none. Done items listed last.

Group A (13)
- Hero legibility fails AA (`frontend/e2e/hero-legibility.spec.ts`, `HeroArt.tsx`, `Library.tsx`).
- Installing state repeats itself (`LibraryParts.tsx` JobLine, `PrimaryAction.tsx`, `ActivePill`).
- Percent is per phase: label by phase (`JobProgressTracker.cs:95` vs `sim.ts`/`stress.ts` mock reset).
- "Ready to play" choreography (`PrimaryAction.tsx`, `StatusLine.tsx`, `frontend/e2e/tools/film.mjs`).
- Toasts cover the Library facts row; duplicate notices (`Notices.tsx`, `notices.ts`, `Library.tsx`).
- Settings "Auto (1024 MB)" wraps in its segment (`Settings.tsx:104`, `ui/SegmentedControl.tsx`).
- Onboarding error callout cut off by sticky action bar (`Onboarding.tsx`).
- Light theme: cards darker than page; no-art hero is a dark slab (`tokens.css` light surfaces, `HeroArt.tsx`).
- Library sparse for returning users (`LibraryParts.tsx` Facts; needs item below).
- Settings is a plain list; grouping/scannability (`Settings.tsx` SECTIONS line 28).
- Contract: `GameState.LastPlayedAt` (A2 playtime; backend field in `Models.cs` / `StateHub.cs` plus `contract.gen.ts`).
- Contract: `Notice.JobId` for toast suppression (`Models.cs:267`, `notices.ts`).
- Contract: document per-phase `JobProgress.Percent`/`EtaSeconds` and `GameState.DownloadSizeBytes` semantics (docs + `Models.cs:134, 83`).

Group B (3)
- Login window: themed loading state while Epic's page loads (`src/Glider.App/EpicLogin.cs:141-221`).
- WebView2 crash recovery: recreate the environment after browser-process crash (`WebViewSetup.cs`, `MainWindow.cs`). Needs GUI test.
- Windows 10 (solid backdrop) never tested on real machine (`HostEnvironment.cs:13`, `MainWindow.Chrome.cs:154-190`). Manual.

Group C (1)
- Contract: `InstallCandidate.Reason` so the UI stops guessing stale/invalid (`Models.cs:167` InstallCandidate; `Egl/EglService.cs:276` MakeCandidate). Note: `FolderValidation.reason` already exists in `contract.gen.ts:83`, so this is only the candidate type.

Group D (4)
- Contract: `AppInfo.AvailableCdns` (`Models.cs:27`, `Abstractions.cs` Legendary download options) replacing hard-coded CDN dropdown.
- Measure download throughput against Epic CDN; benchmark `AutoTune` defaults (`Infrastructure/AutoTune.cs`, `PerfBenchmarks.cs`). Needs a real account.
- Not-yet-reviewed: update checker, error-mapping coverage, disk-full and network-loss (`ErrorClassifier.cs`, `UpdateChecker.cs`). Art cache, process watcher and `cache.glider` mapping are out of scope for D.
- Accepted risks: Job Object assignment race at Legendary start and sign-in code on Legendary's command line (`LegendaryProcessRunner.cs`, `LegendaryCli.cs`). Login-window navigation is B; RPC timeout is E.

Group E (14)
- Design: re-shoot dark and light screenshot sets and write `docs/design-critique.md` (`docs/screenshots/`, `docs/design-review.md`). Docs only.
- Contract: `[RpcMethod("game.close")] CloseGame()` for `FixAction.CloseGame` (no RPC exists; `Errors.cs:60`, `Rpc.cs`, `GliderService.cs`). Process-kill path, not Legendary.
- Installer size 91 MB to under 60 MB (`build/build.ps1`, `Glider.App.csproj`, `release.yml`).
- Idle CPU/polling audit: process watcher, EGL watcher, update timer (cross-cutting; `ProcessWatcher.cs`, `EglService.cs`, `UpdateChecker.cs:167`).
- Architecture review: UI-thread marshalling, disposal, cancellation, "no RPC can hang" (`GliderService.cs`, `HostRuntime.cs`).
- Verify Epic exchange-code lifetime (300 s assumed, prefetch TTL 90 s) (`Auth/AuthService.cs`).
- Write `docs/perf.md` and `docs/decisions/perf.md` (docs only; `docs/perf-host.md` exists).
- Single-instance edge cases with `--data-dir` and multiple sessions (`SingleInstance.cs`).
- Verify Easy Anti-Cheat signer against a real install (`Game/EacService.cs`, `Game/Authenticode.cs`).
- Glider data dir is Velopack's install root; uninstall deletes Legendary data (`Program.cs:78`, `HostEnvironment.cs`).
- FileLog flush: Core log 0 bytes while app open (`Infrastructure/FileLog.cs`).
- Never run against a real Epic account (manual checklist in `README.md`).
- Code-sign the installer (`release.yml`).
- Enable Velopack self-updates from GitHub Releases (`UpdateManager` + `GithubSource`; `HostRuntime.cs`).

Count: A 13, B 3, C 1, D 4, E 14 = 35 open items.

Already looks done
- Found after v0.1.0: "Epic login window was blank" is marked [x] and fixed in commit `834540a` (`EpicLogin.cs`, self-test screenshots login page). No further action.
- Not done, despite similar names: `docs/design-critique.md`, `docs/perf.md`, `docs/decisions/perf.md`, `Notice.JobId`, `GameState.LastPlayedAt`, `game.close` RPC do not exist yet.
