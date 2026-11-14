# Shell integration: links, shortcuts, protocol handlers, tray

Code: `src/Glider.Core/Links`, `Shortcuts`, `Tray`; host adapters `src/Glider.App/Windows{Registry,ShellLinks,ShellIntegration}.cs`, `TrayHost.cs`, `TrayIcon.cs`.

## Links

| Link | Effect |
|---|---|
| `glider://launch/<app>` | Starts the game; no window. Not installed → install dialog; broken / save conflict / failure → game page + toast. |
| `glider://install/<app>` | Game page with its **install dialog open**. Never starts a download. |
| `glider://game/<app>` · `downloads` · `library` · `store[/p/<slug>]` | Shows the window and sends the UI a `navigate` route. |

`GliderLinkParser` allow-list: scheme `glider`, route word, one app segment `[A-Za-z0-9][A-Za-z0-9._-]{0,127}` (no `..`), slug `[A-Za-z0-9][A-Za-z0-9_-]{0,127}`.
Total length ≤ 2048; no control characters or whitespace, userinfo, port, fragment, backslash or embedded `://`; percent escapes are decoded once and
validated afterwards (a decoded `%`, bad UTF-8, `%00`… are refused); the query is ignored. One trailing slash is tolerated (Windows adds it).

`com.epicgames.launcher://` (only while `Settings.HandleEpicLinks` is on) goes through `EpicLauncherUri` and `EpicLinkMapper`:
`apps/<ns>%3A<cat>%3A<app>?action=launch` → launch, `install|update` → install dialog, `verify|updatecheck` → game page, `uninstall` → ignored,
`store/p/<slug>` → store page, `library` → library. `arg`, `env.*`, install paths and other query parameters are dropped. The game is resolved by app name
(case-insensitive), then by (namespace, catalog item id).

A refused or ignored link shows the main window (a cold start must not leave an invisible process). The same link within 1.5 s is handled once.

## UI routes (`navigate` event)

`game/<app>`, `game/<app>/install` (bridge opens the install dialog), `downloads`, `library`, `store`, `store/p/<slug>` (`Route.slug`; the Store view should navigate there).
A route sent before the UI called `app.ready` is held (latest wins) and delivered on `app.ready`.

## Single instance

`Glider.exe <link>` / `Glider.exe --launch <app>` as a second process sends `0x02 <u16 length> <utf-8 link>` over the per-user pipe (`0x01` = plain "come to the front", unchanged).
The first instance re-validates with the strict parsers. A first instance started with a launch link starts hidden (`HostOptions.StartsQuietly`).

## Shortcuts

`game.setShortcut` writes `<Desktop>\<Title>.lnk` / `<Programs>\<Title>.lnk` with target = Glider's stable exe (Velopack root stub), arguments `--launch <app>`, icon = the game's exe (fallback Glider's).
A file by that name that isn't ours (same arguments) is never touched: Glider uses `<Title> (Glider).lnk`. State lives in `games.json`.
Repair (debounced on install-record changes and once after start-up): moved game → new icon; uninstalled → shortcuts removed; file deleted by the user → flag cleared. Nothing is removed while the library state is unknown.
Uninstalling Glider removes shortcuts found by arguments + exe name.

## Protocol registration (HKCU\Software\Classes, no elevation)

* `glider`: written by Velopack first-run / after-install / after-update hooks and at every start of an installed build; removed on uninstall.
* `com.epicgames.launcher`: with the setting on, the whole previous per-user key is saved to `state/protocol-backup.json` (typed values and subkeys), then replaced by a `GliderManaged` key.
  Off (or uninstall) restores the saved key exactly, or deletes ours if there was none (EGL's machine-wide registration shows through). A key somebody else wrote afterwards is never touched.
  Windows' "default app" choice (`UserChoice`) can override it; Core logs that, it cannot change it.
