# Glider v0.2.3

Update now: a running Glider picks this up by itself and installs it when you close it, or run `Glider-win-Setup.exe` and choose **Update**.

## Fixed

- **Windows Security flagged Glider's download tool as a trojan and stopped downloads.** Defender's machine-learning check (`Trojan:Win32/Bearfoos.A!ml`) misjudged the one-file build of Legendary that Glider bundles, then blocked it and killed a running repair. Glider now bundles Legendary `0.21.1-glider.3` as a regular folder instead of a self-extracting file. Defender scans it clean, a real multi-process download ran without a detection, and Legendary starts about 4× faster. Nothing to install: everything Legendary needs ships inside Glider, and a Python you may have installed is never used.
- **If Windows Security blocks it anyway,** Glider now says so ("Windows Security blocked Legendary") with an **Open Windows Security** button, instead of "stopped unexpectedly" or "Glider is damaged".
- **Store tab:** no longer stays blank or spins after Epic's sign-in redirects. Every failure ends in a message with Try again and Open in browser, and the Store loads in the background after sign-in, so opening it is quick.
- **Game art** appears much faster: one download per cover instead of two, more downloads at once, and the rest of the library loads in the background.
- **Wide windows:** pages sit in one centred frame instead of hugging the left edge, and grids gain columns on big screens.
- **Smaller fixes:** the grid/list toggle icons are centred, History rows don't leave an empty gap for their × button, the download details panel no longer has a wide gap between labels and values, and buttons that open menus squeeze the same way on every click.

## Changed

- **Downloads moved to the bottom of the left rail, above Settings**, where Epic Games Launcher users look for it. Shortcuts follow the new order: `Ctrl+3` Store, `Ctrl+4` Downloads.
