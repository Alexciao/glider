# How Heroic Games Launcher drives Legendary (research for Glider)

**Sources read**
- Heroic `2.22.4`, commit `fe8a5f1` (shallow clone). Paths below are relative to the Heroic repo root and are written `file:line`.
- Legendary `0.21.1` source, cited as `legendary/<file>:line`. Heroic pins this version for Windows at `meta/downloadHelperBinaries.ts:30` and downloads `legendary_windows_x64.exe` from `legendary-gl/legendary` (`meta/downloadHelperBinaries.ts:125-137`).
- GitHub issues were found with `gh search issues`. Anything not backed by a file or issue link is marked **[unverified]** or **[inference]**.

**Biggest surprises, up front**
1. Heroic never looks at legendary's exit code. `callRunner` resolves `{stdout, stderr}` on `close` and only treats a signal or a spawn error as failure (`src/backend/launcher.ts:1776-1790`). Failures are detected by string-matching output (§7). Glider should check exit codes and also match strings, because legendary does `exit(1)` on many errors but exits 0 after a failed download (`legendary/cli.py:1087-1092`).
2. Heroic has **no EasyAntiCheat logic at all** on Windows. The only EAC code is Linux-only (`PROTON_EAC_RUNTIME`, `src/backend/launcher.ts:1227-1228`). On Windows, `legendary install -y` also silently skips the game's prerequisites (§5). Glider must handle both itself.
3. `legendary launch` does **not** wait for the game. It does `subprocess.Popen(...)` and exits (`legendary/cli.py:744`). Heroic's "game is running" state is simply the lifetime of a `powershell Start-Process -Wait` wrapper (§5).
4. Heroic's install percentage is computed by Heroic and is known to be wrong, for example Fortnite starting at ~44% or 69% (§3). Legendary prints a real `= Progress: xx.xx%` line that Heroic ignores. Glider should parse that line.
5. Heroic's Windows login uses `https://www.epicgames.com/id/login?responseType=code` with a spoofed `EpicGamesLauncher` user agent, and captures a redirect to `localhost?code=…` (§1).

---

## 1. Auth

### 1.1 The login URL and capture (embedded webview)

| Item | Value | Source |
|---|---|---|
| Embedded login URL | `https://www.epicgames.com/id/login?responseType=code` | `src/frontend/screens/WebView/index.tsx:98`, routed for `/loginweb/legendary` at `:127` |
| Webview session partition | `persist:epic` (cookies survive restarts; Heroic clears them on logout) | `WebView/index.tsx:692`, `src/backend/storeManagers/legendary/user.ts:71` |
| User agent forced on the login page | `Mozilla/5.0 (Windows NT 10.0; Win64; x64) EpicGamesLauncher`, set at runtime with `webview.setUserAgent(...)` inside the first `dom-ready` handler, only when `startUrl === epicLoginUrl` | `WebView/index.tsx:177-178`, `:329-331` |
| Capture trigger | In the `dom-ready` handler (`:408`), take `webview.getURL()`. If `hostname === 'localhost'`, read the query param `code` | `WebView/index.tsx:370-381` |
| Hand-off | `epic.login(code)`, which calls IPC `login` and then `LegendaryUser.login(code)` | `src/frontend/state/GlobalState.tsx:572-575`, `src/backend/main.ts:806` |
| Legendary command | `legendary auth --code <code>` (`{subcommand:'auth', '--code': code}`), abortId `legendary-login`, log prefix "Logging in" | `user.ts:14-37` |
| Log redaction | `--code` and `--token` values are replaced with `<redacted>` | `src/backend/launcher.ts:1854-1865` |

Verbatim capture logic (`WebView/index.tsx:370-381`):
```ts
} else if (runner == 'legendary') {
  const pageUrl = webview.getURL()
  const parsedUrl = new URL(pageUrl)
  if (parsedUrl.hostname === 'localhost') {
    const code = parsedUrl.searchParams.get('code')
    if (code) { /* setLoading… */ epic.login(code).then(() => handleSuccessfulLogin()) }
  }
}
```

**Why the user agent matters.**
- Without the launcher UA, `https://www.epicgames.com/id/login?responseType=code` fails with "Incorrect response. Please refresh the page" or "client_id is required" (issue [#4460](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/4460)).
- That issue was closed by PR [#4478](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/pull/4478), "fix: change user-agent at runtime", whose only changed file is `WebView/index.tsx`.
- **[inference]** The `EpicGamesLauncher` UA makes epicgames.com run its launcher login flow, which finishes with a redirect to `http://localhost/...?code=<authorization code>`. Heroic source does not show the redirect path, only the `localhost` host and the `code` param.
- Glider on WebView2 should hook `NavigationStarting`, not `DOMContentLoaded`. Match host `localhost` or `127.0.0.1` with a `code` query param, then cancel the navigation so nothing tries to connect to localhost. Heroic relies on `dom-ready` firing on Chromium's error page, which is fragile.
- Set the UA before the first navigation (`CoreWebView2.Settings.UserAgent`). Heroic sets it late, on the first `dom-ready`, then relies on later navigation.

**Fallback / manual flow (also in Heroic).**
- The SID modal tells the user to open `https://legendary.gl/epiclogin` (`src/frontend/screens/Login/components/SIDLogin/index.tsx:22`) and paste the "authorization code". It rejects input shorter than 30 characters (`:49`).
- The pasted text goes straight to `auth --code`. Heroic does **not** parse JSON here, so a user pasting the whole JSON blob would fail.
- I verified with `curl -I` that `https://legendary.gl/epiclogin` 302-redirects to:
  `https://www.epicgames.com/id/login?redirectUrl=https%3A//www.epicgames.com/id/api/redirect%3FclientId%3D34a02cf8f4414e29b15921876da36f9a%26responseType%3Dcode`
- This matches `get_auth_url()` in `legendary/legendary/api/egs.py:62-65`. After login, that page shows a JSON body.
- Legendary's own CLI fallback (`legendary/cli.py:166-186`) accepts the whole JSON and does `tmp['authorizationCode']`. Its webview path also reads `sid` from the same JSON (`legendary/utils/webview_login.py:113-119`). So the shape is `{"...": ..., "authorizationCode": "<code>", "sid": ...}`. Other keys **[unverified]**.
- **Glider option:** use this `epiclogin` URL (clientId `34a02cf8...`, `responseType=code`). After `NavigationCompleted` on `/id/api/redirect`, run `ExecuteScriptAsync("document.body.innerText")`, `JSON.parse`, and read `authorizationCode`. This is the flow legendary itself documents and is independent of the localhost redirect and the UA trick.

**Legendary's own webview flow, for comparison.**
- Legendary opens `https://www.epicgames.com/id/login` with UA `EpicGamesLauncher/<egl_version>`, injects `window.ue.signinprompt.*` JS, and receives an **exchange code**.
- It then calls `auth_ex_token` (`legendary/utils/webview_login.py:23-48`, `:129-157`, `legendary/cli.py:190-195`).
- Heroic bypasses this. It passes `--code`, which uses the `authorization_code` grant (`legendary/api/egs.py:110-113`).

### 1.2 Failure handling on login

`user.ts:39-48`:
```ts
if (res.stderr.includes('ERROR: Logging in ')) return errorMessage(res.stderr)
if (res.error || res.abort) return errorMessage(res.error ?? 'abort by user')
const userInfo = this.getUserInfo()   // reads user.json
return { status: 'done', data: userInfo }
```

Weaknesses Glider should avoid:
- The match string corresponds to `[Core] ERROR: Logging in failed with …` (`legendary/core.py:165-167`).
- A *different* failure line, `[cli] ERROR: Login attempt failed, please see log for details.` (`legendary/cli.py:205-208`), is not matched, and the exit code is 0.
- If `user.json` already existed from a previous session, Heroic would report success.
- `legendary auth` **short-circuits** when stored credentials are still valid ("Stored credentials are still valid…", `legendary/cli.py:97-107`). It ignores `--code`. So run `auth --delete` first when switching accounts.
- **Glider should:** delete `user.json` (or run `auth --delete`) before `auth --code`, then require both a successful exit and a freshly written `user.json`. Also match `Successfully logged in as "<name>"` (`legendary/cli.py:206`).
- Corrective-action errors are real. `errors.com.epicgames.oauth.corrective_action_required` appears in the log with `continuationUrl` (`legendary/api/egs.py:129-131`). Issues [#5413](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/5413) and [#5414](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/5414): users must accept new EULA terms on the Epic website. Surface the `continuationUrl`.
- Open issue [#6037](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/6037), "legendary login error" (legendary 0.21.1): login succeeds but the follow-up `list --third-party` errors.

### 1.3 Logout

`user.ts:55-77`:
1. `legendary auth --delete` (abortId `legendary-logout`). This deletes `user.json` (`legendary/lfs/lgndry.py:173-175`, `lfs/utils.py:207-210` and `:186-195`).
2. `session.fromPartition('persist:epic')` then `clearStorageData / clearCache / clearAuthCache / clearHostResolverCache / clearData`.
3. `configStore.delete('userInfo')` and `clearCache('legendary')`. That runs `legendary cleanup` and clears Heroic's library/install/info stores (`src/backend/utils.ts:383-386`).

For Glider: after `auth --delete`, call `CoreWebView2.Profile.ClearBrowsingDataAsync()` on the login profile.

### 1.4 Display name and avatar

- Logged-in test is `existsSync(<config>/user.json)` (`user.ts:79-81`).
- User info is read from `user.json`; only `account_id` and `displayName` are used (`user.ts:83-107`). `user.json` is legendary's stored OAuth token response (`legendary/api/egs.py:139-142`).
- **No avatar endpoint is used anywhere in Heroic or in legendary.** I grepped for `avatar` and `profile` in the legendary store manager. The UI shows only a username.
- **Glider fallback:** initials (or a colour from a hash of `account_id`) from `displayName`. Do not invest in an avatar fetch.

---

## 2. Legendary process management

| Aspect | Heroic behaviour | Source |
|---|---|---|
| Binary | Bundled `legendary` (`.exe` on Windows). User-overridable via `altLegendaryBin`. | `src/backend/utils.ts:455-465` |
| Config dir | `LEGENDARY_CONFIG_PATH = <appFolder>/legendaryConfig/legendary`. In the wild this is `%APPDATA%\heroic\legendaryConfig\legendary` (issue logs). Set "so user-made changes to Legendary's main config don't affect us". | `src/backend/storeManagers/legendary/constants.ts:4-8`, `library.ts:691-698` |
| Files read directly | `user.json`, `installed.json`, `metadata/<app>.json`, `assets.json` | `constants.ts:9-15`, `library.ts:64-95`, `:132-156`, `:345-356` |
| Other env | `{...process.env, ...options.env}`. For `launch` it adds `HEROIC_APP_*`, wine/known-fix vars, and `LEGENDARY_WRAPPER_EXE` (see §5). **Heroic does not set `PYTHONIOENCODING` or `PYTHONUTF8`.** | `launcher.ts:1737-1741`, `games.ts:914-942` |
| Args always passed | Nothing global. No `--debug`, no `-J`. `-y` is added per command (see §4). | `library.ts:823-876` |
| Arg building | `commandToArgsArray`: subcommand, then positionals, then every key starting with `-`. `true` becomes a bare flag; other values become `flag value`. Falsy values are skipped. | `library.ts:823-876` |
| Spawn | Node `child_process.spawn(bin, args, {cwd: runner.dir, env, signal})`, stdout/stderr set to UTF-8 | `launcher.ts:1737-1760` |
| Windows wrapper | If `powershell` is on PATH, everything is re-wrapped as `powershell -NoProfile Start-Process "`"<legendary>`"" -Wait -NoNewWindow -ArgumentList "`"arg1`"","`"arg2`""`, with backslashes doubled. Comment: "wait for the process and its children to exit". | `launcher.ts:1683-1707` |
| Dedup | Identical running commands share one promise (key = runner + args) | `launcher.ts:1722-1730`, `:1830` |
| Abort | `AbortController` per `abortId` (`appName` or a fixed id such as `legendary-login`) passed as the spawn `signal`. Abort resolves `{abort:true}`. | `launcher.ts:1731-1735`, `:1799-1812`, `src/backend/utils/aborthandler/aborthandler.ts:1-29` |
| Kill on Windows | **No `taskkill`, no tree-kill.** Abort uses Node's spawn-signal kill, which would terminate only the `powershell` wrapper **[inference]**. Heroic backs it up with `Stop-Process -name legendary`: `spawnSync('Stop-Process', ['-name', pattern], {shell: 'powershell.exe'})`, with pattern `'legendary'` on Windows. | `src/backend/utils.ts:814-826`, `games.ts:1085-1097`; on app quit `utils.ts:262-273` |
| Pause | Pause = **abort the process**, then mark the queue `paused`. Legendary resumes from its on-disk resume file, so "pause" is really kill and restart. | `src/backend/downloadmanager/downloadqueue.ts:257-280` (`callAbortController(appName)` plus `game.stop(false)`) |
| Resume | Re-run the same `install`/`update` command (`initQueue`). Legendary says: "Downloads are resumable… using the same command" (`legendary/cli.py:1049-1050`). Resume file: `legendary/downloader/mp/manager.py:123-152`, `:579-586`. | `downloadqueue.ts:270-273` |
| Auto-pause | Pauses on connectivity loss and resumes when back online | `downloadqueue.ts:27-40` |
| Concurrency | One download at a time (queue). Legendary uses an installed-data lock; a second instance fails with "Failed to acquire installed data lock" (`legendary/cli.py:846`, `:1189`, `:1350`). | `downloadqueue.ts` |
| Failure detection | `errorHandler(stdout+stderr)` on `close` (§7), `res.error` only for spawn errors or signals. **Exit code is never inspected.** | `launcher.ts:1776-1790` |
| Output retained | `memoryLog(50)` keeps the last 50 chunks. Per-game log files for install, update, repair, import. | `src/backend/utils.ts:1228-1240` |

**Legendary I/O facts to remember (from source).**
- Logging format is `[%(name)s] %(levelname)s: %(message)s` on the default `logging` stream, which is **stderr** (`legendary/cli.py:54-57`). `--json` output is on stdout.
- Legendary forces stdout to UTF-8 only when stdout is not a TTY (`legendary/cli.py:2720-2722`). stderr is not reconfigured. **[Recommendation]** Glider should set `PYTHONIOENCODING=utf-8` (and optionally `PYTHONUTF8=1`) so non-ASCII display names and titles don't mangle.
- Prompts use `input()`, so a missing `-y` hangs the process. This was a real bug for repair: issue [#2306](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/2306). Always pass `-y` or close stdin.
- For Glider (.NET), skip the PowerShell wrapper for non-launch commands and use `Process` with redirected streams. Do use a Job Object or `taskkill /T /F /PID` for cancel, since legendary spawns download worker processes (multiprocessing).

---

## 3. Progress parsing (install/update only)

Heroic reads **both** stdout and stderr through the same `onOutput(data)` callback with no stream distinction (`launcher.ts:1749-1773`). It parses with these exact regexes in `src/backend/storeManagers/legendary/games.ts:399-486`:

| Value | Regex (JS) | Line | Matches this legendary line (`legendary/downloader/mp/manager.py:766-775`) |
|---|---|---|---|
| Total download size | `/Download size: ([\d.]+) MiB/` | 404 | `[cli] INFO: Download size: 12345.67 MiB (Compression savings: …)` (`legendary/cli.py:1044`) |
| ETA | `/ETA: (\d\d:\d\d:\d\d)/m` | 420 | `= Progress: 12.34% (n/N), Running for HH:MM:SS, ETA: HH:MM:SS` |
| Downloaded | `/Downloaded: (\S+.) MiB/m` | 426 | ` - Downloaded: 123.45 MiB, Written: 123.45 MiB` |
| Download speed | `/Download\t- (\S+.) MiB/m` | 433 | ` + Download\t- 12.34 MiB/s (raw) / 23.45 MiB/s (decompressed)` |
| Disk write speed | `/Disk\t- (\S+.) MiB/m` | 441 | ` + Disk\t- 12.34 MiB/s (write) / 0.00 MiB/s (read)` |

**How Heroic computes percent** (`games.ts:447-459`), and why it is wrong:
```ts
totalDownloadSize = round(totalDownloadSize/1024/1024 *100)/100        // manifest.download_size in MiB
const downloadCache = totalDownloadSize - (this.currentDownloadSize ?? 0)  // "Download size:" line
percent = round(((downloaded + downloadCache) / totalDownloadSize) * 10000)/100
```
- It assumes `Download size:` is the remaining download after resume. That is wrong for Fortnite, whose `info` manifest size (all tags) is larger than the real download with install tags. Symptoms: Fortnite starts at ~44% or 69% ([#1483](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/1483), upstream tracker [Heroic legendary #35](https://github.com/Heroic-Games-Launcher/legendary/issues/35)).
- It gets stuck at 90% ([#4190](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/4190)).
- **Glider should parse legendary's own `= Progress: (\d+\.\d+)%` plus `ETA: HH:MM:SS`.** These lines exist at `legendary/downloader/mp/manager.py:766-768`, are chunk-based, and are correct across resume. Pull the speed lines as Heroic does. Convert MiB to bytes yourself. Total/downloaded bytes for the UI can come from `Download size:` and the `Downloaded:` line.

**Throttling.** There is no explicit throttle. Legendary emits one 5-line block per second (`update_interval=1.0`, `legendary/downloader/mp/manager.py:33`). Heroic accumulates the four fields (`progress.eta`, `.bytes`, `.downSpeed`, `.diskSpeed`). It sends a `progressUpdate` IPC message only when **all** fields are filled, then resets (`games.ts:462-485`), so it sends at most about one per second. Because the regexes run per output chunk, a block split across two chunks is still handled thanks to the accumulator. Glider should buffer by line.

---

## 4. Install / update / repair / move / uninstall / import / verify

Exact commands as built by Heroic (`-y` is always explicit):

| Action | Command line | Notes / source |
|---|---|---|
| **Install** | `install <App> --platform Windows --base-path <dir> --skip-dlcs -y [--max-workers N] [--no-https]` plus **either** `--install-tag= --install-tag <t1> --install-tag <t2> …` **or** `--skip-sdl` | `games.ts:598-610`, `library.ts:830-837`. Real Fortnite example from [#1483](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/1483): `install Fortnite --platform Windows --base-path /home/ariel/Games/Heroic --skip-dlcs --install-tag= --install-tag=core --install-tag=br -y`. The empty `--install-tag=` is deliberate: it resets the list. |
| Install retry | If stderr contains `MemoryError:`, re-run the same command with `--max-shared-memory 5000` | `games.ts:632-640` |
| **Update** | `update <App> -y --skip-sdl [--max-workers N] [--no-https]` | `games.ts:505-512`. `--skip-sdl` keeps previously saved `install_tags` (`legendary/cli.py:961-983`). |
| **Repair / verify** | `repair <App> -y --skip-sdl [--max-workers N] [--no-https]`. Heroic has no separate verify; "verify and repair" is `repair`. | `games.ts:780-787`. Without `-y`, legendary prompts and hangs ([#2306](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/2306)). |
| **Uninstall** | `uninstall <App> -y`. "Forget only": `uninstall <App> -y --keep-files`. | `games.ts:748-752`, `:1059-1067` |
| **Import existing install** | `import <App> <dir> --with-dlcs --platform Windows` | `games.ts:813-819`. Errors collected with `stderr.match(/^.*ERROR:.*$/gm)` (`:833-834`). |
| **Move** | Heroic moves files itself with `robocopy <src> <dst> /MOVE /MIR /NJH /NJS /NDL` (Windows), parsing `(\d+)(?:\.\d+)?%` for progress. It then runs `move <App> <parentOfNewPath> --skip-move` so legendary only updates `installed.json`. | `src/backend/utils.ts:1049-1100`, `library.ts:415-452`. Guard: new path may not be inside the old path (`src/backend/main.ts:997-1005`). |
| **Cleanup** | `cleanup` (run after logout/clear cache) | `src/backend/utils.ts:383-386` |
| **EOS overlay** | `eos-overlay install --path <p> [-y]`, `info`, `enable`, `disable`, `remove -y`, plus `status` | `eos_overlay.ts:90-139`, `:237-269`. Trick: run `install` **without** `-y`, parse `Download size:`, then abort (`callAbortController`) to get the size without downloading (`:99-118`). |

**Flags Heroic does *not* use.**
- `--preferred-cdn`, `--enable-reordering`, `--force`, `--disable-patching`, `--game-folder`, `--ignore-free-space`, `--download-only`. They exist in the type definition (`commands/install.ts:11-46`) but I found no call site. `--with-dlcs` is used only for `import`; install uses `--skip-dlcs`.
- `--max-workers` comes from a global setting; `--no-https` from `downloadNoHttps`.
- `--max-shared-memory` is only the automatic MemoryError retry.
- Heroic fetches `https://heroic.legendary.gl/v1/version.json` (`game_overrides.sdl_config`, `executable_override`, `reorder_optimization`) (`library.ts:709-729`), but I found no use of `reorder_optimization`.

**Selective download for Fortnite.**
- Heroic only shows the SDL picker if `game_overrides.sdl_config[appName]` exists, then calls `https://heroic.legendary.gl/v1/sdl/<appName>.json` (`library.ts:731-760`, `src/frontend/screens/Library/components/InstallModal/DownloadDialog/index.tsx:443-454`).
- I fetched `https://heroic.legendary.gl/v1/sdl/Fortnite.json` live. It has exactly two groups, both with a `tags` array:
  - `__required` ("Fortnite Core, Battle Royale, and Save the World"). Tags: `Startup, FrontEnd, Encrypted, Globals, DefaultGameplayChunk, UnusedOldEarlyStartupPatcherChunk, CosmeticPreInstall, FatCosmo, GFP_BaseInstallRoot, GFP_BRCosmetics, GFP_BRCosmeticsInstallOnDemand, GFP_BRCosmeticsInstallOnDemandFat, GFP_CreativeRoot, GFP_BRRoot, GFP_JunoRoot, GFP_DelMarRoot, GFP_Sparks, GFP_BlitzRoot, GFP_StrideMiceRoot, GFP_SaveTheWorldRoot, FortniteBR, FortniteCatchAll, FNOne, Langde, Langfr, Langpl, Langru, ""`.
  - `epic_assets` ("Epic Quality Assets (Streamed by default)"), optional, all `*Optional` tags plus `FortniteBREpicOptionalInstall`.
- Legendary has a hard-coded fallback in `legendary/utils/selective_dl.py:15-24` (`chunk0, chunk10…`). It is stale. Prefer the live JSON.
- `--skip-sdl` on a first install installs only `__required` (`legendary/cli.py:961-971`), so **Glider can simply pass `--skip-sdl` for Fortnite**. Heroic does exactly that when the user selects nothing. Tags are persisted to legendary's `config.ini` under `[Fortnite] install_tags=…` (`cli.py:971`).
- **Pitfalls (all real reports):**
  - Missing `FortniteCatchAll` leaves `FortniteGame/Binaries` empty and launch fails with `FileNotFoundError` ([Heroic/legendary #37](https://github.com/Heroic-Games-Launcher/legendary/issues/37)).
  - After season updates, `repair Fortnite --reset-sdl` fixed missing files ([legendary-gl #725](https://github.com/legendary-gl/legendary/issues/725)).
  - Updating without `--skip-sdl` makes legendary prompt for optional packs, so it hangs or errors ([#3083](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/3083)).

**Free space and install size.**
- Install size: `legendary info <App> --json --platform Windows` (`library.ts:235-244`) returns `{ game, install, manifest }` (`legendary/cli.py:1969-1982`). Heroic's type, `src/common/types/legendary.ts:136-179`, lists:
  - `manifest.disk_size`: installed bytes.
  - `manifest.download_size`: compressed bytes.
  - `manifest.tag_disk_size` and `manifest.tag_download_size`: `[{tag, count, size}]`. Heroic does not use these for the picker, but they are what Glider needs to show the Fortnite size for the selected tags.
  - `manifest.install_tags`, `launch_exe`, `build_version`, `num_files`, `num_chunks`.
  - `manifest.prerequisites`: `{ids, name, path, args}` or `null` (§5).
  - `game.version`, `game.title`, `game.owned_dlc`, `game.cloud_saves_supported`, `game.cloud_save_folder`, `game.launch_options`, `game.platform_versions`.
- `info` results are cached per app in Heroic's `installStore`, retried up to 3 times if `manifest` is missing, and `--offline` is added when Epic status is "major" outage (`library.ts:228-277`, `:241-243`). The info cache is **not** invalidated on game updates in this path, which is a hazard for a game that updates weekly like Fortnite.
- Free space (UI side): `checkDiskSpace` runs `powershell Get-CimInstance -Class Win32_LogicalDisk -Property Caption,FreeSpace,Size | Select-Object … | ConvertTo-Json -Compress`, matches `path.startsWith(disk.Caption)` (`src/backend/utils/filesystem/windows.ts:25-61`, `src/backend/main.ts:554-570`), and also checks writability via `Get-Acl` (`windows.ts:63-93`). The dialog compares `free < manifest.disk_size`, scaling by progress when resuming (`DownloadDialog/index.tsx:455-481`).
- **Glider should use .NET `DriveInfo` (or `GetDiskFreeSpaceEx`) rather than shelling to PowerShell.** Legendary also re-checks: `core.py:1738-1746` reports `Not enough available disk space! X GiB < Y GiB` and exits 1. Heroic doesn't match this message, so updates "silently finish" ([#3962](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/3962), [#4000](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/4000)). Glider should match `! Failure: Not enough available disk space!` and show it.

---

## 5. Launching on Windows, EAC, Fortnite

### 5.1 What Heroic runs

`games.ts:1001-1018` builds:
```
legendary launch <App> [<extra args…>] [--skip-version-check] [--language <xx>] [--override-exe <rel>] [--offline]
```
- `--language <xx>`: set if the per-game or global language is non-empty.
- `--skip-version-check`: from the UI "launch anyway" path (`skipVersionCheck` param) or when launched via CLI with no GUI.
- `--offline`: when `prepareLaunch` says offline mode.
- `--override-exe`: from the per-game target exe setting.
- **Not used by Heroic:** `--json`, `--dry-run`, `--set-defaults`. On Windows it never passes `--wine`, `--wine-prefix`, `--no-wine`, or `--wrapper` (those are wine-only; `isNative()` is true on Windows, `games.ts:1042-1054`).
- Env additions: `LEGENDARY_WRAPPER_EXE=<fakeEpicExePath>` if `USE_FAKE_EPIC_EXE` is not `0` / `false` / unset and the file exists (`games.ts:921-927`). The file is `public/bin/x64/win32/EpicGamesLauncher.exe` (`src/backend/constants/paths.ts:61-63`). Legendary then inserts that exe before the game exe in the launch command line (`legendary/cli.py:696-698`), so games that check their parent process see an "Epic launcher". **This is the closest thing in Heroic to a Fortnite-relevant workaround**, but it is opt-in via env.
- Heroic runs this through the PowerShell `Start-Process -Wait -NoNewWindow` wrapper (§2). The real log in [#5019](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/5019) shows `powershell -NoProfile Start-Process "`"…\legendary`"" -Wait -NoNewWindow -ArgumentList "`"launch`"","`"Fortnite`"","`"--language`"","`"it`""`.

What legendary itself does on launch (`legendary/cli.py:594-744`, `core.py:830-936`):
1. Re-uses or refreshes the login ("Logging in…") and, unless offline, checks for updates. If out of date, it logs `Game is out of date, please update or launch with update check skipping!` and `exit(1)` (`cli.py:639-649`).
2. Fetches a game token with `get_game_token()` (an exchange code) and passes it as the auth parameters (`core.py:889-904`):
   `-AUTH_LOGIN=unused -AUTH_PASSWORD=<token> -AUTH_TYPE=exchangecode -epicapp=<App> -epicenv=Prod -EpicPortal -epicusername=<displayName> -epicuserid=<account_id> -epiclocale=<lang> -epicsandboxid=<namespace>`
3. Adds `-epicovt=<file>` only for games that need an ownership token (`core.py:906-912`).
4. `subprocess.Popen(full_params, cwd=working_dir, env=full_env, shell=os.name=='nt')` and **exits immediately** (`cli.py:744`).
5. Optional `--dry-run` / `--json` are print-only (`cli.py:687-688`, `:718-726`). `--json` dumps the `LaunchParameters`. Useful for Glider's own launch (see below).

### 5.2 Detecting that the game is running or has closed

- Heroic does **not** watch the game process. "Running" is the lifetime of the `Start-Process -Wait` wrapper.
- `sendGameStatusUpdate(playing)` goes out before the command (`games.ts:1020-1024`). When the promise resolves, `launchCleanup()` runs (`games.ts:1037`, defined `launcher.ts:1445`) and the status becomes `done` (`launcher.ts:~310-316`).
- Playtime is wall-clock between those two points (`launcher.ts:138`, `:267-274`).
- Stop is `killPattern('legendary')`, i.e. `Stop-Process -name legendary` (`games.ts:1085-1097`). That does **not** kill the game on Windows **[inference]**.
- Because Epic's launcher-style games (Fortnite included) may respawn, relaunch via `FortniteLauncher.exe`, or the wrapper may return early, this detection is weak. **Glider should instead:**
  - `Process.Start` legendary (it exits immediately), then
  - poll for `FortniteClient-Win64-Shipping.exe` (the actual game; also expect `FortniteLauncher.exe`, the EAC bootstrap stub, and `EasyAntiCheat_EOS.exe`) **[unverified: process and exe names, not present in Heroic or legendary source]**, and
  - treat "closed" as no Shipping process for N seconds, plus fallback to `FortniteLauncher.exe`.
  - Alternative: run `legendary launch Fortnite --json` and spawn the game yourself from the dumped parameters, so you own the `Process` handle. Tradeoff: you then have to replicate the pre-launch handling and env.

### 5.3 EasyAntiCheat / prerequisites (important gap)

**What the source shows:**
- A repo-wide search for `fortnite|easyanticheat|EAC` in Heroic `src/` returns only `eacRuntime` (Linux, Proton) and one UE-for-Fortnite comment (`library.ts:898-899`). There is **no** `EasyAntiCheat_EOS_Setup.exe`, no `install <productid>`, no service-name handling, and no UAC elevation for EAC anywhere. `utils.ts:187` only names `'Fortnite'` as an Epic *status component*.
- The `legendarySetup()` prerequisite runner (`src/backend/storeManagers/legendary/setup.ts:41-74`) runs `<install_path>/<prerequisites.path> <prerequisites.args>` via **wine**, and is invoked only after a wine prefix is created (`launcher.ts:853-890`). It never runs on native Windows.
- On native Windows, `legendary install -y` calls `_handle_postinstall(..., skip_prereqs=args.yes)` which, with `-y`, sets `c = 'n'` ("we don't want to launch anything") and does nothing. It does not even mark the prerequisite as installed (`legendary/cli.py:1103`, `:1157-1185`).
- Therefore **Heroic on Windows never installs Fortnite's prerequisites (including EAC if it is a manifest prerequisite)**.
- Legendary's maintainer says: "EAC is part of the game install, legendary will ask you if you want to install the redistributables that come with the game. But you can also just manually run the installer (see `legendary info <app name>` for the paths/commands)" ([legendary-gl #624](https://github.com/legendary-gl/legendary/issues/624)).

**What Glider should do** (derived from the above, plus facts that need on-machine confirmation):
1. After install/update, run `legendary info Fortnite --json` and read `manifest.prerequisites` (`ids`, `name`, `path`, `args`). If non-null, run `<install_path>\<path> <args>` with `cwd = dirname(path)` (this is exactly what legendary does when interactive: `cli.py:1174-1183`). Run it **elevated** via `ProcessStartInfo{Verb="runas", UseShellExecute=true}`. These installers normally self-elevate, so a manifest-embedded prompt is fine. Remember the choice yourself, since legendary only marks prerequisites installed in its interactive flow.
2. **[unverified]** Epic's EAC bootstrapper for EOS titles is `EasyAntiCheat_EOS_Setup.exe` (usually `install <productid>`) and the Windows service is `EasyAntiCheat_EOS`. Neither string appears in Heroic or legendary source, so I can't confirm Fortnite's exact path, product id, or whether it is in `manifest.prerequisites`. **Ask the user to run `legendary info Fortnite --json` on a machine with Fortnite installed** and paste `manifest.prerequisites` (plus the service state via `sc query EasyAntiCheat_EOS`) before hard-coding anything.
3. Don't assume a verify fixes EAC failures. A reported symptom set is "EAC integrity error" or "Insufficient system resources exist to complete the requested service" (Windows error 1450) after a Fortnite update on Windows, 3 machines with Heroic and 0 with EGL; the reporter says you must verify files again ([#3692](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/3692), open). No root cause or fix has been found upstream.
4. Related Fortnite breakage on Windows that is reproducible with legendary-installed Fortnite: missing cosmetics, glider thumbnails or `Error 20469285-10` ([#4789](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/4789), [legendary-gl #711](https://github.com/legendary-gl/legendary/issues/711)). One user reports it works if installed through EGL and launched through legendary, and breaks again after the first legendary-applied update. `--disable-patching` is reported not to help. Missing files after a version bump: use `repair --reset-sdl` ([legendary-gl #725](https://github.com/legendary-gl/legendary/issues/725)). `legendary install --disable-patching` and `--enable-reordering` are the knobs to expose in an advanced panel.
5. Fortnite also crashes right after launch on Windows in [#5019](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/5019) (open). In that log `egl-sync` was auto-disabled (§6), and the reporter notes cosmetics don't load until files are downloaded via EGL. Treat "Fortnite update by legendary" as the risky operation.
6. `FortniteLauncher.exe` vs `FortniteClient-Win64-Shipping.exe`: no mention in either codebase. The manifest's `launch_exe` is what legendary executes (`info --json` → `manifest.launch_exe`, `igame.executable`). **[unverified]** which of the two Epic ships as `launch_exe`. Read it from `info`, don't hard-code. For process watching, watch both names.

---

## 6. EGL sync

**How Heroic uses it**
- Only as an opt-in toggle in Settings, via `toggleGamesSync` (`src/backend/storeManagers/legendary/library.ts:769-817`, UI `src/frontend/screens/Settings/components/EgsSettings.tsx:16-64`, IPC `main.ts:1129`).
- **Enable on Windows:** `legendary egl-sync -y --enable-sync`. Before that, Heroic creates `C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests` if missing (`library.ts:773-781`), a workaround for legendary's crash when EGL never ran and the folder doesn't exist ([legendary-gl #623](https://github.com/legendary-gl/legendary/issues/623)).
- **Disable:** `egl-sync -y --unlink`.
- On Linux/Mac it adds `--egl-wine-prefix <prefix>`.
- Success/failure is judged by stderr containing `ERROR` or `error`, or a spawn error; the UI then shows `box.sync.error`.
- **Heroic never calls `egl-sync` per game.** Once `egl_sync = True` is in `config.ini`, legendary itself runs sync automatically inside `get_installed_list()` and `get_installed_game()` (`legendary/core.py:707-731`), and exports on install (`core.py:1825-1831`). That is what makes sync "when": every list, launch, install, status.
- The `egl-sync` flags exist in the type, `commands/egl_sync.ts:3-14`, but only `-y`, `--enable-sync`, `--unlink` (and the wine prefix flag) are used.

**Known crash and bug reports**

| Issue | Finding |
|---|---|
| [Heroic #2985](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/2985) | Launching a game crashed with `ValueError: EGS ProgramData path does not exist` (traceback from `egl_restore_or_uninstall` → `egl_export` → `set_manifest`). Cause: EGL uninstalled after sync was enabled. Fixed in Heroic's legendary fork ([Heroic/legendary #5](https://github.com/Heroic-Games-Launcher/legendary/pull/5)). |
| [legendary-gl #623](https://github.com/legendary-gl/legendary/issues/623) | `egl-sync` fails if EGL never created `Manifests`. Workaround: create `%PROGRAMDATA%\Epic\EpicGamesLauncher\Data\Manifests` by hand. |
| [Heroic #5019](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/5019), [#4029](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/4029), [Heroic/legendary #53](https://github.com/Heroic-Games-Launcher/legendary/issues/53) | Every legendary command prints `[Core] ERROR: EGL ProgramData path ("C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests") is invalid! Disabling sync...` on Windows when EGL isn't installed. Noise, not a crash. **Glider should filter this line** from its error detection. In 0.21.1 source this check is only in the non-Windows branch (`legendary/core.py:93-100`); the Heroic fork prints it on Windows. |
| [legendary-gl #650](https://github.com/legendary-gl/legendary/issues/650) | After updating via legendary, `egl-sync` can push the *old* version back and make EGL re-download Fortnite (70 GB); cleanup of `.egstore/bps` and `.egstore/pending` fixed it. Comparison is by version-string inequality, so direction is guessed. |
| [legendary-gl #797](https://github.com/legendary-gl/legendary/issues/797) | "EGL Preview" breaks sync (0.21.1). |
| [Heroic #3442](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/3442) | Benign log line "Synced app … is no longer in the EGL manifest list" after EGL-side uninstall. |
| [Heroic #2170](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/2170), [#1750](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/1750), [#2738](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/2738) | Sync failed when the Windows username or path had a space (closed 2023). |
| [Heroic #5006](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/5006) | Fortnite update loop ("Updating / Update finished" repeating). A maintainer says Fortnite "cannot be updated successfully from Heroic" and recommends enabling EGL sync and updating in EGL. |

**Recommendation for Glider**
- Don't enable `egl-sync`. Glider manages only Fortnite, and sync adds crash surface plus the version-direction bug.
- If you want to import an existing EGL Fortnite, run the one-shot `egl-sync --one-shot --import-only -y` (flags at `legendary/cli.py:1578-1600`, `:3027-3041`) once, or use `import Fortnite <dir>`. Then leave `egl_sync` off in `config.ini`.

---

## 7. Error mapping (what Heroic matches and shows)

All in `src/backend/utils.ts:300-352` (`errorHandler`, called from `launcher.ts:1777-1781` and `:1814` on every command) unless noted.

| Match | Condition | UI shown |
|---|---|---|
| `error.includes('appears to be deleted')` and an `appName` | legendary: `Install directory "…" appears to be deleted, cannot launch <app>!` (`legendary/cli.py:623`) | Dialog "Game folder appears to be deleted, do you want to remove the game from the installed list?" Yes runs `uninstall <app> -y --keep-files` (`utils.ts:282-296`, `games.ts:1056-1067`). |
| `/legendary.*\.py/` (any Python traceback path) | Any traceback; ignored if it contains `MemoryError: ` (handled by retry) | Dialog "An error has occurred! Try to Logout and Login on your Epic account. {error}" (`box.error.legendary.generic`) |
| `error.includes('No saved credentials')` | `core.py:218` | Dialog "Your Crendentials have expired, Logout and Login Again!" (`box.error.credentials.message`) |
| Ignored | `IndexError: list index out of range`, `RequestsDependencyWarning` | Nothing |
| `stderr.includes('MemoryError:')` | Install only (`games.ts:633`) | Silent retry with `--max-shared-memory 5000` |
| `stderr.includes('ERROR: Logging in ')` | Login only (`user.ts:40`) | `status: 'failed'`; the UI shows `button.error` "Error, try a different Code" |
| `res.stderr.match(/^.*ERROR:.*$/gm)` | Import only (`games.ts:833`) | Logged, returned as `res.error` |
| `stderr.includes('ERROR') \|\| includes('error')` | EGL sync toggle only (`library.ts:802-805`) | `box.sync.error` |
| Epic status API | `https://status.epicgames.com/api/v2/components.json`; component name `Epic Games Store`/`Fortnite`/`Rocket League`, status `major` = offline | Desktop notification "… offline / Heroic will maybe not work probably!". Library refresh is skipped, and `info` gets `--offline` (`utils.ts:186-232`, `library.ts:102-109`, `:241-243`). |
| Install/update spawn failure | `res.error` set | Download-manager notification `notify.install.failed` / `notify.update.failed` (`downloadqueue.ts:312-320`) |

**Not handled by Heroic (Glider should):**
- `! Failure: Not enough available disk space! X GiB < Y GiB`: reported silently as success ([#3962](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/3962), [#4000](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/4000)).
- `Install path "…" does not exist, make sure all necessary mounts are available` (`core.py:1748-1751`).
- `Game is out of date, please update or launch with update check skipping!` (`cli.py:649`).
- `Login failed!` / `Login failed, cannot continue!` (`cli.py:213`, `:636`). Treat as "logged out".
- `errors.com.epicgames.oauth.corrective_action_required` plus `continuationUrl`.
- `Failed to acquire installed data lock` (another legendary instance running).
- `requests.exceptions.ConnectTimeout` against `launcher-public-service-prod06.ol.epicgames.com` shows as the generic "log out and in" dialog, which is misleading ([#3893](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/3893)). Map network exceptions to "Can't reach Epic".
- Failed download: `Installation failed after N seconds` (exit code is still 0!). Success marker: `Finished installation process in`.

---

## 8. Other things worth copying (or not)

**Library / metadata**
- Heroic does **not** use `list --json`. `refreshLegendary` runs `legendary list --third-party` (stdout ignored) solely to refresh `assets.json` and `metadata/*.json` on disk (`library.ts:100-126`), then reads those files directly (`loadFile`, `library.ts:485-656`). It never uses `--force-refresh`, `status`, or `list-installed`. The only `status` call is for EOS overlay info (`eos_overlay.ts:77-83`).
- For Fortnite-only, Glider can skip `list` and read the Fortnite metadata directly. Run `legendary list --json` (or `list` and read `metadata/Fortnite.json`) at most every N hours. `list --force-refresh` re-fetches metadata (`legendary/cli.py:215-223`).
- `status --json` returns `{account, games_available, games_installed, egl_sync_enabled, config_directory}` (`legendary/cli.py:1601-1630`). It is the cheapest "am I logged in / is the config healthy" probe, with `--offline` for no network.
- Update detection: compare `installed.json[app].version` with `assets.json[platform][].build_version` for the same `app_name` after running `list` (`library.ts:334-394`). For Glider, `legendary list-installed --check-updates --json` does the same, but Heroic's approach works without parsing output.
- `installed.json` fields Heroic reads: `executable, version, install_size, install_path, platform, save_path` (`library.ts:557-565`). `install_tags` and `egl_guid` are also stored by legendary (`common/types/legendary.ts:20-33`).
- Don't copy: Heroic's `libraryStore`/`installStore` electron-store caches are never invalidated per game version, which is wrong for Fortnite's weekly updates.

**Art (keyImages)** — `library.ts:573-590` (`type` values come from `metadata.keyImages[]`, each `{type, url, width, height, md5, size, uploadedDate}`):

| Heroic field | keyImage `type` | Use |
|---|---|---|
| `art_cover` (wide) | `DieselGameBox` or `OfferImageWide` | hero/banner |
| `art_square` (tall) | `DieselGameBoxTall` or `OfferImageTall` (fallback `DieselStoreFrontTall`, then cover) | portrait cover |
| `art_square_front` | `DieselStoreFrontTall` | tall store art |
| `art_logo` | `DieselGameBoxLogo` | logo overlay |
- Fallback image is `fallBackImage` (Heroic asset) when none matches. I did not verify that Fortnite's metadata has all of these types; check `metadata/Fortnite.json` after the first `list`.
- Extra info (description, reqs) comes from `https://store-content.ak.epicgames.com/api/<lang>/content/products/<slug>` and then GraphQL `launcher.store.epicgames.com/graphql` (`games.ts:135-275`). Optional for Glider.

**Metadata filters** — Heroic skips UE assets (`namespace === 'ue'`, categories `assets/asset-format/plugins/projects`), `mods`, and Android/iOS-only titles (`library.ts:499-535`). Irrelevant when Glider only handles Fortnite. DLC detection uses `dlcItemList[].releaseInfo[0].appId`.

**Other behaviours**
- `legendary` stdout is UTF-8; Glider should read both streams line by line, not by chunk.
- Preflight: Heroic checks `isOnline()` and the Epic status page before network actions; Glider should do the same for the login and refresh buttons.
- Version string: `legendary --version` is run at startup (`legendary version "0.21.1", codename "Lowlife"` per the log in [#6037](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/6037)). Useful to display in a diagnostics panel.
- Cloud saves: `sync-saves <App> --skip-upload|--skip-download --save-path <p> -y` (`games.ts:857-863`), run before and after launch if `autoSyncSaves && isOnline()` (`launcher.ts:146-154`, `:291-306`). Fortnite has no Epic cloud saves; skip.
- EOS overlay on Windows is "installed globally" and Heroic only enables it on Linux (`setup.ts:107-121`, `eos_overlay.ts`). Not needed for Fortnite, which ships its own.
- Heroic's `getInstallInfo` retries up to 3 times when `manifest` is missing from `info` output (`library.ts:249-273`). Epic's CDN sometimes returns 403 on manifests (visible in [#2306](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/2306) logs: "Unable to download manifest … (status: 403), trying next"). Treat as retryable.

---

## Appendix: quick command cheat-sheet for Glider (derived)

```
legendary auth --code <authorizationCode>            # after `auth --delete` if switching
legendary auth --delete                               # logout (removes user.json)
legendary status --json                               # login/config probe
legendary list --json                                 # (or just `list` + read metadata/)
legendary info Fortnite --json --platform Windows     # sizes, tags, prerequisites, launch_exe
legendary install Fortnite --platform Windows --base-path <dir> --skip-dlcs --skip-sdl -y
legendary update  Fortnite -y --skip-sdl
legendary repair  Fortnite -y --skip-sdl              # add --reset-sdl after bad updates
legendary uninstall Fortnite -y [--keep-files]
legendary import Fortnite <dir> --platform Windows
legendary move Fortnite <newParent> --skip-move       # after moving files yourself (robocopy /MOVE /MIR)
legendary launch Fortnite [--skip-version-check] [--language xx]   # exits immediately; watch processes yourself
env: LEGENDARY_CONFIG_PATH=<glider data>\legendary, PYTHONIOENCODING=utf-8
```
Parse: `= Progress: (\d+\.\d+)%`, `ETA: (\d\d:\d\d:\d\d)`, `Downloaded: ([\d.]+) MiB`, `Download\t- ([\d.]+) MiB/s`, `Disk\t- ([\d.]+) MiB/s` (all from stderr). Success markers: `Successfully logged in as "…"`, `Finished installation process in`. Failure markers: `Installation failed after`, `! Failure:`, `Login failed`, `corrective_action_required`, `No saved credentials`, `appears to be deleted`.
