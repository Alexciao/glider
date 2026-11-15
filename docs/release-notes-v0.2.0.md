# Glider v0.2.0

Glider now replaces the Epic Games Launcher for your whole library, not just Fortnite. Bring in the games you already
have (nothing re-downloads), then install, update, repair, launch and keep saves in sync from one quiet app.

## Upgrade from v0.1

The repository is private, so v0.1's built-in updater most likely can't see this release. Update by hand:

1. Download `Glider-win-Setup.exe` from the v0.2.0 release and run it. It installs **in place** over v0.1 (per-user, no
   admin). Windows SmartScreen may warn because the build isn't code-signed: **More info → Run anyway**.
2. Start Glider. On the first start it moves its data from `%LocalAppData%\Glider` to `%LocalAppData%\GliderData`, then
   converts your v0.1 files. You stay signed in, Fortnite stays installed, and your settings, playtime and launch
   arguments carry over.
3. Backups are kept: the v0.1 files are copied to `%LocalAppData%\GliderData\backups\migration\v0.1-<timestamp>\` before
   anything is converted, and the v0.1 files themselves are never deleted.

If the data folder can't be moved at the first start (a leftover process still holds a file), Glider runs on the old
folder and tries again at the next start. Nothing is half moved.

## What's new

### Your whole library
- **Library** with every game your account owns, installed or not: grid or list, search, filters, favorites, hidden games,
  collections. It opens from a cache, so a 500-game library is interactive immediately and refreshes in the background.
- **Home**: continue playing, updates, and what's free this week.
- **Game page**: one status line, one main button (Install, Update, Repair, Play), DLC and components you can pick, facts
  and per-game settings (launch arguments, executable, working directory, update policy, cloud saves).
- **Command palette** (`Ctrl+K`): jump to any game or page, run actions. `Ctrl+1` to `Ctrl+5` switch pages.
- **Onboarding** for a first run: sign in, scan this PC for installed games, choose library folders and a download mode.
  You can run it again from Settings → Advanced.

### Downloads
- **A faster, calmer engine.** Downloads keep full network speed while the writer process runs at low disk and memory
  priority, so the PC stays smooth. Three **download modes**, switchable while a download runs (no restart): **Full**,
  **Balanced** (default) and **Background**. Settings → Downloads also has *Full speed when I'm away*, a speed limit and
  *While a game is running* (pause, throttle or continue).
- **Download server probe and failover.** When a manifest is cached (updates, repairs, re-installs), Glider tests Epic's
  download servers before starting and avoids the ones that are slow. If a server degrades mid-download, the job moves
  to the next one and resumes, at most once per job.
- **It tells you what's actually slow.** When the install drive is the limit, the card says so, names the drive and what
  your connection could do. The install picker remembers each drive's measured write speed and warns before the next
  install on a slow or nearly full drive.
- **Downloads page**: live Download and Disk write speed readouts with a chart, the percentage with time left beside it,
  honest phases (Preparing, Verifying, Repairing, Downloading, Installing, Finalizing), the queue with reorder and
  move-to-top, and History. A **Slow download** indicator explains why; a limit you chose (speed limit, Background mode)
  shows as a quiet label instead. A **Couldn't finish** section lists failed installs, updates, repairs and moves; for a
  lack of space it shows how much is needed and free, offers *Free up space*, and turns *Try again* into the main button
  as soon as enough space is free.
- Launch never waits behind a download. A paused job blocks only its own game.

### Cloud saves
- Saves sync before you play and after the game closes. Instead of trusting file times, Glider compares each side with
  what it saw at the last sync, so a newer save is never overwritten by a stale one.
- If both sides changed, the game page shows **Saves differ** and nothing starts until you choose with **Resolve…**.
- Every overwrite is preceded by a verified local backup (`%LocalAppData%\GliderData\backups\saves`), uploads are checked
  after the fact, and a failed download is restored from the backup. Offline, an unreachable Epic or a slow sync never
  blocks play: the game starts with the save on this PC and the sync is replayed later.

### Games and launchers
- **Epic overlay** (friends and achievements): install, update, turn on or off, remove in Settings. An overlay already
  installed by the Epic Games Launcher can be reused without a download.
- **Prerequisites** (redistributables) run once, at the first launch, and are remembered across updates. "Play anyway"
  stops Glider asking.
- **Ubisoft Connect and EA app games**: a game from an Epic partner launcher starts through that launcher. Ubisoft games
  need a one-time **Link account**.
- **Easy Anti-Cheat** setup for any game that ships it, not just Fortnite.

### Bringing your Epic Games Launcher library in
- **Import everything**, not only Fortnite: Glider reads every game the Epic Games Launcher has installed, on any drive,
  and imports the ones you select. One import job per game, each with its own result (Imported, Needs repair, Failed).
- **The 35 GB repair is fixed.** In v0.1, importing an existing Fortnite install was followed by a 35.5 GB repair. Legendary
  doesn't record which optional packs an imported install has, so the check counted every pack the Epic Games Launcher
  never installed as "missing", and the repair then deleted files outside the packs it selected. v0.2 carries the
  launcher's install tags over, verifies only when something is actually in doubt, and an install that is merely behind the
  latest version is updated from the launcher's own manifest instead of re-checked. A folder the launcher left
  mid-update still needs the files it hadn't applied. In that case Glider says so ("Epic Games Launcher hadn't finished
  updating it") and shows *Needs repair* with a queued verify, instead of calling it corrupted. Details:
  [docs/egl.md](egl.md), "The 35 GB repair after adopting Fortnite".
- **Both launchers keep working** on the same folder. With *Keep in sync* on, Glider writes the new version back into the
  launcher's record after an update or move (backed up first, atomic), so the Epic Games Launcher doesn't ask to
  download again.
- **A readiness check** tells you when the Epic Games Launcher is safe to uninstall (below).
- **Where**: onboarding scans every drive on the first run (all found games selected; *Locate another folder…* adds one
  it missed). Later: Settings → Epic Games Launcher → **Import…**, or the command palette. Glider never reuses the
  launcher's sign-in (that would sign the launcher out); you sign in to Glider on its own.

### Store, shortcuts and the rest of Windows
- **Store tab**: the Epic Games Store inside Glider, signed in with your account. *Install* and *Play* buttons on the
  store hand over to Glider. Buying or claiming a game adds it to your library without a manual refresh. It's the web
  store: Glider doesn't handle payment itself. The **Free this week** strip on Home reads Epic's public promotions and
  works from its cache offline.
- **Shortcuts**: a Desktop or Start menu shortcut per game that launches through Glider.
- **Links**: `glider://launch/<game>`, `glider://game/<game>`, `glider://downloads`, `glider://library` and
  `glider://store`. Optionally Glider can also open `com.epicgames.launcher://` links (Settings → Links); the previous
  handler is saved first and restored when you turn it off.
- **Tray**: close the game, play the last game, and see download progress in the tooltip. Close to tray keeps updates
  running; the Store is suspended while the window is in the tray.
- **Update policies**: Always, Ask or Never, globally and per game, with *Only when I'm not playing*. Ask raises one
  notice per version, not one per launch.
- **Data moved to `%LocalAppData%\GliderData`**, outside the folder that uninstall deletes. Uninstalling Glider no longer
  takes your sign-in, save ledger or backups with it.

## Performance

Measured on one development PC (not a benchmark suite), mostly under heavy background load. Method and caveats are in
[docs/perf.md](perf.md) and [docs/download-perf.md](download-perf.md).

| | v0.1.5 | v0.2.0 |
|---|---|---|
| Library first load, 500 real-shaped games | 2.8–3.7 s | 2–6 ms (from cache) |
| Library search, one keystroke (600 games) | 17.3 ms | 11.6 ms |
| Cold start to interactive | | 1.67 s best case, under heavy load |
| Quit to process exit | ≥ 3 s | 0.18–0.71 s |
| Idle memory: Glider itself | | ~3 MB private working set |
| Idle memory: with WebView2 | | ~95 MB private working set |
| Download start-up under full CPU load (benchmark harness) | 13.5 s to first progress | 4–5 s |

- **Network**: on a fast disk, Full reached about 91% of the measured connection ceiling on average and Balanced about
  102% of Full (ceiling ≈ 24–42 MB/s, varying with the time of day).
- **A slow disk, not the connection, was the real bottleneck on the test PC**: a SATA SSD that sustains about 6.5–8 MB/s
  of writes. v0.2 detects and shows that case; it can't make the drive faster.
- The cold-start target (1.5 s) is **not confirmed**: 1.67 s was the best of several runs on a busy machine.

## When it's safe to uninstall the Epic Games Launcher

Settings → Epic Games Launcher → **Ready to uninstall Epic Games Launcher?** lists, per game, what still depends on it.
A game installed by the launcher but not imported yet, or one the launcher is still working on, **blocks** it ("Nothing
depends on it. You can uninstall it." appears only when no blocker is left). Missing game folders and Ubisoft or EA
games (which keep using their own launcher) are shown as warnings. Imported games keep their files when the launcher is
uninstalled. In Glider's own Uninstall dialog, *Keep the files* leaves the files and the launcher untouched; *Delete the
files* on a game the launcher also knows makes the launcher lose it too. Until you've played a few games from Glider,
keep the Epic Games Launcher installed: some things haven't been verified against a real Epic account yet (below).

## Known limitations

- **Speed limit is approximate below about 5–10 MB/s.** Glider limits speed by throttling the download workers, and one
  connection alone moves more than that, so a very low cap on a fast link or a busy PC isn't held tightly. The card's
  "Speed limit" label shows the measured rate. Fixing it needs a second throttling method; planned for v0.3.
- **Not verified against a real Epic account** (the automated suites never sign in to Epic): sign-in, library sync, real
  install, update and repair, online and offline launch with Easy Anti-Cheat, cloud saves against Epic, overlay install,
  Ubisoft and EA linking, write-back against a real Epic Games Launcher, Store sign-in, checkout and deep links, the
  behaviour of the tray, shortcuts and protocol handlers on real Windows, the data move on a real PC, a real
  install → update → uninstall cycle of the installer. The checklist is in [docs/testing.md](testing.md); please report
  what you find.
- **Windows 10 is untested.** It should use the solid backdrop; Windows 11 is what's been looked at.
- **Cold start** is measured only under load (see above).
- **The Install dialog** gets its plan (sizes, DLC, components) from Epic when it opens, so it can take a moment.
  Planning from cached data is deferred to v0.3.
- The installer is **not code-signed**.
- Unreal Engine, Fab and Marketplace content, mod stores, and chat or friends outside Epic's overlay are out of scope
  (see the README).

## For developers

- **IPC contract v2** (`src/Glider.Core/Contracts`): C# records are the source of truth and the TypeScript types are
  generated. A small `state` snapshot plus `library` deltas and 10 Hz `progress` events replace v0.1's full snapshot.
  Check it with `dotnet run --project tools/Glider.ContractGen -c Release -- --check`.
- **Job lanes**: writer, metadata, saves, launch and read, so launch never queues behind a download
  ([docs/v0.2-design.md](v0.2-design.md) §3).
- **Real-Legendary suite**: `tests/Glider.Core.Tests/RealLegendary` runs Core against the pinned `legendary.exe` with no
  account and no network by default; it runs in CI and is opt-in locally (`GLIDER_REAL_LEGENDARY=1`,
  [docs/testing.md](testing.md) §2).
- **Legendary fork**: Glider bundles `0.21.1-glider.1`, a small patch on Legendary 0.21.1 (failover, a control file for
  live speed changes, clearer progress). Source offer: `tools/legendary-fork/SOURCE-OFFER.txt`.
- At the v0.2 merge the checks were 1,610 Core tests, 552 frontend tests, a `-warnaserror` build and a clean contract
  check.
- Decisions with their reasoning: [DECISIONS.md](../DECISIONS.md). Review of the whole codebase:
  [docs/review-v0.2-architecture.md](review-v0.2-architecture.md).
