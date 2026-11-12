# Status: group B (Mica, title bar, window host)

- AllowsTransparency=false: fixed (verified already false; documented, no change needed).
- WebView2 DefaultBackgroundColor transparent before creation: fixed (already set in controller options; re-asserted by ApplyBackdrop on every state change).
- WPF Background transparent, DwmExtendFrameIntoClientArea(-1), immersive dark mode, SYSTEMBACKDROP_TYPE=Mica: fixed (already correct; now idempotent and re-applied).
- Re-apply on theme change: fixed (WM_SETTINGCHANGE ImmersiveColorSet in any theme mode, WM_THEMECHANGED, colorization; dark/light flip resets the backdrop).
- Re-apply on DPI change and maximize/restore: fixed (WM_DPICHANGED, WM_SIZE state change).
- Inactive window / transparency effects off / Battery Saver: fixed (BackdropPolicy + data-surface solid tint, no white/gray flash).
- Windows 10 solid fallback consistency: fixed (host colours now equal the CSS solid tokens; page and window agree).
- Title bar drag, double-click maximize, min/max/close: fixed (no defect found; self-test hit tests and maximize/restore pass).
- Snap Layouts / max button hit area at every DPI: fixed (pixel-snapped overlay edges, unit-tested at 96-192 DPI).
- Maximized overhang / client clipping: fixed (no defect found; client rect equals work area in self-test, auto-hide taskbar reserved).
- Themed loading state in Epic login window: fixed (verified with the self-test login screenshot).
- WebView2 browser-process-crash recovery: fixed in code (environment recreated); not fixed: never exercised against a real crash (needs manual kill of the msedgewebview2 browser process).
- Windows 10 real-machine test: not fixed: manual (GLIDER_FORCE_SOLID=1 on Windows 11 is only a proxy).
- Real inactive / transparency-off / Battery Saver visual check and multi-monitor DPI move: not fixed: manual, only the default active Mica state was run via --selftest.
