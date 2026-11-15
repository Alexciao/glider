# Glider

A fast, quiet Windows launcher for your whole Epic Games library. Sign in, bring in the games you already have (nothing
re-downloads), then install, update, repair and play without opening the Epic Games Launcher.

<!-- TODO(screenshots): shoot these from the real app (1280x800, dark theme) and save them in docs/screenshots/readme/:
       home.png, library.png, game.png, downloads.png, settings.png, store.png
     then replace the placeholder block below with the <p><img ...></p> grid. Until then the images are broken links. -->

## Screenshots

<p>
  <img src="docs/screenshots/readme/home.png" width="49%" alt="Home: continue playing, updates, free this week">
  <img src="docs/screenshots/readme/library.png" width="49%" alt="Library, grid view">
  <img src="docs/screenshots/readme/game.png" width="49%" alt="A game's page, ready to play">
  <img src="docs/screenshots/readme/downloads.png" width="49%" alt="Downloads: an install with live speed and chart">
  <img src="docs/screenshots/readme/settings.png" width="49%" alt="Settings">
  <img src="docs/screenshots/readme/store.png" width="49%" alt="The Epic Games Store tab">
</p>

> Glider is an unofficial, independent project. It is not affiliated with or endorsed by Epic Games.
> Fortnite and Epic Games are trademarks of Epic Games, Inc.

## Features

**Library**
- Every game your account owns, installed or not: grid or list, search, filters, favorites, hidden games, collections.
  It opens from a cache and refreshes in the background.
- **Home** (continue playing, updates, free this week) and a **game page** with one main button, DLC, components and
  per-game settings (launch arguments, executable, working directory, update policy).
- **Command palette** (`Ctrl+K`); `Ctrl+1` to `Ctrl+5` switch pages.

**Install, update, play**
- Install with a folder picker, a real free-space check and drive hints; update, verify, repair, move, uninstall (keep or
  delete the files).
- **Downloads**: live speed and disk-write readouts, chart, time left, honest phases, a queue you can reorder; pause,
  resume and cancel; resumes after a restart or crash.
- **Download modes** (Full, Balanced, Background) that change CPU, disk and memory priority live, a speed limit,
  *Full speed when I'm away*, and a choice of what happens while a game runs. A **CDN probe** with failover avoids slow
  download servers, and a **disk-bound** indicator tells you when your drive, not your connection, is the limit.
- **Update policies** (Always, Ask, Never) globally and per game.
- Play starts the game directly: Easy Anti-Cheat setup, prerequisites (once), Ubisoft Connect and EA app hand-off,
  playtime, minimize to tray while playing. Launch never waits behind a download.

**Cloud saves, overlay**
- Cloud saves sync before and after play with a conflict check that doesn't trust file times; every overwrite is backed
  up first, and nothing blocks play offline.
- Epic overlay (friends, achievements): install, update, switch on or off, remove.

**Epic Games Launcher**
- **Import your whole library** from the Epic Games Launcher with no re-download, from onboarding or Settings.
- Both launchers keep working on the same folders; a readiness check says when the Epic Games Launcher is safe to
  uninstall.

**Store and Windows**
- **Store tab** (the Epic Games Store inside Glider) and a **Free this week** strip.
- Desktop and Start menu shortcuts per game, `glider://` links, optional `com.epicgames.launcher://` handling, tray with
  per-game actions, single instance, start with Windows, Windows 11 Mica, light and dark themes.

## Non-goals

- **Buying inside Glider.** The Store tab is Epic's own web store; payment happens there, not in a native Glider flow.
- **Friends and chat** outside the Epic overlay.
- **Unreal Engine, Fab and Marketplace** content.
- **Mod stores.**
- **Replacing other launchers.** Ubisoft Connect and EA app games that Epic lists are a hand-off only: they start
  through their own launcher.

## Install and upgrade

1. Download `Glider-win-Setup.exe` from the [latest release](../../releases/latest) and run it.
2. **SmartScreen**: the build isn't code-signed, so Windows may say "Windows protected your PC". Click **More info →
   Run anyway**. Glider installs per-user (no admin) into `%LocalAppData%\Glider` and adds a Start menu shortcut. If the
   WebView2 runtime is missing, the installer fetches it.
3. **Updates are automatic**: Glider checks this repository's releases about a minute after it starts and every 12
   hours, downloads quietly and installs when you close it. To upgrade right away, run the new `Setup.exe` and choose
   **Update**. From v0.1, your sign-in, Fortnite install, settings and playtime carry
   over, and your v0.1 files are copied to `%LocalAppData%\GliderData\backups\migration\` first. See the
   [v0.2.0 release notes](docs/release-notes-v0.2.0.md).

Your data lives in `%LocalAppData%\GliderData`, separate from the install folder, so uninstalling Glider keeps it
(see Troubleshooting).

## First run

1. **Sign in with Epic.** A login window opens in the Epic Games Launcher's own mode and closes by itself when you're in.
2. **Scan.** Glider lists the games the Epic Games Launcher installed on any drive (all selected). *Import* brings them
   in without downloading; *Locate another folder…* adds one the scan missed.
3. Pick your **library folders** (where new games install) and a **download mode** (Balanced is the default).
4. Press **Play** on any installed game. The first launch of a game with Easy Anti-Cheat may show a Windows (UAC) prompt;
   accept it.

You can re-run this from Settings → Advanced, and import more later from Settings → Epic Games Launcher → **Import…**.

## Architecture

A thin WPF host renders a React UI in WebView2 from local files and talks to `Glider.Core` over a typed JSON-RPC bridge.
Core drives a bundled, hash-pinned Legendary as the Epic protocol engine.

```
 Glider.App (WPF, WebView2)  ── window, tray, login, Store view, links, single instance
        │  https://app.glider/  → frontend (React 19, TypeScript, Vite, Tailwind)
        │  IPC contract v2: typed RPC, a small `state` snapshot, `library` deltas, 10 Hz `progress`
 Glider.Core (no UI)  ── library, jobs, game, EGL, saves, overlay, store, infrastructure
        │  job lanes:  writer (installs…) · metadata · saves · launch (never queued) · read
        │  child processes, LEGENDARY_CONFIG_PATH = %LocalAppData%\GliderData\legendary
 legendary.exe  ── Glider's patched Legendary 0.21.1 (0.21.1-glider.1), SHA-256 pinned
```

- **Host** (`src/Glider.App`): Windows glue only.
- **Core** (`src/Glider.Core`): all logic, cross-platform and unit-tested.
- **Frontend** (`frontend`): UI against a mock backend that implements the same contract.
- **Contract**: C# records in `src/Glider.Core/Contracts` are the source; TypeScript types are generated
  (`tools/Glider.ContractGen`).
- **Legendary fork**: a small patch (bounded chunk retries, CDN failover, a control file for live changes, clearer progress); see
  `tools/legendary-fork/`.
- **Data root**: `%LocalAppData%\GliderData`, with `legendary\`, `state\`, `cache\`, `backups\`, `logs\`, `webview2\`.

More: [ARCHITECTURE.md](ARCHITECTURE.md), [docs/v0.2-design.md](docs/v0.2-design.md), [DECISIONS.md](DECISIONS.md),
[docs/egl.md](docs/egl.md), [docs/cloud-saves.md](docs/cloud-saves.md),
[docs/shell-integration.md](docs/shell-integration.md), [docs/glossary.md](docs/glossary.md).

## Known limitations

- The **speed limit is approximate below about 5–10 MB/s**; the card shows the measured rate.
- Several things need a real Epic account to confirm (sign-in, real installs, cloud saves, Store checkout, the
  Epic Games Launcher write-back); the checklist is in [docs/testing.md](docs/testing.md).
- **Windows 10** is untested. The installer is not code-signed.

## Troubleshooting

| Problem | What to do |
|---|---|
| Where are the logs? | `%LocalAppData%\GliderData\logs\`: `glider-YYYYMMDD.log` (Core) and `host-YYYYMMDD.log` (window). Settings → Advanced has a log viewer and **Copy logs** for bug reports; tokens are not included. |
| Sign-in fails or the window closes | Try again; Glider signs in the way the Epic Games Launcher does (launcher mode), which is the mode Epic's captcha accepted in testing. If it keeps failing, copy the diagnostic logs from the "Sign-in was canceled" notice. |
| "Your session expired" | Click **Sign in** again. |
| A download is slow | Look at the Downloads card. **Slow download** explains why: if it names your drive, the drive's write speed is the limit, not your connection (free space, or install on a faster drive); if it names a download server, Glider switches servers on its own. Check Settings → Downloads for a speed limit or Background mode. Details: [docs/download-perf.md](docs/download-perf.md). |
| "Game files need repair" | Game page → **Repair** (downloads only what's broken). |
| A download stalls | Progress is kept; press **Resume**. |
| Epic Games Launcher and Glider together | Glider waits while the Epic Games Launcher is updating or verifying a game, and continues by itself. With *Keep in sync* on (Settings → Epic Games Launcher) Glider updates the launcher's record after each update or move, so it doesn't re-download. See [docs/egl.md](docs/egl.md). |
| Anti-cheat error on launch | Retry **Play** and accept the Windows prompt. |
| Where does my data live? | `%LocalAppData%\GliderData`. **Uninstalling Glider keeps it** (sign-in, library cache, settings, save and Epic Games Launcher backups). Delete that folder to remove everything. Data from v0.1 is moved there at the first start, and a `data-moved-to.txt` is left in the old folder. |
| Something else | Settings → Advanced → **Copy logs** and open an issue. |

## Build from source

Requirements: Windows 10/11, [.NET SDK 10](https://dotnet.microsoft.com/download) (see `global.json`), Node 24+ and
[pnpm](https://pnpm.io) 10 (Node 20 breaks vitest).

```powershell
# Everything: contract check, frontend build, Legendary fetch (SHA-256 verified), self-contained publish
pwsh build/build.ps1 -Version 0.2.0          # add -Pack to produce the Velopack installer in artifacts/releases

# The checks
dotnet build Glider.slnx -c Release -warnaserror
dotnet test --project tests/Glider.Core.Tests -c Release
dotnet run --project tools/Glider.ContractGen -c Release -- --check
cd frontend; pnpm install; pnpm lint; pnpm typecheck; pnpm test

# Inner loop: the UI in a browser against the mock backend (?scenario=eglDetected, downloading, …)
cd frontend; pnpm dev
```

After changing the contract in `src/Glider.Core/Contracts`, regenerate the TypeScript types with
`dotnet run --project tools/Glider.ContractGen`. Release steps are in [build/README.md](build/README.md).

**Tests.** The Core suite (xUnit v3 on Microsoft.Testing.Platform) runs against `tests/FakeLegendary`, a scriptable
stand-in for `legendary.exe`, with no network, Epic account or GUI. Timing logic runs on a manual clock so the suite passes
on a busy machine. A separate suite runs Core against the real pinned `legendary.exe` (opt-in locally with
`GLIDER_REAL_LEGENDARY=1`, in CI always, hermetic: no calls to Epic). What is and isn't covered, and the manual checklist
for a real account, are in [docs/testing.md](docs/testing.md).

## License and credits

Glider's code is released under the [MIT license](LICENSE).

- **[Legendary](https://github.com/legendary-gl/legendary)** (GPLv3) does the Epic protocol work. Glider bundles it as a
  separate executable, patched by Glider (`0.21.1-glider.1`). The patch, license and source offer ship with the app and
  are in `tools/legendary-fork/` (Settings → About has the details).
- **Heroic Games Launcher** was a research reference for how Epic's clients behave. Notes in
  [docs/research/heroic-v0.2.md](docs/research/heroic-v0.2.md).
- Inter (SIL Open Font License) is bundled as the UI font.
