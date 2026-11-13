# Design critique, v0.1.2 pass

Shot with the existing tooling (`pnpm screenshots`, 1280x800, dark and light, solid and Mica-simulated) plus the new
`frontend/e2e/surfaces.spec.ts`, which emulates the host surfaces by setting what the host sets: `data-backdrop=mica`
(Win11 active), `data-backdrop=mica` + `data-surface=solid` (inactive / transparency off / Battery Saver) and
`data-backdrop=solid` (Win10). Saved under `docs/screenshots/surface-*` (solid and Win10, Library + Settings).

## Checked
- Onboarding, Library (not installed, installing, paused, installed, update available, playing, errors), Settings (all six
  pages), dialogs, toasts, in dark and light.
- Settings width: content column measured on every page (nav 200, column 960 inside 1040, scrollbar gutter stable): identical.
- Play and the three-dot button: same 48 px height, 2 px join, same radius language, for Play, Update, Resume and Playing.
- Playtime reads "42 h 17 min"; "Installed size" and "Download size" labels carry tooltips; Download size only shows
  when it differs (update available / not installed).
- Progress bar: sheen while downloading, calm amber static bar when paused.
- Solid surfaces: with `data-surface=solid` and on Win10 the page paints the opaque tint; no white or gray flash and no
  translucent patches in dark or light. Toasts stack with a gap and do not overlap.
- Spacing: facts row, hero, toast stack and dialogs sit on the 4 px grid.

## Fixed
- Nothing needed fixing in this pass; earlier groups' fixes (scrollbar-gutter, split button, playtime, labels, sheen,
  solid surface tokens) hold up in every state shot. Added the surfaces spec and screenshots.

## Remains
- Verify/repair result notice has no screenshot state (the mock does not expose a finished-verify result); add one when
  the mock gains it.
- Real Mica and the inactive-window frame can only be confirmed on a Windows 11 host; headless shots cannot show the
  native backdrop.
- Committed `dark-solid` / `light-solid` reference sets are not re-rendered here (byte noise only); regenerate on release.
