# v0.2 UI review

One consistency pass over every screen the four UI agents built in parallel. Standing rule from the owner:
**verbose, not bloated**: rich, honest data where it helps (the download card), but one job per surface, no fact twice,
no helper text that restates a label, density from type instead of boxes, empty sections hidden, rare controls collapsed.
Words follow [glossary.md](glossary.md).

## Method

Three shoot → critique → fix rounds against the mock backend (headless Chromium, `frontend/e2e`):

1. **Shoot.** 130 states (`e2e/states.ts`) × dark/light × 1280×800 / 960×600 (+ Mica-simulated, + the three host surfaces:
   Mica active, Mica inactive with `data-surface="solid"`, Windows 10), plus worst-case data (`?data=worst`).
2. **Read.** `e2e/copy-dump.spec.ts` dumps every visible string per state (wording, case, repeated facts) and
   `e2e/tools/audit-controls.mjs` measures height / radius / icon size of every control per screen; contact sheets
   (`e2e/tools/sheet.mjs`) for the pixels.
3. **Fix**, re-shoot, re-run the specs. Round 1 was wording and duplication; round 2 was geometry (heights, radii, icon scale,
   gutters), states and motion; round 3 was 960×600, Mica, break-UI and the worst-case key art.

Before/after pairs for the main fixes: [`docs/screenshots/v0.2/`](screenshots/v0.2/).

## Result

**45 issues found, 38 fixed, 7 deliberately left** (reasons below). Specs: `a11y`, `focus`, `hero-legibility` (with
`HERO_ENFORCE=1`), `surfaces`, `break-ui` all green; `pnpm lint && pnpm typecheck && pnpm test` green (515 tests);
`dotnet build -warnaserror` and `dotnet test` (1391) green.

## Checklist

### Repeated or redundant text

- [x] **Title bar pill** repeats the job the page already shows (Downloads card, Home strip, the game's own hero) → hidden on those pages.
- [x] **Title bar pill** said the verb twice (`Verifying Azure Bramble II · Verifying files · 25%`, `Waiting · Waiting`) → the phase is added only when it says something new.
- [x] **Download card**: the state badge (`Downloading`) sat next to the stepper whose lit step says the same → the label yields while the stepper is shown; it stays for paused / waiting / queued and for jobs without a stepper.
- [x] **Download card**: `Average` / `Peak` were in the chart annotations and again in the Speed list, with different precision (`59` vs `59.1 MB/s`) → chart only.
- [x] **Install and Move dialogs**: free space was said three times (`Free on D: 640 GB`, the folder row `640 GB free`, `Fits, with 624.1 GB to spare`) and the stats sat in their own bordered panel inside the dialog → stat removed, panel unboxed.
- [x] **Game hero**: `Ready to play` above `Play`, `Not installed` above `Install` → the line shows only what the button can't say (the download size) and, once, the ready moment.
- [x] **Game hero offline**: `Needs an internet connection` + `Fortnite can't run offline` → one line.
- [x] **Game page, Cloud saves**: an unlabeled switch next to `Sync now` read as that button's toggle → the per-game switch moved into Game settings as `Sync cloud saves`.
- [x] **Library tile / row (screen reader)**: `Update available, update available, cloud saves` → once.
- [x] **Settings → Epic Games Launcher**: row `Import games` + `8 games ready` + button `Import games…` → `Existing installs` / `8 games ready to import` / `Import…`.
- [x] **Settings → Advanced**: `Run setup again` + button `Run setup` → `Welcome setup` / `Run again`.
- [x] **Settings → Epic overlay**: the row `Epic Online Services overlay` restated its section → `Friends and achievements`.
- [x] **Link account notice**: title `Activate with X`, button `Activate`, message `Link … with X` → `Link your X account` / `Link account`.

### Wording and terminology (see glossary)

- [x] `EOS overlay` / `Epic Online Services overlay` / `Epic overlay` (Core) → **Epic overlay**.
- [x] `Retry` / `then retry` vs `Try again` (Core `Errors.cs`, mock, stress data) → **Try again**.
- [x] `cancelled` vs `Cancel` → **canceled** (US spelling; the `ErrorCode` stays).
- [x] Uninstall: `Removing` (job), `removed from Glider` (notice), `Uninstall…` (menu) → **Uninstalling** / `uninstalled`.
- [x] Import: `Glider couldn't link this install`, `Importing links them where they are` → import / keeps.
- [x] `Add a folder…` vs `Add folder…`; `Locate folder…` vs `Locate another folder…` → one form each.
- [x] `Epic is busy` (import reasons) vs `Epic Games Launcher is busy` → `Launcher is busy` in the short label.
- [x] Core: `Activate it once`, `Couldn't activate on {store}`, `then activate again` → link wording.
- [x] Sentence case, `…`, `5 min`, `33%`, `·` separators verified across all 130 states (copy dump): no Title Case, no `...`, no `EGL`, no `Adopt` left on screen.

### Layout, alignment, size

- [x] **Library header** was 12 px wider than the grid (the grid reserves a scrollbar gutter, the header didn't) and the sync glyph sat 9 px inside the edge → shared gutter, glyph on the edge.
- [x] **Segmented control** had a 12 px radius beside 8 px selects and buttons (all 32 px high); the Favorite button was 20 px against the split button's 16 px → both match.
- [x] **Icon scale**: 11 / 13 / 15 / 17 px icons in 70 places → one scale, 12 / 14 / 16 / 18 / 20 (menu items 14, buttons 14–16, split button 16–18, rail 20).
- [x] **Dialog stats** in a bordered card (card-in-dialog) → plain type over the meter.
- [x] Heights of controls in a row (search, filter, collections, sort, view toggle, Pause all, mode switch, play/favorite) measured with `audit-controls.mjs`: equal per row after the radius fix.

### Interaction and motion

- [x] **Switch** had no hover or pressed state; **Select** no pressed state → added (same 0.96–0.98 squeeze as buttons).
- [x] **Chart** (a Tab stop) drew its focus ring as a `box-shadow` of its own → uses the global 2 px outline like every other stop (this was the one failing `focus` case).
- [x] **Inline durations** (`0.18`, `0.2` with a hand-copied easing array) in the split button and the hero status → `tween.base` from `ui/motion.ts`.
- [x] **Reduced motion** still squeezed pressed elements and lifted hovered tiles → both off; colour changes remain.

### Code and tests

- [x] Dead v0.1 `InstallPlanner` (a second copy of the free-space panel) removed.
- [x] **Mock**: `art=busy|bright|white` were requested by `hero-legibility` and the art states but not implemented, so the "worst key art" cases ran on the default art. Now real (busy cells in every lightness, a pale glow, flat white); unit-tested. The hero still passes AA on all 60 cases.
- [x] **Mock**: the Store always loaded; offline now fails like the host and an ended session lands on `signedOut`, so the Store fallbacks have states to shoot.
- [x] **Break-UI** stressed only the snapshot; the v0.2 screens are game-shaped. `?data=worst` now also rewrites game summaries (a 70-character subtitle, a one-letter name, Japanese, an emoji, a long developer, an unparsed version, 1.4 TB, 12 DLC). Held up: tiles and rows truncate with the title in a tooltip, the hero clamps to two lines, versions are prettified. Break-UI states for Home, Library, game pages, dialogs, queue and import added.
- [x] **`e2e/states.ts`**: dropped the two v0.1 states that no longer mean anything (`library-existing-install-hint`, `library-not-installed`) and a `scanning` duplicate; fixed selectors (`Import…`, `Add folder…`); added 40 states (every game-page variant, dialogs, collections, library filters, Store, app loading) and an `early` flag for loading shots.
- [x] **`CommandPalette.test.tsx`** ("Ctrl+Enter opens the game's page") flaked under load: it pressed keys on whatever row was first before filtering finished → waits for the highlighted row.
- [x] New tests: the pill hides on Downloads, the card header doesn't repeat the lit step, Average/Peak are not rows, the ready line is quiet, `Sync cloud saves` lives in Game settings, the worst-case art variants.

### Left alone, on purpose

- [ ] **Page widths differ** (Home / Library 1240, game 1040, Downloads / Settings 900): grids want width, reading columns don't. Not an inconsistency.
- [ ] **Settings nav scrolls with the page** at 960×600 (`About and licenses` is below the fold): a sticky nav is a layout project, not a polish fix; rarely reached.
- [ ] **`Couldn't sync. Try again`** in the Library header reads as one phrase and is a single button; a second fragment would add chrome.
- [ ] **`You're offline` on a waiting job card** next to the `Offline` title-bar pill: the pill is the app's state, the card says why *this* job waits.
- [ ] **Hover and pressed look** verified in code and the audit, not in pixels: headless screenshots can't hold a pointer state.
- [ ] **Real Mica** and the inactive-window frame can't be shot headless (`mica-active` equals solid there); the surfaces spec drives the same attributes the host sets.
- [ ] **Light theme hero stays dark** (art-scoped dark tokens): the only way text on arbitrary key art stays AA.
