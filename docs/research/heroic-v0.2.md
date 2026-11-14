# Heroic Games Launcher: Epic/Legendary patterns for Glider v0.2

Glider v0.2 is a full Epic Games Launcher replacement for the whole Epic library on Windows. This note records how Heroic handles the Epic/Legendary side so we can reuse what works and avoid its known pitfalls. It extends [`heroic.md`](heroic.md), which covers the Fortnite-era topics (login capture, progress regexes, error strings, EGL sync crashes, EAC and prerequisites gap). That material is **not** repeated here. Where this note contradicts `heroic.md`, see "Corrections" below.

## Sources and conventions

| Item | Value |
|---|---|
| Heroic repo | `Heroic-Games-Launcher/HeroicGamesLauncher`, default branch `main` |
| Commit studied | `89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa` ("[Feat] Ability to skip dxvk initial download (#6065)", 2026-11-13). `package.json` still says `2.22.4`. |
| Legendary studied | `legendary-gl/legendary` tag `0.21.1`, commit `42f6bdeadde3a9526dc8eb713763476999ac217b`. Heroic pins `0.21.1` and downloads `legendary_windows_x64.exe` from that repo ([`downloadHelperBinaries.ts:29-31`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/meta/downloadHelperBinaries.ts#L29-L31), [`downloadHelperBinaries.ts:124-138`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/meta/downloadHelperBinaries.ts#L124-L138)). |
| Link format | Every `file:lines` link is a permalink. Links to Legendary's `cli.py`, `core.py`, `game.py` and `eos.py` point at the Legendary commit above; all others point at the Heroic commit. Issue links go to GitHub issues. |
| Evidence labels | **[inference]** = my reading of the code, not confirmed by a run or an issue. **[unverified]** = could not be checked from source. Anything unlabelled is read straight from the cited code or issue. |
| Scope limits | Source reading and `gh` issue search only. I did not run Heroic, Legendary or any GUI. |

Path shorthands used in prose: `L/` = `src/backend/storeManagers/legendary/`, `B/` = `src/backend/`, `F/` = `src/frontend/`, `C/` = `src/common/`.

### Corrections to `heroic.md` (found at this newer commit)

1. **The `info` cache does expire.** `heroic.md` §4 said the install-info cache "is not invalidated". `installStore` is a `CacheStore` with the default lifespan of 6 hours ([`electronStores.ts:5-7`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/electronStores.ts#L5-L7), [`cache.ts:17-36`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/cache.ts#L17-L36)). It is still **not** invalidated when a game updates, so for up to 6 h an update can show stale `manifest.disk_size`, `download_size` and `build_version`. `libraryStore` has `null` lifespan (never expires) ([`electronStores.ts:8-11`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/electronStores.ts#L8-L11)).
2. **`launch --origin` / `--ubisoft` exist and Heroic uses them.** `heroic.md` did not mention the third-party launch path. Heroic sets them for EA/Ubisoft titles ([`games.ts:1017-1018`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L1017-L1018)). They exist in upstream Legendary 0.21.1 ([`cli.py:2917-2920`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L2917-L2920)), so no Heroic fork is needed.
3. **Windows "game running" detection is more reasonable than `heroic.md` implied.** `Start-Process -Wait` waits for the process *and its descendants* (PowerShell semantics; see [`launcher.ts:1683-1704`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L1683-L1704)), so the wrapper stays alive while the game runs even though `legendary launch` exits at once. It is still a weak signal (see §5).
4. Line numbers in `heroic.md` refer to `fe8a5f1`; use the permalinks here.

### Headline findings (new in this note)

1. **Heroic can run two Legendary processes at once.** The download queue is serial, but `list`, `info`, `launch`, `sync-saves` and `eos-overlay install` all go through the same spawn path with no global mutex (§2). Only identical command lines are de-duplicated ([`launcher.ts:1724-1730`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L1724-L1730)).
2. **"Pause" is not persisted.** `queueState` is an in-memory variable, so a queue paused before quitting resumes by itself 5 s after the next start ([`main.ts:489-501`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/main.ts#L489-L501), [`downloadqueue.ts:25-27`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L25-L27)).
3. **`callAllAbortControllers()` does nothing.** It uses `for (const key in map.keys())`, which iterates no keys ([`aborthandler.ts:29-33`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/utils/aborthandler/aborthandler.ts#L29-L33)). On quit Heroic relies on `Stop-Process -name legendary` instead.
4. **Heroic has no `com.epicgames.launcher://` handler and no free-games feature.** The Epic store is just a `<webview>` on epicgames.com (§10). The only `com.epicgames.launcher` string is a Wine registry key ([`setup.ts:28-39`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/setup.ts#L28-L39)).
5. **Windows EOS overlay is global, not per game.** Heroic's per-game overlay toggle is Linux-only; on Windows it offers one global enable/disable ([`index.tsx:353-355`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Game/GameSubMenu/index.tsx#L353-L355), [`index.tsx:84-92`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Settings/sections/AdvancedSettings/index.tsx#L84-L92)).
6. **Heroic never uses `legendary activate`.** EA/Ubisoft support is "download the third-party installer, silent-install it, then `launch --origin|--ubisoft`" (§8).

---

## 1. Library loading

### How Heroic does it

- **Files, not stdout.** Heroic never parses `legendary list` output. [`library.ts:100-126`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L100-L126) runs `legendary list --third-party` only so Legendary rewrites `assets.json` and `metadata/*.json` on disk. The model is then built from those files with synchronous reads ([`library.ts:485-656`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L485-L656)).
- **Which games exist.** `allGames` is every `*.json` filename in `<config>/metadata/` ([`library.ts:83-95`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L83-L95)). That folder also contains DLC, UE Marketplace assets and mobile titles, so filtering happens per file in `loadFile` (below).
- **Installed state.** [`library.ts:131-156`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L131-L156) reads `installed.json`, merges Heroic's own `third-party-installed.json` ([`thirdParty.ts:7-29`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/thirdParty.ts#L7-L29)), and builds a `Map`. A corrupt `installed.json` is caught and treated as "nothing installed" ([`library.ts:137-147`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L137-L147)).
- **Config isolation.** `LEGENDARY_CONFIG_PATH = <appFolder>/legendaryConfig/legendary` is set on every call ([`library.ts:694-698`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L694-L698), [`constants.ts:4-16`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/constants.ts#L4-L16)), so the user's own `~/.config/legendary` is never touched.
- **Caching and refresh.**
  - The built `GameInfo[]` is saved to an electron-store cache (`legendary_library`, no expiry) at the end of `refresh()` ([`library.ts:168-190`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L168-L190)).
  - The renderer shows that cache first and only asks Legendary again when the cache is empty ([`GlobalState.tsx:802-806`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/state/GlobalState.tsx#L802-L806)).
  - Refresh triggers: app start with `checkForUpdates: true` ([`GlobalState.tsx:1121-1138`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/state/GlobalState.tsx#L1121-L1138)); every game status of `done` or `error` ([`GlobalState.tsx:953-976`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/state/GlobalState.tsx#L953-L976)); successful login; cache clear ([`main.ts:895-906`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/main.ts#L895-L906)).
  - The renderer ignores a refresh request while one is in flight (`if (this.state.refreshing) return`, [`GlobalState.tsx:882`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/state/GlobalState.tsx#L882)).
  - Before `list`, Heroic polls `status.epicgames.com/api/v2/components.json` and **skips the refresh** if "Epic Games Store" is `major` ([`utils.ts:186-228`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/utils.ts#L186-L228), [`library.ts:102-109`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L102-L109)).

### Game model: where each field comes from

All from `metadata/<app>.json` → `metadata` unless noted ([`library.ts:497-653`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L497-L653); types in [`legendary.ts:35-135`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/common/types/legendary.ts#L35-L135), `GameInfo` in [`types.ts:204-245`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/common/types.ts#L204-L245)).

| Glider need | Heroic field | Source |
|---|---|---|
| Title | `title` | `metadata.title` |
| App name | `app_name` | file name, equals the manifest app name |
| Namespace | `namespace` | `metadata.namespace` (also used for anti-cheat lookup, `Linux/Mac` only: [`utils.ts:57-62`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/anticheat/utils.ts#L57-L62)) |
| Catalog item id | **not exposed** by Heroic | In the JSON as `metadata.id` and `asset_infos.<platform>.catalog_item_id`. Glider's store/deep-link work needs it, so keep it. |
| Base → DLC | `dlcList`; DLC app names pushed to a local `dlcs` array | `dlcItemList[].releaseInfo[0].appId` ([`library.ts:549-555`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L549-L555)) |
| DLC → base | `install.is_dlc` | `Boolean(metadata.mainGameItem)` ([`library.ts:592`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L592)). Frontend hides DLC rows from search/library via `install.is_dlc` ([`index.tsx:47`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/components/UI/LibrarySearchBar/index.tsx#L47)). |
| Cloud saves | `cloud_save_enabled`, `save_folder` | `customAttributes.CloudSaveFolder.value` (or `CloudSaveFolder_MAC` on Mac); `save_path` comes from `installed.json` ([`library.ts:567-570`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L567-L570), [`library.ts:614`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L614)) |
| Runs offline | `canRunOffline` | `customAttributes.CanRunOffline.value === 'true'` ([`library.ts:543`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L543)) |
| Third-party store | `thirdPartyManagedApp`, `isEAManaged`, `isUbisoftManaged` | `customAttributes.ThirdPartyManagedApp` **or** `ThirdPartyManagedProvider`; EA = `origin` / `the ea app`; Ubisoft = `ubisoftconnect` (lower-cased) ([`library.ts:544-649`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L544-L649)). Same logic as [`game.py:123-135`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/models/game.py#L123-L135). |
| Install folder name | `folder_name` | `customAttributes.FolderName.value` else `app_name` ([`library.ts:571`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L571)). Legendary uses the same rule ([`core.py:1566-1572`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/core.py#L1566-L1572)), and for DLC it uses the **base game's** folder. |
| Platforms | `is_mac_native` | `releaseInfo[0].platform` (DLC falls back to `mainGameItem`) ([`library.ts:596-607`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L596-L607)) |
| Install state | `install.{executable,install_path,version,platform,install_size}` | `installed.json` ([`library.ts:557-565`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L557-L565)) |

Filters in `loadFile`: skip UE assets (`namespace === 'ue'` or categories `assets`, `asset-format`, `plugins`, `projects`, [`library.ts:497-508`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L497-L508)); skip `mods` ([`library.ts:523-526`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L523-L526)); skip entries whose every `releaseInfo.platform` is Android/iOS ([`library.ts:528-535`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L528-L535)).

### Art selection

Heroic picks from `metadata.keyImages[]` by `type` ([`library.ts:573-590`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L573-L590), fallbacks in [`library.ts:609-613`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L609-L613)).

| Use | Preferred keyImage types | Fallback chain |
|---|---|---|
| Wide cover (`art_cover`) | `DieselGameBox`, `OfferImageWide` | tall art, then a bundled `fallback` image |
| Tall cover (`art_square`) | `DieselGameBoxTall`, `OfferImageTall` | `DieselStoreFrontTall`, then wide art, then fallback |
| Store-front tall (`art_square_front`) | `DieselStoreFrontTall` | only used as a fallback above |
| Logo (`art_logo`) | `DieselGameBoxLogo` | none |

Heroic does not pick by `width`/`height`. Images are shown through a `CachedImage` component that rewrites remote URLs to a custom `imagecache://` protocol ([`index.tsx:47`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/components/UI/CachedImage/index.tsx#L47)). The protocol handler stores each file on disk under `images-cache/<sha256(url)>` with no expiry or revalidation and downloads it in the background on first request ([`images_cache.ts:9-52`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/images_cache.ts#L9-L52)). **[inference]** the first request returns a non-image body, which is why the component falls back to the direct URL until the file is cached.

### Staying fast with large libraries

Mostly it doesn't, and it relies on the cache:
- Per refresh it does one `readFileSync` + `JSON.parse` per metadata file, inside a `forEach` ([`library.ts:663-675`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L663-L675)), then writes the full array to one electron-store file.
- There is no incremental reload and no mtime check. The renderer is fast only because it reads the cached array first.
- User report of the UI becoming very slow while a download runs: [#5046](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/5046) (closed, no cause recorded; Steam Deck, 2.18.1). **[inference]** contention between the refresh path and the progress-update flood.

### Legendary commands and flags

| Purpose | Command | Notes |
|---|---|---|
| Refresh disk metadata | `list --third-party` | `--third-party` includes EA/Ubisoft titles with no assets ([`cli.py:205-250`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L205-L250)). Output ignored. |
| Per-game detail | `info <app> --json --platform Windows` | cached 6 h; retried up to 3× if `manifest` is missing ([`library.ts:221-277`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L221-L277)) |
| Not used | `list --json`, `list --force-refresh`, `list-installed`, `status --json` | Glider could use `list --json` (it embeds `dlcs` per game, [`cli.py:250-256`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L250-L256)) but disk files carry more. |

### Pitfalls Heroic works around

- Offline / Epic outage: `list` is skipped; the stale cache is shown.
- `installed.json` can be half-written during an install import, which is why the parse failure is swallowed ([`library.ts:137-147`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L137-L147), comment on false positives). **[inference]** Heroic reads without taking Legendary's installed-data lock.
- A metadata file that fails to parse is logged and silently dropped from the library ([`library.ts:488-496`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L488-L496)).

### Glider recommendation

Read `metadata/*.json`, `installed.json` and `assets.json` directly, keyed by file mtime into a Glider-owned normalized snapshot (persisted, loaded first at startup), and spawn `list --third-party` only for explicit/periodic refresh while keeping `catalog_item_id`.

---

## 2. Download queue

### Data model

- Stored in an electron-store file `store/download-manager.json` with keys `queue` and `finished` ([`downloadqueue.ts:16-19`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L16-L19)).
- Element: `DMQueueElement { type: 'install'|'update', params: InstallParams, addToQueueTime, startTime, endTime, status? }` ([`types.ts:650-658`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/common/types.ts#L650-L658)).
- `InstallParams` = `appName, runner, path, platformToInstall, installDlcs?, sdlList?, installLanguage?, branch?, build?, gameInfo, size?` ([`types.ts:395-411`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/common/types.ts#L395-L411)). It embeds a **full `GameInfo` snapshot**, so a queue entry persists a stale copy of the game record.
- `size` is a display string from `info.manifest.download_size` at enqueue time, or `'?? MB'` for EA/Ubisoft ([`downloadqueue.ts:145-181`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L145-L181)).
- Re-adding the same `appName`+`runner` replaces the existing element in place ([`downloadqueue.ts:136-143`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L136-L143)).
- Queue state is `'idle' | 'running' | 'paused' | 'stopped'` ([`types.ts:828`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/common/types.ts#L828)), held in a module variable.

### Concurrency

- **Queue**: strictly one element at a time. `initQueue()` is a `while` loop over the first element ([`downloadqueue.ts:82-116`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L82-L116)); `addToQueue` starts it only when idle ([`downloadqueue.ts:193-195`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L193-L195)).
- **DLC**: the install IPC handler enqueues the base game, then one extra element per selected DLC ([`ipc_handler.ts:13-44`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/ipc_handler.ts#L13-L44)). Each runs as its own `legendary install <dlcAppName> … --skip-dlcs`. Legendary refuses a DLC install when the base game is not installed ([`cli.py:907-916`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L907-L916)), so order matters. Cancel removes the DLC children as well ([`downloadqueue.ts:231-255`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L231-L255)).
- **What else runs beside a download** (no mutex anywhere):
  - `legendary list --third-party` on every `done`/`error` status and at startup ([`GlobalState.tsx:953-976`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/state/GlobalState.tsx#L953-L976));
  - `legendary info` (install dialog, `addToQueue` for the next games);
  - `legendary launch` for any other game: [`launcher.ts:102-328`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L102-L328) has no "download in progress" check; **the per-game UI state is the only guard** **[inference]**;
  - `sync-saves` before/after launch;
  - `eos-overlay install` runs **outside** the queue and can overlap a game download ([`eos_overlay.ts:90-153`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/eos_overlay/eos_overlay.ts#L90-L153)).
- Legendary only takes the installed-data lock for `install`/`update`/`repair` (all via `install_game`), `import`, `uninstall` and `move` ("Failed to acquire installed data lock", [`cli.py:845-848`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L845-L848), [`cli.py:1349`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L1349), [`cli.py:1188`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L1188)). `launch`, `list`, `info`, `sync-saves` do not take that lock.
- A comment in the renderer admits the hazard: "This avoids calling legendary again before the previous process is killed when canceling" ([`GlobalState.tsx:959`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/state/GlobalState.tsx#L959)).
- Start-up ordering: the queue starts **5 s after `frontendReady`** ([`main.ts:489-501`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/main.ts#L489-L501)), which is after the startup library refresh has kicked off. **[inference]** the delay is a crude way to let that first `list` finish.

### Pause / resume / cancel on Windows

| Action | Implementation |
|---|---|
| Pause | `pauseCurrentDownload()` calls `stopCurrentDownload()` then sets `queueState = 'paused'` ([`downloadqueue.ts:257-268`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L257-L268)). Stop = `callAbortController(appName)` + `game.stop(false)` ([`downloadqueue.ts:275-279`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L275-L279)). |
| What the abort does | `AbortController.abort()` makes Node kill the *spawned* process ([`launcher.ts:1736-1741`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L1736-L1741)). On Windows that process is the **`powershell` wrapper** ([`launcher.ts:1683-1704`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L1683-L1704)), so the abort alone does not reliably stop Legendary or its worker children. **[inference]** |
| The real kill | `game.stop()` → `killPattern('legendary')` → `spawnSync('Stop-Process', ['-name','legendary'], {shell:'powershell.exe'})` ([`games.ts:1085-1097`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L1085-L1097), [`utils.ts:814-826`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/utils.ts#L814-L826)). This kills **every** process named `legendary`, including a concurrent `list`/`launch` helper and PyInstaller worker processes. |
| Resume | `resumeCurrentDownload()` just calls `initQueue()` again, re-running the same `install`/`update` command; Legendary resumes from its own resume file ([`downloadqueue.ts:270-273`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L270-L273)). |
| Cancel | `cancelCurrentDownload({removeDownloaded})` stops the process, removes DLC children and the element, and optionally `removeFolder(path, folder_name)` **immediately** ([`downloadqueue.ts:231-255`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L231-L255)). **[inference]** folder deletion can race a process that is still exiting. |
| Auto-pause | On connectivity loss the queue pauses; it auto-resumes only if *it* paused it ([`downloadqueue.ts:29-39`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L29-L39)). |
| Aborted vs failed | `res.abort` → `{status:'abort'}`; the loop then treats it as "paused" only if `isPaused()` is true, else "cancelled" ([`downloadqueue.ts:104-111`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L104-L111), [`downloadqueue.ts:282-311`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L282-L311)). |

Known reports (all still relevant to a Windows launcher): pause/cancel not stopping ([#5668](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/5668), open); progress/pause confusion after "keep files" ([#2239](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/2239), open); resume failures and "if you kill Heroic somehow the legendary process might continue" ([#702](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/702), closed).

### Abort bookkeeping bugs worth avoiding

- Abort ids are `appName` for **every** command about that game (`info`, `install`, `update`, `repair`, `uninstall`, `launch`, `move`, `sync-saves`). `createAbortController(id)` *overwrites* the map entry and `deleteAbortController(id)` runs when *any* of them finishes ([`launcher.ts:1733-1735`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L1733-L1735), [`launcher.ts:1823-1827`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L1823-L1827), [`aborthandler.ts:5-10`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/utils/aborthandler/aborthandler.ts#L5-L10)). If an `info` call for the same app finishes mid-download, the install's controller is gone and the next pause logs "Aborting not possible" ([`aborthandler.ts:12-27`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/utils/aborthandler/aborthandler.ts#L12-L27)). **[inference]** a plausible cause of #5668.
- Quit: `handleExit` asks for confirmation if a queue is running, then `killPattern('legendary'|'gogdl'|'nile')` and `callAllAbortControllers()`, which is a no-op ([`utils.ts:241-280`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/utils.ts#L241-L280), [`aborthandler.ts:29-33`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/utils/aborthandler/aborthandler.ts#L29-L33)).

### Progress parsing

Covered in `heroic.md` §3 (regexes at [`games.ts:399-486`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L399-L486)). One addition here: progress is pushed over IPC (`sendProgressUpdate`) only when `eta`, `bytes`, `downSpeed` and `diskSpeed` are all filled, then reset, so about one update per second ([`games.ts:462-485`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L462-L485)). The percentage is Heroic-computed from the `info` size and is wrong after resume; use Legendary's own `= Progress:` line.

### Persistence across restarts

- `queue` and `finished` survive (electron-store). `finished` is keyed by `appName` and overwritten on repeat ([`downloadqueue.ts:58-76`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L58-L76)).
- Not persisted: `queueState`, `currentElement`, `autoPaused` ([`downloadqueue.ts:25-27`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L25-L27)). After restart the head element starts again regardless of whether the user had paused. Legendary's resume file makes the restart cheap, but the user-visible pause is lost.
- Notifications per outcome: [`downloadqueue.ts:281-333`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L281-L333).

### Preflight in the queue worker

`installQueueElement` / `updateQueueElement` bail with `error` if the app is offline, and show a dialog if Epic is in `major` outage, before spawning anything ([`utils.ts:29-50`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/utils.ts#L29-L50), [`utils.ts:115-136`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/utils.ts#L115-L136)).

### Glider recommendation

Use one Glider-owned operation scheduler: a single serial download queue persisted with its own paused/running flag, plus a lock so no other Legendary process starts while a download is up, and kill with a Windows Job Object (tree kill) scoped to that child, never by process name.

---

## 3. Install options

### DLC selection

- The install dialog calls `info --json` and **pre-selects every owned DLC** (`game.owned_dlc`) ([`index.tsx:359-375`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Library/components/InstallModal/DownloadDialog/index.tsx#L359-L375)). The selector is shown for `legendary` and `gog` when the list is non-empty ([`index.tsx:586-587`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Library/components/InstallModal/DownloadDialog/index.tsx#L586-L587)).
- Heroic always passes `--skip-dlcs` ([`games.ts:603`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L603)) and installs DLCs itself as separate queue items (§2). Legendary's own `--with-dlcs` is used only for `import` ([`games.ts:817`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L817)).
- The dialog's size maths adds DLC sizes only when the DLC object has `perLangSize`, which is a GOG-only shape ([`index.tsx:483-559`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Library/components/InstallModal/DownloadDialog/index.tsx#L483-L559)). **[inference]** Epic DLC sizes are therefore **not** added to the displayed total or the free-space check.
- Some DLC are launchable add-ons or inject launch arguments (`AdditionalCommandLine`). Heroic turns those into launch options ([`library.ts:878-931`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L878-L931)).

### Install tags / SDL

- Generic games: install tags are only passed if the user picked them. The command builder emits `--install-tag=` (reset) followed by one `--install-tag <tag>` per tag ([`library.ts:830-837`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L830-L837)); with no tags it passes `--skip-sdl` ([`games.ts:608-610`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L608-L610)).
- Which games show an SDL picker is **curated outside Legendary**: `https://heroic.legendary.gl/v1/version.json` lists `game_overrides.sdl_config`; per-game tag groups come from `https://heroic.legendary.gl/v1/sdl/<app>.json` ([`library.ts:709-766`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L709-L766), [`main.ts:673`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/main.ts#L673)). The override list is cached 7 days ([`electronStores.ts:21-26`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/electronStores.ts#L21-L26)), and the picker appears only when `sdl_config[appName]` exists ([`index.tsx:443-454`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Library/components/InstallModal/DownloadDialog/index.tsx#L443-L454)).
- Legendary's own SDL logic and prompts: [`cli.py:942-983`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L942-L983). `--skip-sdl` keeps saved `install_tags` on update/repair.

### Size and free-space checks

- Sizes: `info --json` → `manifest.disk_size` / `download_size` ([`legendary.ts:136-179`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/common/types/legendary.ts#L136-L179)).
- Free space: the renderer calls the `checkDiskSpace` IPC ([`main.ts:559-570`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/main.ts#L559-L570)) and compares `free < diskSize` (scaled by `progress/100` when resuming) ([`index.tsx:455-481`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Library/components/InstallModal/DownloadDialog/index.tsx#L455-L481)). Details of the PowerShell drive query are in `heroic.md` §4.
- Legendary re-checks and fails with "Not enough available disk space"; Heroic does not match it ([#3962](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/3962), open).

### `--base-path` vs `--game-folder`

- Heroic always passes `--base-path <dir>`; it never passes `--game-folder` (typed but unused, [`install.ts:16`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/commands/install.ts#L16)).
- Legendary resolves the final path as `base_path / FolderName` (fallback `app_name`; base game's folder for DLC) ([`core.py:1563-1593`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/core.py#L1563-L1593)). Heroic mirrors that with `folder_name` so cancel cleanup and the "game path" display agree ([`library.ts:571`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L571), [`downloadqueue.ts:244-252`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L244-L252)).
- Moving an install: Heroic moves files itself and then runs `move <app> <parentDir> --skip-move` ([`library.ts:415-452`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L415-L452), details in `heroic.md` §4).
- A path from the renderer has single quotes stripped before use (`path.replaceAll("'", '')`, [`utils.ts:82`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/utils.ts#L82)). **[inference]** a hack against shell-quoting problems; Glider passes argv directly and does not need it.

### Platform

- `--platform` is always passed to `install` and `info` ([`games.ts:601`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L601), [`library.ts:239`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L239)). Epic values: `Windows`, `Win32`, `Mac`, `Android`, `iOS` ([`legendary.ts:5-10`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/common/types/legendary.ts#L5-L10)).
- Legendary falls back from a missing platform to `Windows` by default (`install_platform_fallback`, [`cli.py:893-906`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L893-L906)), which can silently install the wrong build.
- Other install flags Heroic wires up: `--max-workers`, `--no-https` (settings), and a one-shot retry with `--max-shared-memory 5000` on `MemoryError` ([`games.ts:592-640`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L592-L640)).

### Glider recommendation

Show an install sheet fed by `info --json` (sizes + owned DLC + tags), enqueue the base game then each chosen DLC as its own `install <dlc> --skip-dlcs -y`, always pass `--base-path`, `--platform` and `--skip-sdl`/explicit tags, and count DLC bytes in the free-space check.

---

## 4. Updates

### How Heroic checks

- `checkGameUpdates` loops every store manager and awaits each in turn ([`main.ts:648-662`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/main.ts#L648-L662)); there is an open issue to parallelize it ([#5632](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/5632)).
- Epic: [`library.ts:284-405`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L284-L405)
  1. skip if not logged in, offline, or Epic in outage;
  2. run `list --third-party` (this refreshes `assets.json` for every platform that has installs);
  3. read `installed.json` and `assets.json`; for each installed `(app, platform)` find the asset with the same `app_name` and compare `installed.version !== asset.build_version`.
- This includes **DLC**, because DLC are entries in `installed.json` and `assets.json` too; each becomes its own update.
- Triggered once at startup and again after any update finishes or is cancelled ([`GlobalState.tsx:794`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/state/GlobalState.tsx#L794), [`GlobalState.tsx:954-964`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/state/GlobalState.tsx#L954-L964)). There is **no periodic timer** (no `setInterval` around updates; I grepped for it).
- A cached list of updates is also kept in `localStorage` (`updates`) and re-read on start ([`GlobalState.tsx:1118-1121`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/state/GlobalState.tsx#L1118-L1121)).

### Auto-update

- Setting `autoUpdateGames` (`config.ts`). When on, the same IPC feeds each result into `autoUpdate()` ([`index.ts:44-62`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/index.ts#L44-L62)), which enqueues an `update` element for each game that is **not** `ignoreGameUpdates` and whose install path exists (`isGameAvailable`).
- The enqueue is `addToQueue(dmQueueElement)` without `await`, and the "remove from the array" line mutates a local copy ([`index.ts:55-56`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/index.ts#L55-L56)). **[inference]** the returned array still contains games that were already auto-queued, so the UI can show them as pending updates.
- Command: `update <app> -y --skip-sdl [--max-workers N] [--no-https]` ([`games.ts:505-512`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L505-L512)).
- Per-game `ignoreGameUpdates`: at launch Heroic passes `--skip-version-check` instead of prompting ([`library.ts:160-176`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/helpers/library.ts#L160-L176), [`protocol.ts:139-145`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/protocol.ts#L139-L145)).

### Pitfalls

- "Update available" false positives and update loops: [#2160](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/2160) (Fortnite never updates), [#3012](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/3012) (updates re-apply), [#3962](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/3962) (silent failure on low disk). Both comparisons are plain string inequality, so a rollback or a different build label looks like an update.
- Epic manifest format changes break Legendary downloads at 0%: [#5426](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/5426) (Death Stranding 2: new manifest format, fixed in a Heroic-maintained Legendary fork, [Heroic/legendary#36](https://github.com/Heroic-Games-Launcher/legendary/issues/36)). Pinning a Legendary binary needs a way to ship a newer one quickly.

### Glider recommendation

Check at startup and on a timer by running `list --third-party` then diffing `assets.json` vs `installed.json` (including DLC), auto-update through the same serial queue, and surface manifest-format and disk-space failures as explicit errors.

---

## 5. Launch

### Command and online/offline decision

`prepareLaunch` ([`launcher.ts:484-496`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L484-L496)): `offlineMode = gameSettings.offlineMode || !isOnline()`; if still online and runner is Epic, `offlineMode = await isEpicServiceOffline()` (status page `major`). If `!canRunOffline && offlineMode` it **only logs a warning** ([`launcher.ts:498-503`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L498-L503)). The renderer separately asks "this game might not work offline, play anyway?" ([`library.ts:236-257`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/helpers/library.ts#L236-L257)).

The command is built at [`games.ts:1001-1018`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L1001-L1018):

| Flag | When |
|---|---|
| `launch <app> [extra args…]` | `args` from the protocol + the chosen launch option's `parameters` + per-game `launcherArgs` |
| `--skip-version-check` | user chose "play without updating", `ignoreGameUpdates`, or CLI no-GUI |
| `--language <xx>` | per-game or global language set |
| `--override-exe <rel>` | `altExe` launch option or per-game `targetExe` |
| `--offline` | `offlineMode` above |
| `--origin` / `--ubisoft` | EA / Ubisoft managed titles |

Not used on Windows: `--dry-run`, `--json`, `--set-defaults`, `--wrapper`, `--wine*`. The `launch <addon>` form is used for launchable DLC ([`games.ts:990-993`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L990-L993)).

What Legendary does online before spawning: `Logging in…`, then `get_asset(update=True)` (a network call) and exits with `Game is out of date…` if versions differ, unless `--skip-version-check` or the game is in `noupdate` config ([`cli.py:630-650`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L630-L650)). With `--offline` it skips login and passes an empty `-AUTH_PASSWORD=`, warning if the game is not `can_run_offline` ([`core.py:886-896`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/core.py#L886-L896)). Token requests for games needing an ownership token add `-epicovt=` ([`core.py:906-912`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/core.py#L906-L912)).

### Alternative exe, working dir, args

- `--override-exe` is relative to the install dir; the working directory is the exe's directory ([`core.py:851-863`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/core.py#L851-L863)). Heroic does not set a working directory itself on Windows.
- Launch options come from `info.game.launch_options` (alternative executables from the manifest), plus launchable DLC and DLC `AdditionalCommandLine` ([`library.ts:878-931`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L878-L931)).
- Last used option is remembered per game (`lastUsedLaunchOption`, [`launcher.ts:130-132`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L130-L132)).

### Prerequisites, EAC, BattlEye

Beyond `heroic.md` §5.3:
- Legendary records pending prerequisites in `installed.json[app].prereq_info` and sets `prereq_info.installed = true` only when it ran them interactively ([`core.py:1837-1839`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/core.py#L1837-L1839), [`core.py:1885-1887`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/core.py#L1885-L1887)). With `-y` the flag stays **false** ([`cli.py:1162-1165`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L1162-L1165)), so Glider can use `prereq_info.installed == false` as its "needs prerequisites" marker.
- Heroic's prerequisite runner and `HKCR\com.epicgames.launcher` registry key are Wine-only ([`setup.ts:28-74`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/setup.ts#L28-L74)). The registry key exists to fix games that look for the Epic protocol handler (comment: Fallout New Vegas, Dishonored: Death of the Outsider). **Glider on Windows should expect real EGL's protocol handler to be absent and register `com.epicgames.launcher` itself, see §10.**
- EAC / BattlEye: only Linux runtime toggles exist (`eacRuntime`, `battlEyeRuntime` in [`types.ts:247-254`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/common/types.ts#L247-L254)). Heroic shows anti-cheat compatibility from AreWeAntiCheatYet on Linux/Mac only ([`utils.ts:14-17`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/anticheat/utils.ts#L14-L17), [`utils.ts:57-62`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/anticheat/utils.ts#L57-L62)). Nothing for Windows.
- Opt-in `LEGENDARY_WRAPPER_EXE` / `USE_FAKE_EPIC_EXE` fakes an Epic launcher parent process ([`games.ts:921-942`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L921-L942)). See `heroic.md` §5.1.

### Running-state tracking and playtime

- `sendGameStatusUpdate(playing)` before the spawn, `done` after the promise resolves ([`games.ts:1020-1037`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L1020-L1037), [`launcher.ts:316-320`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L316-L320)).
- Playtime is wall-clock between launch and the end of the wrapped command, stored per app in `tsStore` as `firstPlayed`, `lastPlayed`, `totalPlayed` (minutes) ([`launcher.ts:138-142`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L138-L142), [`launcher.ts:266-274`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L266-L274)). No process polling, no idle detection.
- Power: blocks display sleep for the session via `powerSaveBlocker` ([`launcher.ts:175-179`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L175-L179), [`launcher.ts:260-264`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L260-L264)). Note `powerDisplayId` is never reset to `null` after `stop()`, so a second launch skips the blocker. **[inference]**
- Stop = `Stop-Process -name legendary`, which does **not** end the game ([`games.ts:1085-1097`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L1085-L1097)).

### Other launch-time behaviours

- Before launch: if the install dir is gone, offer "forget this game" (`askForceUninstall`, [`launcher.ts:112-125`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L112-L125)).
- Before/after scripts: `runBeforeLaunchScript` / `runAfterLaunchScript` ([`launcher.ts:221`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L221), [`launcher.ts:252`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L252)).
- `minimizeOnLaunch` hides the main window ([`launcher.ts:170-173`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L170-L173)).
- Outdated EOS overlay is only discoverable via verbose logs: [#4841](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/4841) (open).

### Glider recommendation

Run `launch` with explicit `--skip-version-check`/`--offline`/`--override-exe`, decide offline from Glider's own connectivity check (not only an HTTP status page), launch via `Process.Start`, and track the game process tree yourself so running state and playtime survive the launcher stub exiting.

---

## 6. Cloud saves

### Flow

- **Before launch**: if `autoSyncSaves && isOnline()`, run `sync-saves <app> --skip-upload --save-path <p> -y` (download only), swallowing errors ([`launcher.ts:146-162`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L146-L162)).
- **After exit**: `sync-saves <app> --skip-download --save-path <p> -y` (upload only) ([`launcher.ts:291-314`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L291-L314)).
- Command builder: [`games.ts:848-879`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L848-L879). Output is captured and returned to the UI as text.
- Manual sync in the game page offers four actions: `--skip-upload` (download), `--skip-download` (upload), `--force-download`, `--force-upload` ([`CloudSavesSync.tsx:134-140`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Game/GamePage/components/CloudSavesSync.tsx#L134-L140)).
- Manual and auto sync are gated by the Epic status check and connectivity ([`main.ts:1168-1187`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/main.ts#L1168-L1187)).

### Save path computation

[`save_sync.ts:38-101`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/save_sync.ts#L38-L101) (`getDefaultLegendarySavePath`):
1. If `installed.json` already holds a `save_path`, Heroic **nulls it by rewriting `installed.json` directly** (its own FIXME: "This isn't really that safe").
2. It runs `sync-saves <app> --skip-upload --skip-download --accept-path` so Legendary computes the path via `get_save_path` and stores it.
3. It re-reads the game record and returns `save_path`, or `''` plus an error if none.
The UI warns when the computed folder doesn't exist and offers manual selection ([`legendary.tsx:44-139`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Settings/sections/SyncSaves/legendary.tsx#L44-L139)).

### Legendary's behaviour you must account for

[`cli.py:469-592`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L469-L592):
- **There is no real conflict state.** It compares timestamps: remote newer → download, local newer → upload, same → skip. `-y` auto-confirms whichever direction wins.
- With `-y` and no stored path it skips the game unless `--accept-path` is given ([`cli.py:511-514`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L511-L514)).
- Paths containing unprocessed `%…%`/`{…}` variables are refused under `--accept-path` ([`cli.py:521-526`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L521-L526)).
- Download uses `clean_dir=True` ([`cli.py:572-573`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L572-L573)), i.e. the local save folder is cleared before extraction.
- `--force-upload`/`--force-download` override the comparison. `--disable-filters` ignores the game's include/exclude list.

### Pitfalls

- **No local backup before download.** Requested in [#4074](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/4074) (open). Given `clean_dir=True` this is the main data-loss risk.
- **Confusing UX** (users think the save-path field relocates saves): [#3963](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/3963) (open).
- **Crash with an unset path on Windows**: [#5566](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/5566) (open): `check_savegame_state` → `os.walk(None)` → `TypeError` ("Hogwarts Legacy" settings page). Never call `sync-saves` without a resolved `--save-path`.
- No visual indicator of sync state/result: [#4228](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/4228) (open).
- `sync-saves` is skipped silently when offline, so a session can end with stale cloud data.

### Glider recommendation

Resolve the save path with `sync-saves --skip-upload --skip-download --accept-path`, snapshot the local save folder to a Glider backup folder before any download, run download-before/upload-after as separate steps with a visible result, and expose the force flags only behind an explicit "resolve conflict" dialog.

---

## 7. EOS overlay

### Install / enable / update flow

Heroic: [`eos_overlay.ts`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/eos_overlay/eos_overlay.ts); UI in [`index.tsx:43-160`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Settings/sections/AdvancedSettings/index.tsx#L43-L160).

| Step | Command | Source |
|---|---|---|
| Install location | `<appData>/tools/eos_overlay` (`defaultInstallPath`) | [`eos_overlay.ts:23`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/eos_overlay/eos_overlay.ts#L23) |
| Size probe | `eos-overlay install --path <p>` **without `-y`**; parse `Download size:`; then `callAbortController` | [`eos_overlay.ts:90-118`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/eos_overlay/eos_overlay.ts#L90-L118) |
| Install | `eos-overlay install --path <p> -y` with live progress through `onInstallOrUpdateOutput` | [`eos_overlay.ts:119-153`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/eos_overlay/eos_overlay.ts#L119-L153) |
| Installed? | `overlay_install.json` exists | [`eos_overlay.ts:26-47`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/eos_overlay/eos_overlay.ts#L26-L47) |
| Latest version | `overlay_version.json` → `data.buildVersion`, refreshed by running `legendary status` | [`eos_overlay.ts:49-88`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/eos_overlay/eos_overlay.ts#L49-L88) |
| Remove | confirm dialog then `eos-overlay remove -y` | [`eos_overlay.ts:155-183`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/eos_overlay/eos_overlay.ts#L155-L183) |
| Enabled? | `eos-overlay info`, grep `Overlay enabled` … `Yes`, then abort | [`eos_overlay.ts:246-270`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/eos_overlay/eos_overlay.ts#L246-L270) |
| Enable / disable | `eos-overlay enable` / `disable` (no args on Windows) | [`eos_overlay.ts:185-244`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/eos_overlay/eos_overlay.ts#L185-L244) |

- The overlay has a fixed pseudo app name `98bc04bc842e4906993fd6d6644ffb8d`, so it appears in the download UI as a normal item and has its own status/cancel ([`eos_overlay.ts:24`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/eos_overlay/eos_overlay.ts#L24), [`index.tsx:140-143`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Settings/sections/AdvancedSettings/index.tsx#L140-L143)).
- UI: status text (not installed / installing / installed / updating), installed vs latest version, a "check for updates" button, install/update/remove/cancel buttons, and on Windows a single **global** toggle ([`index.tsx:84-160`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Settings/sections/AdvancedSettings/index.tsx#L84-L160)). Per-game toggling is Linux-only ([`index.tsx:353-355`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Game/GameSubMenu/index.tsx#L353-L355)).

### What Legendary does on Windows

- `install` (and `update`) downloads the overlay and, on `os.name == 'nt'`, **also enables it** by writing `HKCU\SOFTWARE\Epic Games\EOS\OverlayPath` and HKCU Vulkan implicit-layer values ([`cli.py:2395-2420`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L2395-L2420), [`eos.py:31-100`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/lfs/eos.py#L31-L100)). HKCU only, so no elevation is needed.
- Heroic's renderer assumes this: after install it sets `eosOverlayEnabledGlobally = isWindows` ([`index.tsx:122`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Settings/sections/AdvancedSettings/index.tsx#L122)).
- The overlay is a shared, global component; the real EGL also manages it through the same keys, so installing both can fight over `OverlayPath`. **[inference]**

### Pitfalls

- Install failures that "install anyway" with no way to enable: [#3238](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/3238) (open), [#4029](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/4029) (open, see `heroic.md` §6 for the EGL-path noise in that log), [#5239](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/5239) (open, traceback from a prompt reading `input()` in `eos-overlay install` after `Download size:`; shows why the no-`-y` probe is fragile).
- Outdated overlay not surfaced: [#4841](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/4841) (open).
- The `eos-overlay info` probe runs and is then aborted on the first match; a late kill can leave Python running. **[inference]**

### Glider recommendation

Offer one global "EOS overlay" row (installed version, latest version from `status`, install/update/remove, enabled state from `eos-overlay info`), probe size with `info`-style output but with a hard timeout and a closed stdin, and warn when a real EGL install also owns the HKCU key.

---

## 8. Third-party stores (Ubisoft Connect / EA app)

### Detection

Library items with `customAttributes.ThirdPartyManagedApp` or `ThirdPartyManagedProvider` are non-installable by Legendary; Heroic maps them to `isEAManaged` / `isUbisoftManaged` (§1). These have no assets, which is why `list --third-party` is required for them to appear ([`cli.py:222-230`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L222-L230)). `legendary install` refuses them: "has to be installed via a third-party store" and exits 0 ([`cli.py:886-891`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L886-L891)).

### Install (Windows) as Heroic does it

[`games.ts:662-739`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L662-L739):
1. Download the vendor installer once into `<tools>/redist/legendary`: EA App from `origin-a.akamaihd.net/.../EAappInstaller.exe`, Ubisoft Connect from `static3.cdn.ubi.com/.../UbisoftConnectInstaller.exe` ([`constants.ts:16`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/constants.ts#L16)).
2. Run it: EA `EAappInstaller.exe EAX_LAUNCH_CLIENT=0 IGNORE_INSTALLED=1`; Ubisoft `UbisoftConnectInstaller.exe /S`. Exit code `3` is treated as "user aborted".
3. Record the game in Heroic's own `third-party-installed.json` as `[appName, platform]` pairs ([`thirdParty.ts:31-59`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/thirdParty.ts#L31-L59)), so it shows as "installed" and gets a Play button. **Heroic has no idea whether the game itself is actually installed in the third-party launcher.**
4. Uninstall just removes that record ([`games.ts:741-746`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L741-L746)). `removeInstalledGame` uses `findIndex` then `splice(index, 1)` without checking for `-1`, which would remove the last entry if the app is missing ([`thirdParty.ts:61-87`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/thirdParty.ts#L61-L87)). **[inference]**
- The download queue treats them specially (`size` = `'?? MB'`, no `info` call) ([`downloadqueue.ts:148-181`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L148-L181)).

### Launch

- `legendary launch <app> --origin` or `--ubisoft` ([`games.ts:1017-1018`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L1017-L1018)).
- On Windows this does **not** start the game directly. After login it builds a URI and calls `webbrowser.open(uri)` ([`cli.py:746-801`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L746-L801)):
  - EA: `link2ea://launchgame/<app>?AUTH_PASSWORD=<exchange code>&AUTH_TYPE=exchangecode&epicusername=…&epicuserid=…&epiclocale=…` ([`core.py:938-956`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/core.py#L938-L956))
  - Ubisoft: `uplay://launch/<GameID or app>?…same params…` ([`core.py:958-977`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/core.py#L958-L977))
  - `--json` prints `{uri}` instead; `--dry-run` logs it.
- Consequently the OS must have the `link2ea:` / `uplay:` handlers (the vendor launcher registered them). If it isn't installed, `webbrowser.open` silently does nothing.

### `legendary activate`

Not used by Heroic at all. Upstream usage ([`cli.py:2067-2200`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L2067-L2200)):
- `activate --uplay`: finds the linked Ubisoft account (opens `https://www.epicgames.com/id/link/ubisoft` if none), claims and redeems all unredeemed Uplay keys through Epic's API; `-y` skips the confirmation.
- `activate --origin`: lists EA titles and **on Windows walks the user through launching each one** (it cannot tell which are already activated); on non-Windows it only prints commands.

### How Heroic tells the user

There is **no explanatory UI** for activation or account linking. The only signals are the install flow (third-party installer runs) and the Play button. Users routinely hit "game doesn't start / asks to link account" without guidance. (EA App/Origin-linking issues are common in the tracker; I did not find a single canonical issue to cite.)

### Glider recommendation

Show an explicit "managed by EA app / Ubisoft Connect" state with a checklist (launcher installed? Epic account linked? activated?), call `activate --uplay -y` for Ubisoft, use `launch --origin/--ubisoft --json` and open the URI ourselves so we can detect a missing protocol handler, and never mark a game "installed" without checking the vendor launcher's own install.

---

## 9. EGL import / sync

Mechanics were covered in `heroic.md` §6 (opt-in toggle, no per-game sync, crash/noise reports). New material that matters for a launcher *replacing* EGL:

- **Per-game import of existing installs**: `import <app> <dir> --with-dlcs --platform Windows` ([`games.ts:809-842`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L809-L842)). Legendary verifies file hashes against the manifest and refuses an empty/wrong folder ([`cli.py:1345-1420`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L1345-L1420)). Errors are scraped from stderr with `/^.*ERROR:.*$/gm`.
- **Bulk import from EGL**: `egl-sync` import reads `C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\*.item`, skips DLC (`main_game_appname != app_name`) and unowned items, and imports anything not already installed ([`core.py:1960-1964`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/core.py#L1960-L1964), [`core.py:2088-2136`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/core.py#L2088-L2136)). `--one-shot --import-only -y` does this once without enabling automatic sync ([`cli.py:3031-3036`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L3031-L3036)).
- **Migration (destructive)**: `egl-sync --migrate` imports, then removes the games from EGL (deletes EGL's `.item` manifest and the game's `.egstore` folder) and turns sync off ([`cli.py:3037-3039`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/cli.py#L3037-L3039), [`core.py:2065-2075`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/core.py#L2065-L2075)). Game files stay.
- **Heroic's creation of the Manifests folder** on enable is a workaround for Legendary crashing when EGL never ran ([`library.ts:773-781`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L773-L781); [legendary-gl#623](https://github.com/legendary-gl/legendary/issues/623)).
- **Automatic sync side-effects**: once `egl_sync=True`, every `get_installed_list()` runs a sync, i.e. a hidden write path inside `launch`, `list-installed`, `status` ([`core.py:707-731`](https://github.com/legendary-gl/legendary/blob/42f6bdeadde3a9526dc8eb713763476999ac217b/legendary/core.py#L707-L731)).
- Windows username with spaces broke sync in older versions: [#2170](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/2170), [#1750](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/1750) (closed).

### Glider recommendation

Ship a first-run "Import from Epic Games Launcher" that runs `egl-sync --one-shot --import-only -y` (plus per-game `import` for non-default paths), keep `egl_sync` disabled, and make `--migrate` an opt-in advanced action with a plain-language warning.

---

## 10. Epic Store embedding

### How Heroic embeds the store

- It is an Electron `<webview>` pointed at `https://www.epicgames.com/store/<lang>/` ([`index.tsx:100`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/WebView/index.tsx#L100)), with `partition="persist:epic"` and `allowpopups` ([`index.tsx:692-694`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/WebView/index.tsx#L692-L694)). The webview tag is enabled in the main window ([`main_window.ts:63`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/main_window.ts#L63)).
- **Login cookie sharing**: there is none with Legendary. The **same partition** (`persist:epic`) is used for the store and for the login page, so after logging in through the webview the store is already signed in. Legendary's own auth is separate: the app only forwards the OAuth code to `legendary auth` (`heroic.md` §1). Logout clears the partition ([`user.ts:71`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/user.ts#L71)).
- **User agent**: the default Electron/Chromium UA is used for the store. The forced `Mozilla/5.0 (Windows NT 10.0; Win64; x64) EpicGamesLauncher` UA is set *only* when the start URL is the login URL ([`index.tsx:177-178`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/WebView/index.tsx#L177-L178), [`index.tsx:329-331`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/WebView/index.tsx#L329-L331)). The separate GraphQL calls to `launcher.store.epicgames.com` use a Chrome-style UA that ends in `EpicGamesLauncher` ([`games.ts:140-141`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L140-L141)).
- **Not-logged-in nag**: if the user opens the Epic store while not logged in to Heroic, a `LoginWarning` is shown ([`index.tsx:550-566`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/WebView/index.tsx#L550-L566)).
- **Remembering position**: the last URL per store is kept in `sessionStorage` (`last-url-epic`), validated by host (`epicgames.com`) and rejected for Cloudflare challenge URLs ([`index.tsx:32-48`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/WebView/index.tsx#L32-L48), [`index.tsx:430-440`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/WebView/index.tsx#L430-L440)).
- **Opening store pages from the library**: game pages link to `/store-page?store-url=<url>` which loads that URL in the same webview ([`index.tsx:391`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Game/GameSubMenu/index.tsx#L391), [`index.tsx:154-157`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/WebView/index.tsx#L154-L157)). Store URL = `https://www.epicgames.com/store/product/<clean title>` or the slug resolved through GraphQL `Catalog.catalogNs(namespace).mappings(pageType: "productHome")` ([`games.ts:116-161`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L116-L161)).
- The main window refuses external navigation and opens it in the system browser; popups from the main window are allowed only for non-app URLs ([`main.ts:262-274`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/main.ts#L262-L274)).

### Deep links (`com.epicgames.launcher://`)

**Not handled.** I grepped the whole repo: the only occurrence is the Wine registry key in `setup.ts`. Heroic has no `apps/<id>?action=launch` / `?action=install` / `store/...` handling, no interception of store "Install"/"Get" buttons, and no protocol handler for the Epic scheme on Windows. The store's "Launch/Install" buttons therefore do nothing useful in Heroic; users install from the library.

### Free games

**Not implemented.** There is no free-games feature, no `freeGamesPromotions` query and no "claim" flow. The Discounts screen only covers GOG, Humble and GMG ([`index.ts:20-21`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/discounts/index.ts#L20-L21)). Users claim Epic free games inside the store webview.

### Glider recommendation

Use WebView2 with a persistent profile shared by login and store, set the `EpicGamesLauncher` UA only for the login page, intercept `com.epicgames.launcher://` so store buttons drive Glider (URL shape such as `apps/<namespace>%3A<catalogItemId>%3A<appName>?action=launch|install` is from memory of real EGL, **[unverified]**: capture it from a real EGL install first), and build free games on Epic's public `freeGamesPromotions` store endpoint (**[unverified]**, not used anywhere in Heroic) instead of scraping the page.

---

## 11. `heroic://` protocol handler and Windows shortcuts

### Protocol

- Registration: `protocol.handle('heroic', …)` for in-app requests and `app.setAsDefaultProtocolClient('heroic')` at startup unless `isDefaultProtocolClient` already says yes ([`main.ts:421-432`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/main.ts#L421-L432)). On Windows Electron's `setAsDefaultProtocolClient` writes the per-user registry entry; Heroic has no registry code of its own for this.
- Single instance: `requestSingleInstanceLock()`; a second launch forwards `argv` through `second-instance`, which shows the window unless `gui=false`, then calls `handleProtocol` ([`main.ts:303-328`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/main.ts#L303-L328)). On a cold start the URL is read once `frontendReady` fires ([`main.ts:489-492`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/main.ts#L489-L492)).
- Parsing ([`protocol.ts:46-60`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/protocol.ts#L46-L60), [`protocol.ts:66-92`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/protocol.ts#L66-L92)):
  - old style: `heroic://launch/<app>` or `heroic://launch/<runner>/<app>`
  - new style: `heroic://launch?appName=<app>&runner=legendary&arg=<a>&arg=<b>&altExe=<path>&gui=false`
  - `ping` exists for testing.
  - "Windows automatically adds a trailing / to shortcuts", so the pathname `/` is treated as "use query params" ([`protocol.ts:72-73`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/protocol.ts#L72-L73)).
- Behaviour:
  - Installed: starts the same `launchEventCallback` as the UI; `altExe` becomes an `altExe` launch option; `ignoreGameUpdates` becomes `--skip-version-check` ([`protocol.ts:120-146`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/protocol.ts#L120-L146)).
  - Not installed: native Yes/No box, then opens the install modal in the UI ([`protocol.ts:148-178`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/protocol.ts#L148-L178)).
  - `gui=false` / `0` / `no`, or the `hideWindowOnProtocolLaunch` setting, hides the main window for this launch ([`protocol.ts:27-44`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/protocol.ts#L27-L44)).
  - Runner defaults to a search across all runners; an unknown game logs `Could not receive game data`.
- Open requests: expose settings via protocol ([#5885](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/5885)); GOG shortcuts opening the wrong folder ([#4470](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/4470)); direct shortcuts instead of protocol ([#3060](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/3060)).

### Shortcuts on Windows

[`shortcuts.ts:34-117`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/shortcuts/shortcuts/shortcuts.ts#L34-L117):
- Target is the protocol URL, not the game exe: `heroic://launch?appName=<app>&runner=<runner>` ([`shortcuts.ts:48`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/shortcuts/shortcuts/shortcuts.ts#L48)).
- Icon = `<install_path>\<executable>` index 0 when known.
- Written with Electron `shell.writeShortcutLink(path, {target, icon, iconIndex})` to:
  - Desktop: `%DESKTOP%\<sanitized title>.lnk`
  - Start menu: `%APPDATA%\Microsoft\Windows\Start Menu\Programs\<sanitized title>.lnk` ([`shortcuts.ts:149-175`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/shortcuts/shortcuts/shortcuts.ts#L149-L175)).
- Gated by `addDesktopShortcuts` / `addStartMenuShortcuts` settings, or forced by the game-menu entry. DLC never gets one. Created after install and import; removed on uninstall.
- Pitfalls: shortcuts are not refreshed on a title/exe change; no taskbar/jump-list integration; the Start-menu entry is a loose `.lnk` at the top level of Programs, so non-Heroic games can be missing ([#5865](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/5865), open); the shortcut hangs off the *launcher's* protocol, so it can't start the game if the launcher isn't registered.

### Glider recommendation

Register a `glider://` scheme plus the `com.epicgames.launcher://` handler at install time, create `.lnk` shortcuts whose target is the Glider exe with `--launch <appName>` (so they don't depend on protocol registration), and parse protocol arguments with a strict allow-list.

---

## Top 15 patterns to adopt

1. **Read Legendary's files, spawn it rarely.** Treat `metadata/*.json`, `installed.json`, `assets.json` as the database; run `list --third-party` only to refresh them ([`library.ts:100-126`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L100-L126)).
2. **Cache-first UI.** Render the persisted library at once and refresh in the background; guard against overlapping refreshes ([`GlobalState.tsx:802-806`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/state/GlobalState.tsx#L802-L806), [`GlobalState.tsx:882`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/state/GlobalState.tsx#L882)).
3. **Isolated Legendary config dir.** Always set `LEGENDARY_CONFIG_PATH` to a Glider folder ([`library.ts:694-698`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L694-L698)).
4. **A typed command builder.** One `LegendaryCommand` union → argv array, with required/forbidden flags per subcommand ([`index.ts`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/commands/index.ts), [`library.ts:823-876`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L823-L876)).
5. **Always `-y` (or closed stdin) and one explicit flag set per action:** `install --base-path --platform --skip-dlcs (--skip-sdl|--install-tag…) -y`, `update/repair -y --skip-sdl`.
6. **DLC as separate queue items** after the base game, each with `--skip-dlcs`, cancelled together ([`ipc_handler.ts:13-44`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/ipc_handler.ts#L13-L44)).
7. **Persisted serial queue** (`queue` + `finished`), deduped by app, with notifications per outcome ([`downloadqueue.ts`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts)). Add: persist the paused flag.
8. **Resume = re-run the same command**; pause = kill, because Legendary writes a resume file.
9. **Auto-pause on connectivity loss, auto-resume only if auto-paused** ([`downloadqueue.ts:29-39`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L29-L39)).
10. **Update detection by diffing `assets.json` against `installed.json`**, DLC included, then optional auto-queue honouring a per-game "ignore updates" ([`library.ts:284-405`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/library.ts#L284-L405), [`index.ts:44-62`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/index.ts#L44-L62)).
11. **Offline decision = user toggle OR no network OR Epic status `major`**, plus a "might not work offline" prompt using `CanRunOffline` ([`launcher.ts:492-496`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L492-L496), [`library.ts:236-257`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/helpers/library.ts#L236-L257)).
12. **Cloud-save sequence:** resolve path with `--accept-path`, download before launch, upload after exit, manual force-up/force-down in the UI ([`save_sync.ts:38-101`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/save_sync.ts#L38-L101), [`launcher.ts:146-162`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L146-L162)).
13. **EOS overlay as a pseudo-game with its own progress and cancel**, versions from `overlay_install.json`/`overlay_version.json`, enable state from `eos-overlay info` ([`eos_overlay.ts`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/eos_overlay/eos_overlay.ts)).
14. **Third-party titles as a first-class state:** detect via `ThirdPartyManagedApp/Provider`, download and silent-install the vendor launcher, launch via `launch --origin|--ubisoft` ([`games.ts:662-739`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/games.ts#L662-L739)).
15. **Protocol + shortcut URLs with query-param style and a `gui=false` option** for headless launches, tolerating Windows' trailing `/` ([`protocol.ts:46-179`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/protocol.ts#L46-L179)).

## Top 10 pitfalls to avoid

1. **Concurrent Legendary processes.** Heroic lets `list`, `info`, `launch`, `sync-saves` and `eos-overlay install` overlap a download with no mutex. Use one operation scheduler and lock around any command that writes Legendary state ([`launcher.ts:1724-1730`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L1724-L1730)).
2. **Killing by process name.** `Stop-Process -name legendary` kills every Legendary and cannot end the game. Use a Job Object / process-tree kill for the specific child ([`utils.ts:814-826`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/utils.ts#L814-L826)).
3. **Wrapping Legendary in `powershell Start-Process`.** It adds quoting hazards (backslash doubling, redaction logic) and makes abort hit the wrapper, not Legendary ([`launcher.ts:1683-1704`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/launcher.ts#L1683-L1704)). Use `Process` with redirected streams.
4. **Shared abort ids and a no-op `callAllAbortControllers`.** One id per operation, not per app; keep a registry that can really stop everything on quit ([`aborthandler.ts:5-33`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/utils/aborthandler/aborthandler.ts#L5-L33)).
5. **Not persisting the pause state.** A paused queue restarts after the next launch ([`downloadqueue.ts:25-27`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/downloadmanager/downloadqueue.ts#L25-L27), [`main.ts:489-501`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/main.ts#L489-L501)).
6. **Trusting stale `info` and queue snapshots.** The `info` cache lives 6 h and ignores updates; queue elements carry a full `GameInfo` copy and a pre-formatted size string ([`electronStores.ts:5-7`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/electronStores.ts#L5-L7), [`types.ts:406-411`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/common/types.ts#L406-L411)). Re-read before acting.
7. **Cloud-save downloads without a backup** (`clean_dir=True`) and `sync-saves` calls with no resolved path ([#4074](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/4074), [#5566](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/5566)).
8. **Treating "installed in a third-party launcher" as known.** Heroic writes `third-party-installed.json` without checking the vendor launcher; its removal also has an unchecked `splice(-1)` ([`thirdParty.ts:61-87`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/storeManagers/legendary/thirdParty.ts#L61-L87)).
9. **Counting only base-game bytes.** DLC sizes are not in the Epic free-space check ([`index.tsx:483-559`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/frontend/screens/Library/components/InstallModal/DownloadDialog/index.tsx#L483-L559)), and Legendary's disk-space failure is not matched ([#3962](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/issues/3962)).
10. **Letting the shortcut/deep-link layer be the launch dependency.** Heroic shortcuts only work if the protocol is registered; it has no Epic-scheme handling at all. Register both schemes and make `.lnk` targets the executable ([`shortcuts.ts:48`](https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher/blob/89e06ad7b89cb7514ea3ce089ff1e2ca5e49dafa/src/backend/shortcuts/shortcuts/shortcuts.ts#L48)).

## Open questions to answer with a machine that has the stack

- **[unverified]** The exact process tree `Start-Process -Wait` waits on for each Epic game (launcher stub vs shipping exe). Needed before replicating "running" detection without PowerShell.
- **[unverified]** Whether the `link2ea://` / `uplay://` URIs launch correctly when the vendor client is already running and when it is not installed.
- **[unverified]** Which Epic games ship non-null `manifest.prerequisites` in practice, to size the prerequisite-install step.
