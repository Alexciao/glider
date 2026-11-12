# Group A status

- A1 Settings width jump: fixed (scrollbar-gutter stable on PageScroll, fixed 640 px content column)
- A1 Three-dot vs Play: fixed (one split button group sharing height, radii, tone, motion, focus ring)
- A1 Settings "Auto (1024 MB)" wrapping: fixed (wider segmented control, nowrap)
- A1 Onboarding error callout hidden by sticky bar: fixed (scrolls into view with scroll margin)
- A1 Toasts cover facts row: fixed (toasts moved top-right below the title bar)
- A1 Per-phase percent label: fixed in the UI (JobLine shows "Phase . N%"); backend per-phase percent semantics unchanged
- A1 Light theme cards darker than page: fixed (surface-1/2 tokens); no-art hero dark slab: not fixed: needs a design decision on a light no-art hero
- A1 Hero legibility AA: not fixed: needs real-pixel contrast work in HeroArt/scrim, out of cheap scope
- A1 Duplicate installing state: not fixed: status line, button and pill each carry a distinct piece; removing one is a design call
- A1 Full 4px-grid audit of every screen: partial: spacing in touched components uses tokens; no systematic deviations found in Library, Settings, Onboarding screenshots (dark/light 1280x800); not exhaustive
- A1 "Ready to play" choreography, Settings grouping, Notice.JobId toast suppression: not fixed: out of the listed scope
- A2 Playtime and Last played on the Library metadata row: fixed (local tracking in Core, persisted in game.json, contract regenerated, tests added)
- A3 Progress bar effect: fixed (speed-tied sheen, leading glow, settle, calm paused state, reduced-motion static)
- A4 Installed size / Download size labels with tooltips: fixed in the UI (needs Group D to fill GameState.InstalledSizeBytes; shows "Measuring…" until then)
