# Testing

What proves what in Glider, how to run it, and what only a person with a real Epic account can still check.
Nothing in the automated suites signs in to Epic, reads `%LocalAppData%\Glider`, or touches a real game folder (every test uses a temp dir).

## 1. What is verified automatically

| Layer | Where | Run | CI |
|---|---|---|---|
| **Unit / service tests** (xUnit v3): parsers on real captured Legendary output, catalog mapping on real catalog JSON, job lanes, progress, EGL scan/drift/write-back (golden files written by Legendary's own code), stores and v0.1 migration (real v0.1 shapes), links, tray, store policy, free games, art, saves, contract | `tests/Glider.Core.Tests` | `dotnet test --project tests/Glider.Core.Tests -c Release` | `Test` |
| **FakeLegendary end to end**: the whole runtime (`GliderRuntime`: migration, stores, `LegendaryCli`, job queue, EGL, services) driving `tests/FakeLegendary`, a scripted stand-in exe: sign-in, install with progress, pause/resume, EGL import, launch lanes, EGL busy | `EndToEnd/`, `Services/Rig.cs` | same | `Test` |
| **Windows shell, real**: `.lnk` files in a temp folder and a throw-away HKCU key | `Host/WindowsShellRealTests.cs` | same | `Test` |
| **Real Legendary** (this repo's pinned exe, no account) | `RealLegendary/` | §2 | `Real Legendary integration tests` |
| **Legendary smoke**: `--version`, `status --offline --json` | `build/smoke-legendary.ps1` | `pwsh build/smoke-legendary.ps1` (after `dotnet run build/FetchLegendary.cs`) | `Legendary smoke test` |
| **Contract**: C# contract and `frontend/src/ipc/contract.gen.ts` agree | `tools/Glider.ContractGen` | `dotnet run --project tools/Glider.ContractGen -- --check` | `Contract is up to date` |
| **Frontend unit** (vitest, mock backend that implements the same contract), lint, typecheck | `frontend/` | `pnpm test`, `pnpm lint`, `pnpm typecheck` | yes |
| **Playwright** (screenshots of every state, break-ui worst-case data, a11y, focus order, hero legibility, host surfaces) against the mock backend | `frontend/e2e/` | `pnpm screenshots`, `pnpm a11y` | no (local; they write `docs/screenshots`) |
| **Download benchmark** (real Legendary pipeline against a local chunk server; opt-in via `GLIDER_BENCH_*`) | `Benchmarks/`, `tools/bench/` | [download-perf.md](download-perf.md) | no |

Timing-sensitive tests (`CdnProbeTests`, `CdnProbeWarmTimeoutTests`, `SpeedLimitTests`, `InstalledSizeTests`, a few job-queue waits) use 2 s stall timeouts and real child processes. On a machine that is also busy with other builds or test hosts (or antivirus scanning each fresh PyInstaller extraction) they can fail while passing in isolation: rerun the failing class alone (`--filter-class`) before treating it as a regression. The real-Legendary collection itself takes about 3 minutes on an idle machine.

The release gate is `dotnet build Glider.slnx -c Release -warnaserror` plus the two `dotnet test` runs (with and without the real exe).

## 2. The real-Legendary suite

`tests/Glider.Core.Tests/RealLegendary/` runs Core's real `LegendaryCli`, job queue and runtime against the real `legendary.exe`. It is opt-in and reported as **skipped** otherwise.

```powershell
dotnet run build/FetchLegendary.cs                                  # once; SHA-256 verified
$env:GLIDER_REAL_LEGENDARY = '1'                                    # '1' = src/Glider.App/legendary/legendary.exe, or a full path
dotnet test --project tests/Glider.Core.Tests -c Release --filter-namespace Glider.Core.Tests.RealLegendary
$env:GLIDER_REAL_LEGENDARY_NET = '1'                                # also the two tests that send an INVALID token to Epic (see below)
```

How it stays safe and hermetic:

- Each test gets a temp data dir; `LEGENDARY_CONFIG_PATH` is its `legendary\` folder. There is no `user.json` unless a test writes the synthetic one (`FakeUser`, tokens are the literal `FAKE`).
- **No network by default.** The exe is started with `HTTPS_PROXY=http://127.0.0.1:9` (a closed port), so a warm-up or sync that a test didn't ask for ends as a plain "connection refused": nothing reaches Epic and Legendary doesn't delete the fake `user.json` (it does when Epic *rejects* a token). Only the two `RealLegendaryNetworkFact` tests lift this; they send the fake token to Epic's public OAuth endpoint, assert Core's mapping of the real rejection (`AuthExpired`), and skip themselves when Epic is unreachable. Drop `GLIDER_REAL_LEGENDARY_NET` to run fully offline.
- The suite is one non-parallel xUnit collection: it runs alone after the other tests, because ~100 PyInstaller start-ups (1.7-4 s each) otherwise starve the timing-sensitive tests of CPU.
- The `verify` fixture is a 3-file install whose manifest was written by **Legendary 0.21.1's own serializer** (`Fixtures/real-legendary/make_verify_manifest.py`, 337 bytes). The files are re-created from a byte rule at run time, so no game data is committed.

| Area | What is asserted against the real exe |
|---|---|
| Version / account | `--version` parsed; `status --offline --json` signed out (`<not logged in>`) and signed in (name and id from `user.json`, same answer from the exe and from Core's no-spawn shortcut) |
| Files the exe reads and writes | `list-installed --json` agrees with Core's `installed.json` reader; `installed.json` rewritten by the exe (`move --skip-move`) still parses; real catalog metadata (Alien: Isolation + DLC, Castlevania, Fortnite) in the on-disk format is accepted by the exe and maps to the same games/installed set in `GameCatalog` (`games_available`, `games_installed`) |
| Verify | clean install: success, 3/3 files, progress parsed, `tmp/<app>.repair` lists every present file with its actual hash; one flipped byte: `Corrupted` (1 corrupted); deleted file: counted missing; manifest copy missing; install folder deleted behind its back (`NotInstalled`); unknown game |
| Uninstall | `--keep-files`: every byte kept, game forgotten, repeat is success; full uninstall removes manifest files only (a user file in the folder survives) |
| Move | `--skip-move` only rewrites the pointer; a same-volume move moves the folder, Legendary follows, `verify` is clean afterwards |
| Import | missing folder is `InvalidFolder` (before any login); **signed out is `NotSignedIn`** (the exe dies with `ValueError: No saved credentials`); already installed |
| Launch | `launch --json --offline` → process start (executable, working dir, argument order: game, user, EGL auth; no exchange code); dry run and JSON describe the same command; not installed; missing metadata; **signed out is `NotSignedIn`**; `--override-exe` semantics; through the runtime: per-game Executable override, Working directory, launch option and arguments reach the process start (the process starter is a recorder, no game runs); Auto mode goes offline when Epic is unreachable |
| Locks | two real processes: while one holds `installed.json.lock` (stopped at an uninstall prompt), uninstall/move/import/install answer `LegendaryLocked` (busy, not failed) and change nothing; verify, launch and account reads are not held up; a queued uninstall shows the busy reason, waits and completes when the lock is released |
| Errors and auth gate | info, list, list-installed `--check-updates`, install, activate all map the real signed-out output to `NotSignedIn` + sign-in fix; signed-out `status` is an answer (null), not an error; a login that ends in a traceback frees the gate at exit (no 45 s hold); concurrent logins queue and are all answered; (network) expired token → `AuthExpired`, `user.json` forgotten, gate free |
| EOS overlay | `eos-overlay info` is read-only and offline and agrees with Core that nothing is Legendary-managed |
| Cleanup | `cleanup` really deletes `tmp/*.resume`, SDL cache and manifests of everything not installed (the reason for the rule); a source scan proves Core never builds a `cleanup` command |
| Runtime | start with no connection keeps account, library, version, credentials; signed-out start still shows the library and runs a verify job; failed repair (no connection) ends in a clean error and leaves the install and the file alone; uninstall jobs (Glider-only and delete files) |

Behaviours of the exe these tests document (all are in the assertions): a clean `verify` still writes `tmp/<app>.repair`; `launch --offline` needs `user.json` (it reads name and id); an online `status --json` while signed out exits 0 with `<not logged in>`; `import` checks the path before it logs in; `eos-overlay info` also lists Epic's own overlay installs found on the machine.

Bugs this suite found in Core (fixed, regression-tested by the same suite): the per-game Executable, Working directory and Launch option settings were stored and shown but never applied at launch; `launch --offline` without an account surfaced a Python `TypeError` as "stopped unexpectedly" instead of asking to sign in; a missing override executable was a generic failure.

## 3. What needs a person with a real account (draft manual checklist)

Everything below needs Epic's servers or a signed-in session, so no automated layer can prove it. Run on a Windows 11 box (and once on Windows 10) with a real account that owns at least one small game, a game with DLC, and Fortnite if possible. Use a scratch library folder. Record results (and any deviation) in `todo.md` or the relevant research doc.

**Sign-in**
- [ ] Sign in through the login window; the account name shows, no EGL sign-in is used (EGL stays signed in).
- [ ] Restart Glider: still signed in without a prompt. Sign out: `user.json` is gone and the library stays visible.
- [ ] Close the login window: "sign-in cancelled", nothing changes. Revoke the session on epicgames.com, then use the app: "session expired" with a sign-in action, no crash.
- [ ] Offline at start: the cached library shows, "offline" state, no error toast storm.

**Library sync**
- [ ] First sync of a big library finishes; counts match EGL. Add a game on the web: it appears after Refresh (and after the Store checkout poll).
- [ ] Hidden DLC, mods and entitlement-only DLC are not listed as games; third-party titles appear (EA/Ubisoft managed).
- [ ] Art (tall, wide, thumbnails) loads and survives offline.

**Install / update / verify / repair / move / uninstall**
- [ ] Install a small game: progress, speed, ETA, phases; finishes; Play works.
- [ ] Pause, quit the app, restart, resume: continues from the resume file. Cancel mid-download.
- [ ] Update: publisher releases a patch (or use an older build): update size shown before, only changed data downloaded.
- [ ] Verify a clean install: success. Corrupt a file, delete one: Verify→Repair→Verify heals it and re-downloads only the bad file.
- [ ] Move to another drive and to another folder on the same drive: files arrive, game still launches, old folder gone; interrupt a cross-drive move and retry.
- [ ] Uninstall keeping files, then deleting files: saves in the folder survive a delete.
- [ ] Not enough space: clear message with a free-space action; disk full mid-download is reported, not hung.

**DLC**
- [ ] Install a game with DLC: the plan lists DLC (installable vs entitlement-only); each DLC is its own job; uninstalling the base game removes DLC.

**Components (install tags)**
- [ ] Fortnite (or any SDL game): choose components in the plan; deselected files are not downloaded; changing the selection later adds/removes files.

**Downloads and modes**
- [ ] Modes Background / Balanced / Full speed: priorities and throughput differ as documented in [download-perf.md](download-perf.md); changing mode mid-download applies live.
- [ ] Speed limit applied and removed live; "slow CDN" / "disk-bound" explanations appear when true; CDN failover after a blocked host.
- [ ] A game running pauses/holds downloads (WhilePlaying); queue reorder, pause all, resume all; two games queued run one at a time.

**Launch online / offline / EAC**
- [ ] Online launch: starts the game, playtime recorded, tray shows the session; launch while another game downloads is not delayed.
- [ ] Offline launch (airplane mode) for a game that allows it; a game that doesn't gets a clear message.
- [ ] EAC game (Fortnite): first launch runs the EAC setup once (one UAC prompt); later launches don't. Launch a second time while the first runs: "already running".
- [ ] Executable override, working directory, launch option and extra arguments from Game settings take effect (checked automatically up to the process start).
- [ ] Prerequisites (redistributables) prompt once, then never again until the game updates.

**Cloud saves**
- [ ] Sync on launch/exit with a game that has cloud saves; both-sides-changed conflict shows the resolution dialog and backs up the loser (`backups\saves`).
- [ ] Offline launch skips sync and uploads on the next online exit; nothing is overwritten without a backup.

**EOS overlay**
- [ ] Install the overlay, toggle on/off, remove; the registry entries (`HKCU\...\EOS`, Vulkan layers) follow; an overlay already installed by EGL is reused.

**Third-party (Ubisoft Connect / EA app)**
- [ ] Activation flow for a Ubisoft game (account linked / not linked); launch hands over to the other launcher, shows "launching", and doesn't hold the download queue forever.

**EGL coexistence: import, write-back, readiness**
- [ ] With EGL installed: Glider lists EGL installs; importing downloads nothing; verify runs only when versions differ.
- [ ] "Keep Epic Games Launcher in sync": after Glider updates a game, EGL shows it installed and up to date and offers no verify/update. Items 1-9 of [egl.md](egl.md) "Manual verification needed on a real EGL install".
- [ ] EGL busy (patching or verifying) makes Glider wait with a reason; EGL running idle does not.
- [ ] Readiness check reports a missing/stale EGL data folder sensibly.

**Store and free games**
- [ ] Store tab: loads signed in (web sign-in via exchange code), Install/Launch buttons route to Glider, no EGL prompt; the checkout poll adds a bought/claimed game to the library. Live-test items L1-L9 in [research/store-webview.md](research/store-webview.md) §9.
- [ ] Free games strip shows the current promotions, offline falls back to the cache.

**Shortcuts, links, tray**
- [ ] Desktop/Start shortcut per game launches it through Glider; `glider://` links and the `com.epicgames.launcher://` handler route correctly; Epic's own handler is restored when Glider's is turned off.
- [ ] Tray: "Close <game>", "Play <last played>", close-to-tray, single instance (second start focuses the first), start with Windows.

**v0.1 → v0.2 upgrade**
- [ ] Install v0.1.5, sign in, install Fortnite, then update to v0.2 in place: no re-login, install adopted, settings and playtime kept, `backups\migration\v0.1-*` exists; downgrade path documented.
- [ ] Windows 10 (solid backdrop) look and the installer on a clean machine (WebView2 present/absent).
