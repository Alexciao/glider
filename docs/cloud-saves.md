# Cloud saves (v0.2)

Code: `src/Glider.Core/Saves/`. Research basis: `docs/research/legendary-v0.2.md` §6 and `docs/research/heroic-v0.2.md` (cloud saves).
Legendary 0.21.1's `sync-saves` compares **one clock** (newest local mtime vs newest cloud manifest name, 60 s tie window), deletes the
local files before a download, never checks an upload, and keeps no last-sync record. It can therefore overwrite a newer save (clock
skew, a touched log file, DST, a game that rewrites old mtimes) with no backup. Glider uses Legendary only as the transport.

## The 3-way check (`SaveSyncPlanner`, pure)

`state/saves.json` records, per game, what both sides looked like at the last successful sync: the local file list (path, size, mtime,
SHA-256 of the files that match the game's include/exclude lists) and the cloud manifest names from `list-saves`. A sync asks two
questions against that record, so no clock of one machine is compared with another's:

| local changed | cloud changed | result |
|---|---|---|
| no | no | InSync |
| yes | no | Upload |
| no | yes | Download |
| yes | yes | **Conflict** (unless both newest times are within 60 s: same moment) |
| no ledger, one side only | | Upload / Download |
| no ledger, both sides | | Conflict unless the newest times are within 60 s |

Local "changed" = other file set, other size, or other content (a file that only got a new mtime is unchanged). Cloud "changed" = a
manifest that was not there, or the newest one is gone (`clean-saves` removing older ones is not a change). An emptied local folder
with a cloud save is a Download (nothing to lose); an empty cloud with a local save is an Upload. A conflict is never resolved by
Auto: launch returns `LaunchOutcome.SaveConflict` (nothing starts), the game shows `SaveSyncStatus.Conflict` with
`SaveState.Conflict`, and `saves.resolve` (Keep Local = upload, Keep Cloud = download, optional launch) is the user's choice.

## Safety rules

1. **Backup before every overwrite**: `backups/saves/<app>/<yyyyMMdd-HHmmss>/{files/…, backup.json}`, built in a `.partial` folder and
   renamed only after every file was copied with its size checked. If no backup can be made (too big: 1 GiB, file vanished/locked)
   the download does not happen. Retention: 10 "overwritten" backups, 5 "uploaded" copies, 4 GiB per game (upload copies go first,
   the newest overwritten backup always stays).
2. **A failed download is undone**: Legendary deletes the local files first and prints "Successfully completed" even when the manifest
   or a chunk failed. Glider requires, on disk, files carrying a manifest's time and no "Download failed / chunk missing" line;
   otherwise it restores the backup (and removes half-written files) and reports an error. A killed download (timeout, shutdown) is
   restored the same way.
3. **Uploads are verified**: Legendary never checks its PUTs, so Glider re-lists the cloud and requires a manifest that was not there
   before (or the exact name of a same-second re-upload). Unverified → error, ledger untouched, retried next time.
4. **Cloud side of an upload**: every upload is a new manifest; nothing is removed, so the cloud's previous save stays recoverable.
   Glider does **not** download it into a backup first: `download-saves` can only write under `<install_dir>/.saves` (no CLI override)
   and `sync-saves --save-path` would rewrite `installed.json`. A copy of what was uploaded is kept locally.
5. One explicit direction per call (`-y sync-saves <app> --skip-download --force-upload` / `--skip-upload --force-download`), issued only
   after the 3-way check (or the user) decided; never a bare `sync-saves`, never `--disable-filters`.
6. A save folder that is the install folder (or its parent, or a drive root) without an include list is refused: a download would clear it.

## Lanes

S lane = one sync at a time (`SaveService._lane`); `list-saves` runs on the metadata lane inside it (it refreshes the game list).
Per game the S lane is exclusive with writer jobs (a sync is refused while the game or its base has one, and `JobHooks` waits for a
running sync of the game) and with the game running. The first-time save folder (`sync-saves --accept-path`, which writes
`installed.json`) runs only when no writer job is running; Legendary's own installed-data lock is the backstop. See DECISIONS.

## Launch

Before launch (`RunState.SyncingSaves`): Auto sync, 60 s budget. Offline / not signed in / Epic unreachable → skipped with `SaveState.Note`,
the game starts. A failed sync → warning notice, the game starts with the save on this PC. Conflict → no launch. After the game closes:
Auto sync with retries (immediately, +30 s, +2 min, +5 min); offline retries are quiet; a final failure shows a notice. Nothing is
ever deleted on failure.
