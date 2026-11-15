# Glider v0.2.2

Fixes finding and importing games the Epic Games Launcher installed. Update now: a running Glider picks this up by itself and installs it when you close it, or run `Glider-win-Setup.exe` and choose **Update**.

## Fixed

- **Games installed by a current Epic Games Launcher weren't found.** Since version 20, the launcher keeps its install list in its new install service (EOSH, `C:\ProgramData\Epic\EpicOnlineServicesShared\InstallHelper\InstalledItems`), and its old `.item` files are only a copy, which on some PCs it can't even write. Glider (like Legendary and Heroic) read only the copy, so it saw nothing. Glider now reads the launcher's install service, so onboarding, Settings → Epic Games Launcher → Import and the readiness check list these games again.
- **Importing an Epic Games Launcher game could start a large "repair" of a game that was fine.** Glider now gives Legendary the list of optional packs the launcher installed. Before, a file check counted every pack the launcher had skipped as missing and downloaded it: 11 GB on one Fortnite install. Games imported with an earlier version get their pack list filled in automatically the next time Glider looks at the launcher (at start-up).
- **"Scan all drives" now also looks in `EpicLibrary`-style library folders**, e.g. `D:\EpicLibrary\Fortnite`.

## If a repair is running on an imported game

It's downloading optional content the launcher never installed; your game isn't damaged. Cancel it in Downloads before updating, or let it finish (it only adds files).

## Known limitation

For games the launcher keeps in its new install service, "Keep Epic Games Launcher in sync" can't update the launcher's record yet. After Glider updates or moves such a game, the launcher may check the game's files the next time you open it there.
