# Design review log

Screenshots live in `docs/screenshots/<theme>-<backdrop>-<WxH>/<state>.png` (60 states × dark/light ×
solid/Mica-simulated × 1280×800 / 960×600). Regenerate with `pnpm screenshots` (headless Chromium + the
mock backend; `SHOT_ONLY=library SHOT_VARIANTS=dark-solid-1280x800` narrows a run).

Each round: look at every state, critique as a product designer (hierarchy, rhythm, alignment,
typography, contrast, density, consistency, motion intent, empty/error states), fix, re-shoot.

---

## Round 1 — first full pass (dark / solid / 1280×800, spot checks elsewhere)

**What worked:** the hero (title, one status line, one big button, quiet facts) reads as a premium
native app; the neutral primary button plus a single blue accent holds up; tabular numerals keep the
live numbers still; error callouts read as "what happened + one fix".

| # | Where | Critique | Change |
|---|---|---|---|
| 1 | All dialogs | The close button was not absolutely positioned (Button's base `relative` beat `absolute`), so it floated at the dialog's left edge, and focus landed on it and popped a "Close" tooltip the moment a dialog opened. | Removed `relative` from the Button base; dialogs focus the dialog itself on open, so the first Tab reaches the first control and no tooltip appears. |
| 2 | Rail | "Downloads" overflowed the 52 px item and the 64 px rail; the label clipped against the window edge. | Rail 72 px (`--sidebar-width`), items 60×54. |
| 3 | Onboarding → "found" | Linking an existing install jumped to "You're all set / installing in the background" while the import was still running, with the wrong copy. | An import job keeps you on the linking step (the button shows "Linking your install…"); only a fresh install job advances to Done. |
| 4 | Onboarding → "found" | With an error callout visible, the primary button fell below the fold at 1280×800. | Actions are sticky at the bottom of the scroll area; reassurance block tightened. |
| 5 | Downloads → card | The sparkline filled only the right 15 % of its width because the window was fixed at 60 s while a young job had 9 s of data. | The window grows with the data (10 s – 60 s) so the chart always fills its width; the caption reads "Last N seconds". |
| 6 | Downloads → card | The "%" sign was as loud as the number. | Number 32 px, sign 20 px in `fg-subtle`. |
| 7 | Downloads → waiting | Subtitle and blocked reason ran together ("40.20Epic Games Launcher…") and an empty chart sat under a job that hadn't started. | Subtitle lines are separate paragraphs; the chart appears only once there is data. |
| 8 | Downloads → history | "Verification Fortnite" / "Update Fortnite" for finished work read like commands. | Finished entries use the past tense ("Verified Fortnite", "Updated Fortnite"); queued ones keep the imperative. |
| 9 | Library → paused | The percentage appeared in the status line and again in the job line. | Status says "Install paused"; the job line owns the number. |
| 10 | Settings → Downloads | The 216 px mode switch plus slider left the description ~190 px wide ("Parallel connections to / Epic's servers…" over four lines) and "Auto (1024 MB)" wrapped inside its segment. | The mode switch is the only control on the row; Custom reveals a full-width slider / stepper panel below it; switch widened to 236 px. |
| 11 | Mock (review aid) | The default 12× time scale made "Under a minute left" appear at 42 %. | Screenshot states for downloads run at `timescale=1` for believable ETAs. |
| 12 | Dialog scrim | 72 % black felt heavier than the task deserves. | Scrim 60 % (dark) / 30 % (light) with the 2 px backdrop blur kept. |

Open after round 1 (checked in round 2): light theme contrast on the hero and rail, Mica legibility,
and the 960×600 layouts.

---

## Round 2 — light, Mica-simulated and 960×600 passes, accessibility audit, motion check

Shot all 8 variants; reviewed light/solid and both Mica sets for contrast and legibility, the 960×600 set
for density, then ran axe-core over every state and filmed the primary-action morph frame by frame.

| # | Where | Critique | Change |
|---|---|---|---|
| 1 | Settings @ 960 | "Epic Games Launcher" truncated in the section nav. | Nav column 188 px at ≤ 1100 px, gap 24 px. |
| 2 | Onboarding "found" @ 960 | The sticky action bar sliced through the reassurance card; the fade was too short to read as a fade. | Taller gradient (`pt-10`, solid for the first 75 %). |
| 3 | Welcome / headings | Orphaned last word ("… play / Fortnite."). | `text-wrap: balance` on onboarding headings. |
| 4 | Primary action (film strip) | Click → "Starting…" → "Preparing…" → "Launching…" flashed three labels in ~200 ms. | Pending reuses the next state's face ("Launching…"), `preparing` and `launching` share one label; the filmed morph is now one continuous change. The label crossfade with 3 px blur reads as one object changing. |
| 5 | Accessibility (axe) | 12 states failed colour contrast: `fg-subtle` text on popovers/callouts was 4.4:1. Kbd used `aria-label` on a span (prohibited). Section/empty-state titles skipped heading levels. | `--g-fg-subtle` raised to L 0.66 (dark) / 0.50 (light); small helper text on tinted fills uses `fg-muted`; Kbd uses visually-hidden text; section titles are `h2`. Result: **0 serious/critical on 122 state × theme combinations**. The one accepted exception is Radix's `aria-hidden` on the page behind an open menu/dialog (the standard modal pattern; axe flags it, the spec documents it). |
| 6 | Sparkline | Gridline ceiling label and dotted lines read well; the window now says "Last 10 seconds" growing to 60. | (kept) |
| 7 | Mica | Wallpaper shows through rail, title bar and panel edges without hurting text; `bg-elevated` at 80 % keeps the panel readable. | Host contract: `html`/`body` are transparent and `#root` carries the translucent `--g-bg`. |

## Round 3 — "break the UI" (worst-case data, 200 % zoom, empty/one/huge) and host integration

Dev toggle: **Demo data / Worst case / Empty / One / Huge** (bottom-centre of any mock-mode page, or
`?data=worst`). Worst-case values are realistic or real limits: a 15-character Epic display name with a
diacritic (`Wiśniewska_K_Jr`, Epic caps at 16), a 100-character Windows path and a UNC path
(`\NAS-ARCHIVE-01.northwind-industries-holdings.example.com\media-share\…`), Legendary versions of an
unparsed shape (`++Fortnite+Release-Cert-Next-40.20-CL-…-Windows-Shipping-Win64`), 1.4 TB installs, a
612 GB download at 312 B/s with a 9½-day ETA, a 300-character `filesLocked` error with a stack trace,
a launch-arguments line with 20 flags, 5 EGL candidates (one stale, one with no version, one without a
size) and 160 history entries. Shot at 1280×800, 960×600 and 960×540 (1080p at 200 % zoom).

| # | Severity | Field | What happened | Fix |
|---|---|---|---|---|
| 1 | Broken | Library facts row | At 960 px the long version squeezed Location to nothing and "Epic Games Launcher" painted over it. | Row wraps; Location has a 220 px floor; fixed items never shrink. |
| 2 | Broken | Library → EGL hint | "Fortnite is already on this PC / Use this install" showed while an install was running. | Only offered when the hero is in its Install state. |
| 3 | Ugly | `version` | Unknown shapes rendered in full (66 characters) in the hero, facts, job rows and the active card. | `prettyVersion` takes the first dotted number anywhere ("40.20"); shapes with none are capped at 28 characters. Test-covered. |
| 4 | Ugly | Hero title | A long localised title ran one line at 44 px. | `line-clamp-2`, `text-balance`, `overflow-wrap:anywhere`, full title in the tooltip. |
| 5 | Ugly | Job rows | The subtitle truncated its *end*: the time and size, the part people scan for, vanished first. | The version yields; "took 4 min · 30 min ago" always stays. |
| 6 | Ugly | Launch arguments | A 300-character line in a one-line field. | Growing multi-line field (`field-sizing: content`, 5-line cap), newlines stripped. |
| 7 | Fragile | History | 160 entries all rendered. | First 12, then "Show N older". |
| 8 | Fragile | Initials | Code-point split would break flags and ZWJ emoji ("🇮🇹" → half a flag). | Initials from grapheme clusters (`Intl.Segmenter`). Test-covered. |
| 9 | Ugly | Candidates | A candidate with no version read "Version —". | "Version unknown". |

What held up: speeds of 312 B/s and ETAs of "9 d 12 h" (tabular numerals, no jitter), 1.4 TB sizes,
UNC paths (the head ellipsizes, the last two folders always show), the account menu with a
15-character name, ErrorCallout with a stack trace behind "Details", Empty and One states for every
list, dialogs at 200 % zoom, and every plural ("Found 1 install").

Not covered, by decision: RTL (the app is English-only for now) and virtualization (history is capped
at 12 until expanded; a few hundred rows measured at 420 ms to first paint in dev).

### Host integration (from the host's contract notes)
- `caption` event drives the maximize button's hover/pressed styles (the native overlay swallows its
  mouse events); min/close keep CSS hover. Covered by a bridge test.
- Title-bar controls keep out of the top 4 px native resize strip (avatar is 28 px, pill 28 px).
- `html`/`body` transparent, `#root` paints `--g-bg`: Mica shows through in `data-backdrop="mica"`.
- Theme setting changes post `{"type":"host","op":"theme","theme":…}` (type first) so the native frame follows.
- `app.ready` is still sent after a double rAF once the first snapshot has painted.
