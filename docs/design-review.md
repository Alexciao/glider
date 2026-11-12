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
