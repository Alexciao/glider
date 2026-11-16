# Glider v0.2.5

Update now: run `Glider-win-Setup.exe` and choose **Update**, or let Glider 0.2.4 update itself (it closes about a minute after the download and installs; start it again afterwards. From this version on, updates wait for you instead).

## Fixed

- **The Store tab no longer stays blank.** The Store loaded fine but sat behind Glider's own page. It now always draws on top.
- **Self-updates no longer close Glider out of the blue.** A downloaded update used to shut Glider down a minute later and install without reopening it. Now the "Glider is ready" notice has a **Restart** button, and otherwise the update installs when you quit Glider.
- **Verifying an imported game now clears its "verify" prompt.** A game you imported from a folder kept asking to be verified even after a clean verify, and every update of it would have been a slow full repair.
- **Updates and repairs never delete game content you have on disk.** For games brought over from the Epic Games Launcher, Legendary could remove optional content outside the packs Glider took over from the Launcher on the next update or repair (for Fortnite, content the game had downloaded on demand). If a mode asks to download content again, that's why.

## Changed

- **The Store looks like part of Glider:** Epic's top bar (with its "Download the launcher" button, account menu and links that duplicate Glider) is hidden, the toolbar gains a **Store home** button, the Store tab fades in instead of sliding, and scrollbars everywhere are Windows 11's thin overlay ones.
