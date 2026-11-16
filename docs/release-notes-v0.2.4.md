# Glider v0.2.4

Update now: a running Glider picks this up by itself and installs it when you close it, or run `Glider-win-Setup.exe` and choose **Update**.

## Fixed

- **Mica is back after Glider returns from the tray.** Hiding the window to the tray (or launching a game, which hides it) and showing it again left the window a flat grey instead of the Mica backdrop. The page's background in WebView2 came back opaque; Glider now shows the window first and the page a moment later, so the backdrop stays see-through.
- **The Store no longer turns grey after it loads.** When Glider got focus back (switching apps, or the Store page itself taking focus), the Store button's tooltip popped up on its own, and any tooltip made Glider swap the live Store for a dimmed snapshot. Tooltips now never cover the Store, and they open on keyboard focus only, so they no longer appear by themselves after you switch back to Glider.
- **Menus no longer wobble when they open.** Sort, Collections, the Install/Play menu and every Select used to slide 1–2 px sideways and back as they opened, because they followed their button's press squeeze. Buttons that open a menu now stay still and stay highlighted while their menu is open.
- **A game's tile stays put while its menu is open.** Opening a tile's menu (right-click or "…") used to drop the tile back down and hide the "…" button the menu came from. List rows behaved the same way.
- **Smaller motion and alignment fixes:** Settings, Downloads and the Store play their entrance once, on content that's there (Settings animated twice, the others animated an empty box on first visit); every onboarding step rises the same distance; a closing dialog's dimmed backdrop fades out together with the dialog; the game page's "Create shortcut" and "Add to collection" menus line up with the library's; menus that open over the title bar take clicks instead of dragging the window.
