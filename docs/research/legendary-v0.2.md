# Legendary 0.21.1 — multi-game research for Glider v0.2

Scope: everything Glider v0.2 (a full EGL replacement for the whole library) needs from the bundled Legendary beyond
what [`legendary.md`](legendary.md) already covers for Fortnite. That doc remains valid except where §14 corrects it,
and this doc does not repeat it.

## Provenance and conventions

* **Source**: `https://github.com/legendary-gl/legendary`, tag `0.21.1` = commit `42f6bdeadde3a9526dc8eb713763476999ac217b`
  ("release: 0.21.1 Lowlife (#786)"). It was cloned outside the repo. The pin in `build/legendary.json` points at the same tag
  (`"source": "https://github.com/legendary-gl/legendary/tree/0.21.1"`).
* **Binary**: `legendary.exe` from `src/Glider.App/legendary/`, sha256 `6be77857…baf5b` (matches the pin).
  `legendary --version` → `legendary version "0.21.1", codename "Lowlife"` (`fixtures/legendary/help-v0.21.1/version.txt`).
  The exe embeds `python314.dll` (string found in the binary). The release CI builds with `uv sync` (`.github/workflows/build-base.yml`),
  so bundled deps come from `uv.lock`: **filelock 3.25.2** (`uv.lock:178-179`) and requests 2.33.1 (`uv.lock:551-552`).
* Cites are `file.py:line` relative to `legendary/` at that tag (e.g. `core.py:211`, `lfs/lgndry.py:322`,
  `downloader/mp/manager.py:657`). The tags are the same as in legendary.md: **[src]** read from source, **[run]** observed with the
  pinned exe, **[net]** observed from a live public endpoint, **[inferred]** reasoned from source but not executed, and
  **[unverified]** not confirmable here. No Epic account was used. Nothing was signed in and no real token was read.
* New fixtures (all under `docs/research/fixtures/legendary/`):
  * `help-v0.21.1/` holds `-h` for the root and every sub-command, including the aliases `download`, `update`, `repair`,
    `list-games` and the hidden `get-token`, captured from the pinned exe.
  * `concurrency-v0.21.1/` holds the lock experiments: raw outputs, the scripts, and a `README.md` with the timings.
  * `catalog-v0.21.1/` holds real public catalog JSON (Fortnite, Alien: Isolation + DLC, Castlevania) and the EOS overlay asset.
    It was fetched with an anonymous `client_credentials` token (the grant Legendary uses for the overlay), never with a user login.
  * `sdl-v0.21.1/` holds every non-UE SDL definition served by `api.legendary.gl` today, plus the current `version.json` (`_api-version.json`).

---

## 0. Decisions in one screen (details below)

1. **There is exactly one cross-process mutex for `installed.json`**: `<cfg>/installed.json.lock`, acquired **non-blocking**
   and **never released until the process exits** (`lfs/lgndry.py:322-341`). A second mutating command does not wait. It
   prints `[cli] CRITICAL: Failed to acquire installed data lock…` and **exits 0** [run]. **Two installs of different games can
   never run at the same time** against one config dir.
2. **`user.json.lock` is a *blocking*, no-timeout lock** (filelock default `timeout=-1`, `lfs/utils.py:169-184`). It is held for the
   entire `login()` (`core.py:280-282`), which includes the 24 h Legendary update check, the EOS overlay update check and the
   verify or refresh HTTP calls. It is also taken by **any read of `lgd.userdata`**. **`launch --offline` and `status --offline` both block on it** [run: 10.9 s
   while the lock was held].
3. **Nothing protects `metadata/*.json`, `assets.json`, `entitlements.json`, `achievements.json`, `version.json`, `aliases.json`,
   `config.ini` or `tmp/*`.** All are written with non-atomic `json.dump(open(path,'w'))`. Readers (including every Legendary
   process at start-up, `lfs/lgndry.py:122-145`) silently treat a half-written file as missing.
4. **`user.json` writes are not atomic either and happen *after* the lock is released** (`lfs/utils.py:186-195`). That leaves a small
   window for a second process to read the old token. Avoid concurrent refreshes (§1.6).
5. A download holds `installed.json.lock` for hours but `user.json.lock` only during its first seconds (its login). **`launch` never
   touches `installed.json.lock`**, so it is never blocked by a download. It can only wait behind another process's login.
6. Job runner shape (§1.8):
   * **one Writer lane** for every `installed.json` writer, and `cleanup`.
   * **one Metadata lane** for online library refreshes.
   * **a Saves lane** that runs per game.
   * **an always-available Launch lane** (`launch <app> --json --skip-version-check`, then Glider spawns the exe).
   * **an Auth gate**, so only one Legendary process is inside `login()` at a time and launch jumps the queue.
   * **Glider reads the JSON files directly** for all UI state and never writes them.
7. Every Legendary spawn costs **≈1.6–1.8 s** of PyInstaller one-file start-up before any work (`--version` alone: 1.62–1.76 s) [run].
   Never spawn Legendary to render the library.
8. **Library = `metadata/<app_name>.json` (one per owned app, raw catalog item in `.metadata`) + `assets.json` (latest build per
   platform) + `installed.json`.** Glider can and should read these directly at start-up (§2.5).
9. **Key art** in real catalog data: `DieselGameBoxTall` (1200×1600 portrait), `DieselGameBox` (1920×1080 or 2560×1440 landscape),
   plus sometimes `Featured`, `AndroidIcon` [net]. No `Thumbnail`/`OfferImageWide` on catalog items (those are store-offer images).
10. **DLC**: separate `installed.json` entries (`is_dlc: true`, same `install_path` as the base). `--with-dlcs` installs
    DLCs serially in the same process. An entitlement-only DLC has no asset and is never installed. A DLC with an asset whose
    download size is 0 **crashes a fresh install** (`cli.py:1031`) [inferred].
11. **SDL** (selective download): hard-coded for `Fortnite` and `Ginger` only (`utils/selective_dl.py:27-30`). Today 19 more
    app names get definitions from `api.legendary.gl/v1/sdl/<app>.json` [net]. `--install-tag` works for **any** game whose manifest has tags.
    The list of components = `info <app> --json` → `manifest.install_tags` / `tag_disk_size`.
12. **Prerequisites are never run by `-y` installs** and never by `launch`. The `installed` flag is lost on every update
    (`lfs/lgndry.py:359-360`). **Glider must run prereqs itself** (ShellExecuteEx `runas`) and remember them itself.
13. **Cloud-save sync is last-writer-wins on mtime with a 60 s tie window** (`core.py:1115-1143`). There is no conflict detection
    and no backup. A download deletes the matching local files first (`core.py:1202-1217`). An upload result is not checked
    (`core.py:1178`). Glider must do its own 3-way detection and backups and call `sync-saves` only with explicit `--force-*` and `--skip-*` flags.
14. **EOS overlay** is global per Windows user (`HKCU\SOFTWARE\Epic Games\EOS\OverlayPath` plus HKCU Vulkan implicit layers). There is
    no per-game switch.
15. **Ubisoft (new in 0.21)**: `launch <app> --ubisoft` builds `uplay://launch/<GameID>?AUTH_PASSWORD=<exchange code>&…` and opens it
    (`core.py:958-977`, `cli.py:764-788`). `activate --uplay` claims and redeems Uplay keys via GraphQL. EA: `launch --origin` → `link2ea://…`.
16. **Update check for all games, cheaply**: `list-installed --check-updates` = login + **one** assets request per installed
    platform (`cli.py:292-299`). Or compare `assets.json` vs `installed.json` yourself with no network at all.

---

## 1. Concurrency (most critical)

### 1.1 The two file locks

| Lock file | Protects | Acquire | Held for | Contended behaviour | Cite |
|---|---|---|---|---|---|
| `<cfg>/installed.json.lock` | `installed.json` (read-modify-write) | `FileLock.acquire(blocking=False)` in `lock_installed()` | **until process exit** ("We do not care about releasing this lock") | `lock_installed()` returns False. CLI commands log CRITICAL and `return` (exit 0). Background callers log a warning and **skip** the work. | `lfs/lgndry.py:120,322-341` |
| `<cfg>/user.json.lock` | `user.json` (tokens) | `with LockedJSONData(...)` (FileLock, default timeout −1 = wait forever, poll) | the `with` block: the whole `login()` (`core.py:280-282`), or one `userdata` read (`lfs/lgndry.py:157-167`) | blocks until free. No timeout. | `lfs/utils.py:169-208`, `lfs/lgndry.py:147-175` |

* `lock_installed()` **re-reads `installed.json` when it acquires the lock** (`lfs/lgndry.py:333-337`). A writer therefore always
  starts from fresh data, and since writers are exclusive, `installed.json` itself cannot lose updates *between Legendary processes*.
* The lock implementation on Windows is `msvcrt.locking` on the `.lock` file. My emulation with that call made
  `uninstall` fail with the CRITICAL message, so it is compatible [run: `concurrency-v0.21.1/README.md`]. The OS releases it when the process
  dies, so a killed install leaves no stale lock.
* There are **no other locks** in the code base. `grep -n FileLock\|lock_installed\|userdata_lock` matches only the places listed in §1.2.

### 1.2 Who takes which lock (and what else each command writes)

`I` = takes `installed.json.lock` (non-blocking, held to exit). `U` = enters `user.json.lock` (blocking; "login" means for the
whole login). `W:` = unlocked files written.

| Command | I | U | Unlocked writes / notes | Cite |
|---|---|---|---|---|
| `install`/`update`/`repair`/`download` | **always, first thing** | login (again per DLC with `--with-dlcs`) | **full library metadata refresh** (`get_game(update_meta=True)`): `metadata/*.json`, `assets.json`, `entitlements.json`, `aliases.json`. Also `manifests/`, `tmp/<app>.resume`/`.repair`, `tmp/<sdl>.json`, `version.json`, `config.ini` (install_tags/disable_sdl) at exit | `cli.py:845,867,879,971,979`, `core.py:1525,1534` |
| `uninstall` | always | – (offline) | removes manifest-listed files. `get_dlc_for_game` may fetch missing metadata (network) | `cli.py:1188,1210`, `core.py:615-620` |
| `import` | always | login | full metadata refresh + `manifests/` | `cli.py:1349,1366,1371` |
| `move` | always | – | – | `cli.py:2631` |
| `egl-sync` | always | – | EGL `.item`/`.egstore`, `config.ini` | `cli.py:1449` |
| `sync-saves` | **only if** `--save-path`, or save path unset and it computes one (then writes `installed.json`) | login + `userdata` | save files in the game's save dir | `cli.py:503-508,511-541` |
| `list-installed` (text mode) | **only if** some game has `install_size == 0` (then writes `installed.json`) | – (+login with `--check-updates`) | `assets.json` with `--check-updates` | `cli.py:341-345,292-299` |
| any command calling `get_installed_list()`/`get_installed_game()` **when `egl_sync = true`** | tries (silently skips if busy) **and then keeps it to exit** | – | EGL files | `core.py:709-714,725-731,2092-2094` |
| `launch` | **never** | login (unless `--offline`) + `userdata` read (always) | `assets.json`/`entitlements.json` (version check, unless `--skip-version-check`), `tmp/<ns><id>.ovt`, `config.ini` (`--set-defaults`/`--reset-defaults`) | `cli.py:632-688`, `core.py:896,906-912` |
| `verify` | never | – | `tmp/<app>.repair` (read by a later `repair`) | `cli.py:1240-1346` |
| `list`/`list-games` | never | login | metadata refresh (`--force-refresh` = everything) | `cli.py:210-229` |
| `info` (online) | never | login | full metadata refresh + CDN manifest download | `cli.py:1654-1702` |
| `info --offline`, `list-files` (installed) | never | – | – | `cli.py:1684-1686` |
| `status` (online) | never | login | forced `version.json` fetch, metadata refresh | `cli.py:1602-1618` |
| `status --offline` | never | `userdata` read | (metadata fetch for assets lacking metadata, `core.py:615-620`) | `cli.py:1612` |
| `list-saves`, `download-saves`, `clean-saves` | never | login | metadata refresh (`list-saves`); `<install_dir>/.saves/…` (`download-saves`); **deletes cloud files** (`clean-saves`) | `cli.py:432-467` |
| `activate --uplay` / `--origin` | never | login | metadata refresh / library items metadata | `cli.py:2067-2216` |
| `eos-overlay install/update` | **never** | **no** (anonymous `client_credentials` session if not logged in) | overlay files, `overlay_install.json`, `overlay_version.json`, HKCU registry | `core.py:2202-2239`, `cli.py:2373-2421` |
| `eos-overlay enable/disable/remove/info` | never | – | registry (HKCU, HKLM on disable) | `cli.py:2274-2371` |
| `cleanup` | **never** (!) | – | **deletes `tmp/*` (resume/repair files, SDL cache, ovt), non-installed manifests, metadata of apps without assets** | `cli.py:2046-2065`, `lfs/lgndry.py:288-320` |
| `alias` | never | – | `config.ini` | `cli.py:1982-2044` |
| `get-token` | never | login (`--bearer`: forced refresh = `user.json` write) | – | `cli.py:2218-2239` |

Every process also **reads** at start-up `installed.json`, **all** `metadata/*.json`, `aliases.json` and `config.ini`
(`lfs/lgndry.py:99-145`). If `config.ini` lacks the default keys it adds them, marks itself modified and writes `config.ini` at exit (`lfs/lgndry.py:113-118`, `cli.py:3254`).

### 1.3 Experiments with the real exe

See `fixtures/legendary/concurrency-v0.21.1/README.md` for the full tables. Summary [run]:

* Process A sat in `uninstall FakeA` at its `[y/N]` prompt, holding `installed.json.lock`. During that time `-y uninstall FakeB`, `-y import`, `move` and
  `-y egl-sync --one-shot` each printed `[cli] CRITICAL: Failed to acquire installed data lock, only one instance of Legendary may install/import/move applications at a time.`
  and exited **0** immediately. `verify`, `status --offline`, `list-installed` and `launch --offline` proceeded.
* With `user.json.lock` held for 12 s, `status --offline --json` took **10 972 ms** and `launch FakeA --offline --json` took
  **10 927 ms** (both waited). `verify`, `list-installed`, `info --offline` and `uninstall` were unaffected (~2.9 s, six processes in parallel).
* Signed-out or partial-data crashes worth knowing:
  * `list-installed` with no `assets.json` → `TypeError` at `core.py:492`, exit 1.
  * `launch` with no `metadata/<app>.json` → `AttributeError` at `core.py:882`, exit 1. **`launch` needs the metadata file even offline.**

### 1.4 What happens when X runs while a download (`install`/`update`/`repair`) of game G runs

| Concurrent command | Outcome | Cite |
|---|---|---|
| `launch` of **another** game | Works. It never takes the installed lock. It may wait for `user.json.lock` only while G's process is still in its initial `login()` (seconds). Without `--skip-version-check` it rewrites `assets.json`, the same file G's process wrote at its start: a benign race unless they overlap. | §1.2 |
| `launch` of **G itself** | **Must be blocked by Glider.** `installed.json` still holds the *old* version. Without `--skip-version-check` launch fails with `Game is out of date…` (an update in progress) or `not currently installed` (a fresh install). With `--skip-version-check` it **launches a half-patched game**. | `cli.py:614-649`, legendary.md §4.7 |
| `list`/`list --json`/`info`/`status` (metadata refresh) | Runs. **Both processes may rewrite `metadata/*.json`/`assets.json` at the same moment**, because G's process refreshes the whole library at its start (`cli.py:879`). Concurrent `open(...,'w')` from two processes can **interleave and corrupt** a file. The next reader silently treats it as missing, and the next online refresh self-heals it [inferred]. Keep metadata writers serialized (§1.8). | `lfs/lgndry.py:237-239,267-271` |
| `verify` of another game | Works (disk contention only). | `cli.py:1240` |
| `verify` of G | Hashes files mid-write and **writes `tmp/G.repair`**, which a later `repair` trusts (`cli.py:925-940`). Never allow it. | |
| `sync-saves` | Works if the game's `save_path` is already stored. If it is not stored and the lock is busy, the branch is skipped and `check_savegame_state(None, …)` runs, so `os.walk(None)` raises a `TypeError` crash [inferred]. With `--save-path` it logs `Unable to lock install data, cannot modify save path.` and stops. | `cli.py:502-543` |
| `import` / `uninstall` / `move` / `egl-sync` / second `install` (any game) | Refused instantly: CRITICAL + exit 0. | [run] |
| `eos-overlay install` | Runs in parallel (no locks). It competes for bandwidth. It never touches `installed.json`. | `core.py:2202-2239` |
| `activate` | Runs (metadata writer; same caveat as `list`). | |
| `cleanup` | **Destroys the running install's state**: deletes `tmp/G.resume` and the just-saved new manifest `manifests/G_Windows_<new>.manifest` (not yet referenced by `installed.json`). After the install finishes, `installed.json` points at a manifest file that no longer exists, so update patching, `verify` and `uninstall` break (`Could not load old manifest`, `Manifest appears to be missing`). Never run `cleanup` while any Legendary process runs. | `cli.py:2055-2062`, `core.py:1534-1536` |
| Glider editing `config.ini` | **Lost.** G's process loaded `config.ini` at its start and writes it at exit if it changed anything; SDL and `--install-tag` installs always do. If the mtime differs, Legendary *renames Glider's file to `config.<mtime>.ini`* and writes its own stale snapshot. | `lfs/lgndry.py:387-400`, `cli.py:971,979` |

### 1.5 Two installs at once?

No. `install_game` takes the lock before anything else (`cli.py:845-848`). Running a second Legendary with a different
`LEGENDARY_CONFIG_PATH` would split `installed.json` and duplicate `user.json`. Then each copy refreshes the shared refresh token
independently, and token rotation is likely to log one of them out [unverified]. Not viable. Conclusion: **one download at a time**. Parallel
downloads would require Glider's own downloader.

### 1.6 Token refresh races

* `_login` (`core.py:211-278`): if the stored `expires_at` is more than 10 min away it calls `GET /oauth/verify`. That is a read; it mutates
  `lock.data` in place but `_initial_data` is the same object, so **nothing is written** (`lfs/utils.py:181-189`). Otherwise it
  calls the refresh grant and sets `lock.data = userdata`, which **writes `user.json`** on exit.
* `LockedJSONData.__exit__` **releases the lock first and writes second** (`lfs/utils.py:186-195`), with a truncating `open('w')`.
  A second process that is waiting on the lock can therefore wake up and read the *old* or a *partial* `user.json`:
  * Partial: `json.load` raises inside `__enter__`, an unhandled crash [inferred].
  * Old: it sees an expiring token and refreshes with the **old refresh token**. If Epic has already invalidated it
    [unverified], `InvalidCredentialsError` → `lock.clear()` → **`user.json` is deleted**, which logs the user out (`core.py:268-271`).
* The window is microseconds to milliseconds and only exists when the token has under 10 minutes left. It is still real for a launcher that fires
  several online commands at start-up.
* Lock hold time per `login()`: the update check (10 s timeout, only if `version.json` is older than 24 h), the overlay check (if an overlay is installed, 24 h),
  then verify **or** refresh (each 10 s, `-A` to change). That is up to ≈30–40 s worst case on a bad network, ≈0.3–1 s normally [inferred].
* **Glider rule**: allow **one Legendary process inside `login()` at a time** (the "auth gate", §1.8), and warm the token at start-up
  with one serialized online call (`list-installed --check-updates`). After that, normal commands only *verify*, which writes nothing.

### 1.7 What Glider must never do

* Write `installed.json`, `metadata/*`, `assets.json`, `user.json` or `tmp/*`. Read them only, and retry on a JSON parse error, because
  they are rewritten in place.
* Write `config.ini` while any Legendary process is alive. Better: keep Glider-owned settings in Glider's own store and pass them as CLI
  flags. The per-game keys (`start_params`, `.env`, `wrapper`, `override_exe`) only matter if Legendary spawns the game,
  and Glider spawns it itself (§9).
* Enable `egl_sync`. With it on, any `list-installed`/`status`/`launch` grabs `installed.json.lock` until exit
  (`core.py:709-714,2092`) and makes a concurrently queued install fail.
* Run `cleanup` except with no other Legendary process alive and no resumable download pending.
* Launch, verify or save-sync a game that has a Writer-lane job queued or running.

### 1.8 Concrete rule set for Glider's job runner

**Lanes**

| Lane | Concurrency | Commands | Notes |
|---|---|---|---|
| **Writer (W)** | exactly 1 job at a time, priority queue | `install`/`update`/`repair`/`download`, `uninstall`, `import`, `move`, `egl-sync`, `verify` (writes `.repair`), `sync-saves` **when `save_path` is not yet set**, `eos-overlay install/update/remove` (bandwidth plus simplicity), `cleanup` (only when W is idle **and** no other lane runs **and** no resumable job exists) | If Legendary still prints `Failed to acquire installed data lock` (an external Legendary, or an EGL-sync reader), treat the job as "busy, retry in 5 s", not as a failure (exit code is 0). |
| **Metadata (M)** | 1 at a time | `list --json` (`-T`), `list-installed --check-updates`, `info <app> --json` (online), `status --json`, `activate`, `list-saves` | Never start an M job while a W job is in its **pre-download phase**, which runs from spawn until the first `= Progress` line or until it exits. That is when the W job refreshes the same files. Running in parallel with the *download phase* is fine. |
| **Saves (S)** | 1 at a time, per game exclusive with W jobs on that game and with that game running | `sync-saves <app> -y --accept-path`/`--skip-*`/`--force-*` once `save_path` is set, `download-saves`, `clean-saves` | First-time path setup goes to W (it writes `installed.json`). |
| **Launch (L)** | unlimited, highest priority | `launch <app> --json --skip-version-check` (online), or `--offline` | Never touches `installed.json.lock`. Refuse if the app or its base game has a W job. |
| **Read (R)** | in-process, no Legendary | `installed.json`, `metadata/*.json`, `assets.json`, `config.ini`, `overlay_install.json`, `tmp/<app>.resume` (resumable?) | Parse with retry (3× / 100 ms). Treat a missing or corrupt file as "unknown", never as "not installed". |

**Auth gate** (one token for the whole process tree):

* Any command that calls `login()` (all online commands, i.e. everything except `--offline`, `verify`, `uninstall`, `move`,
  `list-installed` without `--check-updates`, `eos-overlay`, `cleanup`) must acquire Glider's in-memory *auth gate* before it is spawned.
* The gate is released when the process prints its first log line after the login lines (`[Core] INFO: Trying to re-use existing login session...`,
  `[Core] INFO: Logging in...`, `Falling back to using refresh token...`), or exits, or after 45 s.
* **L jobs pre-empt the queue**: a launch request waits for at most the one login in progress.
* At app start, run `list-installed --check-updates --json` once (through the gate) to warm `user.json`, `version.json` and `assets.json`.

**How launch never waits behind a download**

1. A download holds `installed.json.lock`, which launch never takes.
2. The download is inside `login()` only during its first seconds, and the gate stops a launch from colliding with that phase or makes the launch go first.
3. `--skip-version-check` removes the asset refresh. Glider has already decided "up to date" from `assets.json`.
4. Glider spawns the exe itself from the JSON (§9), so the launched game is not a child of Legendary's job object.

The residual unavoidable cost is ≈1.7 s spawn + login verify (1 HTTPS) + exchange code (1 HTTPS), plus the ownership token for DRM titles.

**Error handling the runner needs**: exit code 0 does not mean success (legendary.md §1.6). Use the CRITICAL lock message → retry.
Use `Finished installation process in` + the new version in `installed.json` → success. Kill the whole tree with a Job Object (legendary.md §4.8).

---

## 2. Library listing — fastest path

### 2.1 Commands and flags

`list [--platform P] [--include-ue] [-T|--third-party|--include-non-installable] [--csv|--tsv|--json] [--force-refresh]`
(`cli.py:2956-2967`; `list-games` is a hidden alias, `cli.py:2761`). There is **no `--include-dlc` flag**. DLC always appear in
`--json` under each game's `dlcs` array and in text mode as `+` lines (`cli.py:249-256,284-287`). Flow (`cli.py:210-256`):

1. `login()` (required; exit 1 on failure).
2. `get_game_and_dlc_list(platform, skip_ue=not --include-ue, force_refresh)` (`core.py:523-631`):
   * `get_assets(update_assets=True)` for `{Windows, --platform, every installed platform}`. That is **one `GET launcher/api/public/assets/<platform>?label=Live` per platform**,
     plus `entitlements` (paged by 1000) only if the asset list changed (`core.py:464-489`).
   * For each asset (UE namespace skipped unless `--include-ue`), metadata is re-fetched when: no `metadata/<app>.json`, **or**
     `--force-refresh`, **or** the build version changed for any platform, **or** the sidecar revision changed, **or** `achievements` are missing (`core.py:555-570`).
   * A fetch is `catalog bulk/items` (+ `assets/v2/...` manifest API when the sidecar changed) + **one GraphQL achievements query** (new in
     0.21, `core.py:572-598`). With more than 5 to fetch it uses a 16-thread pool with a 60 s `map` timeout. Failures are retried serially, and a second
     failure is an uncaught exception, which crashes `list` (`core.py:600-620`) [inferred].
   * The result keeps `platform in app_assets` items only. DLC are grouped by `metadata.mainGameItem.id`. Items in category `mods`
     are dropped (`core.py:622-625`).
   * Aliases are regenerated. `_prune_metadata` deletes metadata of apps that lost their asset, except installed or third-party ones (`core.py:627-650`).
3. `-T`: also `get_non_asset_library_items()`. That is the paged `library-service …/library/api/public/items` call, and it fetches catalog
   metadata only for items without a cached file (or all with `force_refresh`, which `list` does not pass here: `cli.py:228`), skipping `sandboxType == 'PRIVATE'` (`core.py:652-692`).
   These are the EA/Ubisoft-managed titles and anything else without an installable asset.

**Partial refresh**: there is no per-game metadata refresh. Every `update_meta=True` path (install, import, info online,
status, list-saves) runs the *whole-library* check above. That check is cheap when nothing changed (1 call per platform) and slow on the first
run for a big library: 2–3 requests per game.

### 2.2 On-disk cache (`LEGENDARY_CONFIG_PATH`)

| File | Shape | Written when | Cite |
|---|---|---|---|
| `metadata/<app_name>.json` | `Game.__dict__`: `{app_name, app_title, asset_infos:{<Platform>: GameAsset}, base_urls:[], metadata:<raw catalog item>, sidecar:{config,rev}\|null, achievements:{…}\|null}` (indent 2, sorted keys) | metadata refresh, `prepare_download` (base_urls), `import` | `models/game.py:203-211`, `lfs/lgndry.py:267-271` |
| `assets.json` | `{"<Platform>": [GameAsset…]}`, where GameAsset = `{app_name, asset_id, build_version, catalog_item_id, label_name, namespace, metadata:{…}, sidecar_rev}` | any assets refresh **if changed** | `models/game.py:8-46`, `lfs/lgndry.py:231-239` |
| `entitlements.json` | raw EGS entitlement list (`entitlementName`, `namespace`, `grantDate`, …) | assets changed or file missing | `core.py:480-483` |
| `installed.json` | `{"<app_name>": InstalledGame}` (fields in legendary.md §3.5) | W lane commands | `lfs/lgndry.py:355-379` |
| `achievements.json` | per-namespace user achievement records | `achievements` command | `core.py:345-357` |
| `aliases.json` | `{app_name: [aliases]}` | metadata refresh | `lfs/lgndry.py:479-511` |

A sanitized `metadata/<app>.json` built from a **real** catalog response (`catalog-v0.21.1/catalog-df37f065…-4106….json`;
`asset_infos`/`base_urls` values are placeholders):

```json
{
  "app_name": "8935bb3e1420443a9789fe01758039a5",
  "app_title": "Alien: Isolation",
  "asset_infos": {"Windows": {"app_name": "8935bb3e1420443a9789fe01758039a5", "asset_id": "<id>", "build_version": "<build>",
                  "catalog_item_id": "4106b71233fa4b4ba3b04c818fce91d4", "label_name": "Live-Windows",
                  "namespace": "df37f065c3f14eadbf011177396e2966", "metadata": {}, "sidecar_rev": 0}},
  "base_urls": ["https://<cdn>/Builds/Org/<org>/<build>/default"],
  "sidecar": null,
  "achievements": {"namespace": "…", "total_achievements": 0, "total_product_xp": 0, "achievement_sets": [], "platinum_rarity": {}, "achievements": []},
  "metadata": {
    "id": "4106b71233fa4b4ba3b04c818fce91d4", "namespace": "df37f065c3f14eadbf011177396e2966", "title": "Alien: Isolation",
    "categories": [{"path": "public"}, {"path": "games"}, {"path": "applications"}],
    "customAttributes": {
      "CanRunOffline": {"type": "STRING", "value": "true"},
      "CloudSaveFolder": {"type": "STRING", "value": "{AppData}/The Creative Assembly/Alien Isolation/"},
      "FolderName": {"type": "STRING", "value": "AlienIsolation"}, "…": "…"},
    "keyImages": [
      {"type": "DieselGameBox", "width": 1920, "height": 1080, "md5": "1cf3…", "size": 620728, "uploadedDate": "2020-12-17T17:18:35.389Z",
       "url": "https://cdn1.epicgames.com/df37…/item/EGS_AlienIsolation_CreativeAssembly_S5-1920x1080-1cf3….jpg"},
      {"type": "DieselGameBoxTall", "width": 1200, "height": 1600, "url": "https://cdn1.epicgames.com/…-1200x1600-af00….jpg", "…": "…"}],
    "releaseInfo": [{"appId": "8935bb3e1420443a9789fe01758039a5", "id": "2f50…", "platform": ["Windows"], "dateAdded": "2020-11-16T00:00:00.000Z"}],
    "dlcItemList": [{"id": "57cd8d4cb4524152a57192555063334a", "title": "Alien: Isolation - Last Survivor",
                     "entitlementName": "57cd8d4cb4524152a57192555063334a",
                     "categories": [{"path": "public"}, {"path": "addons"}, {"path": "applications"}],
                     "releaseInfo": [{"appId": "6b61b3692dae45be889bf38cb1f9ed84", "platform": ["Windows"], "…": "…"}], "…": "…"}],
    "creationDate": "…", "lastModifiedDate": "…", "description": "…", "developer": "…", "entitlementType": "EXECUTABLE", "…": "…"
  }
}
```

A DLC's own metadata file has `metadata.mainGameItem` (`{id, namespace, title, releaseInfo:[{appId,…}], …}`), which is what
`is_dlc` keys on (`models/game.py:119-121`).

### 2.3 Where each field lives (Game model for Glider)

| Concept | Field | Cite / evidence |
|---|---|---|
| ID used by every command | `app_name` (= `releaseInfo[].appId`, often a 32-hex id, e.g. `8935bb3e…`; Fortnite = `Fortnite`) | [net] |
| Title | `app_title` (= catalog `title`) | `core.py:593` |
| Namespace / catalog id | `metadata.namespace` / `metadata.id` | `models/game.py:169-179` |
| Latest version per platform | `asset_infos.<P>.build_version` (also `assets.json`) | `models/game.py:114-117` |
| Is DLC / parent | `'mainGameItem' in metadata`; parent app = `metadata.mainGameItem.releaseInfo[0].appId` | `models/game.py:119-121`, `cli.py:909` |
| DLC of a game | `metadata.dlcItemList[]` (`id`, `title`, `entitlementName`, `releaseInfo[0].appId` if installable, `categories`) | [net] |
| Launchable add-on (e.g. Fortnite modes) | DLC with category `addons/launchable` | `models/game.py:163-167`; Fortnite's 12 DLC [net] |
| Hidden content DLC | category `hidden` | Fortnite `…Content` entries [net] |
| Mods (excluded) | category `mods` | `core.py:624` |
| Cloud saves | `customAttributes.CloudSaveFolder` (Win) / `CloudSaveFolder_MAC`; filters `CloudIncludeList` / `CloudExcludeList` (comma lists); also `MaxSizeMB` seen | `models/game.py:149-155`, `core.py:1155-1158` |
| Offline | `customAttributes.CanRunOffline.value == 'true'`, **defaulting to `'true'` when absent** (Fortnite has none) | `core.py:1677` |
| DRM token | `customAttributes.OwnershipToken.value == 'true'` | `core.py:1678` |
| Third-party store | `customAttributes.ThirdPartyManagedApp.value` **or** `ThirdPartyManagedProvider.value` (`Origin`, `the EA app`, `UbisoftConnect`, …) | `models/game.py:131-135` |
| Partner account link | `customAttributes.partnerLinkType.value` (`ubisoft`, …), `partnerLinkId` | `models/game.py:137-147` |
| Extra launch options | `customAttributes.extraLaunchOption_NNN_Name/_Args` | `cli.py:1736-1754` |
| Extra cmd line | `customAttributes.AdditionalCommandLine` (note: the EA/Ubi URI builders read `AdditionalCommandline` with a lowercase l, `core.py:952,973`) | `models/game.py:157-161` |
| Install folder name | `customAttributes.FolderName.value` (Fortnite = `Fortnite` [net]) | `core.py:1566-1572` |
| Install size | **not in metadata**. Only after a manifest: `info --json` → `manifest.disk_size`/`download_size`/`tag_*`, or `installed.json` `install_size` | legendary.md §3.4 |
| Grant (purchase) date | `entitlements.json` (`namespace` match) → `grantDate`, else `metadata.creationDate` | `cli.py:1712-1714` |
| Key art | `metadata.keyImages[]` `{type,url,width,height,md5,size,uploadedDate}` | [net] |

**Key art types observed on catalog items** [net]:
* Fortnite: `AndroidIcon` 512×512, `Featured` 800×800, `DieselGameBoxTall` 1200×1600, `DieselGameBox` 2560×1440.
* Alien: Isolation and Castlevania: `DieselGameBox` 1920×1080, `DieselGameBoxTall` 1200×1600.
* Use `DieselGameBoxTall` for grid tiles and `DieselGameBox` for hero/backdrop. Fall back to the other one, then to a generated placeholder.
* Images are on `cdn1.epicgames.com`. EGL-style resizing via `?h=…&w=…&resize=1` query parameters is [unverified].
* Some items (e.g. age-rating helper items) have no images at all [net].

### 2.4 Third-party, non-installable, and offline behaviour

* Third-party-managed games have no asset, so they appear only with `list -T` (via library items), and `install` refuses them:
  `The selected title has to be installed via a third-party store: <store>`, **exit 0** (`cli.py:886-891`).
* `partnerLinkType=ubisoft` games *do* have assets and install normally. They then need Ubisoft Connect and a one-time `activate --uplay` (§8).
* Offline: `list` cannot run offline (login required). **Glider's offline library = the cached files**.

### 2.5 Can Glider read the JSON directly instead of spawning Legendary?

**Yes, and it should**: it avoids ≈1.7 s per spawn. Legendary itself does the same at every start (`lfs/lgndry.py:122-145`).
Recipe:

1. Read `installed.json` (dict by app_name).
2. Read every `metadata/*.json` (each contains `app_name`).
3. Read `assets.json` for the latest builds. **Update available** = `assets[platform][app].build_version != installed[app].version`, which is exactly Legendary's
   own test (`cli.py:647`, `core.py:1370`).
4. The visible library = metadata entries that are not DLC, not `mods`, and (have an asset for Windows **or** have a third-party store). Group DLC under
   `mainGameItem.releaseInfo[0].appId` / `mainGameItem.id`.

Refresh in the background with one M-lane `list --json` (or `list -T --json`) and re-read the files when it exits. Don't parse
its stdout: `list --json` also dumps achievements, so it is large. Tolerate partial files (§1.7).

---

## 3. DLC

* **Install a DLC**: `install <dlc_app_name>`.
  * It requires the base game to be installed (`Base game "<x>" is not installed!`, exit 1) unless the command is `download`/`--no-install` (`cli.py:907-916`).
  * Path = the base game's `install_path` (`core.py:1560-1561`).
  * SDL is disabled for DLC (`cli.py:943`). Its own manifest, resume file and `installed.json` entry use `is_dlc: true` (`core.py:1698`).
* **`--with-dlcs` / `--skip-dlcs`** (`cli.py:2886-2889`). After a successful base install:
  * `dlcs = get_dlc_for_game()`, i.e. DLC that **have an asset for the platform** (`core.py:622-623,694-704`). Entitlement-only DLC are never in this list.
  * Default with `-y` = install them. Without `-y`, a prompt appears unless `--with-dlcs`/`--skip-dlcs` is given.
  * Install is **recursive in the same process** (`cli.py:1118-1124`). Each DLC repeats login + whole-library metadata check + manifest + analysis.
  * Hazards [inferred]:
    * a DLC whose analysis says download size 0 hits `exit(0)` (`cli.py:1040`), which **ends the whole process**, so later DLC and the base game's
      post-install messages never run;
    * for a *fresh* DLC, `old_igame` is `None` and `old_igame.install_tags` (`cli.py:1031`) raises `AttributeError`;
    * the base game's `args.install_tag` carries over to the DLC call (it is the same `args` object), so a DLC install inherits the base game's tag filter.
  * **Glider: always pass `--skip-dlcs`, then install each wanted DLC as its own W-lane job.**
* **Uninstall**: `uninstall <dlc>` deletes only the files in that DLC's manifest (`delete_root_directory=False`) (`cli.py:1217-1218`, `core.py:1843-1864`).
  Uninstalling the base game first uninstalls every installed DLC listed by `get_dlc_for_game` (`cli.py:1208-1214`).
* **In `installed.json`**: its own key (DLC app_name), `is_dlc: true`, `install_path` = base path, `executable` = the DLC manifest's `launch_exe` (usually empty).
  `launch <dlc>` with no exe → `… is DLC without an executable; please launch the base game instead!` (exit 1, `cli.py:618-620`). A *launchable add-on*
  that is not installed is launched as the base game with the add-on's metadata (`cli.py:606-612`, `core.py:840`).
* **Entitlement-only DLC**: `info <game> --json` → `game.owned_dlc[]` = `{app_name, title, installable, id}`. Owned via entitlement ⇒ `app_name: null`
  even when `installable` (the releaseInfo list) is present: a quirk at `cli.py:1767`. Owned via asset ⇒ `app_name` set. `installable: null` ⇒ "no installation
  required" (`cli.py:1759-1787`). Glider should compute ownership itself: `entitlements.json` `entitlementName` ∈ `dlcItemList[].entitlementName`,
  and installable iff `releaseInfo` exists **and** the appId is in `assets.json`.
* Import: `import <game> <dir>` also offers to import DLC (same recursion; `--with-dlcs`/`--skip-dlcs`) (`cli.py:1431-1444`). A DLC import needs the base game
  installed (`cli.py:1376-1387`).

---

## 4. Install tags / selective download

* A manifest file entry carries `install_tags` (a list; empty = always installed).
* `--install-tag <tag>` (repeatable) keeps files that have any listed tag, or untagged files when `""` is listed
  (`downloader/mp/manager.py:172-186`). Skipped files are *also* queued for deletion (`TaskFlags.DELETE_FILE | SILENT`), so shrinking the tag set deletes the deselected files.
* For non-DLC, non-`--no-install` installs the tags are saved to `config.ini [<app>] install_tags` (`cli.py:976-979`), and `verify` uses them (`cli.py:1281-1289`).
* **SDL definitions** (`cli.py:942-986`, `core.py:434-457`):
  * Hard-coded: `Fortnite`, `Ginger` (Cyberpunk 2077) (`utils/selective_dl.py:27-30`).
  * At login, every key in `version.json → game_overrides.sdl_config` is added as a placeholder (`core.py:322-328`), and its JSON is downloaded from
    `https://api.legendary.gl/v1/sdl/<app>.json` when the config version is newer than the cache `tmp/<app>.json`.
  * Today (2026-11-14) that is 49 keys: 19 non-UE (`Fortnite` = version 14, `Fortnite_Mac`, `Ginger`, `Lavender`, `Suphalak` and 14 hex app names) plus 30 `UE_*` [net: `sdl-v0.21.1/`].
  * Shapes: `{"<pack>": {"name", "description", "tags": [...]}, "__required"?: {...}}`. Most are voice/language packs; one is `hd_textures`. Only `Fortnite`,
    `Fortnite_Mac` and `a1e2ce30…` have `__required`.
  * App matching is `app_name.startswith(key)` (`utils/selective_dl.py:33-40`).
  * When SDL applies (first install, or `--reset-sdl`, or no tags in config), the user is prompted. **`--skip-sdl`** = `[''] + __required.tags`, which for a game without `__required`
    means **only untagged files** (`cli.py:965-969`). On a game whose SDL JSON cannot be downloaded: `Unable to get SDL data for <app>`, and the install proceeds with **all files** (`cli.py:972-973`).
  * `--disable-sdl` stores `disable_sdl = true` and installs everything. `--reset-sdl` forgets stored tags and re-asks (`cli.py:944-960,980-983`).
* **Which games support it**: SDL prompts only for the apps above. Any other game whose manifest has tags supports `--install-tag`, but has no human names
  for its tags.
* **Listing components for a game**:
  * Tag names + sizes: `info <app> --json` (online, downloads the manifest) → `manifest.install_tags` (sorted, includes `""`), `tag_disk_size`/`tag_download_size` = `[{tag,size,count}]`
    (legendary.md §3.4). Offline after install: `info <app> --offline --json`.
  * Pack names: fetch `api.legendary.gl/v1/sdl/<app>.json` (or read `<cfg>/tmp/<app>.json` once cached). Pack size = Σ tag sizes.
  * Per-file: `list-files <app> --json` (`install_tags` per file).
* **Changing components later** = re-run `install <app> --install-tag … [--install-tag ""]`. Missing files are downloaded ("Found N missing files", because the resume branch
  checks existence, `manager.py:154-170`). Deselected files are deleted at the end (`cli.py:1148-1153` → `core.py:1866-1883`), or right away when nothing needs downloading (`cli.py:1024-1029`).

---

## 5. Prerequisites

* **Manifest**: `meta.prereq_ids` (list of GUID strings), `prereq_name`, `prereq_path` (relative to the install dir), `prereq_args` (a single string). Encrypted
  manifests carry them in the encrypted blob (`models/manifest.py:111-117`).
* `prepare_download` copies them into `installed.json prereq_info = {ids, name, path, args}` (`core.py:1667-1670`). `info --json` shows them as
  `manifest.prerequisites` (`cli.py:1859-1866`).
* **`install`**: after `install_game`, if `prereq_info` exists and lacks `installed: true`, `_handle_postinstall` prints the block
  (legendary.md §4.2 row 14) and then (`cli.py:1157-1185`):
  * with `-y` → **does nothing** (not run, not marked);
  * interactive `y` → marks it installed *first*, then runs `Popen([fullpath, args], cwd=<install>/<dir of path>, shell=True).wait()` and ignores the exit code.
    The list form quotes `args` as **one argument**, so `"/install /quiet /norestart"` is passed as a single token [inferred, `subprocess.list2cmdline`];
  * `i` → mark only.
* **`launch` never looks at prerequisites** (no reference in `cli.py:594-744`/`core.py:830-936`).
* **The `installed` flag does not survive an update**: `set_installed_game` does `dict.update(new.__dict__)` and the new `prereq_info` dict has no `installed` key
  (`lfs/lgndry.py:359-360`, `core.py:1667-1670`). Legendary re-reports prereqs after every update.
* **Glider plan**:
  * After a W-lane install or update finishes, read `prereq_info` from `installed.json`.
  * If its `ids` set is not already in Glider's own *done* store, run
    `ShellExecuteEx(verb="runas", file=<install_path>\<path>, params=<args verbatim>, dir=<its folder>, SEE_MASK_NOCLOSEPROCESS)` and wait.
  * Record `{ids, path, exitCode, at}` per app, and the ids globally: the same id means the same redistributable across games [unverified].
  * Treat exit 0/3010/1638 as done (common VC++/MSI codes [unverified]).
  * Never drive Legendary's prompt.

---

## 6. Cloud saves

**Commands** (`fixtures/legendary/help-v0.21.1/{sync-saves,list-saves,download-saves,clean-saves}.txt`). All need login.
* `sync-saves [<app>] [--skip-upload] [--skip-download] [--force-upload] [--force-download] [--save-path <p>] [--disable-filters]`, plus a hidden `--accept-path` (`cli.py:2994-3007`).
  `--skip-upload` sets `download_only`, `--skip-download` sets `upload_only`.
* `list-saves [<app>]` (also refreshes library metadata).
* `download-saves [<app>]` writes every remote save to `<install_dir>/.saves/<app>/<YYYY.MM.DD-HH.MM.SS>/` (default `~/Games/.saves`; no CLI override, `core.py:1182-1200`).
* `clean-saves [<app>] [--delete-incomplete]` **deletes** cloud manifests with missing chunks (only when all are missing, or with the flag) and orphaned chunks (`core.py:1282-1357`).

**Save path resolution** (`core.py:991-1113`):
* Template = `customAttributes.CloudSaveFolder` (Windows) / `CloudSaveFolder_MAC`. Backslashes become `/`, then each `/`-separated component is matched case-insensitively against these variables:
  * `{installdir}` = install path;
  * `{epicid}` = the account id (from `userdata`, so it takes `user.json.lock`);
  * `{appdata}` = **`%LOCALAPPDATA%`** (Epic's `{AppData}` is *Local*);
  * `{userdir}` = `%USERPROFILE%/Documents`;
  * `{userprofile}`;
  * `{usersavedgames}` = `%USERPROFILE%/Saved Games`.
* The result goes through `os.path.realpath`, so `{AppData}/../Roaming/…` works (Castlevania [net]).
* Variables not in the table stay literal. `sync-saves` then warns `Path contains unprocessed variables` and asks for a path.
* Wine/CrossOver logic applies to non-Windows only.
* The computed path is stored once in `installed.json save_path` (W lane). `install --save-path` can preset it (`cli.py:1098-1099`).

**Decision logic** (`cli.py:484-592`, `core.py:1115-1143`):
* Remote = the **latest** manifest per app by its timestamp name.
* Local = **max mtime of every file under the save dir**. Include/exclude filters are *not* applied here, so a touched log or config file makes local "newer".
* Results:
  * no local and no remote → `NO_SAVE`;
  * remote only → `REMOTE_NEWER`;
  * local only → `LOCAL_NEWER`;
  * `|local−remote| < 60 s` → `SAME_AGE` (skip unless forced);
  * otherwise newer wins.
* Local time conversion uses a timezone offset fixed at process start (`core.py:102,1126`), so files from the other DST period are off by 1 h [inferred].
* Actions:
  * `REMOTE_NEWER` (or `--force-download`) → `download_saves(clean_dir=True)` **deletes all local files matching the filters, then writes** the remote files and sets their mtime to the manifest time (`core.py:1202-1217,1260-1278`);
  * `LOCAL_NEWER` (or `--force-upload`) → packages filtered files into 1 MiB chunks + manifest `manifests/<local mtime UTC>.manifest` and PUTs them; **the PUT status is never checked** (`core.py:1172-1178`).
* With `-y`, both directions run without asking. Without `-y`, prompts `Download cloud save?` / `Upload local save?`. `-y` and no stored path → `skipping due to --yes` unless `--accept-path` (`cli.py:512-514`).
* **Conflicts**: none are detected. There is no last-sync bookkeeping, so "both changed since last sync" is resolved by timestamp. The loser is **overwritten
  locally with no backup**, or remains only as an older remote manifest (recoverable via `download-saves`). Legendary *can* overwrite a newer save:
  * with `--force-*`;
  * with clock skew between machines;
  * when the game rewrites saves keeping old mtimes;
  * when an unrelated file in the folder bumps the local mtime (an upload of stale saves);
  * across a DST boundary;
  * when an upload silently fails.
* **Output**: stderr log lines only, all human text, dates printed UTC without a marker:
  * `Got N remote save game(s)`
  * `Checking "<title>" (<app>)`
  * `Computed save path: "<p>"`
  * `Save game for "<title>" is up to date, skipping...`
  * `Cloud save for "<title>" is newer:` / `- Cloud save date: %Y-%m-%d %H:%M:%S` / `- Local save date: …|N/A`
  * `Local save for "<title>" is newer`
  * `Downloading remote savegame...`
  * `Uploading local savegame...` / `Finished uploading savegame.`
  * `No files to upload. If you believe this is incorrect run command with "--disable-filters"`
  * `Successfully completed savegame download.`
  * There is no JSON.

**What Glider needs for safe conflict UI and backups**:
1. Resolve the save dir itself (same table as above, from `metadata`) or read `installed.json save_path`.
2. Keep a per-game sync ledger: last remote manifest name + local file list with sha1 and mtimes after each successful sync.
3. Before a sync, list remote manifests: `list-saves <app>` text (parse `+ <name>` lines) or `GET datastorage …/savesync/<account>/<app>/`, which needs a token, so prefer `list-saves`.
4. Decide 3-way: local changed vs ledger, and remote changed vs ledger.
5. **Always snapshot the local save dir** to Glider's backup store before any download.
6. Execute exactly one directional command: `sync-saves <app> -y --skip-upload --force-download` or `sync-saves <app> -y --skip-download --force-upload`.
   Never use bare `sync-saves`. Pass `--disable-filters` only if the user asks.
7. Verify an upload by re-listing and checking that a manifest named after the local mtime exists (the PUT is unchecked).
8. Never sync while the game runs, or while a W job touches the game.

---

## 7. EOS overlay

`eos-overlay <install|update|remove|enable|disable|info> [--path P]` on Windows. `--prefix`/`--bottle`/`--app` are hidden no-ops on Windows
(`cli.py:3071-3093`). App ids: overlay `98bc04bc842e4906993fd6d6644ffb8d` (namespace `302e5ede…`, catalog `cc15684f…`); EOSH service
`c9e2eb99…` (`lfs/eos.py:21-29`).

| Action | Exact behaviour | Cite |
|---|---|---|
| `info` | Prints the Legendary-managed version and path (`overlay_install.json`), then the found installs: Legendary's own, `%ProgramFiles(x86)%\Epic Games\Launcher\Portal\Extras\Overlay`, `%ProgramFiles(x86)%\Epic Games\Epic Online Services\managedArtifacts\98bc…`, and the registry path. Then `Overlay enabled: Yes` iff HKCU `OverlayPath` points at a dir containing `EOSOVH-Win64-Shipping.dll`, and the Vulkan layers whose value names contain `EOS` from HKCU + HKLM (64 and 32-bit views). Read-only, offline. | `cli.py:2274-2306`, `core.py:2163-2200` [run: `concurrency-v0.21.1/eos-overlay-info.stderr.txt`] |
| `install` | Anonymous `client_credentials` session (no user login, no `user.json.lock`). CDN manifest for the overlay. `DLManager` with **no resume file** into `--path` or `<install_dir>\.overlay` (`~/Games/.overlay`). Free-space check is ineffective: `min(0, …)` ⇒ ≤0 (`core.py:2221`). Prompt unless `-y`. Then writes `overlay_install.json` and **registry**: removes old entries if the path differs, then sets `HKCU\SOFTWARE\Epic Games\EOS` `OverlayPath` (REG_SZ, forward slashes) and `HKCU\SOFTWARE\Khronos\Vulkan\ImplicitLayers` values `<path>\EOSOverlayVkLayer-Win32.json` and `…-Win64.json` = DWORD 0. Takes no installed lock. | `core.py:2202-2239`, `cli.py:2373-2421`, `lfs/eos.py:84-96` |
| `update` | Same as install; refuses if nothing is installed. `Installed version is up to date, nothing to do.` if the build matches. | `cli.py:2374-2383` |
| `enable [--path]` | Path = managed install, else the first found install. Must contain the DLL. Replaces existing entries and writes the same HKCU values. | `cli.py:2308-2335` |
| `disable` | Removes HKCU `OverlayPath`, HKCU `EOS` Vulkan layer values, **and HKLM ones (64 and 32-bit)**. HKLM needs admin. `remove_registry_value` only catches `FileNotFoundError` on `OpenKey`, so an unelevated run on a machine with HKLM EOS layers (present on this PC, set by the EOS service) should raise `PermissionError` (traceback) [inferred, not run because it modifies the registry]. | `lfs/eos.py:130-143`, `lfs/windows_helpers.py:52-66` |
| `remove` | Deletes the managed folder recursively + `overlay_install.json`, then registry entries as in disable. Prompt unless `-y`. | `core.py:2241-2245`, `cli.py:2348-2371` |

* **Per-game enable**: none. It is global per user. The game's EOS SDK reads `OverlayPath` [unverified mechanism].
* **Prefix vs Windows**: on Linux/macOS the same value is written into the prefix's `user.reg` as text (`lfs/eos.py:97-125`). Irrelevant for Glider.
* Every `login()` also runs an overlay update check when a managed overlay exists (24 h cache) and prints `EOS Overlay update available…` on stdout at exit
  in non-JSON mode (`core.py:240-244,2142-2157`, `cli.py:3248-3252`).
* **Glider**: prefer `enable --path <EOSH managedArtifacts dir>` if that exists, otherwise a W-lane `-y eos-overlay install`. Run `disable` elevated or not at all.

---

## 8. Third-party stores

* **Detection** (from metadata):
  * `third_party_store` = `ThirdPartyManagedApp` or `ThirdPartyManagedProvider` value (`models/game.py:131-135`).
  * `is_origin_game` ⇔ value lower ∈ {`origin`, `the ea app`}; `is_ubisoft_game` ⇔ value lower == `ubisoftconnect` (`models/game.py:123-129`).
  * `partnerLinkType` (e.g. `ubisoft`) marks games that install from Epic but need a linked account.
  * The `install` requirements check also flags Uplay when every shipped `.exe` contains `uplay`, the prereq path mentions uplay/ubisoft, or `executable == UplayLaunch.exe`. It warns
    `This game requires installation of Uplay/Ubisoft Connect…`. Any other `partnerLinkType` gets `This game requires linking to "<x>", this is currently unsupported` (`core.py:1749-1780`).
* **`activate --uplay`** (`cli.py:2072-2168`):
  * login, then `externalAuths` of the account. If there is no `ubisoft` link it opens `https://www.epicgames.com/id/link/ubisoft` and stops.
  * GraphQL `accountUplayCodes` → redeemed set. Candidates are owned games and owned DLC (entitlements) with `partnerLinkType == 'ubisoft'` whose `partnerLinkId` is not redeemed.
  * Confirmation (skipped by `-y`), then for each `claim` (GraphQL) and finally `redeem`. Errors come back as `Claiming "<t>" failed with: …` / `Redeeming codes (partially) failed with: …`.
  * Ubisoft Connect does not need to be installed.
* **`activate --origin`**: lists non-asset library items that are EA games and opens `link2ea://launchgame/<app>?…` for each via `webbrowser.open`.
  It **always prompts, ignoring `-y`** (`cli.py:2196,2211`). It cannot tell which are already activated (`cli.py:2169-2216`).
* **0.21 Ubisoft protocol support** (commit `46f8445`, #750) is **launch only**:
  * The commit adds the `launch --ubisoft` flag (`cli.py:2919`) and renames `_launch_origin` to `_launch_third_party` (`cli.py:603-604,746`).
  * That requires `is_ubisoft_game`, then builds `uplay://launch/<customAttributes.GameID or app_name>?AUTH_PASSWORD=<exchange code or 0 offline>&AUTH_TYPE=exchangecode&epicusername=…&epicuserid=…&epiclocale=…[&<AdditionalCommandline pairs>]` (`core.py:958-977`).
  * On Windows it runs the optional pre-launch command, then `webbrowser.open(uri)`. `--json` prints `{"uri": "…"}` (`cli.py:764-789`).
  * EA is the same with `link2ea://launchgame/<app>?…` (`core.py:938-956`). 0.21 also sets cwd for EA launches under Wine (`1be4b7e`, non-Windows only).
* **Launching such games from Glider**: call `launch <app> --origin|--ubisoft --json` (login + exchange code), then `ShellExecute(uri)`. Ubisoft Connect or the EA app must be installed
  and registered for the protocol. The URI contains a live exchange code, so treat it as a secret. Glider cannot track the game process (it belongs to the other launcher).

---

## 9. Launch

* **`--json` output fields** (`models/game.py:296-314`, printed at `cli.py:687-688` before any spawn and also before `--dry-run`):
  `game_parameters` (manifest `launch_command` split + catalog `AdditionalCommandLine`), `game_executable` (relative, `/`), `game_directory` (install path),
  `egl_parameters`, `launch_command` (wrapper argv), `working_directory` (**directory of the exe**, `core.py:860`), `user_parameters` (extra CLI args + config `start_params`),
  `environment` (`[default.env]` + `[<app>.env]` only), `pre_launch_command`, `pre_launch_wait`.
  Real-format sample: `concurrency-v0.21.1/out2-launch-offline-json.stdout.txt` [run].
* **Full argv** as Legendary builds it (`cli.py:694-701`): `launch_command…`, `[$LEGENDARY_WRAPPER_EXE]` (new in 0.21, `ece601b`), `game_directory\game_executable`, `game_parameters…`, `user_parameters…`, `egl_parameters…`.
* Spawn: `subprocess.Popen(argv, cwd=working_directory, env=os.environ+environment, shell=True)` on Windows (`cli.py:744`). `shell=True` was added in 0.21 (#668, "Display UAC prompt for games which requires admin rights").
  * `cmd.exe` falls back to ShellExecute for `requireAdministrator` executables [unverified].
  * `cmd` metacharacters in arguments (`&`, `^`, `%`), e.g. in a display name, are interpreted by `cmd` [inferred].
* **Glider spawning the exe itself**:
  * Use `CreateProcessW` with the argv from `--json` (quote per MSVCRT rules) and cwd = `working_directory`.
  * Use env = Glider's env + `environment`. Run `pre_launch_command` first if non-empty.
  * On `ERROR_ELEVATION_REQUIRED` (740), retry with `ShellExecuteEx runas` (that loses `env`).
  * This gives Glider the PID for "running" state, play time and stop. Keep the game out of any kill-on-close job object.
* **Auth**:
  * Online launch = `login()` (verify, or refresh if under 10 min left), then `GET /account/api/oauth/exchange`. That gives a fresh one-time **exchange code**, passed as `-AUTH_PASSWORD=` (`core.py:889-892`).
  * If `requires_ot`: `POST …/ownershipToken`, written to `tmp/<ns><catalogId>.ovt`, `-epicovt=<path>` (`core.py:906-912`).
  * If the sidecar has a `deploymentId`: `-epicdeploymentid=` (`core.py:926-928`).
  * Exchange-code lifetime is short (minutes) [unverified], so spawn immediately after the JSON arrives.
* **Offline**:
  * `--offline` (or config `[<app>] offline = true`, `core.py:1359-1360`) skips login, the version check, the exchange and the ovt (`-AUTH_PASSWORD=` empty).
  * It still reads `userdata` for name and id, which takes the user lock (`core.py:896-897`).
  * Eligibility shown to the user: `installed.json can_run_offline` (from `CanRunOffline`, default true). Non-eligible games just log `Game is not approved for offline use and may not work correctly.` (`core.py:893-894`).
  * DRM (`requires_ot`) games will likely fail offline [inferred].
* **`--skip-version-check`** (or config `skip_update_check`) skips `get_asset(update=True)`: one assets request plus possible `assets.json`/`entitlements.json` writes (`cli.py:639-649`).
* **Alternative exe**:
  * `--override-exe <relative path>` (must exist, else `ValueError: Executable path is invalid`) or config `override_exe` (`core.py:851-855`).
  * Publisher-provided alternatives = `info --json` `game.launch_options` (`extraLaunchOption_NNN_*`), which are **arguments**, not exes.
  * API-side `executable_override` is applied at install time (`core.py:1684-1690`).
* Other flags:
  * `--language <code>` → `-epiclocale=` (else config `language`, else the system locale: `it` here [run]);
  * `--override-username`; `--wrapper "<cmd>"` (shlex-split, prepended);
  * `--set-defaults`/`--reset-defaults` (**write `config.ini`; don't use**). `--reset-defaults` calls `remove_section`, which `LGDConf` doesn't override,
    so `modified` stays False and the change is probably never saved (`cli.py:628`, `models/config.py:26-49`) [inferred].
  * Unknown args are passed to the game (`cli.py:3114`).
* **`--dry-run`**: logs `Not Launching <app> (dry run)`, `Launch parameters: <shlex.join>`, `Working directory:`, `Environment overrides:` (stderr). It still performs login and the exchange.
* **Minimum work before process start**, using `launch <app> --json --skip-version-check`:
  1. ≈1.7 s spawn.
  2. LGDLFS init (reads all metadata files).
  3. `login()`: user lock, plus the 24 h update check only if stale, and the overlay check if stale; then 1 verify HTTPS.
  4. 1 exchange HTTPS (+1 ownership-token HTTPS for DRM).

  A pre-warmed token and a fresh `version.json` keep it to two HTTPS round-trips. Spawning the game outside Legendary does not remove the need for an exchange code;
  only owning OAuth in Glider would (not recommended for v0.2: Glider would have to share `user.json` under its lock semantics).

---

## 10. Updates

* **Detection**: version string inequality between `assets.json` (or a fresh assets call) and `installed.json version` (`cli.py:647`, `core.py:1365-1372`).
* **Cheapest checks**:
  * no network: compare the cached files (§2.5);
  * with network: one `list-installed --check-updates --json`, i.e. login + one assets request per installed platform (+ entitlements if changed), **no metadata fetch** (`cli.py:292-299`). Re-read `assets.json` afterwards. The JSON output does not contain the available version; the TSV does (legendary.md §3.3).
* **Delta manifests**:
  * Unless `--disable-delta-manifests`, or a manifest override, or old build id == new build id, `prepare_download` fetches `GET {base_url}/Deltas/{new_build_id}/{old_build_id}.delta`.
    That request has **no timeout** (`core.py:1468-1474`, `unauth_session.get` without `timeout`) [src].
  * If HTTP 200, `apply_delta_manifest` replaces matching file entries and adds the delta's chunks (`models/manifest.py:244-270`). "Doesn't seem to exist for most games" (source comment, `core.py:1469`).
* **Chunk reuse** (`downloader/mp/manager.py:93-449`):
  * The comparison is by path and file hash (`models/manifest.py:996-1026`). **Unchanged** files are never touched or verified. **Added** files are fully downloaded. **Removed** files are deleted at the end.
  * For **changed** files only, each new chunk part that lies wholly inside a chunk part of the *old version of the same file* is copied from the old file (`manager.py:308-330`).
    Such files are written as `<file>.tmp` and then the old file is deleted and the tmp renamed (`manager.py:393-401`). There is no cross-file reuse.
  * A chunk referenced several times is downloaded once and kept in shared memory until its last use (`manager.py:359-389`).
  * `--disable-patching` doesn't load the old manifest at all, so every file is "added" and the **full game is re-downloaded** (`core.py:1496`) [inferred].
  * Resume and missing-file logic as in legendary.md §4.6–4.7.
* **`--repair-and-update`**:
  * The repair verifies against the *installed* manifest first (`cli.py:933`), then downloads the **latest** manifest.
  * Since 0.21 (`6631459`) `old_manifest` is always `None` in repair mode (`core.py:1604-1608`), so there is **no chunk reuse**. Every file whose hash differs from the `.repair` line is downloaded whole.
  * It is more expensive than a normal update. Use it only when the installed manifest is missing or the install is corrupt.
* **Imported games** with `needs_verification` are auto-switched to repair mode on update (legendary.md §5.4).

---

## 11. Install-phase architecture and performance knobs

Processes (Windows spawn; each child is a fresh copy of the PyInstaller exe; see legendary.md §0 item 8):

```
legendary.exe (bootloader) ── python (CLI: login, metadata, manifest, analysis; waits in dlm.join())
                                 └─ DLManager (multiprocessing.Process, manager.py:31)
                                      ├─ threads: download_job_manager, dl_results_handler, fw_results_handler (+ main progress loop)
                                      ├─ DLWorker 1..N  (Process, workers.py:38)   N = --max-workers or config max_workers, default min(2*CPU, 16)
                                      └─ FileWorker     (Process, workers.py:154)  exactly ONE writer
```

* **Shared memory**:
  * One `SharedMemory(create=True, size=max_shared_memory)` is created by the DLManager (`manager.py:658`), sliced into segments of `biggest_chunk` bytes (usually 1 MiB; `manager.py:662-665`).
  * Size = `--max-shared-memory` MiB, else config `max_memory`, else **2048** (`core.py:1645-1646`).
  * Analysis computes the minimum (`min_memory` = peak cached multi-use chunks + 32 MiB) and raises `MemoryError` if it is larger (`manager.py:416-432`).
* **Queues** (all `multiprocessing.Queue`, unbounded): `dl_worker_queue` (DownloaderTask: url + shm segment), `dl_result_q`, `writer_queue` (WriterTask), `writer_result_q` (`manager.py:670-673`).
  In-flight downloads are capped at `2 × max_workers` and by free segments (`manager.py:451-487`).
* **Where the work happens**:
  * **DLWorker**: HTTP GET (`requests.Session` per worker, created in `__init__` and pickled to the child; keep-alive reuse per worker; UA `EpicGamesLauncher/11.0.1-…`; timeout `--dl-timeout`, default 10 s) (`workers.py:47-55,104`).
  * DLWorker also does **chunk parse + AES-GCM decrypt (encrypted chunks, new in 0.21) + zlib decompress**, then copies into the shm segment (`workers.py:115,133-142`, `models/chunk.py:38-55`).
  * **No hash check** of downloaded chunks: the `sha_hash` and rolling hash are parsed but never compared (`models/chunk.py:38-55`). Corruption is only caught by a later `verify`.
  * Retry loop: `tries` is never incremented, so a failing chunk retries **forever without backoff** (`workers.py:88-118`; confirmed, as in legendary.md).
  * **FileWorker**: a single sequential writer. It opens with `open(path,'wb')`, writes the shm slices or old-file ranges (patching reads), closes, renames and deletes (`workers.py:180-324`). No fsync.
  * **DLManager** threads: scheduling, bookkeeping, appending the resume file after each completed file (`manager.py:579-586`), and progress logging every 1 s (`manager.py:724-787`).
* **Hashing**: none during install (the manifest's file SHA-1 only goes into the resume file). `verify` is a separate single-threaded SHA-1 pass (legendary.md §5.1).
* **HTTP session reuse**: per worker process. The manifest/API calls use `EPCAPI.session` (pool 16, `api/egs.py:42-46`). CDN manifest fetch: `unauth_session` with a 10 s timeout (`core.py:1435`).
* Knobs and their `config.ini [Legendary]` equivalents:

  | Knob | Config key |
  |---|---|
  | `--max-workers` | `max_workers` |
  | `--max-shared-memory` | `max_memory` |
  | `--preferred-cdn` | `preferred_cdn` (substring match on base URL) |
  | `--no-https` | `disable_https` |
  | `--dl-timeout` | none |
  | `--bind` | none |
  | `--enable-reordering` | none (API `reorder_optimization` list auto-enables it per app) |
  | `-A` (API timeout) | none |

  (`core.py:1510,1621-1659`, `manager.py:52`.)
* `status_q`/`UIUpdate` exists, but the CLI never passes one (`cli.py:989-1010`), so progress is log-only (legendary.md §4.3).

---

## 12. Move / uninstall / import / egl-sync at library scale, and 0.20.34 → 0.21.1

* **All are W-lane, one process per game.** None has a batch mode except `egl-sync`.
  * Costs per game: ≈1.7 s spawn; `uninstall`/`move` are offline; `import` = login + whole-library metadata check + (no `.egstore` ⇒ CDN manifest download).
* **move**: `os.rename` of the folder; cross-drive gives `OSError errno 18`.
  * Verified on this PC with CPython 3.12: WinError 17 maps to errno 18. The bundled 3.14 should map the same way [inferred].
  * Legendary then prints `Moving to a different drive is not supported…` (`cli.py:2653-2655`).
  * Glider should copy itself (with progress), then run `move <app> <newBase> --skip-move`. The target folder name is the *current* folder name (`cli.py:2642-2643`).
* **uninstall**:
  * Runs the manifest uninstaller unless `--skip-uninstaller` (Windows, `cli.py:1204-1205`; same one-argument `args` quoting issue as prereqs).
  * Deletes manifest files filtered by install tags (`core.py:1853-1857`), then empty dirs.
  * DLC first. If metadata is missing for an owned app, `get_dlc_for_game` may make an unauthenticated network call and abort the uninstall with `Removing game failed` [inferred, `core.py:615-620`].
* **import**: per legendary.md §5.4. New finding: an EGL install whose `.egstore` manifest is **encrypted** gets `decrypt({})` = False, ignored (`core.py:1939`), so file names stay obfuscated, `found == 0` and the import fails with "No files belonging…" [inferred].
* **egl-sync --one-shot --import-only** (bulk adoption):
  * A single process under the lock. It needs `assets.json` (`Legendary is missing game metadata…`). No login.
  * For every EGL `.item` whose `MainGameAppName == AppName` (DLC skipped) and which is owned, it copies `.egstore/<guid>.manifest` and marks the game installed **without verification**, **keeping `egl_guid`** (linked) (`core.py:1972-2024`).
  * `--migrate` does the same, then deletes the EGL `.item`s and the games' `.egstore` folders, i.e. it removes them from EGL (`cli.py:1584-1595`).
  * Glider recommendation: per-game `import <app> <dir> --skip-dlcs` (unlinked), or `--migrate` only with explicit consent.

**Changes 0.20.34 (tagged 2023-12-08) → 0.21.1** (`git log 0.20.34..0.21.1`, 48 commits), with Glider impact:

| Commit | Change | Impact |
|---|---|---|
| `1841da5`, `837c166` | catalog `AdditionalCommandLine` appended to game args; shown in `info` | in `game_parameters` |
| `691048d`, `ac62906`, `96e07ff` | launchable add-ons/DLC (`addons/launchable`) | Fortnite modes, §3 |
| `7fefdc4` | entitlements paging past 1000 | big libraries |
| `90e5f75` | EA App name (`the ea app`) as Origin | §8 |
| `09d280f` | FileLock ≥3.15.3 workaround (`lock_file` param name) | lock semantics unchanged |
| `9395eb9` | skip library items without `appName` | `-T` |
| `f1f5cc0` | `OwnershipToken` lowercased | |
| `56a2314` | Sidecar config + `-epicdeploymentid` | launch args, extra manifest-API call on sidecar change |
| `42af7b5` | GraphQL host `launcher.store.epicgames.com` | |
| `9942cd9` | `.netrc` credentials no longer injected | `session.auth = lambda r: r` |
| `bc5d12c`, `fdfb712` | **encrypted manifests and chunks** (AES-GCM with secrets from the manifest API) | stored manifests are decrypted; EGL-import edge case above |
| `5de8add`, `ce12e3f` | uv/pyproject build, Python ≥3.10 (the exe now bundles 3.14) | |
| `546bb74`, `e1f67bb` | case-insensitive file tasks; no-op on Windows | |
| `aeb61d4` | save-sync chunk read crash fix | |
| `41e3cfa` | webview login callback fix | not used by Glider |
| `46f8445` | **Ubisoft `uplay://` launch** | §8 |
| `48eb5f6` | `info` grant date | |
| `10d8d70` | **achievements**: extra GraphQL per game in metadata refresh, `achievements` command, `achievements.json` | slower first `list`; 0.20-era metadata without `achievements` is refetched once [inferred] |
| `490b577` | `launch` uses `shell=True` (UAC) | §9 |
| `6631459` | no chunk reuse in any repair mode | §10 |
| `eb76c9f` | no KeyError when metadata lacks id/namespace | |
| `1be4b7e` | cwd for EA launches (Wine) | |
| `ece601b` | `LEGENDARY_WRAPPER_EXE` env var | argv |
| `b4d75f1` | manifest **symlink** entries (`CREATE_SYMLINK`) | needs symlink privilege/Developer Mode on Windows, otherwise a write error [inferred] |

---

## 13. `--help` fixtures

`fixtures/legendary/help-v0.21.1/<command>.txt` contains:
* every sub-command: `activate`, `alias`, `auth`, `clean-saves`, `cleanup`, `crossover`, `download-saves`, `egl-sync`, `eos-overlay`, `import`, `info`, `install`, `launch`, `list`, `list-files`, `list-installed`, `list-saves`, `move`, `status`, `sync-saves`, `uninstall`, `verify`, `achievements`;
* the aliases `download`, `update`, `repair`, `list-games` and the hidden `get-token`;
* `legendary.txt` (root `-h`) and `version.txt`.

All exit 0 and printed nothing on stderr. The older `fixtures/legendary/help-*.txt` files only differ in the program name
(`legendary_windows_x64.exe` vs `legendary.exe`) and line wrapping. Their option sets are identical, so they were kept.

---

## 14. Differences from `docs/research/legendary.md`

1. §1.2 / §2.3 claim "tokens saved to `user.json` **atomically** under a file lock". That is **wrong**. The write is a truncating `open('w')` done **after**
   the lock is released (`lfs/utils.py:186-195`). See §1.6.
2. §1.2 / §4.9 list the commands that take `installed.json.lock`. Three are missing:
   * `list-installed` in text mode, when a game has `install_size == 0` (`cli.py:341`);
   * `sync-saves`, when setting a save path (`cli.py:503,511`);
   * any reader when `egl_sync` is on, which *keeps* the lock until exit.

   `launch`, `verify`, `info` and `status` never take it [run].
3. §3.2 lists key-art types `DieselStoreFrontWide`, `Thumbnail`, `OfferImageWide` as typical [unverified]. Real catalog items carry **`DieselGameBox`,
   `DieselGameBoxTall`**, sometimes `Featured`/`AndroidIcon`, and none of the three listed [net, §2.3].
4. §3.1 / §9.4 mark Fortnite's ids and folder as unverified. They are verified [net]: namespace `fn`, catalog id `4fe75bbc5a674f4f9b356b5c90567da5`, `FolderName = Fortnite`.
5. §5.7 says `launch --offline` for Fortnite gives `Game is not approved for offline use`. Fortnite's catalog item has **no `CanRunOffline`** attribute, and
   Legendary defaults it to `'true'` (`core.py:1677`). `installed.json` therefore records `can_run_offline: true`, and that warning does not appear.
   Fortnite still needs the network in practice [inferred].
6. §5.2 marks the cross-drive `move` errno as unverified. It is errno 18 (WinError 17) [run, CPython 3.12].
7. §3.3 says that with no `assets.json`, `get_asset` "may raise TypeError" [inferred]. That is now [run]: `list-installed` crashes with exactly that (`concurrency-v0.21.1/out1-D-list-installed.stderr.txt`).
8. §8 says the shared-memory help text "says 1 GiB". Correct. Addition: `max_memory` 2048 is also the default for `download`/`repair`. The overlay installer uses the DLManager default of 1 GiB (`core.py:2215`).
9. §0 item 20 / §4 say there is "no `--include-dlc`". Confirmed: `list` has no such flag (DLC are always included in `--json`).

---

## 15. Summary of decisions for the job runner and game model

See the final report of this task. The same 20 lines are reproduced here so they are not lost:

1. Single **Writer lane**: install/update/repair/download, uninstall, import, move, egl-sync, verify, first-time sync-saves path, eos-overlay install/update/remove, cleanup.
2. The lock is non-blocking and fails with exit 0 + `Failed to acquire installed data lock`, so treat that line as "busy → retry", never as success.
3. No parallel downloads, ever, with one config dir. Queue them, and give Glider ordering, pause (kill + resume) and priorities.
4. **Launch lane** is independent: `launch <app> --json --skip-version-check`, then Glider `CreateProcess`, with a `runas` fallback on error 740.
5. Block launch/verify/save-sync for any app (or its base game) with a queued or running Writer job.
6. **Auth gate**: one Legendary in `login()` at a time, launch first. Warm with `list-installed --check-updates` at start-up.
7. **Metadata lane** (1): `list -T --json`, `info`, `status`, `activate`, `list-saves`. Don't start one during a Writer job's pre-download phase.
8. Glider **reads** `installed.json`, `metadata/*.json`, `assets.json` directly (retry on parse error) and **never writes** Legendary's files.
9. Keep `config.ini` static: write it only when no Legendary runs, keep Glider settings in Glider's store, pass flags, and leave `egl_sync` off.
10. Never run `cleanup` while anything runs or a `tmp/<app>.resume` exists.
11. Game model key = `app_name`. Also store namespace and catalogItemId, title, latest build per platform, installed version/path/size/tags/platform.
12. Model flags: isDlc + baseAppName, launchableAddon, hidden, thirdPartyStore, partnerLinkType, canRunOffline (default true), requiresOwnershipToken, cloudSaveFolder (+include/exclude), extraLaunchOptions.
13. Art: `DieselGameBoxTall` for tiles, `DieselGameBox` for hero, then fall back to the other one, then a placeholder.
14. DLC: install each one as its own Writer job with `--skip-dlcs` on the base. Treat ownership from `entitlements.json` + `dlcItemList` and installability = releaseInfo ∧ asset.
15. Components: SDL packs (api.legendary.gl definitions) + raw tags from `info --json`. Always install with explicit `--install-tag`s or `--skip-sdl`.
16. Prerequisites: Glider runs them (`runas`) after install/update and remembers them by prereq ids. Legendary forgets them on every update.
17. Cloud saves: Glider ledger + 3-way detection + mandatory local snapshot, then one directional `sync-saves -y --skip-* --force-*`; verify uploads by re-listing.
18. Updates: compare `assets.json` vs `installed.json`. Refresh with one `list-installed --check-updates`. Use `--repair-and-update` only for broken installs (no chunk reuse).
19. Third-party titles: badge them, `activate --uplay` once, launch via `--ubisoft`/`--origin --json` → ShellExecute(uri). No process tracking.
20. EOS overlay is a global setting, not per game. Prefer `enable --path <EOSH dir>`, and run `disable` elevated or not at all.
