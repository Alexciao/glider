# EGL coexistence

Many users already have Fortnite installed through the Epic Games Launcher (EGL). Glider adopts that folder with
**zero re-download** and **never breaks EGL**: both launchers keep working on the same folder. Code:
`src/Glider.Core/Egl/` (`EglService : IEglService`). Tests: `tests/Glider.Core.Tests/Egl/`. One-line decisions:
`docs/decisions/egl.md`. Legendary facts referenced here come from `docs/research/legendary.md` §5.4 and §6.

Confidence tags: **[src]** read in Legendary 0.21.1 source · **[test]** covered by a test here · **[known]** widely
documented EGL behaviour · **[inferred]** reasoned, not observed · **[unverified]** needs a real EGL install (see the
checklist at the end).

## Files involved

| File | Glider reads | Glider writes |
|---|---|---|
| `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item` | scan, drift, busy | write-back, move (sync on) |
| `…\Data\Manifests\Pending\*.item` | busy evidence | never |
| `%ProgramData%\Epic\UnrealEngineLauncher\LauncherInstalled.dat` | scan (secondary) | two values, only if it lists the install |
| `<install>\.egstore\<guid>.manifest`, `<guid>.mancpn` | adoption check | write-back |
| `<install>\.egstore\bps\`, `<install>\.egstore\Pending\` | busy and adoption evidence | never |

Glider never deletes an EGL file, and never creates EGL's own folders. The only things it creates are a new
`<guid>.item`, `.egstore` and its two files, and only for an export where EGL had no record of the folder.

## Detection (`ScanAsync`, target < 20 ms)

* `LauncherInstalled` = `…\EpicGamesLauncher\Data` exists. `LauncherRunning` = a process named `EpicGamesLauncher`
  (behind `IProcessProbe`, faked in tests).
* **Primary source: every `Manifests\*.item`** (top level only). Parsing is tolerant: UTF-8 with or without a BOM,
  UTF-16 with a BOM, `//` and `/* */` comments, trailing commas, missing keys (Legendary's `from_json` defaults), and
  values of the wrong type. A file that can't be parsed, isn't a JSON object, or has no `AppName` is logged and
  skipped. **[test]**
* Kept: `AppName == "Fortnite"` (case-insensitive) and `MainGameAppName` empty or equal to `AppName`. That excludes
  DLC items and other games. This matches Legendary's importable filter `main_game_appname == app_name` **[src]**, but
  is more lenient about an empty `MainGameAppName`.
* **Secondary source: `LauncherInstalled.dat`** (`{"InstallationList":[{InstallLocation, NamespaceId, ItemId,
  ArtifactId, AppVersion, AppName}]}`). Legendary never touches this file **[src]**. Its shape is well known from
  third-party launchers (Playnite, GOG Galaxy integrations, various scripts) **[known, high confidence on keys;
  medium on details]**. EGL writes it with tab indentation, as UTF-8 or, through Unreal's AutoDetect, as UTF-16LE
  with a BOM when the text contains non-ANSI characters **[inferred, medium]**. Glider reads both encodings.
* **Merge and dedupe** use a normalized path: case-insensitive, `\` and `/` treated the same, repeated and trailing
  separators removed, and `\\?\` stripped. The `.item` wins over `.dat`, and `.dat` only fills in a missing version.
  When several `.item` files point at one folder, the best one is used: complete before incomplete, then the newest
  write.
* Each candidate carries `FolderExists`, `ExecutableFound` (`FortniteLayout.LooksLikeInstall`: the shipping exe exists),
  `IsStale = !ExecutableFound` (the manifest points at a missing folder or a folder without the game), `Version`
  (`AppVersionString` / `AppVersion`), and `SizeBytes` (`InstallSize`, null from `.dat`). Stale candidates are reported,
  not hidden, so the UI can say "EGL thinks Fortnite is at X, but it isn't there".
* `Id` = `egl-` + the first 16 hex digits of SHA-256(normalized path). It stays the same across scans, restarts and
  spellings.
* Any drive, any path, and any number of candidates are supported. Nothing assumes `C:\Program Files\Epic Games`.
* `SyncAvailable` / `ManifestPath`: the `.item` that matches the current Glider `GameRecord`. `BusyWithGame`: EGL is busy
  with the record's folder, or with any existing candidate folder when Glider has no record yet.
* Measured: ~2 ms median for 7 items plus `.dat` (Debug build) **[test]**.

A folder with no EGL manifest at all, whether hand-copied or left after an EGL uninstall, is not an EGL candidate. The
caller's folder validation (`FortniteLayout`) handles it. `FindByLocation` returns null for it **[test]**.

## Adoption flow

1. The user picks a candidate (or a folder). The caller validates it with `FortniteLayout` and checks
   `InspectForAdoption(path)`, which predicts Legendary's `import` logic (core.py:1890-1924) **[src][test]**:
   * a `.egstore\*.mancpn` with `AppName == "Fortnite"` plus its `<guid>.manifest` → Legendary imports that manifest
     (no network manifest) and, **unless** `.egstore\bps` exists or `.egstore\Pending` is non-empty, sets
     `needs_verification = false`.
   * otherwise Legendary downloads the *latest* manifest and sets `needs_verification = true`. That makes the next
     update a full SHA-1 repair pass.
   * Legendary quirk: a `.mancpn` with **no** `.manifest` makes Legendary record the *latest* version with
     `needs_verification = false`. `VerifyRecommended` is true in that case, so Glider verifies anyway.
2. A **one-time `legendary import Fortnite <path>`**. This is not continuous `egl-sync`: Legendary's `egl_sync` stays
   off (see `DECISIONS.md`).
3. Glider stores the `GameRecord` with `AdoptedFromEgl = true` and `EglInstallationGuid` = the `.item` guid.
4. Verification policy:
   * **Skip the full verify** when the EGL manifest version (`EglAdoptionInfo.EglVersion`, i.e. the `.item`
     `AppVersionString`) **equals the latest version** and `VerifyRecommended` is false. The files are the ones EGL
     installed and validated.
   * **Otherwise** (older version, staging present, metadata missing): run the verify **in the background**. The game
     stays playable where possible and updates wait for it.
5. Each startup after that runs `CheckDrift` (below), never a continuous sync.

## Drift (`CheckDrift(GameRecord)`) — reads only **[test]**

| EGL `.item` for the record's folder | Result | Caller does |
|---|---|---|
| none (no item, or the items point elsewhere; EGL uninstalled) | `EglMissing` | keep going; with sync on, the next write-back re-creates it |
| several items with different guids claim the folder | `Unreconcilable` | quick verify |
| `InstallationGuid` ≠ `GameRecord.EglInstallationGuid` (EGL reinstalled the folder) | `Unreconcilable` | quick verify, then adopt the new guid |
| `bIsIncompleteInstall = true` | `Unreconcilable` | wait for EGL / quick verify |
| versions not orderable (empty, no numbers, same CL but different label, numerically equal but textually different) | `Unreconcilable` | quick verify |
| EGL version > ours | `EglNewer` | silently re-`import`, refresh the record; **never re-download** |
| ours > EGL version | `GliderNewer` | `WriteBackAsync` if "Keep EGL in sync" is on |
| equal (case-insensitive) | `InSync` | nothing |

**Version comparison** (`FortniteVersion.Compare`): for `++Fortnite+Release-33.10-CL-12345678-Windows`, the changelist
(`-CL-n`) is authoritative when both sides have one, because hotfix CLs can carry an older release label. Otherwise
every number in the string is compared component-wise (`33.10` > `33.9`; `33.10.1` > `33.10`). If one side has a CL and
the other doesn't, or nothing is numeric, the result is null (`Unreconcilable`).

## Write-back (`WriteBackAsync`) — mirrors Legendary's `egl_export`

Preconditions (all checked before anything is written; each failure is a `GliderException` and touches nothing)
**[test]**:

* EGL's `Manifests` folder exists. Legendary raises `ValueError` here; Glider never creates EGL's folders.
* The install folder exists, and EGL is not busy with it (`EglBusy`).
* Legendary's manifest for the new version exists (`ILegendary.GetInstalledManifestPath`). Legendary refuses to export
  without it. An item claiming a version whose manifest EGL doesn't have would make EGL re-validate or re-download.
* Every `.item` can be read. A locked one is retried and then fails with `FilesLocked`, because skipping it could make
  Glider write a second item for an install EGL already has **[test]**.

GUID: if EGL already has an `.item` for the folder, its `InstallationGuid` (and its file name) is used even if the
caller passed another one, since two items for one folder would make EGL list the game twice. Otherwise the caller's
`ExistingInstallationGuid` is used, or a new one as Legendary makes it: `str(uuid4()).replace('-','').upper()`, which
is 32 uppercase hex digits. The used guid is returned so the caller can store it.

Files, in Legendary's order: `.egstore\<guid>.manifest` (byte copy of Legendary's manifest), `.egstore\<guid>.mancpn`,
then `Manifests\<guid>.item`, and finally `LauncherInstalled.dat` (Glider addition).

### `.item` content

This is a literal port of `EGLManifest` (models/egl.py). `from_json` pops the modelled keys with Legendary's defaults
and keeps the rest as `remainder`. `to_json` writes `_template` ∪ `remainder` ∪ the modelled keys. The overrides are
`from_lgd_game`'s:

| Key | Written | Notes |
|---|---|---|
| `AppName`, `MainGameAppName` | `Fortnite` | |
| `AppVersionString` | new version | what EGL compares |
| `BuildLabel` | `Live` | constant, as in Legendary |
| `CatalogItemId`, `CatalogNamespace` | from the caller, else kept | |
| `DisplayName` | kept, else `Fortnite` | |
| `InstallLocation` | install path, native separators, trailing separator trimmed | |
| `InstallSize` | bytes | |
| `InstallTags` | caller's tags; **kept if the caller passes none** | deviation: an imported install has no tags in Legendary, and writing `[]` could change EGL's selection [inferred] |
| `InstallationGuid` | guid | |
| `LaunchExecutable` | from the caller, else kept | |
| `MandatoryAppFolderName` | kept, else the folder's leaf name | Legendary uses catalog `FolderName` |
| `ManifestLocation` / `StagingLocation` | `f'{install}/.egstore'` / `f'{install}/.egstore/bps'` | forward slash after the native path, exactly as Legendary (and EGL) write it |
| `OwnershipToken` | `"true"`/`"false"` from the existing value | `from_lgd_game` uses a bool |
| `bIsIncompleteInstall`, `bNeedsValidation` | `false` | written after a successful job |
| `BaseURLs`, `LaunchCommand`, `bCanRunOffline` | kept | Glider doesn't track `igame` values for these |
| everything else (`InstallSessionId`, `PrereqIds`, `bAllowUriCmdArgs`, …) | kept from the existing file | deviation: Legendary's `egl_export` starts from `from_lgd_game`, which **drops** EGL-only keys |
| `ManifestHash` | **removed when the version changes** | deviation: it describes the manifest we just replaced; Legendary-written items never have it, and EGL accepts them [unverified] |

**Format**: exactly `json.dump(obj, f, indent=4, sort_keys=True)` as Legendary writes it on Windows (`PyJson`):

* 4-space indent, `", "` → `","` + newline, `": "`, empty containers written `[]`/`{}`, no trailing newline;
* keys sorted by Unicode code point (uppercase before `b…`, astral after U+FFFF);
* `ensure_ascii`: everything outside 0x20–0x7E is written `\uXXXX` with **lowercase** hex, and non-BMP characters as
  surrogate pairs; `\"`, `\\`, `\n`, `\r`, `\t`, `\b`, `\f`;
* floats via Python `repr` (`1e+16`, `1000000000000000.0`, `1e-05`), integers of any size;
* **CRLF** line endings: Python's text mode on Windows, the only platform where Legendary writes EGL files.

`.mancpn`: `{"AppName","CatalogItemId","CatalogNamespace","FormatVersion":0}` in the same format. The catalog values come
from the caller, else the `.item`, else the existing `.mancpn`.

Golden files are produced by **running Legendary's own code**. `tools/egl-golden/make_goldens.py` imports
`legendary/models/egl.py`, applies the same field assignments, and calls the same `json.dump`. The tests compare
Glider's output to them byte for byte **[test]**. To regenerate them:
`python -I tools/egl-golden/make_fixtures.py . && python -I tools/egl-golden/make_goldens.py <legendary-src> .`

### `LauncherInstalled.dat`

Decision: **update it, surgically, only if it already lists the install**. EGL is believed to rebuild it from its
manifests [inferred], but other tools read it, and a stale `AppVersion`/`InstallLocation` there is exactly the kind of
inconsistency that confuses them. The patch replaces only the bytes of the `AppVersion` (write-back) or
`InstallLocation` (move) string in the matching Fortnite entry. EGL's tabs, key order, other entries, BOM/encoding and
line endings are untouched **[test]**. New values use Unreal-style escaping, with non-ASCII as `\uXXXX`, so the file is
valid whatever encoding EGL assumes. Glider never adds an entry or creates the file. An unreadable `.dat` is left alone,
because it is secondary.

### Safety: backup, atomic write, locks, rollback

* Before the first write, every target that exists **and would change** is copied byte for byte to
  `%LocalAppData%\GliderData\backups\egl\<yyyyMMdd-HHmmss-fff>\{Manifests|egstore|UnrealEngineLauncher}\…`. A
  `backup-index.txt` maps each copy to its original path. If the backup fails, nothing is written. The newest 20 sets
  are kept **[test]**.
* Each file is written to `.<name>.<random>.glider.tmp` in the **same directory**, flushed with `WriteThrough` +
  `Flush(true)`, and swapped in with `File.Replace` (Windows `ReplaceFile`, which keeps the destination's ACLs). A new
  file is moved into place instead. The target is never partially written, and temp files are always removed
  **[test]**. The suffix isn't `.item`/`.manifest`/`.mancpn`, so EGL never picks up a temp file.
* Sharing violations and transient denials are retried 5 times, 200 ms apart, then fail with `FilesLocked`
  ("Epic Games Launcher files are in use … Close Epic Games Launcher, then retry") or `PermissionDenied` **[test]**.
* If a later file fails, files already committed in the same write-back are restored from the backup, so EGL never
  sees a new `.manifest` next to an old `.item` **[test]**.
* An identical file is not rewritten, so a repeated write-back changes nothing and makes no backup **[test]**.
* Writes are serialized inside the service (one write-back or move at a time).

## Concurrency with EGL

* **Launching while EGL is merely open is fine**, and EGL being open is never evidence of being busy on its own
  **[test]**.
* **Busy** (`IsBusyWith`, `GetBusyReason`) requires EGL running **and** evidence of an operation on *that* install:

| Evidence | Meaning | Confidence |
|---|---|---|
| `.item` `bIsIncompleteInstall = true` (any age) | first install in progress or interrupted | high [known; Legendary models it] |
| `.egstore\bps\` non-empty and written within 3 min (folder or any of its first 256 entries) | EGL downloading or patching into staging (`StagingLocation`) | high that bps = staging [src: Legendary treats it as in-progress]; medium on the recency window |
| `.egstore\Pending\` non-empty and recently written | EGL staging the next manifest | medium [src: Legendary checks it] |
| `Manifests\Pending\*.item` for the folder, written recently | EGL's pending record during install or update | low-medium [inferred] |
| `InstallSessionId` | **not used**: it stays set between operations | medium [inferred] |

  Staging older than 3 minutes while EGL is open is treated as a **paused or abandoned** update, which doesn't block.
  If EGL is closed, staging is never "busy", but `InspectForAdoption` still reports it and Legendary will require
  verification. Known gap: during EGL's own *verify* phase EGL may write nothing for minutes, and then it isn't
  detected [unverified]. The next drift check catches whatever EGL changes.
* `WaitUntilNotBusyAsync` checks once. If the install is busy, it calls `onBlocked(reason)` (for example "Epic Games
  Launcher is updating Fortnite, so Glider will wait and continue automatically.") each time the reason changes. It
  wakes on `FileSystemWatcher` events (top level of `.egstore`, plus `Manifests` and its subfolders) or on a 2 s poll,
  and finishes with `onBlocked(null)` once the evidence clears or EGL exits. It is cancellable and costs nothing when
  idle **[test]**.
* The job hooks must call it in `BeforeRunAsync` for every mutating job (install, update, repair, verify, move,
  uninstall with files, write-back). `WriteBackAsync` and `UpdateLocationAsync` also refuse on their own if EGL is busy.
* The two launchers must never update at the same time. Glider waits for EGL, and EGL can't see Glider's work until the
  write-back.

## Uninstall semantics

* **"Remove from Glider only"**: `legendary uninstall --keep-files` (Legendary's `egl_guid` is empty, because Glider
  never enables `egl_sync`, so Legendary's `egl_uninstall` doesn't run) and Glider deletes its `GameRecord`.
  **No EGL file is touched**: the `.item`, `.egstore` and `.dat` stay, and EGL keeps working.
* **"Uninstall and delete files"** on a folder shared with EGL: the game files go, which EGL can't prevent. Glider
  still doesn't delete EGL's `.item` or `.egstore`. EGL then shows the game as needing repair or missing, and the user
  can remove it there. The UI must say so before confirming. This deliberately differs from Legendary, whose
  `egl_uninstall` deletes the `.item` and `.egstore`.

## Move semantics

* The game is moved with `legendary move` (same volume), or by Glider copying and then running `move --skip-move`
  (cross-volume). `.egstore` moves with the folder.
* With sync on and an EGL guid known, `UpdateLocationAsync(guid, newPath)` updates `InstallLocation`,
  `ManifestLocation` and `StagingLocation` in the `.item`. All other keys are kept, and the file is rewritten in
  Legendary's format, like Legendary's re-export after `move`. It also updates the entry's `InstallLocation` in
  `LauncherInstalled.dat`, with the same backup and atomic guarantees **[test]**. The guid is matched
  case-insensitively, and an unknown guid fails clearly.
* With sync off, EGL is not told. The next scan shows EGL's old location as a stale candidate.

## Fixtures

`tests/Glider.Core.Tests/Fixtures/Egl/` (committed as `-text`, so their CRLFs survive):

* `fortnite-egl.item`: a realistic EGL-written Fortnite manifest with EGL's key order, tabs and CRLF. The keys are
  Legendary's template plus EGL-only keys (`ManifestHash`, `bAllowUriCmdArgs`, `PrereqSHA1Hash`,
  `MainGameCatalogItemId`, …). It is reconstructed, not copied from a real machine [inferred, medium].
* `edge-cases.item`: unicode (BMP, astral and key sorting), control characters, floats, big integers, nested objects,
  and `OwnershipToken: "True"`.
* `LauncherInstalled.dat`: two entries, EGL style.
* `*.golden.*`: generated by `tools/egl-golden/make_goldens.py` from Legendary's code.
* `EglTestTree` builds throw-away machines under the temp dir (fake ProgramData, fake drives, game folders with just the
  shipping exe and optional `.egstore`). `EglTreeHasher` (SHA-256, size and mtime of every file under `Epic\` and each
  `.egstore`) proves that scan, drift, busy and adoption checks leave every EGL file byte-identical **[test]**.

## Manual verification needed on a real EGL install

1. Take a real `Fortnite.item` and `LauncherInstalled.dat` (copies) and confirm the key set and encoding match the
   fixtures. Check whether `ManifestHash` exists and what it hashes.
2. After a Glider update and write-back, open EGL. It should show Fortnite as installed and up to date, and must not
   offer an update, verify or re-download. Test with EGL closed during the write, then with EGL open (EGL may cache
   items until it restarts).
3. With the `.item` reformatted in Legendary's style (sorted keys, 4 spaces, `\u` escapes, no `ManifestHash`), confirm
   that EGL still launches, verifies and updates the game, and doesn't rewrite or duplicate the item.
4. Confirm that EGL updates `LauncherInstalled.dat` itself after it changes the game, and tolerates Glider's in-place
   value edits.
5. During an EGL update, watch what appears: `.egstore\bps`, `.egstore\Pending`, `Manifests\Pending`,
   `bIsIncompleteInstall`, `InstallSessionId`. Tune `BusyRecentWindow` accordingly. Check the same during an EGL
   *verify*, which is the known detection gap.
6. Confirm that writing to `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests` works unelevated (Legendary's egl-sync
   suggests it does), and that `File.Replace` keeps its ACLs.
7. Check `InstallTags`: does EGL treat a Legendary/SDL tag list differently from its own (risk of an optional-content
   download)?
8. Install on a non-ASCII path, and check how EGL writes `LauncherInstalled.dat` (UTF-16?) and reads Glider's `\u`
   escapes back.
9. The process name `EpicGamesLauncher` covers the main process. Check whether EGL can do downloads with only helper
   processes alive (for example after the main window closes to the tray).
