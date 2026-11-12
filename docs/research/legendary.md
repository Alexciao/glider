# Legendary 0.21.1 ("Lowlife") — research for Glider

Scope: driving `legendary_windows_x64.exe` as a subprocess to install / update / verify / launch **Fortnite only** (app name `Fortnite`).

## Conventions & provenance

* Source = tag `0.21.1` (commit `42f6bdeadde3a9526dc8eb713763476999ac217b`). All `file:line` cites are relative to `legendary/legendary/` (e.g. `cli.py:1015`).
* Binary = official `legendary_windows_x64.exe` 0.21.1, sha256 `6be77857dc0a6dd33ea7442ce8b5291944ec6da53757f81ca95b57ce794baf5b` (verified; it also matches `release_info.download_hashes.windows` in the live API response).
* Fixtures (raw outputs, config dir path replaced by `<CONFIG_DIR>`) live in `docs/research/fixtures/legendary/`. Referred to below as `fx:<file>`.
* Tags used: **[src]** read from source, **[run]** observed by running the 0.21.1 binary signed-out, **[net]** observed from the live `api.legendary.gl`, **[inferred]** reasoning from source (not executed), **[unverified]** general knowledge I could not confirm here (no Epic account was used, so nothing signed-in was run).
* Everything signed-in (real `list --json`, `info --json`, real progress lines) is derived from source; there are no signed-in fixtures.

---

## 0. The 20 facts that matter most for Glider

1. **Data dir override = `LEGENDARY_CONFIG_PATH`** (`lfs/lgndry.py:26-31`). Without it: `XDG_CONFIG_HOME\legendary`, else `%USERPROFILE%\.config\legendary`. Everything (tokens, installed.json, manifests, resume files, config.ini) lives under it.
2. **Logging (everything from `logging`, incl. the "Install size" summary and all download progress) goes to stderr**; JSON/CSV and all `print()`/`input()` output (prompts, the requirements-check block, DLC list, `verify` progress) go to stdout. Log format is exactly `[%(name)s] %(levelname)s: %(message)s` (`cli.py:54-57`, `cli.py:70`).
3. **stdout is forced to UTF-8 only when it is not a TTY** (`cli.py:2721`), but **stderr is the Windows ANSI code page (cp1252 here) with `\uXXXX` backslash-escapes for unencodable chars** [run: `fx:encoding-test-default.stderr.bin` — `D:\ünïcödé日本` came out as bytes `fc 6e ef 63 f6 64 e9 5c 75 36 35 65 35 …`]. `PYTHONUTF8=1` / `PYTHONIOENCODING=utf-8` had **no effect** (`fx:encoding-test-PYTHONUTF8.stderr.bin` is byte-identical; PyInstaller build). Decode stderr as the ANSI code page and expect `\u65e5`-style escapes for CJK.
4. **Exit codes are nearly useless**: most failures `return` → exit **0** (see §1.6). Parse log text. Only some paths `exit(1)`; unhandled Python exceptions give exit **1** + a traceback + `[PYI-<pid>:ERROR] Failed to execute script 'cli' due to unhandled exception!`.
5. **Signed-out `install`/`list`/`list-files`/`auth`-less calls crash with a traceback** (`ValueError: No saved credentials`, `core.py:218`) rather than a nice message [run: `fx:install-notloggedin.stderr.txt`]. Check login first with `status --offline --json` (`account == "<not logged in>"`).
6. **`status` (online) and `auth` force a Legendary self-update check that is NOT wrapped in try/except** (`cli.py:1610`, `cli.py:112`) → if `api.legendary.gl` is unreachable they crash with a traceback and exit 1, even with `disable_update_check = true` [run: `fx:status-network-failure.stderr.txt`]. `status --offline --json` never touches the network.
7. **Ctrl-C leaves exit code 0** (`cli.py:3216-3256`) and the game *not* marked installed (for fresh installs) — see §4.7/§4.8.
8. **PyInstaller one-file build ⇒ every invocation is TWO processes** (bootloader parent + Python child, same command line) [run: PIDs 7144→21252], and a download adds `DLManager` + N `DLWorker` + `FileWorker` child processes (multiprocessing, `downloader/mp/manager.py:31,675-693`). Killing only the root PID can orphan the downloader. **Use a Windows Job Object (KILL_ON_JOB_CLOSE) or `taskkill /T /F`.**
9. **Download workers retry a failing chunk forever with no backoff** (`downloader/mp/workers.py:88-118`: `tries` is never incremented). Offline/CDN errors = endless `Chunk download for <id> failed: …, retrying...` warnings, never an error exit. Glider needs a **stall watchdog** (no change in `= Progress` for N seconds).
10. **Write failures do not abort the install** (`manager.py:588-590` only `log.fatal('Writing for … failed!')`; `workers.py:320-322`). Disk-full / locked-file during writing can end with "Finished installation process" and the game marked installed. Always run `verify` after install/update (or parse for `Writing for` / `Something in writing a file failed`).
11. **Resume = `<config>\tmp\Fortnite.resume`**, one line `<sha1 hex>:<relative path>` per *completed file*, appended on each file close (`manager.py:579-586`), deleted at success (`manager.py:811-816`). A re-run of the same `install` command resumes; partially downloaded files restart from byte 0.
12. **`-y` is mandatory for unattended use but is not enough for Fortnite**: the SDL ("selective download") prompt still appears unless you pass `--skip-sdl` (or `--install-tag`/`--disable-sdl`) (`cli.py:961-971`, `utils/cli.py:40-66`). Also pass `--skip-dlcs` (with `-y`, DLCs are auto-installed: `cli.py:1113`) and `--skip-uninstaller` on uninstall.
13. **Fortnite SDL is live data (version 14)**, not the hardcoded 0.21.1 table (which is stale: `chunk0/chunk10`). `__required` = 27 named tags + `""`; optional pack `epic_assets` [net: `fx:api-sdl-Fortnite.json`, `fx:api-version.json.cached.json` `sdl_config.Fortnite = 14`]. If the SDL download fails, legendary silently falls back to the stale hardcoded tags (`core.py:449-453`) → wrong file set.
14. **Update detection without downloading**: `list-installed --check-updates --tsv` prints `Installed version` + `Available version` + `Update available` (`cli.py:313-321`). `--json` does **not** carry the available version (`cli.py:323-324`).
15. **`launch` on Windows = `subprocess.Popen(..., shell=True)` then legendary exits immediately with 0**; it does not wait for the game (`cli.py:744`). `launch --json` prints the full command (including a live one-time exchange code) and does **not** launch — Glider can use it to spawn the game itself and own the process handle (§5.7).
16. **`import` writes nothing into the game folder** unless EGL sync is enabled; it needs login + network (manifest/metadata) and marks the install `needs_verification` unless an EGL `.egstore` manifest is found (§5.4).
17. **Legendary 0.21.1 never touches `LauncherInstalled.dat`** (zero references in source). EGL integration is only: `%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\<guid>.item` + `<install>\.egstore\<guid>.{manifest,mancpn}` (§6). EGL sync is **off by default**.
18. **`auth --import` reads EGL's `GameUserSettings.ini` and spends the EGL refresh token**; Epic's help text says "(logs out of EGL)" (`cli.py:2815`). Source never writes EGL's ini, so the logout is a side-effect of refresh-token rotation [inferred]. Do not offer it by default.
19. **Unknown flags are silently accepted** (`parse_known_args`, `cli.py:3114`): `install Fortnite --bogus` was *not* rejected [run]. Typos in flags are swallowed (and for `launch` they are forwarded to the game). Global flags (`-v`, `-y`, `-J`, `-A`) must come **before** the sub-command except `-y`, which is also honoured after it (`cli.py:3160-3163`).
20. **Several flag names in the task brief don't exist in 0.21.1** — real names: `--enable-reordering` (not `--dl-optimizations`), `--no-https` (not `--disable-https`; config key is `disable_https`), `--download-only`/`--no-install`, `--disable-delta-manifests`, `--disable-sdl`, `--dl-timeout`, `--bind`, `--prefix`/`--exclude`. There is **no `--yes` flag on subparsers** — it is global `-y/--yes`.

---

## 1. Config / data-dir isolation, files, config.ini, exit codes

### 1.1 Location

```python
# lfs/lgndry.py:26-31
if config_path := os.environ.get('LEGENDARY_CONFIG_PATH'):
    self.path = config_path
elif config_path := os.environ.get('XDG_CONFIG_HOME'):
    self.path = os.path.join(config_path, 'legendary')
else:
    self.path = os.path.expanduser('~/.config/legendary')
```
Hidden `-c/--config-file <path/name>` (`cli.py:2733`) only relocates `config.ini` (`lgndry.py:51-60`); not needed.

On first run legendary creates `<dir>`, `manifests\`, `metadata\`, `tmp\` (`lgndry.py:63-65`) [run: fresh dir contains exactly `config.ini manifests metadata tmp`, plus `version.json` after an online command]. `config.ini` is written at process exit only if modified (`lgndry.py:387-400`, `core.py:2314-2318`).

### 1.2 Files under the config dir

| Path | Written by | Format / contents | Cite |
|---|---|---|---|
| `user.json` (+ `user.json.lock`) | `auth`, every successful `login()` refresh | EGS OAuth token response (JSON, indent 2). Keys read by source: `access_token`, `expires_in`, `expires_at`, `refresh_token`, `account_id`, `displayName`. Remaining keys are whatever EGS returns (`token_type`, `refresh_expires`, `refresh_expires_at`, `client_id`, … **[unverified]**). Deleted by `auth --delete` and when refresh fails with invalid credentials. | `lgndry.py:147-175`, `utils.py:169-208`, `core.py:269-270` |
| `installed.json` (+ `.lock`) | install/import/move/repair | `{ "<app_name>": InstalledGame fields }` (§3.5) | `lgndry.py:355-379` |
| `assets.json` | `get_assets` | `{ "<Platform>": [GameAsset…] }` — build versions per platform | `lgndry.py:219-239` |
| `entitlements.json` | `get_assets` | raw EGS entitlements list | `lgndry.py:177-196` |
| `metadata\<app_name>.json` | `list/install/info/import` | the `Game` object (§3.1) incl. raw catalog JSON in `metadata` | `lgndry.py:267-271` |
| `manifests\<app>_<Platform>_<buildversion>.manifest` | install/import/EGL import | binary (or JSON) manifest, name = `clean_filename(f'{app_name}_{platform}_{version}')`; fallback w/o platform | `lgndry.py:241-260` |
| `tmp\Fortnite.resume` | install | resume list (§4.7) | `core.py:1610-1617` |
| `tmp\Fortnite.repair` | `verify` / repair | same line format; see §5.1 | `cli.py:1334-1339`, `core.py:1610-1612` |
| `tmp\Fortnite.json` | SDL | `{"version": 14, "data": {SDL…}}` cache | `lgndry.py:424-436` |
| `tmp\<namespace><catalogItemId>.ovt` | launch (only if `requires_ot`) | ownership token bytes | `core.py:906-912` |
| `version.json` | any online update check | `{"data": <api.legendary.gl/v1/version.json>, "last_update": <epoch>}`; see `fx:api-version.json.cached.json` | `lgndry.py:405-422` |
| `aliases.json` | metadata refresh | auto-aliases | `lgndry.py:479-511` |
| `config.ini` | exit | INI | `lgndry.py:387-400` |
| `overlay_install.json`, `overlay_version.json` | `eos-overlay` | not used by us | `lgndry.py:438-477` |
| `<install>\.cache\` | — | `DLManager.cache_dir` default; disk cache is a TODO (`manager.py:418`), effectively unused | `manager.py:41` |

Notes: `legendary cleanup` deletes unused metadata, non-installed manifests, **and everything in `tmp\` (including resume files and SDL cache)** (`cli.py:2046-2065`, `lgndry.py:288-293`) — never run it between an interrupted install and its resume.
File locks: `installed.json.lock` is held for the life of any `install/import/move/uninstall/egl-sync` process (`verify`, `launch`, `status`, `list-installed`, `info` do not take it) (`lgndry.py:322-341`). A 2nd concurrent mutating legendary prints `[cli] CRITICAL: Failed to acquire installed data lock, only one instance of Legendary may install/import/move applications at a time.` (`cli.py:846`) and **exits 0**. Glider must serialise.

### 1.3 `config.ini` — keys honoured (INI; parser `LGDConf`, comment_prefixes `/`, `allow_no_value`)

`models/config.py`. **`;`-lines are NOT comments** in this parser (prefix is `/`), they become valueless keys — that is why legendary filters `;` keys in `.env` sections (`core.py:743`). Write only real `key = value` lines.
Legendary rewrites `config.ini` on exit when it changed anything (e.g. `install_tags`); if the file was modified externally while running, legendary renames the user's file to `config.<mtime>.ini` and writes its own (`lgndry.py:392-397`). ⇒ **Write config.ini before spawning, never while a legendary process is running.** A malformed ini puts legendary in read-only "safe-mode" (`lgndry.py:99-106`).

`[Legendary]` section:

| Key | Type / default | Effect | Cite |
|---|---|---|---|
| `disable_update_check` | bool, default written as `false` | skip the 24h-cached Legendary self-update + API config fetch **in `_login()` only**; `status`/`auth` still force it | `lgndry.py:113-115`, `core.py:231-237`, `cli.py:112,1610` |
| `disable_update_notice` | bool, default `false` on Windows | suppress "Legendary update available!" text on exit (non-JSON) | `lgndry.py:116-118`, `core.py:287-291`, `cli.py:3229-3246` |
| `max_workers` | int, `0`/unset ⇒ `min(2*cpu_count, 16)` | download worker processes | `core.py:1654-1655`, `manager.py:52` |
| `max_memory` | int MiB, fallback **2048** | shared-memory cache | `core.py:1645-1646` |
| `preferred_cdn` | hostname substring | pick matching base URL | `core.py:1625-1631` |
| `disable_https` | bool | rewrite manifest + base URLs to `http://` | `core.py:1510,1429,1638` |
| `install_dir` | path, default `~/Games` (→ `%USERPROFILE%\Games`) | default `--base-path` | `core.py:1818-1822` |
| `install_platform_fallback` | bool, default true | fall back to Windows asset | `cli.py:895` |
| `default_platform` | str | default `--platform` (`Windows`) | `cli.py:3153-3156` |
| `locale` | `en-US`/`en_US` | EGS API + launch `-epiclocale` language part | `core.py:105-111` |
| `log_level` | `debug`/`info` | `debug` ⇒ DEBUG root logger | `cli.py:3146-3151` |
| `egl_sync` | bool | auto EGL sync (default off) | `core.py:2138-2140` |
| `egl_programdata` | path | non-Windows only | `core.py:93-100` |
| `disable_auto_aliasing` | bool | alias generation | `core.py:460` |
| `mac_install_dir`, `disable_auto_crossover` | — | macOS, ignore | |

Per-game sections `[Fortnite]` (app name is the section name): `install_tags` (comma list; written by legendary after SDL choice, `cli.py:971,979`), `disable_sdl`, `offline` (`core.py:1360`), `skip_update_check` (`core.py:1363`), `start_params` (shlex'd, appended after EGL args, `core.py:933-934`), `language` (`core.py:914`), `wrapper`, `override_exe` (`core.py:851`), `pre_launch_command`, `pre_launch_wait`, `no_wine`, `wine_*`/`crossover_*` (non-Windows). `[Fortnite.env]` and `[default.env]`: environment variables merged into the launch env (`core.py:739-750`). `[default]` mirrors per-game keys as defaults. `[Legendary.aliases]`.

### 1.4 Disabling update check / prompts

* Self-update check: set `disable_update_check = true` and `disable_update_notice = true` under `[Legendary]` (`fx:config.disable-update-check.ini`). [run] With that config and no creds: `status --offline --json` made **no** `version.json`; online `status --json` **did** (forced).
* The version/API-config fetch is *useful*: it supplies the current EGL client version/user-agent and the Fortnite SDL version (`core.py:316-328`). Keep `version.json` present; `auth` forces one fetch so it will exist. Latent bug [inferred]: with `disable_update_check=true`, a never-fetched `version.json`, and the SDL path active (including with `--skip-sdl`), `get_sdl_data` does `version_info.get(...)` on `None` (`core.py:445-446`) → `AttributeError`. Simplest safe policy: leave `disable_update_check` at its default (`false`) and only set `disable_update_notice = true`; the check is 24h-cached (`core.py:299`) and in `_login` its failure is only a warning.
* Prompts: global `-y/--yes` (`cli.py:2731`, also accepted after the sub-command `cli.py:3161`). Prompts that remain **even with `-y`**: SDL pack choice (`utils/cli.py:55`) unless `--skip-sdl`/`--install-tag`/`--disable-sdl`; `auth` code entry; `_handle_postinstall` is skipped with `-y` (`cli.py:1162-1164`), i.e. prerequisites are **not run and not marked installed** (the prereq print still appears). No environment variable disables prompts. Always give stdin an immediately-closed/NUL handle so a stray `input()` raises `EOFError` instead of hanging.

### 1.5 Env vars legendary reads

`LEGENDARY_CONFIG_PATH`, `XDG_CONFIG_HOME` (`lgndry.py:26-29`), `LGDRY_WRAPPER`, `LGDRY_WINE_BINARY`, `LGDRY_WINE_PREFIX`, `LGDRY_NO_WINE` (`cli.py:2909-2933`), `LEGENDARY_WRAPPER_EXE` (inserted before the exe, `cli.py:696-697`). Windows `%LOCALAPPDATA%`/`%PROGRAMDATA%` are expanded for EGL (`lfs/egl.py:16-21`). `HTTPS_PROXY` etc. are honoured by `requests` [run: proxy made `status` time out].

### 1.6 Exit codes (observed + source)

| Situation | Exit | Source / run |
|---|---|---|
| `-V` / normal success / `-h` / no args | 0 | `cli.py:3116-3118,3256` [run] |
| argparse error (bad command, missing positional) | **2** | [run: `bogus-cmd`] |
| Unhandled exception (signed-out `install/list/list-files`, network failure in `status`/`auth`, `PermissionError`, `MemoryError` from analysis) | **1** (+ traceback + `[PYI-nnn:ERROR]` line) | [run] |
| `install`: login failed / game not found / `--update-only` & not installed / requirements failure | 1 | `cli.py:869,877,884,902,916,1069` |
| `install`: "Download size is 0 … Exiting…" | 0 | `cli.py:1015,1040` |
| `install`: user answers "n" | 0 | `cli.py:1076-1077` |
| `install`: lock not acquired | **0** | `cli.py:846-848` |
| `install`: exception during `dlm.start()/join()` | **0** (logs warning only) | `cli.py:1088-1093` |
| `list`, `list-files`, `status`: login failed | 1 | `cli.py:214,383,1605` |
| `launch`: not installed / dir missing / login failed / out of date | 1 | `cli.py:616,623,637,649` |
| `verify` not installed / **verification FAILED** | **0** | `cli.py:1243-1246,1344` |
| `uninstall` not installed | 0 | `cli.py:1197` |
| `move`/`import`/`egl-sync` errors | 0 | `cli.py:2640,1360-1374,1474` |
| **Ctrl-C (KeyboardInterrupt)** | **0** | `cli.py:3216-3217,3256` |

---

## 2. Authentication

### 2.1 `auth` flags (`fx:help-auth.txt`)

```
legendary auth [--import] [--code <authorization code>] [--token <exchange token>]
               [--sid <session id>] [--delete] [--disable-webview]
```
Flow (`cli.py:93-208`):
1. `--delete` → `invalidate_userdata()` → deletes `user.json`; logs `[cli] INFO: User data deleted.` [run: `fx:auth-delete.stderr.txt`]. No server-side logout.
2. Else tests existing creds: `[cli] INFO: Testing existing login data if present...` → `core.login()`. If valid: `Stored credentials are still valid, if you wish to switch to a different account, run "legendary auth --delete" and try again.` and returns (so `auth --code` is a **no-op while logged in**). If invalid: `Stored credentials were found but were no longer valid. Continuing with login...` and wipes `user.json`.
3. Forces an (unguarded) self-update check (`cli.py:112`).
4. `--import` → EGL import (§2.4). `--sid` → `core.auth_sid` exchanges a session id for an exchange code via `https://www.epicgames.com/id/api/{set-sid,csrf,exchange/generate}` (`core.py:123-155`). `--token` = exchange token (grant `exchange_code`). `--code` = authorization code (grant `authorization_code`). All end in `egs.start_session` → `POST https://account-public-service-prod03.ol.epicgames.com/account/api/oauth/token` with HTTP Basic client creds `34a02cf8f4414e29b15921876da36f9a:daafbccc737745039dffe53d94fc76cf` (EGL launcher client; also in `fx:api-version.json.cached.json`), `token_type=eg1` (`api/egs.py:24-25,100-144`).
5. No `--code/--sid/--token`: tries `pywebview` (only if installed & Edge/CEF renderer & no `webview_killswitch`, `utils/webview_login.py:9-21`, `cli.py:174`); otherwise (or with `--disable-webview`) the manual flow.

### 2.2 Manual (browser) flow — exact URL and what to capture

```python
# cli.py:176-186
print('Please login via the epic web login!')
url = 'https://legendary.gl/epiclogin'
webbrowser.open(url)
print(f'If the web page did not open automatically, please manually open the following URL: {url}')
auth_code = input('Please enter the "authorizationCode" value from the JSON response: ')
auth_code = auth_code.strip()
if auth_code[0] == '{':
    tmp = json.loads(auth_code); auth_code = tmp['authorizationCode']
else:
    auth_code = auth_code.strip('"')
```
* `https://legendary.gl/epiclogin` is a redirector to Epic's login (the canonical target is built in `api/egs.py:62-65`, unused by the CLI):
  `https://www.epicgames.com/id/login?redirectUrl=` + urlencode(`https://www.epicgames.com/id/api/redirect?clientId=34a02cf8f4414e29b15921876da36f9a&responseType=code`).
* After login Epic's `/id/api/redirect` returns a JSON document; the only key the source requires is **`authorizationCode`** (paste either the whole JSON object or just the code, quotes optional). **Empty input crashes** (`auth_code[0]` IndexError). Code is short-lived and single-use [unverified lifetime].
* Glider-friendly path: host its own browser/WebView2 to the canonical URL above, extract `authorizationCode` from the JSON response page, run `legendary auth --code <code> --disable-webview`. Success line: `[cli] INFO: Successfully logged in as "<displayName>"` (`cli.py:206`); failure: `[Core] ERROR: Logging in failed with …` + `[cli] ERROR: Login attempt failed, please see log for details.` (`core.py:166`, `cli.py:208`). Note the exit code is still 0 in both cases.

### 2.3 Token refresh / validation

`core._login` (`core.py:211-278`):
* In-process cache: if already logged in with ≥10 min left → reuse.
* Update check (guarded; warning only) → `Checking for Legendary updates failed: …` (`core.py:231-237`).
* If `expires_at` has **> 10 minutes left** (and no `force_refresh`): `[Core] INFO: Trying to re-use existing login session...` → `GET …/account/api/oauth/verify` with the stored access token (`egs.py:83-98`). OK ⇒ done (no token change).
* Otherwise `[Core] INFO: Falling back to using refresh token...` / `[Core] INFO: Logging in...` → grant `refresh_token` (`egs.py:100-120`), new tokens saved to `user.json` **atomically under a file lock** (`utils.py:169-195`). Refresh tokens rotate; a refresh token reused elsewhere (EGL) is dead afterwards [inferred].
* Failure modes: `InvalidCredentialsError` → `[Core] ERROR: Stored credentials are no longer valid! Please login again.` + `user.json` deleted + `login()` returns False (`core.py:268-271`). `HTTPError/ConnectionError` → `[Core] ERROR: HTTP request for login failed: …, please try again later.` + False, user.json kept (`core.py:272-274`). `ReadTimeout` is not caught → traceback [inferred]. No `user.json` → `ValueError('No saved credentials')` (`core.py:217-218`).
* Callers that call `login()` unguarded crash on missing creds: `install`, `list`, `list-files`, `launch`, `get-token` (`cli.py:212,382,635,867,2219`; `launch` only reaches it if the game is installed and not `--offline`). `status` and `info` wrap it in `try/except ValueError` (`cli.py:1603-1608,1655-1660`).

**"Just refresh/validate" commands**

| Goal | Command | Notes |
|---|---|---|
| Is anyone signed in (no network, no refresh) | `status --offline --json` | `account` is `"<not logged in>"` iff `user.json` missing/empty (`cli.py:1612-1616`). Says nothing about token validity. |
| Validate + refresh online | `status --json` | calls `login()` (→ verify/refresh), forced self-update check (**can crash offline**), then refreshes the assets for the *whole library* (`get_game_list(update_assets=True)`, `cli.py:1618`) — slow for big libraries, fine for a Fortnite-only account. Login failure: `[cli] ERROR: Log in failed!`, exit 1. |
| Force a refresh and get the bearer | hidden `get-token --bearer --json` | `login(force_refresh=True)`; prints `{token_type, access_token, expires_in, expires_at, account_id}` (`cli.py:2218-2239`). Exposes a secret to the host — only use if needed. `get-token` without `--bearer` prints a one-time *exchange code* `{"code": …}`. |

### 2.4 `auth --import`

`auth_import` (`core.py:181-209`): reads `%LOCALAPPDATA%\EpicGamesLauncher\Saved\Config\Windows\GameUserSettings.ini` (`lfs/egl.py:16-18,30-37`), `[RememberMe] Data` → base64 → AES-256-ECB decrypt with keys from the API response (`egl_config.data_keys`, `utils/egl_crypt.py`) → JSON list → `[0]['Token']` = refresh token → `start_session(refresh_token=…)`. It **never writes** the EGL ini (only `read_config` is called; `save_config` has no callers). The `--import` help says "(logs out of EGL)": the refresh grant rotates the token EGL still holds [inferred]. Success text: `Successfully imported login session from EGS!` / `Now logged in as user "<name>"`; failure `No EGS login session found, please login manually. (Exception: …)` exit 1. Skipped silently if existing legendary creds are still valid.

### 2.5 `status --json` (exact)

```python
# cli.py:1620-1627
dict(account=user_name, games_available=games_available, games_installed=games_installed,
     egl_sync_enabled=self.core.egl_sync_enabled, config_directory=self.core.lgd.path)
```
Signed-out [run, `fx:status-signed-out.stdout.txt`, `fx:status-offline-json.stdout.txt`] (key order is insertion order; `-J` sorts keys):
```json
{"account": "<not logged in>", "games_available": 0, "games_installed": 0, "egl_sync_enabled": false, "config_directory": "<CONFIG_DIR>"}
```
Signed-in (derived): `"account": "<displayName>"`, `games_available` = count of non-DLC games with a Windows asset on the account (UE marketplace skipped, `core.py:520-631`), `games_installed` = non-DLC installed (triggers EGL sync if enabled, `core.py:709-714`). `config_directory` uses forward slashes as given by the env var. Human output (`fx:status-text.stdout.txt`): `Epic account: …`, `Games available: …`, `Games installed: …`, `EGL Sync enabled: …`, `Config directory: …`, `Platform (System): …`, `Legendary version: 0.21.1 - "Lowlife"`, `Update available: no|yes`.

**Account display name** comes from `user.json` → `displayName` (token response); also `account_id` (used in `-epicuserid=`, `core.py:896-897`). The host can read `user.json` directly (offline) for `displayName`/`account_id`/`expires_at`.

---

## 3. Commands and their JSON

Global: `legendary [-v] [-y] [-J] [-A <seconds>] <command> …` (`fx:help.txt`). `-J/--pretty-json` = indent 2 + sorted keys; otherwise compact, insertion order (`cli.py:86-91`). All JSON uses `default=str`.

### 3.1 `list --json` (`cli.py:210-256`)
Needs login. Flags `--platform`, `--include-ue`, `-T/--third-party`, `--force-refresh`, `--csv`, `--tsv`. Array of `Game` dicts (`models/game.py:203-211`):
```
{ "app_name", "app_title", "base_urls": [..],
  "metadata": <raw EGS catalog item JSON>,           // see 3.2
  "asset_infos": { "Windows": {"app_name","asset_id","build_version","catalog_item_id","label_name","namespace","metadata":{...},"sidecar_rev"} },
  "sidecar": {"config":{...},"rev":N} | null,
  "achievements": {...} | null,
  "dlcs": [ <Game dicts> ] }
```
`asset_infos.<platform>.build_version` is the *latest* version string. CSV/TSV header: `App name,App title,Version,Is DLC`. Fortnite: `app_name == "Fortnite"` (`utils/selective_dl.py:28`; `sdl_config.Fortnite`), catalog namespace `fn`, catalogItemId `4fe75bbc5a674f4f9b356b5c90567da5` **[unverified — read them from `metadata.namespace` / `metadata.id`; `Game.namespace/catalog_item_id` are exactly those, `game.py:169-179`]**.
Caveat: `list`/`install`/`info` refresh metadata+achievements for **every** owned game whose asset changed (`core.py:548-631`, threaded when >5), so first run on a big library is slow.

### 3.2 Key art — where `keyImages` live
Legendary never parses `keyImages` (no source references). It stores the raw EGS catalog item verbatim: `metadata\Fortnite.json → "metadata" → "keyImages"` (array of `{type, url, md5, width, height, size, uploadedDate}`; types such as `DieselGameBox`, `DieselGameBoxTall`, `DieselStoreFrontWide`, `Thumbnail`, `OfferImageWide` **[unverified — standard EGS catalog shape; confirm on a signed-in `list --json`]**). The same object is returned by `list --json` as `[i].metadata.keyImages` and by `GET https://catalog-public-service-prod06.ol.epicgames.com/catalog/api/shared/namespace/{ns}/bulk/items?id={catalogItemId}&includeDLCDetails=true&includeMainGameDetails=true&country=..&locale=..` (`api/egs.py:211-217`; response keyed by catalog id). Also in `metadata`: `title`, `id`, `namespace`, `customAttributes.FolderName.value` (install folder), `customAttributes.CanRunOffline`, `OwnershipToken`, `AdditionalCommandLine`, `CloudSaveFolder`, `categories`, `dlcItemList`, `creationDate` (used at `core.py:1568-1678`, `cli.py:1713`). Prefer reading `metadata\Fortnite.json` off disk (offline) instead of running a command.

### 3.3 `list-installed --json` (`cli.py:291-361`)
`--check-updates` (online: logs in; on failure logs `Login failed! Not checking for updates.` and continues), `--csv/--tsv/--json`, `--show-dirs`. `--json` ⇒ `[vars(InstalledGame)…]` **including DLC** (`include_dlc=True`). Signed-out/empty [run]: `[]` (`fx:list-installed-json-empty.stdout.txt`); text form `\nInstalled games:\n\nTotal: 0`. Per-item keys (§3.5). Header for TSV/CSV: `App name,App title,Installed version,Available version,Update available,Install size,Install path,Platform` — **this is the only command that reports an available version next to the installed one without a manifest download**. The `Update available` column is Python bool text `True`/`False`. Entries whose metadata is missing are silently dropped from CSV (`if game.app_name in versions`) and warn `Metadata for "X" is missing…` (`cli.py:309`). If `assets.json` doesn't exist, `get_asset` may raise `TypeError` [inferred, `core.py:492`].

### 3.4 `info <app> --json` (`cli.py:1646-1980`)
Flags `--offline`, `--platform`, `--json`. Output: `{"game": {...}|null, "install": {...}|null, "manifest": {...}|null}`; empty groups become `null` (`cli.py:1976-1980`). Signed-out unknown app [run, `fx:info-fortnite-notloggedin.stdout.txt`]: `{"game": null, "install": null, "manifest": null}` exit 0 (no error text).
Online it logs in, refreshes all metadata, queries the catalog, and **downloads the CDN manifest** (hash-checked); `--offline` uses only local data (+ installed manifest if installed). Exact keys:

`game`: `app_name, title, version` (latest for `--platform`), `platform_versions` (`{platform: build_version}`), `grant_date, cloud_saves_supported, cloud_save_folder, cloud_save_folder_mac, is_dlc, external_activation, launch_options` (`[{name,parameters}]`), `command_line, owned_dlc` (`[{app_name,title,installable,id}]`).
`install` (only if installed): `platform, version` (installed), `disk_size` (bytes, `InstalledGame.install_size`), `install_path, save_path, synced_egl_guid, install_tags` (list), `requires_ovt, installed_dlc` (`[{app_name,title,install_size}]`).
`manifest` (if a manifest was available): `size` (bytes of manifest file), `type` (`"binary"|"json"`), `version` (manifest format ver), `feature_level, compressed, encrypted, app_name, launch_exe, launch_command, build_version, build_id, prerequisites` (`{ids,name,path,args}|null`), `uninstaller` (`{path,args}|null`), `install_tags` (sorted list; includes `""`), `num_files, num_chunks`, **`disk_size`** (bytes, sum of file sizes = full uncompressed install), **`download_size`** (bytes, sum of all chunk file sizes = compressed full download), **`tag_disk_size`** `[{tag,size,count}]` and **`tag_download_size`** `[{tag,size,count}]` (only if >1 install tag; else the string `"N/A"` in text mode / `[]` in JSON), `is_preloaded`.
For Fortnite: sizes per SDL pack = sum over the `tag_*` entries whose tag ∈ chosen tags (`""` always included). `build_version` for the installed vs latest comparison: `game.version` vs `install.version`. Human output sizes are `…:.02f GiB`.
Platform mismatch warning: `Game is installed for platform "X", but requested metadata is for "Y", this may lead to unexpected results.` (`cli.py:1669`).

### 3.5 `InstalledGame` / `installed.json` entry (`models/game.py:214-267`, `lgndry.py:355-365`)
Keys: `app_name, install_path, title, version, base_urls[], can_run_offline, egl_guid, executable, install_size, install_tags[], is_dlc, launch_parameters, manifest_path, needs_verification, platform, prereq_info (null|{ids,name,path,args[,installed]}), uninstaller (null|{path,args}), requires_ot, save_path, is_preloaded`. `executable` = manifest `launch_exe` (or workaround override); `install_size` = analysis `install_size` (post-tag-filter). `install_path` for a Windows install uses native separators (`os.path.normpath`, `core.py:1593`).

### 3.6 `launch <app> --dry-run --json`
See §5.7. Keys: `game_parameters, game_executable, game_directory, egl_parameters, launch_command, working_directory, user_parameters, environment, pre_launch_command, pre_launch_wait` (`models/game.py:296-314`, printed at `cli.py:687-688`, **before** the `--dry-run` branch so `--json` always wins and never launches).

### 3.7 `list-files` (`cli.py:363-430`)
`list-files [<app>] [--manifest <uri>] [--force-download] [--platform] [--install-tag <tag>] [--csv|--tsv|--json|--hashlist]`. Uses the *installed* manifest if installed (offline, no login); otherwise logs in and downloads the CDN manifest. `--json`: `[{"filename","sha_hash","install_tags":[…],"file_size":N,"flags":N}]` sorted by lowercase filename. CSV: `path,hash,size,install_tags` (tags `|`-joined). `--install-tag ""` ⇒ only untagged files. Plain mode prints names to stdout and `Install tags: a, b` to stderr (`cli.py:430`). Manifest decrypt failure: `Manifest key wasn't found. File names will be obfuscated` (`cli.py:393`).

### 3.8 Other
`status --json` §2.5. `uninstall`, `move`, `verify`, `import`, `egl-sync` have **no JSON** output.

---

## 4. install / update / repair

`install`, `download`, `update`, `repair` are one parser (`cli.py:2755-2758`); `repair` ⇒ `--repair`, `download` ⇒ `--no-install` (`cli.py:859-865`). Real flag list (`fx:help-install.txt`, `cli.py:2827-2890`):

| Flag | Meaning |
|---|---|
| `--base-path <path>` | parent dir for new installs (default `install_dir` config → `~/Games`). Ignored if already installed (existing `install_path` reused, `core.py:1560-1564`). Created if missing (`core.py:1589-1591`). |
| `--game-folder <path>` | folder name instead of catalog `FolderName` (`core.py:1566-1572`). Final path = `normpath(base\folder)`. |
| `--max-shared-memory <MiB>` | shared-mem cache; **help says 1 GiB but real default is 2048 MiB** (`core.py:1645-1646`; `DLManager` ctor default is 1 GiB but is overridden). |
| `--max-workers <n>` | default `min(2*CPUs,16)` (`manager.py:52`) |
| `--manifest / --old-manifest / --delta-manifest <uri>` | override manifests (downgrade/testing) |
| `--base-url <url>` | force CDN base |
| `--force` | download everything, ignore existing, **no resume file** (`core.py:1613-1617`) |
| `--disable-patching` | don't reuse chunks from old files |
| `--download-only` / `--no-install` | don't mark installed / don't run prereqs |
| `--update-only` | error (exit 1) if not installed: `Update requested for "X", but app not installed!` |
| `--dlm-debug` | DEBUG logs in downloader processes |
| `--platform <Windows\|Win32\|Mac>` | |
| `--prefix <p>` / `--exclude <p>` (repeatable) | file path prefix filters; imply `--no-install` for `--prefix/--exclude` (`cli.py:871-872`) |
| `--install-tag <tag>` (repeatable) | only files with these tags; **disables SDL**; saved to `[Fortnite] install_tags` (`cli.py:976-979`) |
| `--enable-reordering` | processing-order optimisation (RAM); auto-on for a few titles (`utils/game_workarounds.py`); capped at 100 000 files (`manager.py:232-236`) |
| `--dl-timeout <sec>` | per-request timeout, default 10 |
| `--save-path` | cloud-save path |
| `--repair` | verify-then-redownload bad/missing files, no version change |
| `--repair-and-update` | repair against the **latest** manifest |
| `--ignore-free-space` | demote free-space failure to a warning |
| `--disable-delta-manifests` | |
| `--reset-sdl` | forget stored SDL choice (re-prompt unless `--skip-sdl`) |
| `--skip-sdl` | choose defaults = `['']` + `__required.tags` (`cli.py:965-969`) |
| `--disable-sdl` | no tag filtering at all (all files, ~everything incl. optional); sets `[Fortnite] disable_sdl = true` |
| `--preferred-cdn <host>` | substring match against base URLs; fallback warning `Preferred CDN "x" unavailable, using default selection.` |
| `--no-https` | plain HTTP (`core.py:1429-1430,1638-1639`) |
| `--with-dlcs` / `--skip-dlcs` | |
| `--bind <ip,ip>` | source IPs for workers |
| (global) `-y` | |

### 4.1 Recommended unattended command (Fortnite)
```
legendary -y install Fortnite --base-path "D:\Games" --skip-sdl --skip-dlcs [--max-workers N] [--max-shared-memory MiB] [--preferred-cdn host]
```
Update: same command (it auto-detects the install, reuses `install_path`, patches from the old manifest). Repair: `legendary -y repair Fortnite` (verifies first if no `.repair` file exists; `--repair-and-update` to also update). `--update-only` guards against accidentally fresh-installing.

### 4.2 Phase sequence and the log lines (all stderr, logger-prefixed)

| # | Phase | Line (verbatim format) | Source |
|---|---|---|---|
| 1 | login | `[Core] INFO: Trying to re-use existing login session...` or `[Core] INFO: Logging in...` | `core.py:253,266` |
| 2 | metadata | `[Core] INFO: Fetching metadata for N app(s).` (only if stale) | `core.py:604` |
| 3 | SDL | `[cli] INFO: Saving install tags …` / `Using install tags from config: …` / `ERROR: Unable to get SDL data for Fortnite` | `cli.py:978,985,973` |
| 4 | prepare | `[cli] INFO: Preparing download for "Fortnite" (Fortnite)...` | `cli.py:988` |
| 5 | manifest | `[Core] INFO: Parsing game manifest...` (after CDN manifest fetch; fetch failures warn `Unable to download manifest from "<host>" …, trying next URL...`) | `core.py:1527,1437,1445` |
| 6 | target | `[Core] INFO: Install path: D:\Games\Fortnite` ; `[Core] INFO: Selected CDN: <host> (https)` | `core.py:1602,1643` |
| 7 | analysis | `[DLM] INFO: Found previously interrupted download. Download will be resumed if possible.` / `Skipping N files based on resume data.` / `Found N missing files.` / `Found N files to skip based on install tag.` / `Remaining files after filtering: N` / `Processing order optimization is enabled, analysis may take a few seconds longer...` | `manager.py:124,150,167,181,214,236` |
| 8 | summary | `[cli] INFO: Install size: 123.45 MiB` / `[cli] INFO: Download size: 100.00 MiB (Compression savings: 19.0%)` / `[cli] INFO: Reusable size: 0.00 MiB (chunks) / 0.00 MiB (unchanged / skipped)` / `[cli] INFO: Downloads are resumable, you can interrupt the download with CTRL-C and resume it using the same command later on.` | `cli.py:1042-1049` |
| – | **nothing to do** | `[cli] INFO: Download size is 0, the game is either already up to date or has not changed. Exiting...` → exit 0 | `cli.py:1015` |
| 9 | checks (stdout) | `\nInstallation requirements check returned the following results:` then ` - Warning: …` / ` ! Failure: …`; on failure `[cli] CRITICAL: Installation cannot proceed, exiting.` exit 1 | `cli.py:1055-1069` |
| 10 | confirm | prompt `Do you wish to install "Fortnite"? [Y/n]: ` (auto with `-y`) | `cli.py:1074-1077` |
| 11 | start | `[DLManager] INFO: Download Manager running with process-id: 12345`, `Starting download workers...`, `Starting file writing worker...` | `manager.py:621,675,688` |
| 12 | progress | 5 lines/second, see §4.3 | `manager.py:766-775` |
| 13 | finish | `[DLManager] INFO: Waiting for installation to finish...`, `[DLManager] INFO: All done! Download manager quitting...` | `manager.py:792,823` |
| 14 | post | stdout: `\nThis game lists the following prerequisites to be installed:` / `- <name>: <path> <args>` (prereq present); `\nThe following DLCs are available for this game:` … ; stderr `This game supports cloud saves…` | `cli.py:1103,1158-1160,1105-1111` |
| 15 | done | `[cli] INFO: Finished installation process in 1234.56 seconds.` | `cli.py:1155` |
| ✗ | failure | `[cli] INFO: Installation failed after 12.34 seconds.` + `[cli] WARNING: The following exception occurred while waiting for the downloader to finish: …` (exit 0) | `cli.py:1090-1093` |

The authoritative "success" marker is the final `Finished installation process in …` line **and** post-install `installed.json` containing the new `version`; the downloader process's own exit code is not inspected (`dlm.join()` without exitcode check, `cli.py:1086-1087`), so a crashed DLManager still reaches that line [inferred from source].

### 4.3 Progress lines — verbatim format strings and example

```python
# downloader/mp/manager.py:766-775 (logger 'DLManager', level INFO, one block per ~1.0 s `update_interval`)
self.log.info(f'= Progress: {perc:.02f}% ({processed_chunks}/{num_chunk_tasks}), '
              f'Running for {rt_hours:02d}:{rt_minutes:02d}:{rt_seconds:02d}, '
              f'ETA: {hours:02d}:{minutes:02d}:{seconds:02d}')
self.log.info(f' - Downloaded: {total_dl / 1024 / 1024:.02f} MiB, '
              f'Written: {total_write / 1024 / 1024:.02f} MiB')
self.log.info(f' - Cache usage: {total_used:.02f} MiB, active tasks: {self.active_tasks}')
self.log.info(f' + Download\t- {dl_speed / 1024 / 1024:.02f} MiB/s (raw) '
              f'/ {dl_unc_speed / 1024 / 1024:.02f} MiB/s (decompressed)')
self.log.info(f' + Disk\t- {w_speed / 1024 / 1024:.02f} MiB/s (write) / '
              f'{r_speed / 1024 / 1024:.02f} MiB/s (read)')
```
Resulting stderr (note two spaces after `INFO:` on the indented lines; `\t` is a literal TAB; each line CRLF on Windows):
```
[DLManager] INFO: = Progress: 12.34% (4321/35000), Running for 00:01:23, ETA: 00:09:51
[DLManager] INFO:  - Downloaded: 812.50 MiB, Written: 1024.00 MiB
[DLManager] INFO:  - Cache usage: 96.00 MiB, active tasks: 16
[DLManager] INFO:  + Download	- 55.20 MiB/s (raw) / 120.40 MiB/s (decompressed)
[DLManager] INFO:  + Disk	- 118.00 MiB/s (write) / 0.00 MiB/s (read)
```
(Examples are synthetic from the format strings — not captured from a live install.)

Suggested regexes (per line; logger name could also be `DLM` pre-run, but progress is always `DLManager`):
```
^\[DLManager\] INFO: = Progress: (?<pct>\d+\.\d{2})% \((?<done>\d+)/(?<total>\d+)\), Running for (?<rh>\d{2,}):(?<rm>\d{2}):(?<rs>\d{2}), ETA: (?<eh>\d{2,}):(?<em>\d{2}):(?<es>\d{2})$
^\[DLManager\] INFO:  - Downloaded: (?<dl>\d+\.\d{2}) MiB, Written: (?<wr>\d+\.\d{2}) MiB$
^\[DLManager\] INFO:  - Cache usage: (?<cache>\d+\.\d{2}) MiB, active tasks: (?<active>\d+)$
^\[DLManager\] INFO:  \+ Download\t- (?<raw>\d+\.\d{2}) MiB/s \(raw\) / (?<dec>\d+\.\d{2}) MiB/s \(decompressed\)$
^\[DLManager\] INFO:  \+ Disk\t- (?<w>\d+\.\d{2}) MiB/s \(write\) / (?<r>\d+\.\d{2}) MiB/s \(read\)$
```
**How to compute**
* **%**: `pct` directly. It is `processed_chunks/num_chunk_tasks` where chunk tasks = *writer-side chunk writes* (a chunk used in several files counts several times, reused-from-disk chunks count too) — i.e. write progress, not download bytes; it can sit at 0.00% while the first big files open. 100% ⇒ about to finish (then `Waiting for installation to finish...`).
* **Speed**: `raw` = compressed network rate (bytes/s over the last ~1 s; `total_dl`/`dl` counts **compressed** bytes). `dec` = decompressed rate. `w` = disk write rate.
* **Downloaded bytes**: `dl` MiB (compressed, cumulative **this run**, after a resume it restarts from 0). Compare against `Download size:` from the summary line (`cli.py:1044`) for a bytes-accurate bar, noting that `Download size` is the compressed total for chunks actually needed, resume-adjusted.
* **ETA**: legendary's own = `(chunks_left)/(avg chunks per second since start)` (`manager.py:754-758`), i.e. a long-run average; `00:00:00` until the first chunk completes. Smooth your own from `raw` speed and remaining `Download size − dl` if you want.
* Stall detection: if no `= Progress` line for >30 s, or `Downloaded` unchanged for >60 s, with `Chunk download for … failed` spam → treat as stalled (§4.8).
* There is **no** machine-readable progress channel in the CLI: `status_q` (`UIUpdate`) is only used by library callers and the CLI passes none (`cli.py:991-1010`). Workers' own log lines: `[DLWorker N] WARNING: Chunk download for <guid_num> failed: (<exc repr>), retrying...` (`workers.py:107`), `[DLWorker N] ERROR: Job for <guid> failed with: …` (`:120`), `[DLManager] ERROR: Download for <guid> failed, retrying...` (`manager.py:552`), `[FileWorker] WARNING: Writer queue empty!` (every 10 s when idle, `workers.py:185`).

### 4.4 Free-space check
`check_installation_conditions` (`core.py:1730-1747`): `base_path = dirname(install_path)`; `required = analysis.disk_space_delta` = the maximum running total of bytes for *added/changed files* (full uncompressed size, patching subtracts old file sizes, `manager.py:243-265`) — **not** the download size (and it does not model extra `<file>.tmp` copies beyond the running-total logic); `free = shutil.disk_usage(base_path).free`.
```
 ! Failure: Not enough available disk space! 12.34 GiB < 98.76 GiB      # stdout, from print(' ! Failure:', msg)
[cli] CRITICAL: Installation cannot proceed, exiting.                   # stderr, exit 1
```
With `--ignore-free-space`: ` - Warning: Potentially not enough available disk space! 12.34 GiB < 98.76 GiB`. If the parent dir doesn't exist: ` ! Failure: Install path "<base>" does not exist, make sure all necessary mounts are available. If you previously deleted the game folder without uninstalling, run "legendary uninstall -y Fortnite" first.` The check runs once, *after* analysis (i.e. after the manifest fetch) and before any download starts; running out of disk **mid-install** is not detected (§0 item 10). Glider should pre-check using `info Fortnite --json`'s `manifest.tag_disk_size` sums.

### 4.5 Shared memory error (analysis)
`MemoryError: Current shared memory cache is smaller than required: 2048.0 MiB < 2150.0 MiB. Try running legendary with "--enable-reordering" to reduce memory usage, or use "--max-shared-memory 2182" to increase the limit.` (`manager.py:419-432`) — raised from `prepare_download` → **uncaught traceback, exit 1**, before the summary lines.

### 4.6 Update-needed / not-needed logic
Version check is simply `asset.build_version != installed.version` (strict string inequality; `launch` uses the same, `cli.py:647`). An install of the same version as the manifest still runs analysis: it compares manifests and (when there's no resume file) adds any **missing files** to "added" (`manager.py:154-170`) — that is how new SDL tags get installed without a repair, and why an "up to date" `install` can still download things after tag changes.

### 4.7 Resume
* `core.prepare_download` picks the file (`core.py:1604-1617`): `repair` ⇒ `tmp\Fortnite.repair`; normal ⇒ `tmp\Fortnite.resume`; `--force` ⇒ none. Filename = `clean_filename(f'{app_name}.resume')`.
* Format: UTF-8 text, `<sha1-hex-of-manifest-file-hash>:<path as in manifest>\n`, one per completed file (`manager.py:579-586`); `.tmp` suffix stripped. Not rewritten, only appended; removed at the end (`manager.py:811-816`).
* On re-run (same command): analysis reads it (`manager.py:123-152`): for each line the file must exist in the install dir **and** the recorded hash must equal the manifest's current hash for that path; matching files are moved to `unchanged` (skipped); stale ones warn `N previously completed file(s) are missing, they will be redownloaded.` / `N existing file(s) have been changed and will be redownloaded.`. A *new build* between interruption and resume simply re-downloads everything whose hash changed. Partially written files are not in the list ⇒ restarted from 0 (they are truncated by `open(path,'wb')`, `workers.py:238`).
* An interrupted **fresh** install is not recorded in `installed.json` (that happens only after the downloader exits cleanly, `cli.py:1101`). An interrupted **update** keeps the *old* version in `installed.json` while some files are already new → game is in a mixed state until the update completes; Glider should mark "update in progress / do not launch" itself. `launch` (without `--skip-version-check`) refuses to start an out-of-date game: `Game is out of date, please update or launch with update check skipping!`.
* `.tmp` files (`<name>.tmp`) are used only when an updated file reuses chunks from its old version (`manager.py:393-401`); a kill can leave them behind (harmless, overwritten next time).

### 4.8 Cancel / pause on Windows
* Internals: `DLManager` is a `multiprocessing.Process` (spawn on Windows) of the same frozen exe; `KeyboardInterrupt` in it logs `[DLManager] WARNING: Immediate exit requested!`, joins threads (5 s), `terminate()`s children (`manager.py:625-655`); workers log `Immediate exit requested, quitting...`. The CLI parent catches KeyboardInterrupt in `dlm.join()` (BaseException is not caught by the `except Exception`, `cli.py:1088`) → `Command was aborted via KeyboardInterrupt, cleaning up...` → `config` saved → **exit 0**, `installed.json` untouched, resume file intact.
* Graceful stop from a GUI host is awkward on Windows: a console Ctrl-C only reaches processes sharing a console; a process started with `CREATE_NEW_PROCESS_GROUP` ignores Ctrl-C; `CTRL_BREAK_EVENT` is not converted to KeyboardInterrupt by CPython [unverified for this PyInstaller build]. I did not test a console-event approach.
* **Recommended: hard stop = kill the whole tree, then re-run the same command to resume.** Create legendary inside a Job Object with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` (or `taskkill /PID <root> /T /F`). Because resume state is per completed file and `installed.json` is only written on success, this is safe for fresh installs; cost = restart of in-flight files (can be multi-GB pak files) and a re-login + manifest fetch + analysis on resume. Never kill only `DLManager`: the parent would then run the post-install branch and mark the game installed (`cli.py:1094-1155` has no exit-code check) [inferred].
* Avoid orphaning: the parent being killed does not stop the `DLManager`/workers (no parent-death monitoring in the code) [inferred].
* "Pause" = cancel + resume; keep `tmp\Fortnite.resume`, don't run `cleanup`.
* Stall watchdog (since workers retry forever): if no new `= Progress` line within e.g. 30–60 s, treat as stalled → kill tree → surface "connection lost".

### 4.9 Concurrency
One mutating legendary at a time (lock, §1.2). Read-only commands (`status --offline`, `list-installed`, `info --offline`) take no lock but `list-installed`/`status` run EGL sync when enabled (`core.py:709-714`) and can try to acquire it (skipped silently if held, `core.py:2092-2094`).

---

## 5. verify / move / uninstall / import / egl-sync / launch

### 5.1 `verify <app>` (`cli.py:1240-1346`)
No flags. Offline (uses the installed manifest from `manifests\`). SHA-1 of every file (filtered by `[Fortnite] install_tags` if set, `cli.py:1281-1289`), sequential single thread. stdout progress, **carriage-return updated** (split on `\r`):
```
Verification progress: 123/456 (27.0%) [210.5 MiB/s]\t\r     # cli.py:1315
=> Verifying large file "FortniteGame/Content/Paks/x.pak": 45% (1234.0/2740.0 MiB) [200.1 MiB/s]\t\r   # lfs/utils.py:140 (files >512 MiB)
Verification progress: 456/456 (100.0%) [210.5 MiB/s]\t\n    # final, cli.py:1332
```
Result lines (stderr): `[cli] ERROR: File does not match hash: "<path>"`, `[cli] ERROR: File is missing: "<path>"`, `[cli] ERROR: Other failure (see log), treating file as missing: "<path>"`, `[LFS Utils] CRITICAL: Could not verify "<path>"; opening failed with: <exc>` (locked file / permissions), then **success**: `[cli] INFO: Verification finished successfully.` **failure**: `[cli] ERROR: Verification failed, N file(s) corrupted, M file(s) are missing.` + `[cli] INFO: Run "legendary repair Fortnite" to repair your game installation.` Exit code is **0 either way**. Always writes `tmp\Fortnite.repair` (matching files `hash:path`; mismatching files with their *actual* on-disk hash, which won't equal the manifest hash so they are redownloaded). Missing manifest: `[cli] CRITICAL: Manifest appears to be missing! To repair, run "legendary repair Fortnite --repair-and-update" …`.
Repair flow: `repair` runs verify (prompting `Verify "Fortnite" now ("no" will abort repair)?` unless `-y`), then downloads only failed/missing files; at the end removes `.repair` and clears `needs_verification` (`cli.py:1016-1022,1139-1146`).

### 5.2 `move <app> <New Base Path> [--skip-move]` (`cli.py:2630-2669`)
`os.rename(old_install_path, <new base>\<folder>)`, then rewrites `installed.json` (and re-exports the EGL `.item` if EGL sync enabled via `core.install_game`). Same-volume only; messages: `Moving to a different drive is not supported. Move the folder manually to "…" and run "legendary move Fortnite "…" --skip-move"` (errno 18; **[unverified]** whether Windows' cross-drive rename maps to errno 18 in CPython), `The target path already contains a folder called "…", please remove or rename it first.`, `Moving failed with unknown error …`. `--skip-move` = only rewrite metadata (use after Glider moves the files itself across drives). Finish line `[cli] INFO: Finished.`. Errors exit 0. Needs the lock.

### 5.3 `uninstall <app> [--keep-files] [--skip-uninstaller]` (`cli.py:1187-1221`)
Prompt `Do you wish to uninstall "…"? [y/N]` (bypassed by `-y`). On Windows, if the manifest declares an uninstaller it is **run** unless `--skip-uninstaller` (`cli.py:1204-1205`, `_handle_uninstaller`). Deletes only files listed in the installed manifest (filtered by install tags) then empty dirs, then the game root if empty (`core.py:1853-1864`, `lfs/utils.py:34-85`), removes the entry from `installed.json`; if `egl_guid` set also deletes the `.item` and the whole `.egstore` (`core.py:2065-2075`). Extra files (user configs, `.cache`) keep the folder alive. `--keep-files` = just forget. Not installed: `[cli] ERROR: Game Fortnite not installed, cannot uninstall!` exit 0 [run].

### 5.4 `import <app> <dir> [--disable-check] [--with-dlcs] [--skip-dlcs] [--platform]` (`cli.py:1348-1446`, `core.py:1890-1958`)
Order: lock → abs path → exists? (`Specified path "…" does not exist!` [run, exit 0]) → already installed? (`Game is already installed!`) → **login required** (`Log in failed!`) → refresh metadata → manifest source:
 1. If `<dir>\.egstore` exists (EGL-installed): find `*.mancpn` with `AppName == app` (JSON `{FormatVersion, AppName, CatalogItemId, CatalogNamespace}`) → use `<guid>.manifest` from `.egstore` (**no network manifest**), log `Found EGL install metadata, verifying...`. If `.egstore\bps` exists, or `.egstore\Pending` non-empty ⇒ install considered in-progress ⇒ `needs_verification = True`; otherwise `needs_verification=False` (`core.py:1916-1924`).
 2. Else downloads the **latest** CDN manifest (`Downloading latest manifest for "Fortnite"`) and records that version as installed with `needs_verification = True`.
 The chosen manifest is saved into `manifests\`; `installed.json` gets version = that manifest's `build_version`, `install_size = Σ file sizes` (not tag-filtered), `install_tags` empty, `egl_guid=''`.
Checks: counts how many manifest files exist (`ratio`); none ⇒ `No files belonging to Game "…" (Fortnite) found in the specified location, please verify that the path is correct.` (+ `Did you mean "<dir>\Fortnite"?`); launch exe missing ⇒ `Game executable could not be found at "…", please verify that the specified path is correct.` (skipped by `--disable-check`); `<95%` found ⇒ warning `Some files are missing…`; else `Game install appears to be complete.`
Outcome lines: `NOTE: The Game installation will have to be verified before it can be updated with legendary.` + `Run "legendary repair Fortnite" to do so.` **or** `Installation had Epic Games Launcher metadata for version "<v>", verification will not be required.` then `Game "Fortnite" has been imported.`
**Writes into the game folder?** No (only `legendary` data dir), *unless* EGL sync is enabled, in which case `core.install_game` → `egl_export` creates `.egstore\<guid>.manifest/.mancpn` + the EGL `.item` (`core.py:1824-1830,2026-2063`). It does **not** verify files (only existence counting). Note: while `needs_verification` is true, `install` auto-switches to repair mode (`cli.py:854-856`: `Game needs to be verified before updating, switching to repair mode...`) — a full SHA-1 pass over the whole install (>100 GB) before any update. Adoption of an EGL install therefore avoids that only if `.egstore` has its manifest and no `bps`.

### 5.5 `egl-sync` (`cli.py:1448-1599`, §6)
Flags: `--egl-manifest-path <dir>`, `--egl-wine-prefix <dir>` (non-Windows; on Windows the ProgramData path is preset), `--enable-sync`, `--disable-sync`, `--one-shot`, `--import-only`, `--export-only`, `--migrate`, `--unlink`. Needs the lock and previously fetched assets (`Legendary is missing game metadata, please login …` [run: `fx:egl-sync-nodata.stderr.txt`]). Non-interactive: `legendary -y egl-sync --one-shot [--import-only|--export-only]`. Without `--one-shot` it asks `Enable automatic synchronization?` (answered yes by `-y` ⇒ persists `egl_sync = True`, after which **every** `list-installed/status/launch` runs a sync). `--unlink`/`--migrate` blank `egl_guid` in installed.json; `--migrate` also deletes EGL `.item`s ("remove them from EGL", `cli.py:1590-1595`; `.egstore` is deleted by `egl_uninstall`).

### 5.7 `launch <app>` (`cli.py:594-744`; `fx:help-launch.txt`)
Flags: `--offline`, `--skip-version-check`, `--override-username <name>`, `--dry-run`, `--language <xx>`, `--wrapper <cmd>`, `--set-defaults`, `--reset-defaults`, `--override-exe <relative path>`, `--json`, (`--origin`, `--ubisoft` irrelevant); `--wine/--wine-prefix/--no-wine` are hidden no-ops on Windows (`no_wine` defaults True, `cli.py:2934-2939`). Any unrecognised args are passed straight to the game (`extra`, `cli.py:3114,931`) — typos included.
Sequence: resolve alias → installed? (`Game Fortnite is not currently installed!` [run], exit 1) → install dir exists (`Install directory "…" appears to be deleted, cannot launch Fortnite!`, exit 1) → unless offline: login (`Login failed, cannot continue!`) → unless `--skip-version-check` / config `skip_update_check`: refresh the asset list (1 network call) and `latest.build_version != igame.version` ⇒ `Game is out of date, please update or launch with update check skipping!` exit 1 → `get_launch_parameters`: gets a fresh **exchange code** via `GET …/account/api/oauth/exchange` (`core.py:889-892`, skipped with `--offline`) and, if `requires_ot`, an ownership token written to `tmp\<ns><cat>.ovt`.
Parameter assembly (`core.py:830-936`, `cli.py:694-701`) — final argv:
```
[wrapper…] <install_path>\<manifest launch_exe>  <manifest launch_command> <catalog AdditionalCommandLine> <extra/user args + [Fortnite] start_params> <egl params>
egl params = -AUTH_LOGIN=unused -AUTH_PASSWORD=<exchange code> -AUTH_TYPE=exchangecode -epicapp=Fortnite -epicenv=Prod
             [-epicovt=<path>] -EpicPortal -epicusername=<displayName> -epicuserid=<account_id> -epiclocale=<lang> -epicsandboxid=<namespace>
             [-epicdeploymentid=<sidecar.config.deploymentId>]
```
Working dir = directory of the exe. Env = current env + `[default.env]` + `[Fortnite.env]`. `--language` default = system/config locale language code.
Actual start: `subprocess.Popen(full_params, cwd=…, env=…, shell=os.name=='nt')` — **non-blocking; legendary then exits 0 immediately** (config saved, update notice printed). On Windows the game is a child of legendary's python process but outlives it. Beware Job Objects: if Glider places legendary in a KILL_ON_JOB_CLOSE job the game dies with the job.
`--dry-run` (without `--json`): `[cli] INFO: Not Launching Fortnite (dry run)` / `Launch parameters: <shlex.join argv>` / `Working directory: …` / `Environment overrides: k=v, …` — still performs login + exchange-code fetch.
`--json` ⇒ stdout JSON `{"game_parameters":[…],"game_executable":"…","game_directory":"…","egl_parameters":[…],"launch_command":[…],"working_directory":"…","user_parameters":[…],"environment":{…},"pre_launch_command":"","pre_launch_wait":false}`. Contains a **live single-use exchange code** in `egl_parameters[1]` (`-AUTH_PASSWORD=…`) — treat stdout as secret, don't log it. Constructed example (keys verified from source; values illustrative):
```json
{"game_parameters": [], "game_executable": "FortniteGame/Binaries/Win64/FortniteLauncher.exe", "game_directory": "D:\\Games\\Fortnite",
 "egl_parameters": ["-AUTH_LOGIN=unused","-AUTH_PASSWORD=<code>","-AUTH_TYPE=exchangecode","-epicapp=Fortnite","-epicenv=Prod","-EpicPortal","-epicusername=<name>","-epicuserid=<id>","-epiclocale=en","-epicsandboxid=fn"],
 "launch_command": [], "working_directory": "D:\\Games\\Fortnite\\FortniteGame/Binaries/Win64", "user_parameters": [], "environment": {}, "pre_launch_command": "", "pre_launch_wait": false}
```
(`game_executable` comes from the manifest `launch_exe`; read it from `info Fortnite --offline --json` → `manifest.launch_exe`.) `launch --offline`: no login, no version check, empty `-AUTH_PASSWORD=`; Fortnite is online-only so useless here (`can_run_offline` warning `Game is not approved for offline use and may not work correctly.`, `core.py:894`).
**Option for Glider**: call `launch Fortnite --json` (+`--skip-version-check` if Glider has already compared versions), then `CreateProcess` the argv itself outside the job object, so Glider owns the PID and can monitor/stop it. The exchange code is single-use and short-lived; spawn immediately. Epic's anti-cheat (BattlEye/EAC) and `FortniteLauncher.exe` behaviour under a non-EGL parent **[unverified]**.

---

## 6. EGL interoperability

### 6.1 Paths (Windows) — `lfs/egl.py:15-21`
| What | Path | Legendary action |
|---|---|---|
| EGL per-game manifests | `%PROGRAMDATA%\Epic\EpicGamesLauncher\Data\Manifests\*.item` | read all (`read_manifests`, `egl.py:49-60`; keyed by `AppName`); write `<InstallationGuid>.item` (`egl.py:77-88`); delete (`egl.py:90-97`) |
| EGL user settings | `%LOCALAPPDATA%\EpicGamesLauncher\Saved\Config\Windows\GameUserSettings.ini` | **read only**, for `auth --import` (`[RememberMe] Data`) |
| `…\UnrealEngineLauncher\LauncherInstalled.dat` | — | **not referenced anywhere in 0.21.1** (grep: 0 hits). Legendary neither reads nor writes it. (EGL regenerates it from its manifests **[unverified]**.) |
| Per-game `<install>\.egstore\` | `<guid>.manifest` (binary manifest copy), `<guid>.mancpn` (JSON), `bps\`/`Pending\` (EGL in-progress markers) | `egl_export` writes `.manifest` + `.mancpn` (`core.py:2047-2060`); `egl_import`/`import` read them (`core.py:2006-2017,1895-1914`); `egl_uninstall` deletes the whole `.egstore` (`core.py:2074-2075`) |
EGL integration is **only active when** `egl_sync = true` (default false) or via explicit `egl-sync`/`import` of an EGL-installed game.

### 6.2 `.item` schema (EGLManifest) — `models/egl.py`
Template defaults (`models/egl.py:6-46`), written merged with whatever `.item` content was loaded (`remainder`, `:100,105`) and with these overrides from `from_lgd_game` (`:129-153`; written by `to_json` `:103-127`, `json.dump(indent=4, sort_keys=True)`, `egl.py:88`):

| `.item` key | Value legendary writes | Source of value |
|---|---|---|
| `FormatVersion` | `0` | template |
| `AppName` | `Fortnite` | `game.app_name` |
| `AppVersionString` | installed build version | `igame.version` (**this is what EGL & sync compare**) |
| `BaseURLs` | list | `igame.base_urls` |
| `BuildLabel` | `"Live"` | constant |
| `CatalogItemId` / `CatalogNamespace` | catalog id / `fn` | `game.catalog_item_id` / `game.namespace` |
| `DisplayName` | `Fortnite` | `igame.title` |
| `InstallLocation` | `D:\Games\Fortnite` | `igame.install_path` |
| `InstallSize` | bytes | `igame.install_size` |
| `InstallTags` | list | `igame.install_tags` (legendary's list incl. `""` entries; EGL's own native tag format may differ → possible sync flapping **[inferred]**) |
| `InstallationGuid` | 32-char UPPER hex (uuid4 no dashes) | `igame.egl_guid` (`core.py:1827,2042`); also filename `<guid>.item` |
| `LaunchCommand` | manifest launch command | `igame.launch_parameters` |
| `LaunchExecutable` | manifest `launch_exe` | `igame.executable` |
| `MainGameAppName` | `Fortnite` | `game.app_name` (todo: base game for DLC) |
| `MandatoryAppFolderName` | `customAttributes.FolderName.value` | catalog metadata |
| `ManifestLocation` | `<install>/.egstore` (forward slash) | f-string |
| `StagingLocation` | `<install>/.egstore/bps` | f-string |
| `OwnershipToken` | `"true"`/`"false"` (string) | `str(requires_ot).lower()` |
| `bCanRunOffline` | bool | `igame.can_run_offline` |
| `bIsIncompleteInstall` | `false` | constant |
| `bNeedsValidation` | bool | `igame.needs_verification` |
| other template keys kept at template defaults | `AppCategories ["public","games","applications"]`, `ChunkDbs []`, `CompatibleApps []`, `FullAppName ""`, `HostInstallationGuid ""`, `InstallComponents []`, `InstallSessionId ""`, `MainWindowProcessName ""`, `PrereqIds []`, `ProcessNames []`, `TechnicalType ""`, `VaultThumbnailUrl ""`, `VaultTitleText ""`, `bIsApplication true`, `bIsExecutable true`, `bIsManaged false`, `bRequiresAuth true` | `_template` |
Read side (`from_json` `:75-101`) tolerates missing keys (defaults) but **requires `AppName`**. `to_lgd_igame` (`:155-161`) maps `AppVersionString→version`, `InstallLocation→install_path`, `LaunchExecutable→executable`, `bNeedsValidation→needs_verification`, `InstallTags→install_tags`, `InstallationGuid→egl_guid`, `InstallSize`, `OwnershipToken→requires_ot`, `bCanRunOffline`.
Illustrative `.item` for Fortnite as legendary would write (values placeholder): see keys above; pretty-printed, sorted keys.

### 6.3 `.mancpn`
`{"AppName":"Fortnite","CatalogItemId":"<id>","CatalogNamespace":"fn","FormatVersion":0}` indent 4, sorted (`core.py:2056-2060`). `.manifest` = byte copy of `manifests\Fortnite_Windows_<ver>.manifest`.

### 6.4 How sync decides (`core.py:2088-2136`)
* EGL → Legendary (`egl_sync`): for each `.item` with `MainGameAppName == AppName` and an owned asset (`asset_valid`, **Windows platform assets only**, `core.py:500-502`): not installed in legendary ⇒ `egl_import`; installed but `AppVersionString` or `InstallTags` differ ⇒ re-import ("App "X" has been updated from EGL, syncing...").
* `egl_import` (`core.py:1972-2024`): converts `.item`→InstalledGame, requires `<InstallLocation>\.egstore\<guid>.manifest` (else `Game Manifest "…" not found, cannot import!`), saves that manifest into `manifests\` under its own `build_version`, copies `InstallTags` into `[Fortnite] install_tags`, marks installed. **No verification** is done.
* Legendary → EGL (`egl_export`): after every `install_game` when sync enabled & Windows & not DLC (`core.py:1824-1830`): assign GUID if empty, copy the **current** installed manifest to `.egstore\<guid>.manifest`, write `.mancpn`, write `<guid>.item`. This is the step that keeps EGL from re-downloading after a Legendary update: new `AppVersionString`, new `.egstore` manifest copy, new `InstallSize/InstallTags`. If sync is **not** enabled none of these are updated ⇒ EGL would still see the old `.item`/old manifest.
* Removal detection: a synced game whose `.item` vanished ⇒ `egl_restore_or_uninstall` (`core.py:2077-2086`): exe still present ⇒ re-export, else uninstall (files kept).
* `egl_export` needs `programdata_path` to exist, else `ValueError('EGS ProgramData path does not exist')` (`egl.py:81-82`) — crash if EGL is not installed but sync enabled.
* Pitfalls: (a) mismatch of `InstallTags` between EGL-native lists and Legendary's SDL tag list (§9.3: `""` + 27 names) ⇒ constant re-import; (b) Fortnite's EGL-side layout uses `.egstore\bps` while EGL is mid-install; an import while that exists marks `needs_verification` (full hash pass); (c) the two launchers must not both update concurrently; (d) EGL `.item` writes use forward slashes in `ManifestLocation`/`StagingLocation` while `InstallLocation` is native; (e) legendary rewrites sidecar files only for Windows titles (`platform.startswith('Win')`, `core.py:1825`). Decision for Glider: leave `egl_sync` **off** unless explicitly offering EGL coexistence.

---

## 7. User-facing error / critical messages (map to human errors)

Format on the wire: `[<logger>] <LEVEL>: <message>` on stderr. Levels: `fatal` → `CRITICAL`.

| Human error | Message (verbatim / template) | Level | Source |
|---|---|---|---|
| Not signed in (crash) | `ValueError: No saved credentials` (traceback; exit 1) | – | `core.py:218` [run] |
| Session expired / revoked | `Stored credentials are no longer valid! Please login again.` | ERROR | `core.py:269` |
| Login rejected | `Login to EGS API failed with errorCode: <code>` / `Resuming failed due to invalid credentials: …` / `Logging in failed with <exc>, please try again.` / `Login attempt failed, please see log for details.` | ERROR/WARN | `egs.py:92,133`, `core.py:259,166-208`, `cli.py:208` |
| Epic requires user action | `<errorMessage> (<correctiveAction>), open the following URL to take action: <continuationUrl>` | ERROR | `egs.py:130` |
| Login failed (callers) | `Login failed! Cannot continue with download process.` (install, exit 1); `Login failed, cannot continue!` (list/launch); `Log in failed!` (status exit 1 / import exit 0) | ERROR | `cli.py:868,213,636,1605,1367` |
| Network down at login | `HTTP request for login failed: <exc>, please try again later.` | ERROR | `core.py:273` |
| Legendary API unreachable | traceback `requests.exceptions.…ProxyError/ConnectionError … api.legendary.gl/v1/version.json` (exit 1) for `status`/`auth`; `Checking for Legendary updates failed: <exc>` (warning) elsewhere | – / WARN | `core.py:300,235`, `cli.py:112,1610` [run] |
| Another legendary running | `Failed to acquire installed data lock, only one instance of Legendary may install/import/move applications at a time.` (exit 0) | CRITICAL | `cli.py:846` (+ same at 1189,1350,1450,2632) |
| Game unknown / not owned | `Could not find "Fortnite" in list of available games, did you type the name correctly?` | ERROR | `cli.py:882` |
| Not installed | `Game Fortnite is not currently installed!` / `Game "Fortnite" is not installed` / `Game Fortnite not installed, cannot uninstall!` / `No installed game found for "Fortnite"` / `Update requested for "Fortnite", but app not installed!` / `Game "…" (…) is not installed!` | ERROR | `cli.py:616,1243,1196,2640,876,922` [run] |
| Install dir gone | `Install directory "<p>" appears to be deleted, cannot launch Fortnite!` (launch) / `Install path "<p>" does not exist, make sure all necessary mounts are available. If you previously deleted the game folder without uninstalling, run "legendary uninstall -y Fortnite" and reinstall from scratch.` (verify) / `… Install path "<base>" does not exist, make sure all necessary mounts are available…` (install) | CRIT/ERROR | `cli.py:623,1249`, `core.py:1745` |
| Not enough disk | `Not enough available disk space! <free> GiB < <need> GiB` (stdout ` ! Failure:` + `Installation cannot proceed, exiting.`) | CRITICAL | `core.py:1742`, `cli.py:1064-1069` |
| No write permission | `PermissionError: No write access to "<path>"` (traceback, exit 1) | – | `core.py:1598,1600` |
| Not enough RAM budget | `MemoryError: Current shared memory cache is smaller than required: … Try running legendary with "--enable-reordering" … "--max-shared-memory N"` | – | `manager.py:431` |
| Manifest fetch failures | `Unable to download manifest from "<host>" (Exception: …), trying next URL...` / `(status: N), trying next URL...` (WARN, per CDN); then `ValueError: Unable to get manifest from any CDN URL, last result: <status> (<reason>)`; `Manifest sha hash mismatch!`; `No manifest URLs returned by API`; `No base URLs found, please try again.`; `Decrypting manifest failed, key was missing, preloading isnt implemented yet` | WARN / exceptions | `core.py:1437,1445,1448,1451,1427,1635,1530` |
| Chunk/CDN download trouble | `Chunk download for <guid> failed: (<exc>), retrying...` / `… failed: status <code>, retrying...` (**infinite**); `Download for <guid> failed, retrying...` | WARN/ERROR | `workers.py:107,111`, `manager.py:552` |
| File write problems (disk full, AV lock, sharing violation) | `Something in writing a file failed: <exc>` ([Errno 28] No space left / [Errno 13]/[WinError 32]); `Writing for <file> failed!` (CRITICAL, install continues!); `Removing file failed: …`; `Renaming file failed: …`; `Job <file> failed with: …` | WARN/ERROR/CRIT | `workers.py:321,261,269,326`, `manager.py:590` |
| Verify: file in use / unreadable | `Could not verify "<file>"; opening failed with: <exc>` (CRITICAL; counted as missing) | CRITICAL | `lfs/utils.py:157` |
| Verify result | `File does not match hash: "<p>"`, `File is missing: "<p>"`, `Verification failed, N file(s) corrupted, M file(s) are missing.`, `Verification finished successfully.` | ERROR/INFO | `cli.py:1322-1344` |
| Update available (launch blocked) | `Game is out of date, please update or launch with update check skipping!` (exit 1) | ERROR | `cli.py:648` |
| Version/metadata missing | `Metadata for "Fortnite" does not exist, cannot launch!` | FATAL | `cli.py:644` |
| Launch exe invalid | `ValueError: Executable path is invalid: <path>` | – | `core.py:855` |
| Needs repair after import | `Game needs to be verified before updating, switching to repair mode...` | INFO | `cli.py:855` |
| SDL data failure | `Unable to get SDL data for Fortnite` / `Downloading SDL data failed with <exc>` | ERROR/WARN | `cli.py:973`, `core.py:453` |
| Aborted by Ctrl-C | `Command was aborted via KeyboardInterrupt, cleaning up...` | INFO | `cli.py:3217` |
| Unhandled crash | last line `[PYI-<pid>:ERROR] Failed to execute script 'cli' due to unhandled exception!` (preceded by a Python traceback) | – | [run] |
| Third-party store title | `The selected title has to be installed via a third-party store: <store>` (exit 0) | ERROR | `cli.py:887` |
| Install warnings Fortnite may emit | ` - Warning: This game is not marked for offline use (may still work).`, ` - Warning: This game requires an ownership verification token and likely uses Denuvo DRM.`, EOS service/bootstrapper warnings (`This game ships the Epic Online Services …`) | – | `core.py:1726-1728,1806-1814` |

---

## 8. Performance knobs

| Knob | Default | Where | Notes |
|---|---|---|---|
| `--max-workers` / `max_workers` | `min(2*cpu_count, 16)` | `manager.py:52` | Each is a **process** (PyInstaller bootstrap each). In-flight chunk jobs capped at `2*max_workers` (`manager.py:453`). Max meaningful 16 by default but any int is accepted. |
| `--max-shared-memory` / `max_memory` | **2048 MiB** (help text wrongly says 1 GiB) | `core.py:1645-1646`, `manager.py:658` | Allocated up-front as one `SharedMemory` block (commit charge on Windows). Must be ≥ analysis `min_memory` (grows with chunk re-use patterns) else MemoryError; segments = `size / biggest_chunk` (1 MiB chunks ⇒ ~2048 segments). |
| `--enable-reordering` | off (on for a few titles) | `manager.py:232-306`, `game_workarounds.py` | reduces RAM needs, costs analysis time, "may have adverse results"; capped at 100k files. |
| `--dl-timeout` | 10 s | `workers.py:55` | per HTTP request; no retry limit (bug) |
| `--preferred-cdn` | first CDN in API list | `core.py:1625-1636` | substring match on base URL host |
| `--no-https` / `disable_https` | HTTPS | `core.py:1429,1638` | only useful behind a cache |
| `--bind` | none | `manager.py:54,677-684` | round-robin local IPs |
| `--disable-patching`, `--disable-delta-manifests`, `--force` | patching + delta on | `core.py:1496,1539` | patching reuses old-file chunks (saves bandwidth, adds disk reads; "Disk … (read)" column) |
| SDL / install tags | see §9 | | the biggest lever on size |
| Verify | single thread SHA-1, 1 MiB reads | `lfs/utils.py:134` | disk-bound; repair verification runs before any repair/update after an import |
| Update interval | 1.0 s log cadence | `manager.py:34` | |
| API timeout | `-A <seconds>`, 10 | `cli.py:2737` | global flag, before the sub-command |

Download chunk URL = `<base_url>/<chunk.path>` per chunk, fetched by `requests` with a per-process session (`manager.py:465`); UA `EpicGamesLauncher/11.0.1-14907503+++Portal+Release-Live Windows/10.0.19041.1.256.64bit` (`workers.py:49`).

---

## 9. Fortnite specifics in 0.21.1

### 9.1 App name & SDL wiring
* `utils/selective_dl.py:27-30`: `games = {'Fortnite': _fortnite_sdl, 'Ginger': _cyberpunk_sdl}`; `get_sdl_appname(app)` returns a key if `app.startswith(key)` (skipping `*_Mac`) → `Fortnite` (also matches e.g. `FortniteXYZ`).
* SDL is active when no `--install-tag` is given, the title isn't DLC and `disable_sdl` isn't set (`cli.py:942-960`). First install, or `--reset-sdl`, or no `install_tags` in config ⇒ fetch SDL data; otherwise reuse `[Fortnite] install_tags` (`cli.py:961-975`).
* `core.get_sdl_data` (`core.py:434-457`): hardcoded table → overridden by `tmp\Fortnite.json` cache → re-downloaded from `https://api.legendary.gl/v1/sdl/Fortnite.json` when `version.json.data.game_overrides.sdl_config.Fortnite` (**14** today, `fx:api-version.json.cached.json`) is newer than the cached `version`. Download failure ⇒ warning + fallback to hardcoded/stale.

### 9.2 Hardcoded fallback (stale in practice) — `selective_dl.py:16-25`
`__required`: `chunk0, chunk10` ("Fortnite Core"); `stw: chunk11, chunk11optional`; `hd_textures: chunk10optional`; `lang_de: chunk2`, `lang_fr: chunk5`, `lang_pl: chunk7`, `lang_ru: chunk8`, `lang_cn: chunk9`.

### 9.3 Live SDL v14 [net, `fx:api-sdl-Fortnite.json`]
```
__required (name "Fortnite Core, Battle Royale, and Save the World"): tags
  Startup FrontEnd Encrypted Globals DefaultGameplayChunk UnusedOldEarlyStartupPatcherChunk CosmeticPreInstall FatCosmo
  GFP_BaseInstallRoot GFP_BRCosmetics GFP_BRCosmeticsInstallOnDemand GFP_BRCosmeticsInstallOnDemandFat GFP_CreativeRoot
  GFP_BRRoot GFP_JunoRoot GFP_DelMarRoot GFP_Sparks GFP_BlitzRoot GFP_StrideMiceRoot GFP_SaveTheWorldRoot
  FortniteBR FortniteCatchAll FNOne Langde Langfr Langpl Langru ""
epic_assets (name "Epic Quality Assets (Streamed by default)"): the same set with an "Optional" suffix
  (StartupOptional … FortniteBREpicOptionalInstall) — optional pack key = `epic_assets`
```
With `--skip-sdl`: `install_tag = [''] + __required.tags` (duplicate `''` harmless) → saved as `[Fortnite] install_tags = ,Startup,FrontEnd,…,Langru,` (`cli.py:971`). Adding the optional pack non-interactively = pass explicit `--install-tag` values (`--install-tag ""` plus each tag), which skips SDL and stores them. The prompt text would be `Please enter tags of pack(s) to install (space/comma-separated, e.g. "epic_assets")` (`utils/cli.py:52-54`) — only one optional key exists in v14, so the example list is `epic_assets`.
To size the packs: `info Fortnite --json` → `manifest.install_tags`, `tag_disk_size`, `tag_download_size` (sum the chosen tags; `""` is the untagged bucket).
Changing the tag set later: re-run `install` with the new tags: files newly needed are downloaded ("Found N missing files"), now-unneeded files are deleted (`Deleting now untagged files.`, `cli.py:1148-1153,1027-1029`, `core.py:1866-1883`).

### 9.4 Other Fortnite facts
* Install folder: `customAttributes.FolderName.value` from catalog metadata (for Fortnite expected `Fortnite` **[unverified]**), joined to `--base-path`.
* Windows executable overrides apply only to `kinglet` (Civ VI) in the hardcoded table / `executable_override` in `version.json` — none for Fortnite (`fx:api-version.json.cached.json`).
* Fortnite is not in `reorder_optimization`.
* `game_wiki` (tips) has no Fortnite entry (`get_game_tip` returns None).
* `launch_exe` / prerequisites / uninstaller / `can_run_offline` for Fortnite come from the live manifest + catalog; I could not fetch them (no entitlement). Capture them with `info Fortnite --json` on a signed-in machine and add to the fixtures.

---

## 10. Quick recipes (suggested, derived from the above)

| Need | Command (stdout / stderr) |
|---|---|
| Is signed in? | `status --offline --json` → `account != "<not logged in>"` |
| Sign in | `auth --code <authorizationCode> --disable-webview` (stderr `Successfully logged in as "<name>"`) |
| Sign out | `auth --delete` |
| Validate/refresh | `status --json` (needs api.legendary.gl + Epic) |
| Installed? version? | read `installed.json` or `list-installed --json` (offline) |
| Update available? | `list-installed --check-updates --tsv` (`Update available` col) or `info Fortnite --json` (`game.version` vs `install.version`) |
| Install/update | `-y install Fortnite --base-path <p> --skip-sdl --skip-dlcs` (stderr progress) |
| Repair | `-y repair Fortnite` (`--repair-and-update` to also update) |
| Verify | `verify Fortnite` (parse stdout + stderr summary) |
| Move | `move Fortnite <newbase> [--skip-move]` |
| Uninstall | `-y uninstall Fortnite --skip-uninstaller [--keep-files]` |
| Launch | `launch Fortnite --json [--skip-version-check]` → spawn yourself; or plain `launch Fortnite` |
| Adopt EGL install | `-y import Fortnite <dir> --skip-dlcs` (then `repair` if told) |
Always: `LEGENDARY_CONFIG_PATH=<glider data>\legendary`, stdin = NUL, run inside a Job Object, decode stderr as ANSI, treat exit 0 as non-success, serialise mutating commands.

---

## 11. Fixtures index (`docs/research/fixtures/legendary/`)

`version.txt`, `help.txt`, `help-{auth,status,list,list-installed,info,list-files,install,update,repair,verify,launch,uninstall,move,import,egl-sync}.txt` — verbatim `-h` outputs.
`status-signed-out.stdout.txt`, `status-offline-json.stdout.txt`, `status-text.stdout.txt` — signed-out status.
`status-network-failure.stderr.txt` — traceback when `api.legendary.gl` is unreachable (proxy blackholed).
`list-installed-json-empty.stdout.txt` (`[]`), `list-installed-text-empty.stdout.txt`, `info-fortnite-notloggedin.stdout.txt`, `launch-notinstalled.stderr.txt`, `install-notloggedin.stderr.txt` (traceback), `list-notloggedin.stderr.txt`, `list-files-notloggedin.stderr.txt`, `verify-notinstalled.stderr.txt`, `uninstall-notinstalled.stderr.txt`, `move-notinstalled.stderr.txt`, `import-badpath.stderr.txt`, `egl-sync-nodata.stderr.txt`, `auth-delete.stderr.txt`, `noargs.stdout.txt`.
`invalid-command.stderr.txt` (argparse error, exit 2), `encoding-test-default.stderr.bin` / `encoding-test-PYTHONUTF8.stderr.bin` (raw stderr bytes proving cp1252 + `\u` escapes, identical with/without `PYTHONUTF8=1`).
`api-version.json.cached.json` — a real `version.json` cache (what `https://api.legendary.gl/v1/version.json` returned at research time: `egl_config`, `game_overrides.sdl_config.Fortnite = 14`, `release_info`…). `api-sdl-Fortnite.json` — `https://api.legendary.gl/v1/sdl/Fortnite.json`. `config.disable-update-check.ini` — minimal ini used for the update-check test.
Each `<name>.stdout.txt/.stderr.txt` pair holds the two streams (empty file = nothing printed). All were produced with `LEGENDARY_CONFIG_PATH` pointing at a throw-away dir.

## 12. Not verified / open questions (need a signed-in session)

1. Real `list --json` / `info Fortnite --json` / `launch Fortnite --json` payloads; `keyImages` types for Fortnite; Fortnite `FolderName`, `launch_exe`, prerequisites, `CanRunOffline`, catalog IDs.
2. Real progress/log output of an install (formats are from source only); behaviour of Ctrl-C/`CTRL_BREAK_EVENT` against the PyInstaller process tree; tree-kill orphan behaviour.
3. Token lifetimes (`expires_in`, `refresh_expires`), authorizationCode lifetime, exact JSON the Epic redirect page returns beyond `authorizationCode`.
4. Whether EGL tolerates Legendary-written `.item`/`.egstore` for a Fortnite install (and the `InstallTags` format difference).
5. Windows errno mapping for cross-drive `move` (`errno 18`).
