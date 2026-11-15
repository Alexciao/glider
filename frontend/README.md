# Glider frontend

React 19 + TypeScript + Vite 8 + Tailwind v4 + Radix + Motion + Zustand. pnpm only.

```sh
pnpm install
pnpm dev                 # http://localhost:5173, mock backend (no host needed)
pnpm lint && pnpm typecheck && pnpm test && pnpm build
pnpm screenshots         # every screen/state, see ../docs/screenshots/README.md
pnpm a11y                # axe-core over every state (dark + light) + a Tab-order focus-ring check
```

## Mock backend (when not inside the WebView2 host)

`src/ipc/mock` implements the whole v2 contract (`src/ipc/contract.gen.ts`, generated from
`src/Glider.Core/Contracts`: never edit it by hand) with a deterministic, seeded fictional library of ~300
games plus DLC (invented titles and generated art; Fortnite appears by name as the v0.1 game). Pick a scenario
and options by URL:

| Param | Values |
|---|---|
| `scenario` | `returning` (default) `firstRun` `firstRunSignedIn` `emptyLibrary` `offline` `syncError` `perf600` `queue` `downloading` `playing` `updates` `authExpired` `diskFull` `eglBusy` `errors` and one frozen scenario per job phase: `frozenPreparing` `frozenVerifying` `frozenRepairing` `frozenDownloading` `frozenInstalling` `frozenFinalizing` `frozenMoving` `frozenRemoving` `frozenImporting` |
| `theme` / `backdrop` | `dark` `light` `system` / `solid` `mica` |
| `seek` `timescale` `freeze` | start a running job at 0..1 of its phase / simulation speed (default 12, `1` = realistic ETAs) / hold the clock |
| `fail` | fail the next mutating call with an `ErrorCode` |
| `art` | `none` strips all art (the no-art fallback); `busy` `bright` `white` replace every game's art with the worst key art for text legibility (`e2e/hero-legibility.spec.ts`) |
| `games` | library size override (base games), e.g. `games=1000` |
| `seed` | catalog seed (same seed, same library) |
| `eac` | `0` leaves Fortnite's anti-cheat prerequisite uninstalled |
| `bottleneck` | `disk`, `cpu` or `cdn` forces what holds a download back (the Downloads card's "Slow download" status; `disk` also caps the speeds at what a slow drive writes) |
| `data` | `worst` `empty` `one` `huge` (dev stress data over the snapshot, `src/dev/stress.ts`) |
| `dev` | `0` hides the dev switchers |

In mock mode a scenario menu (bottom-left, with failure injection, offline, EGL busy and "drop the next library
delta") and a Demo / Worst case / Empty / One / Huge toggle (bottom-centre) appear, and `window.__glider` exposes
the stores (`ui`, `app`, `library`, `router`), `rpc` and the mock controller to tests.

Honest job phases: a verify only reads (files/bytes checked, read speed, bad files), repairs only what is bad
(network + disk), downloads show network + disk, the install tail is disk only; the whole-job percent is
monotonic and ~1 Hz `JobSample` history is served by `jobs.getHistory`.

## State layer

- `state/store.ts`: the small `AppSnapshot` (structural sharing); `state/library.ts`: `Map<appName, GameSummary>` + revision,
  `library` deltas applied in order, a gap (or an unloaded store) means `library.get` again (`bridge.ts` buffers deltas during a load).
- `state/libraryIndex.ts`: in-memory search / filter (installed, not installed, updates, cloud saves, favorites,
  hidden) / sort (A-Z, recently played, recently added, size) / collection index, memoized per library change,
  < 16 ms for 600 games (tested). Hooks: `useLibraryQuery`, `useLibraryCounts`, `useRecentlyPlayed`, `useGame`.
- `state/progress.ts`: progress keyed by jobId, ~1 Hz history seeded from `jobs.getHistory`.
- `state/router.ts`: hash router, routes `home` `library` `game/<appName>` `downloads` `store` `settings`; the host's
  `navigate` event goes through it.

## Onboarding, Settings, command palette

- Onboarding (`features/onboarding`): Welcome → Sign in → Find games → Folders → Downloads → Done. Every step goes through
  `onboarding.setStep`, so a restart resumes where it stopped; skipped steps are remembered. Re-run from Settings → Advanced
  (`onboarding.restart`). Scenarios: `firstRun`, `firstRunSignedIn`, `games=0` (nothing found), `offline` via the dev menu.
- Import list (`features/import`): selection, per-game results (`Imported`, `Needs repair: 12 files`, `Failed: folder in use`),
  shared by onboarding and Settings → Epic Games Launcher → Import games. Notices for a batch's jobs are not toasted while the list is on screen.
- Settings (`features/settings`): flat rows (label + at most one line), rare controls under an `Advanced` disclosure, one section
  at a time; typing in the search box shows matching rows from every section. `SettingsSection` ids are in `sections.tsx`.
- Command palette (`app/CommandPalette.tsx`, `app/palette/model.tsx`): Ctrl+K; games with their one action (Enter), Ctrl+Enter opens the page;
  fuzzy matching in `state/fuzzy.ts`, recents in `state/recents.ts`.

## Layout

`src/ui` design system · `src/state` stores, actions, formatters · `src/app` shell ·
`src/features` Home / Library list / game page / Downloads / Settings (12 sections + search) / Onboarding / import (the shared "bring existing installs in" list) / dialogs ·
`src/ipc` typed client + mock · `e2e` Playwright (screenshots, a11y, focus, hero legibility, surfaces, break-UI; every state in `e2e/states.ts` drives the v0.2 screens; `e2e/tools` has the contact-sheet, control-audit and film helpers).
