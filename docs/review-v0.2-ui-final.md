# Final UI review, v0.2

A fresh review of `v0.2` at `b7edddd`, made after the Downloads redesign and the P1/P2 pass were merged. It checks the
work against `docs/design-critique-v0.2.md` and the owner's amendments in `DECISIONS.md`. The bar is "premium native
Windows 11 app, verbose, not bloated".

**How.** I used a production build (`pnpm build`, served by `vite preview`) with the mock backend. The repo suite shot
576 images (144 states × dark/light × 1280×800/960×600). A scratch script shot 17 more states the suite doesn't
reach: offline mid-queue, a download held while a game runs, the `settings?focus=` deep links, Home with failures,
"Nothing downloading", and row hover, focus and menus. `e2e/tools/film.mjs` recorded five filmstrips. Where a
screenshot couldn't answer a question, I read the code: Core's queue policy and connectivity, `state/failures.ts`,
`CouldntFinish.tsx`, the Settings deep link and the toast store. Screenshots cited below are in
[`screenshots/review-v0.2-final/`](screenshots/review-v0.2-final/).

**Out of scope (owner).** The active download card (`ActiveJobCard`, `SpeedStatus` and its dialog, `ThroughputChart`,
`PhaseStepper`, `ProgressBar` on the card) is being redesigned from scratch. Nothing below is about it. Some shots show
the card, but only because it sits on the page. M2 reaches the card's "Queued" label, but the fix proposed here is
on the page.

## Verdict

**Not release-quality yet, but close.** Outside the card, the app now reads as one system:
- one left edge on every page;
- one status table;
- a calm Home without the greeting or three primaries;
- a cover morph that flies the cover;
- an Install dialog that opens ready;
- "Couldn't finish" rows that recheck free space live and never take "Try again" away.

Almost every P1 and P2 item is done, and done well.

Two problems block release. Both are about the Downloads page **not saying why work isn't moving**, which matters more
than any spacing issue:

1. **Offline:** the page promises "Downloads continue on their own when your connection is back". Meanwhile the queue
   drains into "Couldn't finish" with one identical toast per job, and nothing resumes when the connection returns.
2. **While a game runs, with the default "Pause" setting:** a download you start sits as "Queued" forever, and no
   surface says why.

The Should list is roughly a day of work: a deep link that lands nowhere, honest import results, a floating
"Sync now", duplicated Sign in, and alignment and copy on Downloads.

---

## Must fix before release

### M1 · Offline: the queue drains into "Couldn't finish" while the page promises it will continue

**Screen:** Downloads (and every page, through toasts). **Shots:** `m1-offline-promise.png` (offline);
`m1-offline-after-reconnect.png` (4 s after the connection returns).

**What's wrong.**
- Core has no offline gate. `Jobs/` never reads `Connectivity`, and the mock says so
  (`ipc/mock/backend.ts:909`). Every queued network job starts, fails with `network` and lands in "Couldn't finish".
- Meanwhile the notice (`QueueNotice.tsx:23`) says "You're offline. Downloads continue on their own when your
  connection is back."
- Nothing resumes on reconnect. `CameOnline` (`Services/GliderService.cs:103`) only re-runs the update check and the
  save replay.
- Each failure raises its own toast. `notices.ts:28` keeps up to 3, with no grouping, so you get three identical "Can't
  reach Epic · Check your internet connection, then try again. Progress is kept." toasts with no game names.
- The toasts stay after the connection is back, and they repeat the "Couldn't finish" rows on the same screen.

On the owner's Wi-Fi, a 30-second drop during "Update all" fails the whole queue.

**Fix (preferred, small).**
1. **Hold instead of fail.** In `Services/UpdateService.cs:480` (`QueuePolicy.HoldReason`), return
   `"Waiting for an internet connection"` when `_c.Hub.State.Connectivity == Connectivity.Offline`, before the
   while-playing check. `JobQueue` already stops running pausable jobs under a hold and starts nothing new
   (`JobQueue.cs:472`, `:693`). The 5 s policy push picks up the change. Mirror it in the mock's `holdReason()`
   (`backend.ts:510`) and drop the offline `failJob` in `tick()` (`:909`).
2. **Resume the casualties.** On `CameOnline`, call `Jobs.Resume` for failed jobs whose `Error.Code` is `Network` or
   `CdnUnavailable` and whose `FinishedAt` falls inside the offline spell. These are the jobs that failed before the
   probe flipped.
3. **Toasts.** In `notices.ts` `push`, merge a toast with the same `title` + `message` into the existing one and show
   "· 3 downloads". In `Downloads.tsx`, register `suppressNoticesWhen((n) => n.level === "error" && !!n.jobId)` while
   mounted (the import list already uses this pattern), because the page lists those failures. When connectivity flips
   to online, dismiss toasts titled "Can't reach Epic".
4. With 1 in place, the notice copy becomes true. Also show it when only queued jobs exist:
   `Downloads.tsx:70`, `offline && (!!current || queued.length > 0)`.

**Minimum if Core can't change before release:** keep 3, and change the notice to lead `You're offline.`, rest
`Downloads stop until you're back online.`. Never promise an automatic resume Core doesn't do.

### M2 · A download started while you play sits as "Queued" with no reason anywhere

**Screen:** Downloads. **Shot:** `m2-held-by-game.png` (Fortnite running, "Update all" pressed, 6 s later).

**What's wrong.** "While a game is running" defaults to **Pause** (`Contracts/Models.cs:889`). Under that hold, Core
keeps queued jobs `Queued` with no `BlockedReason`, and so does the mock (`backend.ts:819`). Only a job that was already
running becomes `Waiting` with "Paused while Fortnite is running".

So the most common case shows a "Queued" card, eight "Update · 421.3 MB" rows, and nothing explaining why nothing starts:
start a game, then click Update (or Update all). The reason does exist in the snapshot, as
`queue.modeReason = "Paused while Fortnite is running"`. But P0-2 rightly removed the page-level mode notice, and
nothing replaced it for this case.

**Fix.**
1. **Core (additive):** `QueueState.HoldReason` (string?), filled from `QueuePolicy.HoldReason`, which is already
   computed for `JobQueue`.
2. **Page:** in `QueueNotice.tsx` `queueNotice()`, add a third case, shown when `queue.holdReason` is set and the
   current job isn't `waiting` (a waiting card already says it):
   - lead `Paused while Fortnite is running.`, rest `Glider continues when it closes.`;
   - a quiet `Change` link (`text-accent-text underline underline-offset-2`) to `settings?focus=whilePlaying`.

   With M1's hold this also covers offline: lead `You're offline.`, rest `Downloads continue when you're back.`.
   Without the contract change, derive it from `settings.whilePlaying === "pause"` and a running session.
3. **For the card designer:** a held queued job must not read "Queued". `stateOf()` (`state/transfer.ts:54`) should
   give it the Waiting label and tone when a hold is on.

---

## Should fix

### S1 · `settings?focus=cdn` highlights nothing

**Screen:** Settings → Downloads. **Shot:** `s1-focus-cdn.png` (after `go("settings?focus=cdn")`, chunks warm).
`downloadMode`, `speedLimit` and `whilePlaying` work (`ok-focus-speed-limit.png`). The router route and the raw
`#/settings?focus=` hash both work.

**What's wrong.** The "Download server" row (`DownloadsSection.tsx:41`) lives inside
`<Disclosure title="Advanced">` (`:134`), which is closed. Radix `Collapsible.Content` unmounts closed content, so the
lookup in `Settings.tsx:130` finds no `[data-setting-id="cdn"]`, retries once, and gives up. Settings then consumes the
request, so the user gets Settings with nothing highlighted and nothing focused. Two entry points rely on this link:
- "Choose a server" in the CDN slow-download dialog;
- the failure copy "pick another CDN in Settings".

**Fix.** Make the disclosure controlled and open it for this link. In `DownloadsSection`:

```tsx
const route = useRoute();
const [advanced, setAdvanced] = useState(route.page === "settings" && route.focus === "cdn");
<Disclosure title="Advanced" open={searching || advanced} onOpenChange={setAdvanced}>
```

The section mounts before Settings consumes the focus (+60 ms), and the existing 120 ms retry then finds the row. Add a
`settings-focus-cdn` state to `e2e/states.ts` and a test beside the `goToSetting` test in `settings.test.tsx`.

### S2 · Import results say "Needs repair" for games Core is only checking, so there's no file count to show

**Screen:** Onboarding → Find games (and Settings → Epic Games Launcher → Import). **Shot:** `s2-import-done.png`.

**What's wrong.** You flagged that the rows say `Needs repair` without `· 12 files`. That's confirmed, and the cause is
deeper:
- `resultPhrase` (`import/model.ts:154`) reads `job.verify.filesBad` from the **import** job, which never carries
  verify data.
- In Core, `NeedsRepair` on an import means "Imported; verifying the files." (`Services/JobHooks.cs:225`). Core has
  already queued a verify job.
- A verify job repairs on its own: verify → repair → verify (DECISIONS, group D).

So at this moment nobody knows whether anything is damaged. Yet the headline says "6 games ready, 2 need a repair", the
subtitle explains repairs, and each row offers a Repair button for work Core already queued. The mock diverges too: its
`needsRepair` means "Epic's copy is an older version…" (`backend.ts:979`).

**Fix.** Show what is happening, then the result of the verify job:

| While | Row (right side) | Headline |
|---|---|---|
| verify queued or running | `Checking files` with `Spinner size={12}`, `text-accent-text` (working = accent) | `8 games added, 2 being checked` |
| verify passed | `Ready` (muted, no icon) | `8 games ready` |
| verify repaired | `Repaired · 12 files` (muted), from the verify job's `JobInfo.Verify.FilesFixed` | `8 games ready` |
| verify left damage | `Needs repair · 12 files` (`text-warning-text`, wrench) + `Repair` | `7 games ready, 1 needs a repair` |

Subtitle `Nothing was downloaded.`, plus `Repairs re-download only damaged files.` only in the last case. In
`model.ts`, take the game's verify job created after the import finished, not the import job. Remove the always-on
Repair button in `ImportList.tsx:63–83`. In the mock, make `completionOutcome` (`backend.ts:975`) enqueue a verify the
way Core does.

### S3 · Cloud saves: "Sync now" floats mid-row, aligned to nothing

**Screen:** every installed game page, signed in or out, both sizes. **Shots:** `s3-sync-now-signed-out.png`,
`s3-sync-now-960.png`.

**What's wrong.** You noticed this on the signed-out page, but it isn't specific to signing out:
- The body column is `max-w-[720px]` (`GameView.tsx:163`), and the saves text has `flex-1` (`SavesSection.tsx:65`).
  The ghost "Sync now" therefore sits at the column's invisible right edge: x≈767–823 at 1280. The hero ends at 1153
  and the facts row at about 794, so the button lines up with neither.
- Signed out, it is a near-invisible disabled ghost with no reason.
- The conflict variant's tinted box ends at 845, which also matches nothing.

**Fix.**
- In `SavesSection.tsx:64`, use `flex flex-wrap items-center gap-x-6 gap-y-2` and drop `flex-1` from the text block,
  so the actions follow the status. For the conflict tint, add `w-fit` so the box hugs its content.
- Render "Sync now" only when `signedIn && !offline && idle`; never render it disabled.
- When signed out, the status line ends with `Sign in to sync` (muted); the hero already carries the Sign in button.

### S4 · "Couldn't finish" rows: a one-item "…" menu with no focus ring

**Screen:** Downloads. **Shots:** `s4-row-menu.png` (the menu holds only "Dismiss"), `s4-row-focus.png` (keyboard
focus on Sunken Keep II's "…" is a faint grey square).

**What's wrong.** Two clicks for one action. The hand-rolled trigger (`CouldntFinish.tsx:64–72`) uses `outline-none`
with only `focus-visible:bg-surface-hover`, so it never gets the app's focus ring. Meanwhile History removes a row with
a hover-revealed X. That makes two patterns for the same "take it off the list" action on one page.

**Fix.** Replace `CouldntFinish.tsx:63–78` with the History pattern (`HistoryRow.tsx:79–87`):
`<IconButton label={`Dismiss ${job.title}`} size="sm" className="opacity-0 transition-opacity duration-150 focus-visible:opacity-100 group-focus-within:opacity-100 group-hover:opacity-100" onClick={() => void actions.dismissJob(job.id)}><X size={14} /></IconButton>`.
`IconButton` brings the focus ring.

### S5 · Four left edges on Downloads

**Screen:** Downloads. **Shot:** `s5-left-edges.png` (guides drawn at the four edges).

**What's wrong.** Inside 14 px there are four starts:
- page title at 113;
- section headers and the "Other games have updates." row at 117 (`px-1`);
- failure art at 125 (`pl-3`);
- History icons and "Show N earlier" at 127.

Right edges drift the same way: 918 for the "…", 928 for "Update 9 games" and "Clear all". P1-3 fixed this between
pages; it's back within the page.

**Fix.** Use one edge, the page title's.
- Remove `px-1` from `SectionHeader` (`Downloads.tsx:25`), the "Couldn't finish" header (`CouldntFinish.tsx:89`), the
  updates row (`Downloads.tsx:124`) and `Notice` (`QueueNotice.tsx:32`).
- List items bleed their hover fill into the gutter: add `-mx-3` on the `li`s (`Downloads.tsx:144`,
  `CouldntFinish.tsx:94`) and on `ReorderList`'s `itemClassName` (`Downloads.tsx:111`).
- Row padding becomes symmetric `px-3` (`CouldntFinish.tsx:35`, `HistoryRow.tsx:57`, `QueueRow.tsx:47`).
- "Show N earlier" goes from `ml-1` to `-ml-3`.

Art, icons and headers then start at 113, and right ends meet the column edge.

### S6 · Error copy: "CDN" and "try again" beside a Try again button

**Screen:** Downloads → Couldn't finish, toasts, error callouts. **Shot:** `s6-error-copy.png`.

**What's wrong.**
- "pick another CDN in Settings": Settings calls it "Download server", and the README says "download server".
- Several messages end in "then try again" / "Try again;" right next to a `Try again` button. That's the same fact
  twice on every surface that shows them, including the toasts in M1.

**Fix.** Edit `Contracts/Errors.cs` and the mock's `ipc/mock/errors.ts`:

| Code (Errors.cs line) | Now | Use |
|---|---|---|
| `network` (30) | Check your internet connection, then try again. Progress is kept. | `Check your internet connection. Progress is kept.` |
| `cdnUnavailable` (33) | Epic's download servers didn't respond. Try again, or pick another CDN in Settings. | `Epic's download servers didn't respond. You can choose another download server in Settings.` |
| `filesLocked` (35) | Close the game and any program using its files, then try again. | `Close the game and any program using its files.` |
| `legendaryFailed` (137) | The operation stopped unexpectedly. Try again; progress is kept. | `The operation stopped unexpectedly. Progress is kept.` |

Also align the mock to Core. The mock puts the game's name into `corrupted` and `filesLocked` (`errors.ts:76`, `:94`), and Core
doesn't, so screenshots show copy users never see.

### S7 · "Sign in" offered two or three times while signed out

**Screens:** Home, Library, game page, signed out. **Shots:** `s7-signed-out-home.png`, `s3-sync-now-signed-out.png`.

**What's wrong.**
- Home and Library show the title-bar "Sign in" (`TitleBar.tsx:108–114`) and, about 40 px below it, the banner's
  primary "Sign in" (`SignInBanner.tsx`).
- The game page shows the title-bar button, the hero's "Sign in" split button and "Sign in to play".
- Home also keeps an Updates group whose "Update" and "Update all" buttons are all disabled, which a signed-out player
  can't act on.

This is the P1-5 pattern ("same action offered twice") again.

**Fix.**
- In `AccountMenu`, return nothing while signed out on pages that offer their own sign-in:
  `const page = usePage(); if (page === "home" || page === "library" || page === "game") return null;`. Downloads,
  Settings and Store keep the title-bar button.
- Hide `UpdatesPanel` while signed out: `Home.tsx:110`, `updates.length > 0 && signedIn`.

### S8 · The rail dot is invisible to screen readers

**Screen:** rail. **Code:** `Rail.tsx:69` renders the dot `aria-hidden`, and the button's name stays "Downloads"
(`Rail.tsx:31`).

A failure ("Couldn't finish"), a running job and a paused job are signalled only by colour. Home deliberately has no
failures group, so this dot is the only cross-app signal.

**Fix.** Give `RailItem` a `status?: string` and render `aria-label={status ? `${label}, ${status}` : label}`:
- from `railDotTone`, `danger` → `something couldn't finish`, `accent` → `downloading`, `warning` → `paused`;
- the Library dot → `updates available`.

### S9 · Failures reported as an outcome land in History in the old style

**Screen:** Downloads → History (code). An import that Legendary didn't register ends with status `Completed` and
outcome `Failed` (`JobHooks.cs:201`; `SetOutcome` keeps the status, `JobQueue.cs:1326`).

`isFailed` (`failures.ts:11`) looks only at `status`, so this job:
- skips "Couldn't finish" and the red rail dot;
- shows in History with the pre-redesign look: red ×, red "Failed", red text (`HistoryRow.tsx:23–33`, `:87–91`);
- can be cleared by "Clear all".

**Fix.**
- In `failures.ts:11`, use `isFailed = (j) => j.status === "failed" || j.outcome?.kind === "failed"`. Exclude it in
  `pastJobsOf` (`:17`) and use it in `railDotTone` (`Rail.tsx:60`).
- In `FailureRow`, retry such a job with `actions.retryJob(job)` (a completed job can't be resumed).
- Delete History's failed branch.

---

## Nits

- **N1 · Free space can lag about 12 s after you come back from Explorer.** The focus refresh (`Downloads.tsx:42`) hits
  Core's 10 s drive cache, so the row can still say "Needs 4.8 GB, 921.6 MB free" until the next 12 s poll. Fix: in
  `onFocus`, also schedule `setTimeout(refreshSnapshot, 10_500)` and clear it on cleanup. The rest of the space logic
  checks out:
  - it compares `requiredBytes` with the drive's current free space;
  - shows "There's enough space now." and promotes "Try again";
  - never disables "Try again";
  - polls only while a space failure is on screen and the window is visible.

  Shots: `s6-error-copy.png`, `ok-space-freed.png`.
- **N2 · The deep link focuses the wrong segment.** `focus=downloadMode` focuses "Background" (`aria-checked=false`), not
  the selected "Balanced", so Space switches the mode. Change the selector in `Settings.tsx:133` to
  `'[role="radio"][aria-checked="true"], button, [role="combobox"], input'`.
- **N3 · Home lists updates that are in "Couldn't finish".** In the `errors` scenario, Home lists 10 updates, including
  the two failed ones, under "Update all". Downloads offers "Update 8 games", and `library.updateAll` skips the failed
  two. Fix: filter Home's list (`Home.tsx:71`) with the same rule as `updatesToOffer`. Shot: `n-home-errors.png`, which
  also shows that Home correctly has no failures group.
- **N4 · Green checks break the one status table.**
  - Green "done" checks on every History row (`HistoryRow.tsx:30`, `text-success-text`) and every imported row
    (`ImportList.tsx:95`, `text-success`). DECISIONS says green is only the dot of a running game. Use
    `text-fg-subtle`.
  - Imported rows say "Imported" under a headline that says "ready". Say `Ready` (`model.ts:153`).
- **N5 · Store: "Open in browser" appears twice when signed out** (the toolbar icon and the fallback button). The
  toolbar icon also stays enabled offline, which P2 asked to drop. Fix: render the toolbar `IconButton`
  (`StorePage.tsx:105`) only when `!error && phase !== "signedOut"`. Shot: `n-store-signed-out.png`.
- **N6 · The morph's art swap lands after the motion.** The hero lands at about +330 ms, and the wide-art crossfade
  runs at about +500 to +660 ms (`HeroArt.tsx:41`, 520 ms timer), a second change after everything has stopped. Real
  art makes it bigger than the mock does: a portrait cover gives way to a different landscape key art. Start it at
  about 320 ms, so the 150 ms crossfade ends as the spring settles. Shot: `n-morph-film.png`.
- **N7 · The default mock world seeds a failed update** (`ipc/mock/scenarios.ts:261`), so most suite shots and every
  README shot (`readme-shots.spec.ts` reuses the states) show a red Downloads dot (`n-red-dot-default.png`). Make it
  `["update", "completed"]`; the `errors` scenario covers failures.
- **N8 · Suite gaps that hid M1, M2 and S1.**
  - `downloads-empty` (`e2e/states.ts:456`) captures onboarding's "You're all set" (`onboardingHold`), not "Nothing
    downloading" (`n-suite-downloads-empty.png`; the real state is `ok-nothing-downloading.png`).
  - `store-offline` never reaches the offline fallback, because the mock's `store.show` succeeds offline.
  - Add `downloads-held-by-game`, `downloads-offline-later` and `settings-focus-cdn`.
  - `e2e/tools/downloads-shots.mjs` makes the page tall for its "tall capture" but never takes that screenshot.

---

## Checklist

Done = verified on screen or in code. Partly = the main ask is in, with a gap named in the findings.

### Critique P0

| Item | Status | Note |
|---|---|---|
| P0-1 Card: readouts, chart, no stats table | Out of scope (card redesign) | |
| P0-2 Mode control off the page; header keeps only the title | Done | Header has no mode, limit or Pause all. Paused queue: one notice with Resume all (`ok-queue-paused-notice.png`). |
| P0-2 "Change" deep link to Settings | Partly | `downloadMode`, `speedLimit`, `whilePlaying` work; `cdn` lands nowhere (S1); wrong segment focused (N2). |
| P0-2 Page shows only the effect, one line | Partly | The card part is out of scope. On the page, the hold reason lost its only surface (M2). |
| P0-3 Failures leave History, get their own section | Done | Owner placement and copy. `Clear all` keeps failures; outcome-failures slip through (S9). |
| P0-3 No "All caught up" over failures; no `Update N` when an update failed | Done | The updates row excludes failed updates (`s6-error-copy.png`). |
| P0-3 Red rail dot wins | Done | Not announced to screen readers (S8). |
| P0-3 Home group | Done (removed by owner) | `n-home-errors.png` |

### Critique P1

| Item | Status | Note |
|---|---|---|
| P1-1 Cover morph | Done | Cover flies, scrims fade late, cached art has no fade; reduced motion skips the morph (`n-morph-film.png`, `ok-morph-reduced-film.png`). The art swap lands late (N6). |
| P1-2 Install dialog | Done | Opens ready at about +250 ms with no resize (`ok-install-film.png`); a cold plan opens top-anchored and only grows down (`ok-install-cold-film.png`). |
| P1-3 One left edge | Done between pages | Within Downloads, see S5. |
| P1-4 One status table | Done | Library list, tiles and hero agree (`ok-library-list.png`). Green checks remain (N4). |
| P1-5 Duplicated actions | Done | Palette, onboarding sign-in, nothing-found and invalid folder (`ok-palette.png`, `ok-onboarding.png`). New duplicate: signed-out Sign in (S7). |
| P1-6 Scroll edge under the stepper | Done | |
| P1-7 Import results | Partly | Headline, subtitle and per-row Repair are in; no file count, and "Needs repair" is premature (S2). |
| P1-8 Signed out | Done | Library kept, banner, hero "Sign in to play", empty states branch. Duplicate Sign in and floating Sync now (S7, S3). |
| P1-9 Home primaries and greeting | Done | Play on hover; greeting gone. |
| P1-10 "Install size" | Done | `ok-game-states.png` |
| P1-11 Save-conflict dates | Done | Calendar days, fixture fixed (`ok-conflict-store-egl.png`). |

### Critique P2

| Item | Status |
|---|---|
| Tile badges (dark glass, wrench for repair) | Done |
| Filter counts reconcile, "All games (294)" | Done |
| List "Installed" uncoloured, one date rule | Done |
| Two-line tile titles | Done |
| Cancel dialog `Cancel install` / `Keep going` | Done (`ok-dialogs.png`) |
| `Uninstall and keep files` | Done |
| No corner × on dialogs | Done |
| Move dialog: switch unboxed, sentence dropped | Done |
| Error dialog primary `Download Glider` | Done |
| Overlay: no "Update available" badge | Done (`ok-settings-store.png`) |
| EGL readiness copy, warning colour | Done |
| Settings search dims nav | Done |
| White switch thumb | Done |
| Store signed out | Done (duplicate "Open in browser", N5) |
| Store offline: no "Open in browser" | Partly (fallback done; toolbar icon stays, and the state isn't shot, N5/N8) |
| Downloads empty: one unboxed "Nothing downloading" | Done (`ok-nothing-downloading.png`) |
| Welcome line break; `Go to your library` | Done |
| Account menu copy | Done (`ok-signed-out.png`) |
| Rail tooltips show only the shortcut | Done (`ok-signed-out.png`) |
| Perpetual motion (pill spinner 2 s, static dot, sheen only while focused) | Done |
| Lazy pages prefetch on idle | Done |
| Typeface | Done (decided: Inter stays) |

### Owner amendments (DECISIONS, 2026-11-15)

| Amendment | Status |
|---|---|
| Card has no caption line | Out of scope (card redesign) |
| Time left beside the %, elapsed in its tooltip; only bytes right of the bar | Out of scope (card redesign) |
| Stepper below the bar | Out of scope (card redesign) |
| Amber "Slow download" + info icon (tooltip/dialog); muted label for chosen limits | Out of scope (card redesign); its Settings links are in S1 |
| No Pause all in the header; a paused queue shows one notice with Resume all | Done |
| "Couldn't finish" below the card and Up next, above History | Done (`m2-held-by-game.png` shows the order) |
| Space failures keep Try again; know when space was freed | Done (`ok-space-freed.png`, `ok-errors-light-960.png`); refresh lag N1 |
| Home has no failed-jobs group | Done |
| Speed limit documented as approximate | Done (README "Known limitations") |
| `game.planInstall` from cache deferred | Deferred to v0.3; P1-2 is fixed without it |
| Try again = `jobs.resume`; a new run of the same task replaces the failure | Done (outcome-failures need `retryJob`, S9) |
| `Clear all` keeps failures; `Update all` skips failed updates | Done (Home's list doesn't, N3) |
| Space failure carries `RequiredBytes` + `Drive`; free space kept fresh by the page | Done (N1) |
| Row buttons: short → Free up space + Try again; enough → Try again primary; others one quiet secondary | Done |
| Speed status order, "Throttled while playing", dialog in the card | Out of scope (card redesign) |
| One deep-link mechanism, `settings?focus=<row>` on four rows | Partly (`cdn` broken, S1) |
| No description under "Nothing downloading"; quiet "Other games have updates." row | Done |
| "Details" only with technical detail; row menu holds only Dismiss | Done; the one-item menu is a finding (S4) |
| Mock handles `?bottleneck=` and `setFreeSpace` | Done |
| Red rail dot for failures | Done (S8 for screen readers) |
