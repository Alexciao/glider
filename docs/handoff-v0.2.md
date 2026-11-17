# Glider v0.2: handoff (state on 2026-11-14)

> **Update 2026-11-17: v0.2.6 is the current release** (<https://github.com/Alexciao/glider/releases/tag/v0.2.6>; v0.2.6: content on Mica (`--g-layer`, frame tint 12%), Create shortcut ticks follow `game.setShortcut`, rail tooltips stay out of the Store view; the Store itself is not on Mica yet (its WebView background is opaque, `StoreView.cs`). v0.2.5: Store view raised above the page (it was created under it), Epic top bar hidden, overlay scrollbars, self-update staged until quit/Restart, installed.json install_tags kept equal to config.ini so Legendary never prunes, clean verify clears needs_verification via a 0-byte repair; v0.2.0–v0.2.2 are drafts). v0.2.4 keeps Mica after a tray hide/reshow (WebView2 lost its transparent background; `--selftest` step `mica-restore` checks it) and fixes menu wobble and small motion bugs. v0.2.1 fixed the fork's version-check crash, v0.2.2 reads EGL 20+'s EOSH installs and hands EGL's install tags to Legendary, v0.2.3 bundles Legendary as a PyInstaller onedir build (Defender false positive on the one-file exe) plus the owner's walkthrough fixes. Before every release run the real-machine checks in `docs/testing.md` (`GLIDER_REAL_EGL=1`, online `status`). v0.2.0 was cut from `main` = `v0.2`
> at `ac7e0fe`. The design critique was implemented with the owner's amendments, the download card was redesigned
> (direction D, `docs/design-download-card.md` §7), the final UI review (`docs/review-v0.2-ui-final.md`) was fixed, and CI is
> green. History was rewritten to the owner's GitHub noreply address (backup bundle outside the repo), v0.1.x releases are
> drafts, and the repo is **public**, so the Velopack self-updater now works. Open for v0.3: a second speed-limit actuator for
> caps under ~5–10 MB/s, `game.planInstall` from cached data, everything under "Not verified" below.

Where the v0.2 work stands, what's left before the v0.2.0 release, and how to work in this repo. The full mission brief
is the user's original v0.2 request; this file is the condensed state.

## Branches

- **`v0.2`** = integration branch with everything below. CI ran green on it at `9694d16` and `0850646`, and every later
  merge was verified locally (1,610 Core tests, 552 frontend tests, `-warnaserror` build, contract check).
- **`main`** = v0.1.5 + Phase 0 research docs. **Not merged yet**: merge `v0.2` into `main` only when CI on `v0.2` is green
  and the remaining UI work is in.
- Tags: `v0.1.5` (latest release), `legendary-fork-0.21.1-glider.1` (prerelease; the patched Legendary Glider now bundles).

## Done (merged into `v0.2`)

- Phase 0 research: `docs/audit-v0.1.md`, `docs/research/{legendary-v0.2,heroic-v0.2,store-webview}.md`,
  `docs/download-perf.md` (the real bottleneck on the user's PC is drive D:, a slow SATA SSD).
- Phase 1: contract v2 (`src/Glider.Core/Contracts`), game model + library cache/deltas, profiles/quirks (Fortnite
  bootstrapper fix), job runner lanes (launch never waits behind a download), v0.1 → v2 migration, 300-game mock.
- Phase 2: Library/Home/navigation, game page + dialogs + `SplitButton`, Downloads + card + graphs, onboarding +
  Settings + command palette, download engine (modes per process, CDN probe/failover, speed limit, fork), cloud saves,
  EOS overlay + prerequisites + third-party, EGL whole-library import (root cause of the user's 35 GB repair fixed),
  Store tab + free games, shortcuts + `glider://` + `com.epicgames.launcher://` + tray, art cache, update policies.
- Phase 3: real-Legendary integration suite in CI (`tests/Glider.Core.Tests/RealLegendary`, `docs/testing.md`).
- Phase 4: UI review sweep (`docs/ui-review.md`, `docs/glossary.md`), architecture review
  (`docs/review-v0.2-architecture.md`) with all 10 work packages implemented (incl. data root moved to
  `%LocalAppData%\GliderData`, schema 3), GPT-6-Luna reviews of migration, job runner, cloud saves, EGL write-back, links,
  data move (all findings fixed), test hardening.

## Remaining before v0.2.0

1. **Implement the design critique** `docs/design-critique-v0.2.md` (mockups in `docs/screenshots/critique-v0.2/`). The
   user has **not yet approved** it: show them P0-1's mockups first. Then one Sonnet (high) UI agent implements P0-1
   (download card: live speed readouts as the chart legend, full-width 80 px chart, one caption line; no stats table),
   P0-2 (download mode moves to Settings; Downloads header keeps only "Pause all"; one quiet line on the card when the
   mode/auto-throttle matters), P0-3 ("Needs attention" section for failed jobs + red rail dot + Home row group), the P1s,
   then the P2s. Afterwards a fresh Opus (xhigh) reviewer checks the result against the critique.
2. **Decisions left open** (record each in `DECISIONS.md`):
   - Speed limit can't hold low caps on a busy PC (duty-cycle limiter in `SpeedGovernor.cs` saturates): fix or document.
   - `game.planInstall` from cached data with async size refinement (skipped; needs contract + UI change).
   - Rare test flakes seen once under heavy load: `UpdatePolicyTests.AVersionTheUserWasToldAbout…`,
     `ArtCacheTests.ACacheMiss_WaitsForTheCatalog…`, `LibraryServiceTests.FiveHundredGames…`, `OverlayTests.AStaleVersionCache…`.
3. **CI green on `v0.2`**, then merge into `main`.
4. **Release v0.2.0**: version bump, `docs/release-notes-v0.2.0.md`, README (features, screenshots, **non-goals**: native
   in-app purchasing, friends/chat outside the EOS overlay, Unreal Engine/Fab/Marketplace, mod stores; architecture;
   troubleshooting), tag `v0.2.0`, `release.yml` publishes the Velopack `Setup.exe`. The repo is **private**, so v0.1's
   self-updater likely can't see releases: the user upgrades by running v0.2's `Setup.exe` (in-place over v0.1).
5. **Final report to the user** (the mission's "Done means" list): release URL, how to upgrade from v0.1 (Fortnite +
   settings carry over via the migration + data move), how to bring the EGL library in (onboarding scan / Settings →
   Epic Games Launcher → Import), whether/when it's safe to uninstall EGL (the readiness check), how cloud saves / Store /
   shortcuts work, measured numbers (below), manual checklist (`docs/testing.md`), known limitations, next steps.

## Measured numbers so far

- Download ceiling on the user's Wi-Fi ≈ 24–42 MB/s; Full ≈ 91% of ceiling (avg), Balanced ≈ 102% of Full; drive D:
  sustains ~6.5–8 MB/s (disk-bound, now detected and shown). Details: `docs/download-perf.md` §8.
- `library.get` cold (500 real-shaped games) 2–6 ms (was 2.8–3.7 s); search keystroke 11.6 ms (600 games, target 16).
- Cold start to interactive: 1.67 s best under heavy load (target 1.5 s, unconfirmed: re-measure on a quiet machine).
- Idle RAM: host ~3 MB private working set; total with WebView2 ~95 MB private WS.
- Quit-to-exit 176–714 ms (was ≥ 3 s).

## Not verified (needs the user with a real Epic account; see `docs/testing.md`)

Sign-in, library sync, real install/update/repair, launch online/offline + EAC, cloud saves against Epic, overlay
install, Ubisoft/EA linking, EGL write-back against a real EGL, Store sign-in/checkout/deep links
(`docs/research/store-webview.md` §9), tray/shortcut/protocol GUI behaviour, the data move on the real machine, a real
Velopack install → update → uninstall cycle, Windows 10.

## How to work here

- Orchestrate with subagents in their own worktrees; merge into `v0.2` yourself, then run the full checks.
- Model rules (user): **never Fable**; Opus 5.5 high/xhigh for design-critical work and fresh reviews; Sonnet 5.5 for
  features; Haiku 5.5 for mechanical work; GPT-6-Luna (codex provider) as second-opinion reviewer of risky code.
- Keep ≤ 6 agents running at once (13 at once hit the API rate limit).
- UI rule: **"verbose, not bloated"** (rich data, one job per surface, no repeated facts, no tables/boxes-in-boxes,
  hide empty sections). Route UI redesigns through the reviewer's recommendation and the user's OK first.
- Checks: `dotnet build Glider.slnx -c Release -warnaserror`; `dotnet test --project tests/Glider.Core.Tests -c Release`;
  `dotnet run --project tools/Glider.ContractGen -c Release -- --check`; in `frontend/`: `fnm use 24` then
  `pnpm lint && pnpm typecheck && pnpm test` (Node 20 on PATH breaks vitest).
- If NuGet restore fails with "access denied" in `~/.nuget/packages` (parallel builds), set `NUGET_PACKAGES` to a private
  folder and pass `-nodeReuse:false`.
- Safety: never sign in to Epic, never read tokens (`legendary\user.json`), never touch the user's real
  `%LocalAppData%\Glider`/`GliderData`, registry, Desktop or Start menu; GUI only via bounded `--selftest` runs with a temp
  `--data-dir`, announced to the user.
- Commits: Conventional Commits ending with the `Co-Authored-By` trailer; push `v0.2`; `main` stays green.
