# Design critique, v0.2

A fresh-eyes review of `v0.2` at `bf4749f` (2026-11-14) against the owner's bar ("beautiful, modern, professional,
polished and restrained, like a premium native app") and standing rule (**verbose, not bloated**). The earlier docs
(`ui-review.md`, `design-critique.md`, `design-review.md`) were read only after this view was formed; where their
claims don't hold, see the last section.

Screenshots cited below are in [`screenshots/critique-v0.2/`](screenshots/critique-v0.2/). The full set behind them
(127 states × dark/light × 1280×800/960×600 solid, plus Mica-simulated and the three host surfaces, 26 extra states
the suite doesn't cover, 17 motion filmstrips) was shot headless with the repo's tooling and a few scratch scripts.
None of it is committed.

## Verdict

**What's excellent.** The bones are premium. Graphite neutrals with one accent and a neutral white primary keep every
screen calm. The light theme is as finished as the dark one. The Windows 11 shell is right: 46×40 caption buttons with
Snap Layouts, and a content layer with a rounded top-left corner over the Mica chrome. The game page is the best
screen: title, one status line, one split button, then a quiet facts row. Its edge states are specific and honest
("Your session expired | Sign in to keep playing", "Needs an internet connection"). Copy is plain and actionable
("Close Sunken Keep II and any program using its files, then try again."). Dialogs do honest space math ("Fits, with
624.1 GB to spare"). Most motion is restrained: menus pop from their trigger in 160 ms, toasts settle in about 70 ms,
the segmented indicator slides in about 100 ms, the palette opens instantly, and reduced motion is handled.

**What holds it back.** The **Downloads page is the showcase screen, and it's where the "verbose, not bloated" rule
breaks.** The live card ends in a spreadsheet of nine label/value rows under four headings. A download-mode control
duplicates a setting, and on screen it contradicts what the queue is actually doing. Failed jobs sit under "History",
below a big "All caught up". Around that: the signature cover-to-page transition flies a dark box instead of the cover.
The Install dialog opens, then resizes and jumps. And the same small drifts recur everywhere: three left edges, two
"Needs repair" colours, and the same action offered twice (palette, sign-in, folder picking).

Fix the three P0s and the top P1s (the morph, the dialog jump, left edges, status colours) and this reads as a
first-party app.

---

## P0: must fix before release

### P0-1 · Active download card: replace the stats table with readouts, chart and one caption (owner request)

**Screen:** Downloads, active job, every byte-moving phase.
**Shots:** `p0-1-card-before.png` (today), `p0-1-card-proposal.png` (six phases, dark), `p0-1-card-proposal-light.png`.

**What's wrong.** Below the bar, the card has a chart on the left and `StatGroups` on the right: four group titles
(Progress / Disk / Speed / Time) over nine right-aligned rows. It reads as a spreadsheet dump, and it breaks the
"never twice" rule:

- Speed appears three times at three precisions: `Now 58.8 MB/s`, `Peak 59 MB/s` and `Average 59 MB/s` on the chart.
- Group titles head one-row groups (`Time → Elapsed`, `Progress → Files`).
- While installing, `Disk → Written 2.36 GB / Final size 2.57 GB` repeats the headline `2.36 GB of 2.57 GB`.
- While repairing, `Damaged 10` and `Fixed 5 of 10` repeat the header's `10 files to fix · 1.8 GB`, and `Final size
  7.72 GB` describes the whole game, not the repair.
- The chart's legend (`Network`, `Disk write`) carries no values, while its values live in another column.
- At 960×600 the table stacks under the chart and pushes the card to ~560 px, so Up next falls below the fold.

**Redesign.** One column, three tiers. The chart's legend *becomes* the live readout, the chart goes full width, and
every other fact becomes one caption line of short phrases.

```
┌───────────────────────────────────────────────────────────────────────────────────┐
│ [art]  Vault & Thorned Co.                                    [ Pause ]  × Cancel │
│        Install · 3.8.9                                                            │
│ (✓) Preparing ─────────── (2) Downloading ─────────── (3) Installing ─── (4) Fin… │
│ 33%                                  15.81 GB of 37.64 GB downloaded · 7 min left │
│ ████████████████░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░░ │
│                                                                              24px │
│ 58.8 MB/s     116.8 MB/s                Disk is the limit: drive D: writes about  │  readouts + note
│ ━ Download    ━ Disk write                                            117 MB/s.   │
│                                                                              12px │
│ ∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿ chart, full width, 80 px ∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿∿ │
│                                                                              16px │
│ 5,465 of 13,013 files   27.54 GB of 76.25 GB on disk   from Akamai   4:35 elapsed │  caption
└───────────────────────────────────────────────────────────────────────────────────┘
```

| Element | Spec (existing tokens and Tailwind scale) |
|---|---|
| Readout row | `mt-6 flex flex-wrap items-end justify-between gap-x-8 gap-y-2` |
| Readout value | number `text-lg` (20/28) `font-semibold tracking-tight tabular-nums text-fg`; unit `ml-1 text-sm font-medium text-fg-subtle`. This is the same number-big, unit-quiet treatment as the `33 %` above. |
| Readout label | `mt-1 flex items-center gap-1.5 text-xs text-fg-muted`, swatch `h-0.5 w-3.5 rounded-full` in `--g-chart-net` / `--g-chart-disk` |
| Readout markup | `<dl class="flex items-end gap-x-8">`, each `<div class="flex flex-col-reverse"><dt>Download</dt><dd>58.8 MB/s</dd></div>`, so screen readers get label then value and the eye gets value on top |
| Note slot | right end of the readout row: `max-w-[44ch] text-right text-xs`. `text-warning-text` for disk/CPU, `text-fg-muted` otherwise. Optional link `Change` in `text-accent-text underline underline-offset-2`. At most one note (priority under P0-2). |
| Chart | `mt-3`, full card width, plot `h-20` (80 px), `h-16` when the card is under 720 px. No legend `<ul>`: the readouts are the legend. |
| Chart annotations | Keep the dashed average line. Show its chip (`avg 58.8 MB/s`, existing 11 px style) only when \|now − avg\| ≥ 5 % of avg; when the job is steady, the line alone says so. Peak becomes the dot only, with the value in the hover tooltip. |
| Caption | `mt-4 flex flex-wrap gap-x-5 gap-y-1 text-xs text-fg-muted tabular-nums`. Each phrase is a `<span>` with its number in `text-fg`. Space separates phrases, not `·`. |
| Numbers | One formatter (`formatSpeed`) for readouts, chart chip and tooltip. Today the chart uses `formatSpeedCompact` (`59`) beside `formatSpeed` (`58.8`). |
| Headline | Name the bytes: `… downloaded`, `… written` (installing), `… copied` (moving), `… checked` (verifying, as today). Now the headline and the caption's `on disk` pair can't be confused. |

**Per phase:**

| Phase | Readouts | Chart | Caption |
|---|---|---|---|
| Downloading | Download, Disk write | network + disk | `5,465 of 13,013 files` · `27.54 GB of 76.25 GB on disk` · `from Akamai` · `4:35 elapsed` |
| Repairing | Download, Disk write | network + disk | `5 of 10 files fixed` · `from Akamai` · `0:07 elapsed` (header keeps `10 files to fix · 1.8 GB`) |
| Verifying | none (no chart, so no legend) | none | `Reading at 1.89 GB/s` · `605 of 1,317 files` · `3 damaged so far` (`text-warning-text`, only when > 0) · `0:01 elapsed` |
| Installing | Disk write | disk; the finished download line stays at 40 % opacity, with no readout | `{n} of {m} files` when known · `0:13 elapsed` |
| Moving | none | none | `Copying at 259 MB/s` · `212 of 472 files` · `0:06 elapsed` |
| Preparing, Finalizing, Removing, Importing | unchanged: no lower half, `0:14 elapsed · Under a minute left` beside the % | | |

(The `·` in this table separates the cells' phrases for reading here; on screen they're spaced spans.)

**Where every current figure goes:** `Now` and `Write speed` become the readouts. `Server` becomes `from Akamai`.
`Files` and `Fixed` move to the caption. `Written` and `Final size` become `… of … on disk` while downloading and are
dropped while installing (that's the headline) and repairing (it's the whole game). `Damaged` is dropped while
repairing (the header says it) and kept in the caption while verifying. `Elapsed` is always the caption's last phrase.
The legend becomes the readout labels; `Network` is renamed **Download**, which is what people call it. Peak moves to
the tooltip. The limiter line under the card moves into the note slot.

**States.**
- **Paused / waiting:** keep the readout row, so pressing Pause doesn't make the card jump. Show the job's
  averages (`NetworkStats.averageBytesPerSecond`, `DiskStats.averageBytesPerSecond`) in `text-fg-subtle`, labelled
  `Download, average` / `Disk write, average`. Chart at `opacity-60`, average chip hidden (the readouts already are the
  averages), note slot empty (the header says why it's waiting).
- **Narrow (< 720 px card, the 960×600 window):** the same single column. The note drops to its own line under the
  readouts (`basis-full text-left`), the chart is `h-16`, and the caption wraps. The lower half is ~190 px instead of
  ~330 px today.
- **Young job:** `ThroughputChart` has `minSpan` 60 s, so at full width a 7-second-old job draws in the first 11 % of
  920 px (`p0-2-mode-after.png` shows it). Set `minSpan={20}` on this card.

**Implementation pointers.** In `state/transfer.ts`, swap `Figures.groups` for
`readouts: { series: "network" | "disk"; label: string; bytesPerSecond: number; average: boolean }[]` and
`caption: { key: string; value: string; text: string; tone?: "warning" }[]`, keeping the module's rule (only real
values, never twice). In `ActiveJobCard.tsx`, drop `StatGroups`, `STAT_COLUMNS` and the `@[900px]` two-column grid,
render readouts, then chart, then caption, and move `limiter` into the readout row. `StatGroups` has no other user
and can go. In `ThroughputChart`, add `legend={false}`, `peakLabel={false}` and an average-chip threshold.

The mockups were made by rewriting the live card's DOM in headless Chromium (production build, mock backend), so type,
tokens and spacing are the app's. The values are the mock's.

### P0-2 · Download mode is a setting, not a Downloads-page control (owner request)

**Screen:** Downloads header. **Shots:** `p0-2-mode-before.png`, `p0-2-mode-after.png`.

**What's wrong.** `Background | Balanced | Full` sits at the top of the page, unlabelled, beside `Pause all`. It reads
like tabs or a view filter, competes with the one queue-wide action, and duplicates Settings → Downloads, the tray menu
and the palette (`Download mode: Full`). It also misleads. With the while-playing policy on Throttle and a game
running, the control shows **Balanced** (your choice) while the queue runs as Background. The page then says the same
fact twice more: a notice above the card (`Running as Background. Throttled while Fortnite is running.`) and a line
under it (`Background mode is easing off to keep your PC responsive.`).

**Design.**
- **Header:** title `Downloads` plus only `Pause all` / `Resume all` (secondary, `sm`), shown while anything is active
  or queued, as today. Also remove the `Limit 25 MB/s` ghost button. It's a setting too, and its effect gets a line
  below when it binds.
- **The control lives in Settings → Downloads** (unchanged row), the tray and the palette. Onboarding's Downloads step
  stays.
- **The page shows only the effect:** at most one quiet line in the card's note slot (P0-1), from
  `QueueState.effectiveMode` / `modeReason` and `JobProgress.limiter`. A `Change` link deep-links to Settings →
  Downloads and focuses the Download mode row. Nothing shows when the mode is what you chose and isn't the limit.

| Situation (data) | Line in the note slot |
|---|---|
| Throttled for a game (`modeReason` "Background mode while {game} is running") | `Background mode while Fortnite is running.` `Change` |
| Idle boost (`modeReason` "Full speed while the PC is idle") | `Full speed while the PC is idle.` `Change` |
| Paused for a game (job `waiting`, "Paused while {game} is running") | none here: the card header already says `Paused while Fortnite is running. Glider continues automatically.` |
| Your mode is the limit (`limiter: mode`, effective = chosen) | `Background mode: downloading gently.` `Change` |
| Your speed limit is the limit (`limiter: speedLimit`) | `Held to your 25 MB/s limit.` `Change` |
| Disk, CPU, CDN (`limiter`) | as today, e.g. `Disk is the limit: drive D: writes about 117 MB/s.`, no link |

Priority when several apply: automatic mode change, then disk, CPU, speed limit, mode, CDN. In `Downloads.tsx`, drop
the page-level `Notice` for mode reasons while a card is on screen. Keep it only for queue-wide states with no running
card (`Downloads are paused. Nothing starts until you resume all.`, offline).

**A quick switch on the page?** I considered "make this one finish before I leave". That's a temporary Full, and it's
one keystroke away (Ctrl+K, "full") or one click from the note's `Change`. Nothing argues for a permanent control.
Agreed with the owner.

### P0-3 · Failed downloads are invisible: the page says "All caught up" above three failures

**Screens:** Downloads and Home in the `errors` scenario (not in `e2e/states.ts`; add it).
**Shots:** `p0-3-downloads-failed.png`, `p0-3-home-failed.png`.

**What's wrong.** With no job running, Downloads leads with `All caught up / Nothing is downloading right now` and a
primary `Update 10 games`, even when updates just failed for lack of disk space. The failures sit below, under
**History**, which also has `Clear all`, so one click dismisses work that still needs doing. Each failed row says
"failed" four times: a red `×`, a red `Failed`, a red-tinted callout, and the callout's red icon and title. Three of
them stack into a wall of pink boxes. Home shows nothing at all, and the rail's Downloads item has no badge (the dot
only marks running or paused jobs). A failure is reachable only by opening Downloads and scrolling.

**Fix.**
- **Downloads:** a **Needs attention** section above everything else (above the active card too), with
  `SectionHeader title="Needs attention"`. One row per failed job: art (as in Up next), title, `Update failed · 25 min
  ago`, then the error's one-line message in `text-fg-muted` and its fix button (`Free up space`, `Try again`)
  right-aligned. No callout box, no `×`, no `Failed` label: the section title says it. `Details` stays a disclosure.
  History then lists only completed and canceled jobs, and `Clear all` can't clear failures (a failure leaves only by
  succeeding or by `Dismiss` in its row menu).
- **Empty headline:** with failures present, replace `All caught up` with nothing; the Needs attention section is the
  top. Don't offer `Update N games` while an update failed for space.
- **Rail:** the Downloads dot in `bg-danger` while any job is failed. It wins over the running and paused dots.
- **Home:** a `Needs attention` row group at the top, the same row component, max 3, then `2 more`.

---

## P1: should fix for v0.2

### P1-1 · Cover → game page morph flies a dark box; the art arrives last
**Shot:** `p1-cover-morph-film.png` (production build, 60 fps). From the click: at +62 ms the grid is gone and a small
*black* rectangle sits where the tile was. At +95 ms the title is already at its final spot, faded, outside any
container. +128 to +195 ms, the box grows, still dark, with the Play button drawn over it. The key art fades in only
from about +230 ms and finishes 700 ms later. The cause is in `HeroArt.tsx`: the `layoutId` layer starts as its
`bg-[oklch(0.16…)]` plus 0.84–0.96 scrims, and the `<img>` fades `opacity-0 → 100` over `duration-700` on every
mount, even from cache.

| Before | After | Why |
|---|---|---|
| Morphing layer = dark bg + full scrims | Render the tile's already-decoded tall cover (`art.tall`) inside the morphing layer; crossfade to `art.wide` (150 ms) after the spring settles | A shared element must look like the thing you clicked |
| `<img>` fades in over 700 ms on every open | `if (img.complete) setLoaded(true)` in a ref callback, no transition when cached; keep 200 ms for a real network load | Cached art should be there at frame 1 |
| Scrims at full opacity from the start | Scrims fade 0 → 1 over the last 40 % of the morph | The cover reads as the cover until it lands |
| Title, status, actions mount immediately | Mount with the hero; `opacity 0 → 1, translateY(4px → 0)`, 160 ms `--ease-out`, 120 ms delay | Content shouldn't arrive before its container |
| Facts/description render under skeleton lines, then reflow at +287 ms | Reserve the facts row height (one 40 px line) while loading | No layout jump under a moving hero |

### P1-2 · Install dialog opens in a loading state, then resizes and re-centers
**Shot:** `p1-install-dialog-film.png`. At +120 ms the dialog is about 200 px tall (`Checking free space…`, `Choose a
folder`). At about +337 ms it grows to about 400 px and jumps upward to re-center. Headless capture also drops to 23
fps while the full-screen `backdrop-filter` blur is up.
**Fix:** fetch the install plan on hover/focus of `Install` (or before opening) and open only when ready (spinner on
the button up to 400 ms). Otherwise top-anchor the dialog (`top: 12vh`, no vertical centering) and reserve the
components block with three skeleton rows (24 px each). Check the scrim blur on an integrated GPU, and use `blur(2px)`
→ `none` if it costs frames.

### P1-3 · Three left edges
**Shot:** `p1-left-edges.png`. Page titles start at x = 113 (Home, Library), 152 (game page) and 192 (Downloads,
Settings), so the header jumps under the cursor as you move through the rail. `ui-review.md` kept the different
widths on purpose ("grids want width, reading columns don't"). The widths are fine; the problem is centring narrower
columns. **Fix:** every page starts at the same left edge (`PageScroll` padding 40 px, so x = 113 at 1280); narrower
pages end earlier (`max-w-[900px]`, left-aligned, not `mx-auto`). The game hero keeps its 1040 px width from the same
edge.

### P1-4 · Status colours aren't one system
**Shot:** `p1-status-colors.png`. `Needs repair` is danger red in the Library list but warning amber in the import list.
`Saves differ` is red text on an amber (`warning-soft`) fill. The readiness check's `1 game still needs it` is red.
The Library list colours the most common normal state, `Installed`, green on every row. **Fix:** danger = something
failed or data is at risk (failed job, launch failed). Warning = needs your action but nothing is broken (needs
repair, saves differ, readiness blocker, low space). Accent = informational (update available). Normal states get no
colour: `Installed` in `text-fg-muted`, or blank, since the filter already offers it. One table in `GameStatus.tsx`
that every surface reads.

### P1-5 · The same action offered twice, in four places
- **Command palette** (`p1-palette-duplicate.png`): `Update Fortnite` and the row `Fortnite · Update` both sit in
  Suggested. With "fort", the Games row's `Update ↵` and `This game → Update Fortnite` repeat it. **Fix:** a game row
  with a primary action suppresses that action from Suggested and This game.
- **Onboarding sign-in** (`p1-onboarding-duplicates.png`): the heading `Sign in with Epic` equals the button
  `Sign in with Epic`. After an error the callout adds its own `Sign in` button above the primary. **Fix:** heading
  `Connect your Epic account`; the error callout has no action (the primary is right below it).
- **Onboarding, nothing found:** the heading `Finding your installed games` stays while the result says `No installed
  games found`, under a subtitle about importing. `Skip` and `Continue` do the same thing. **Fix:** heading `No games
  found on this PC`, body `Scan your drives or point Glider to a folder.`, actions `Scan all drives` (secondary) and
  one footer `Continue`.
- **Onboarding, invalid folder:** the callout's `Choose folder` duplicates the footer's `Locate another folder…`.
  **Fix:** drop the callout button.

### P1-6 · Onboarding list scrolls under the stepper with no edge
**Shot:** `p1-onboarding-duplicates.png` (bottom right). Rows slide under the step labels and checkboxes clip against
them. **Fix:** give the stepper bar `bg-bg-elevated` plus a 16 px scroll-edge fade (`mask-image:
linear-gradient(to bottom, transparent, black 16px)` on the scroller), shown only once scrolled.

### P1-7 · "8 games imported" when two need repair
**Shot:** `p1-import-done.png`. The headline counts 8 imported, while two rows say `Needs repair`, with no icon, no
file count (the README promises `Needs repair: 12 files`) and no action. **Fix:** headline `6 games ready, 2 need a
repair`; subtitle `Nothing was downloaded. Repairs re-download only damaged files.`; repair rows show
`Needs repair · 12 files` with a `Repair` button; the primary becomes `Continue` (repairs queue in the background).

### P1-8 · Signed out has no designed state
**Shots:** `p1-signed-out-game.png`, `p1-signed-out-home.png`. After `Sign out` the open game page says `That game
isn't in your library / Nothing is called "Fortnite" in your Epic library`. Home and Library say `Your library is
empty` with `Sync library`, while `Free this week` on the same screen says Fortnite is `In your library`. Core's
`AuthService.SignOutAsync` doesn't clear the library, so the real app shows the full library with only a title-bar
`Sign in`. The mock clears it (`backend.ts` `auth.signOut` → `clearLibrary`). Neither is designed. **Fix:**
(a) align the mock with Core. (b) While `auth.status === "signedOut"`, Home and Library show a one-line banner at the
top (`You're signed out. Installed games that support offline play still launch.` + `Sign in`, primary `sm`), and the
game hero uses the `expired` treatment (`Sign in to play`). (c) Empty-state copy branches on auth: never `Your library
is empty` when signed out.

### P1-9 · Home: three primary buttons in a row
**Shot:** `p1-home.png`. Each Continue playing card carries a white `Play` pill, which is the primary style, so the page
opens with three competing primaries plus `Update all`. **Fix:** `Play` appears on card hover/focus, as on library
tiles: bottom-left of the art, `bg-primary`, 150 ms fade, on top of the existing 3 px lift. At rest, the card shows
title and `3 h ago`. Optionally make the first card 2× wide as the one primary moment. Also drop the `Welcome back, Marlowe`
greeting, or make it `text-sm text-fg-muted` above `Continue playing`. It pushes everything 72 px down and says nothing.

### P1-10 · "On disk 13.9 GB" for a game that isn't installed
**Shot:** `p1-game-not-installed.png`. **Fix:** label it `Install size`. The hero's `7.2 GB download` stays.

### P1-11 · Save-conflict dialog dates disagree
**Shot:** `p1-save-conflict-dialog.png`. In one dialog: `last synced yesterday (Nov 12, 2026, 10:20 PM)` and the PC copy
`2 days ago · Nov 12, 2026, 1:57 AM`, so the same date gets two relative words. `formatRelativeTime` counts 24-hour
blocks, not calendar days. **Fix:** `Yesterday` / `N days ago` by local calendar date, everywhere. (The mock's PC
change also predates the last sync, which contradicts "changed since they last synced". Fix the fixture.) This is a
data-loss decision: the dates must agree.

---

## P2: polish

- **Library tiles: status badges are hard to read.** (`p2-library-grid.png`) A 20 px glass circle with a 12 px check
  disappears on busy art. Use 22 px, `bg-black/55` with `backdrop-blur-sm`, and a 1 px `ring-white/15`. Or drop the
  `Installed` badge, since installed is the common state in a filtered view, and keep only update, repair and playing.
- **Library filter counts don't add up.** (`p2-filter-counts.png`) `Installed (42)` + `Not installed (238)` = 280 of
  `Search 300 games`. Give `All games` its count and make the parts reconcile (or list `Needs repair (n)` as its own
  line).
- **List view:** see P1-4 for `Installed` in green on every row (`p2-library-list.png`). Also `Last played` mixes
  `10 days ago` and `Oct 28`; that's fine, but use one rule (relative under 7 days).
- **Worst-case titles all truncate to the same 16 characters** (`p2-worst-titles.png`): `line-clamp-2` on grid titles
  (2×18 px), as the hero already does.
- **Cancel dialog** (`p2-cancel-dialog.png`): the destructive button repeats the title, `Cancel install of Scarlet
  Sanctum II`. Use `Cancel install`.
- **Uninstall dialog grammar:** `Uninstall, keep files` vs `Uninstall and delete files` → `Uninstall and keep files`.
- **Dialog close buttons:** Install, Move, Error, Save conflict and New collection have an `×`; Uninstall and Cancel
  don't. Windows `ContentDialog` has none. Drop `×` wherever there's a Cancel button.
- **Move dialog** (`p2-move-egl.png`): `Keeping it in sync updates Epic Games Launcher with the new location after the
  move.` restates the switch `Keep Epic Games Launcher in sync`. Keep the title and the switch, drop the sentence, and
  unbox the switch from the amber callout.
- **Error dialog:** the message says `Reinstall Glider to fix it`, but the primary is `Open logs`. Make the primary
  `Download Glider` (or the reinstall path); `Open logs` becomes secondary.
- **Settings → Epic overlay:** an `Update available` badge next to an `Update` button. Drop the badge.
- **Settings → EGL readiness:** `Project Embassy  Link Project Embassy with Ubisoft Connect first.` repeats the name.
  Use `Link with Ubisoft Connect first; it can't start without that.` Make the name the link instead of `Open`, and
  use warning, not red (P1-4).
- **Settings search:** the nav keeps all 12 sections at full strength while results show three. Dim non-matching nav
  items to `text-fg-disabled`.
- **Switch thumb (dark theme):** "on" is a dark navy knob (`--g-accent-fg`, `oklch(0.16 0.03 252)`) on a light-blue
  track. Windows 11 uses a light knob on the accent. Give the knob its own token, white (`oklch(0.99 0 0)`), in both
  themes.
- **Store, signed out** (`p2-store-signed-out.png`): the heading `Sign in to the Store` has no `Sign in` button, and
  the body restates the two buttons. Use title `Sign in to use the Store`, primary `Sign in`, secondary
  `Open in browser`, and no body. **Store, offline:** drop `Open in browser`, since the browser is offline too.
- **Downloads empty states** (`p2-downloads-empty-variants.png`): first run is boxed with two sentences; returning is
  unboxed with `All caught up`. Use one unboxed state, `Nothing downloading`, with the action.
- **Onboarding welcome** (`p2-welcome.png`): at 1280 the headline breaks `Your whole Epic / library, one launcher.`
  (at 960 it fits on one line). Set the break: `Your whole Epic library,⏎one launcher.` (`max-w-[17ch]`, no balance).
  The Done step's `Open Glider` (you're already in it) → `Go to your library`.
- **Account menu:** `Signing out keeps your install and settings.` → `Signing out keeps your games and settings.`
- **Rail tooltips** repeat the visible label (`Home` over `Home`). Make the tooltip just the shortcut, `Ctrl+1`.
- **Perpetual motion during long downloads:** the bar sheen (2.4 s loop), the breathing rail dot and the pill spinner
  all run for hours. Keep one: the bar's sheen while the window is focused; a static rail dot; the pill spinner only
  for the first 2 s.
- **First visit to a lazy page** (Downloads, Settings, Store) shows a blank content area for ~280 ms in the production
  build (Suspense `fallback={null}` plus a 220 ms fade). Prefetch the three chunks on idle after first paint.
- **Typeface:** Inter is well tuned (cv05, ss03, tabular figures), but it's the one thing that says "web app" on
  Windows. Consider `Segoe UI Variable` (Text/Display optical sizes) for UI chrome, keeping Inter's tabular figures
  for numbers. It's a brand call, not a defect.

---

## The earlier docs, checked

- `ui-review.md` "Average/Peak … chart only": **half true.** They aren't rows, but `Now 58.8 MB/s` still sits next to
  `Peak 59` / `Average 59` with different formatters (P0-1).
- `ui-review.md` "`Add a folder…` vs `Add folder…` → one form each": **doesn't hold.** The onboarding empty state says
  `Add a library folder`, and the populated list says `Add folder…`.
- `ui-review.md` "Hover and pressed look … can't be shot headless": **not so.** Playwright `hover()` holds a pointer
  state; this review shot tile, list and card hovers.
- `ui-review.md` "Page widths differ … not an inconsistency": the widths are fine; the different left edges are the
  issue (P1-3).
- `ui-review.md` card header/stepper de-duplication, pill hiding, `Try again` wording, offline single line, light hero
  staying dark: **hold.**
- `design-review.md` round 1 #5, "the chart window grows so it always fills its width": **superseded.** `minSpan` is
  now 60 s and a young job fills 10 % of the width (P0-1, young job).
- `design-critique.md` (v0.1.2): describes the v0.1 UI (three-dot button, six Settings pages); obsolete for v0.2.
- `states.ts` lacks the `errors` scenario on Downloads and Home, signed-out Home/Library, hover states and Library
  search results. Add them; P0-3 and P1-8 were invisible to the suite because of it.

## Screenshot index

| File | Shows |
|---|---|
| `p0-1-card-before.png` | Today's card (queue scenario, downloading) |
| `p0-1-card-proposal.png` | Proposal: downloading (note slot shown for placement), repairing, verifying, installing, paused, moving |
| `p0-1-card-proposal-light.png` | Proposal, light theme |
| `p0-2-mode-before.png` / `p0-2-mode-after.png` | Throttled while a game runs: today (control says Balanced; three lines) vs proposal (no control; one line with `Change`) |
| `p0-3-downloads-failed.png` / `p0-3-home-failed.png` | `errors` scenario: "All caught up" over failures; Home silent |
| `p1-cover-morph-film.png` | Cover → page filmstrip, production build |
| `p1-install-dialog-film.png` | Install dialog open, resize and jump |
| `p1-left-edges.png` | x = 113 / 152 / 192 |
| `p1-status-colors.png` | Needs repair red vs amber, red on amber, red blocker |
| `p1-onboarding-duplicates.png` | Sign-in triple, nothing-found, folder duplicate, scroll under stepper |
| `p1-palette-duplicate.png` | Update Fortnite twice in Suggested |
| `p1-import-done.png` | "8 games imported" with 2 needing repair |
| `p1-signed-out-game.png` / `p1-signed-out-home.png` | Signed-out states |
| `p1-home.png` | Three primary Play pills, greeting |
| `p1-game-not-installed.png` | "On disk" for a not-installed game |
| `p1-save-conflict-dialog.png` | Yesterday vs 2 days ago for one date |
| `p2-*.png` | P2 items as named above |
