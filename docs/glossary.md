# Glider glossary and copy rules

One word per idea. Every string a person reads (frontend, `Errors.cs`, Core notices, the mock) follows this page. If a
word you need is missing, add it here in the same change. Code identifiers (`ErrorCode`, `JobKind`, `appName`) keep
their names; only the words on screen are governed here.

## Style

| Rule | Example |
|---|---|
| Sentence case everywhere: titles, buttons, menu items, tabs, column headers. Capitals only for proper names. | `Library folders`, `Move to…`, `Epic Games Launcher` |
| Buttons are verbs (or verb + object when the verb alone is vague) and say what happens. Keep the verb through the whole flow: the button `Install` leads to `Installing` and the toast `Installed`. | `Install` → `Installing Fortnite` → `Fortnite installed` |
| `…` (one character) ends a button or menu item that opens a dialog or asks for more input, and ends an in-progress label. Never three dots. | `Move…`, `Import…`, `Launching…` |
| Retry is `Try again` (button) and `try again` (sentence). Never `Retry`. | `Check your connection, then try again.` |
| US spelling: `canceled`, `favorites`, `color`. | `Sign-in was canceled` |
| Errors say what happened and what to do, never apologize. Titles are short facts, not exclamations. | `Not enough disk space` |
| A fact appears once per surface. A label is not restated by its own description, a status is not repeated by the button beside it, and an empty section is hidden rather than showing `None`. | No `Ready to play` above `Play` |
| Numbers: `5 min`, `2 h 10 min`, `1.2 GB`, `58.8 MB/s`, `33%` (no space before `%`). Counts with a noun are pluralized (`1 game`, `8 games`). | |
| Separators in one-line metadata are `·`. | `Install · 1.2.4` |
| Product names are written in full and never abbreviated in the UI. | `Epic Games Launcher` (never `EGL`), `Epic Games Store`, `Easy Anti-Cheat` |

## Terms

### Actions on a game

| Term | Means | Use | Do not use |
|---|---|---|---|
| **Install** | Put a game that is not on this PC onto it (download, then set up). | Button `Install`; running `Installing`; done `Installed`. | `Download` as the action, `Get` |
| **Update** | Bring an installed game to a newer version. | `Update`, `Updating`, `Updated`; `Update available`; `Update all` | `Upgrade`, `Patch` |
| **Verify** | Read every file and compare it with Epic's manifest. Changes nothing. | Menu item `Verify`; running `Verifying`; done `Verified` | `Check`, `Scan` for files |
| **Repair** | Download again only the files that verify found damaged or missing. A verify that finds bad files continues as a repair. | Button `Repair` (game is `Needs repair`); running `Repairing`; done `Repaired` | `Fix`, `Re-download`, `Verify and repair` |
| **Import** | Bring a game that is already installed on this PC (for example by Epic Games Launcher) into Glider without downloading it again. | `Import`, `Import…`, `Importing`, `Imported`, `Existing installs` | `Adopt`, `Use this install`, `Link` (for installs) |
| **Move** | Change the folder a game is installed in. | `Move…`, `Moving`, `Moved` | `Relocate` |
| **Uninstall** | Remove a game from Glider, and optionally delete its files. | `Uninstall…`, `Uninstalling`, `Uninstalled`; `Keep the files` / `Delete the files` | `Remove` for a game (see below) |
| **Play** | Start the game. While it starts: `Launching…`. While it runs: `Playing`; `Close game`. | | `Launch` as a button |
| **Link account** | The one-time step that connects your Epic account to another launcher (Ubisoft Connect, EA app) so a game can start. | `Link your Ubisoft Connect account`, button `Link account` | `Activate` (that is what Epic calls it internally), `Adopt` |
| **Resume / Pause** | Continue or stop a job. A paused job keeps its progress. | `Pause`, `Resume`, `Pause all` | `Stop`, `Continue` as a button |
| **Cancel** | Stop a job and give up on it. Progress already on disk is kept. | `Cancel`, `Cancel install of …?` / `Keep going` | `Abort`, `Stop` |
| **Remove** | Take something out of a *list* without touching files: a library folder, a game from a collection, a favorite. | `Remove folder`, `Remove from favorites` | `Delete` for lists |
| **Delete** | Destroy data: files, a collection. Always confirmed. | `Delete the files`, `Delete collection?` | |

### Places and things

| Term | Means |
|---|---|
| **Library** | Every game the signed-in Epic account owns, installed or not. |
| **Library folder** | A folder Glider installs new games into (Settings → Library folders). One is the default. |
| **Install folder** | Where one specific game lives (`D:\Games\Fortnite`). Shown as `Location` in a game's facts. |
| **Game files** | The files of an installed game on disk. |
| **Downloads** | The page and the queue of everything Glider is doing: installs, updates, repairs, verifies, moves, imports, uninstalls. It is the queue, not only downloads. |
| **History** | Finished jobs on the Downloads page. Past tense (`Installed in 28 min`). |
| **Collection** | A named group of games you made. `Add to collection`, `New collection…`. |
| **Favorites / Hidden** | Library filters. `Hide from library`, `Show in library`. |
| **Cloud saves** | Epic's cloud copy of a game's save files, synced before and after you play. States: `In sync`, `This PC is newer`, `The cloud is newer`, `Saves differ`, `Off`. A *conflict* is `Saves differ`; the action is `Resolve…`. Never `Cloud sync` or `Save sync`. |
| **Save backups** | Copies Glider makes before it overwrites a save. |
| **Epic Games Launcher** | The Epic app Glider replaces. Always written in full. Short labels may say `Launcher` (`Launcher is busy`) only when the surface already names it. Settings section: `Epic Games Launcher`. Never `EGL`, `the Epic launcher`, `Epic client`. |
| **Keep in sync** | The setting that makes Glider update Epic Games Launcher's record of a game after a move or update, so the launcher does not download it again. |
| **Epic overlay** | The Epic Online Services overlay (friends, achievements). Never `EOS overlay`. |
| **Epic Games Store** | The Store tab. `Store` in navigation. |
| **Sign in / Sign out** | Verbs. The noun is `sign-in` (`Sign-in failed`). |
| **Account** | The signed-in Epic account (`Epic Games account`). |

### Download modes

| Mode | Means |
|---|---|
| **Full** | Uses everything for the fastest installs. Best when you are away. |
| **Balanced** | Full network speed with low disk and CPU priority, so the PC stays smooth. The default. |
| **Background** | Low priority and fewer connections. Best while you work or play. |

The setting is `Download mode`. `Speed limit` caps the network in MB/s. `Full speed when I'm away` switches to Full while the
PC is idle. `While a game is running` is `Pause`, `Throttle` or `Continue`.

### Job phases

Planned phases appear as a stepper, in this order and with these words: `Preparing`, `Verifying`, `Repairing`,
`Downloading`, `Installing`, `Finalizing`, plus `Moving`, `Removing` and `Importing` for jobs that are only that.
`Downloading` is the network transfer only; `Installing` is the disk work after it. A job's own verb is the headline
(`Updating Fortnite`); the phase is never repeated next to it.

## Where copy lives

| Surface | File |
|---|---|
| Job verbs and past tense | `frontend/src/state/derive.ts` (`JOB_VERB`, `JOB_ACTION`, `JOB_PAST`, `PHASE_LABEL`) and `frontend/src/state/transfer.ts` (`PHASE_STATE`, `KIND_NOUN`) |
| Game page primary action | `frontend/src/features/game/actionModel.ts` |
| Library status | `frontend/src/features/library/status.ts` |
| Command palette | `frontend/src/app/palette/model.tsx` |
| Errors (title, message, fix) | `src/Glider.Core/Contracts/Errors.cs` and its mock twin `frontend/src/ipc/mock/errors.ts` |
| Notices (toasts) | Core `ServiceContext.Notify` call sites (`JobHooks.cs`, `UpdateService.cs`, `SaveService.cs`, …) |
| Fix button labels | `frontend/src/state/ui.ts` (`FIX_LABELS`) |
