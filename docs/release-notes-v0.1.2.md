# Glider v0.1.2

## Fixed
- Settings: "Auto (1024 MB)" no longer wraps; the page no longer jumps in width.
- Onboarding: error messages scroll into view instead of hiding behind the action bar.
- Toasts appear top-right, below the title bar, so they no longer cover the Library facts row.
- Light theme: cards read as raised, not darker than the page.
- Mica window stays correct when inactive, with transparency off, on Battery Saver, and across theme, DPI and maximize changes.
- Epic login window shows a themed loading state while Epic's page loads.
- Updates are recognised after a game is updated outside Glider; verify and repair now repair bad files.
- Game close button closes the game gracefully; the log flushes promptly while the app is open.
- Existing installs are found on any drive; quick scan of fixed drives is in the command palette.

## Improved
- Library shows Last played and Playtime (tracked by Glider; time played outside Glider is not counted).
- Installed size and download size are shown; updates show only the update download.

## Known issues
- Hero text can fail contrast checks in some error states and at 960x600.
- Installer is about 91 MB (target 60 MB); needs a framework-dependent build, a product decision.
- Not yet tested with a real Epic account, on Windows 10, or with a real self-update release.
- Uninstalling deletes Legendary data (shared Velopack root); needs a data migration.
- Code signing is missing, so SmartScreen still warns.

Update: run the new Setup.exe over your existing install; your data is kept.
