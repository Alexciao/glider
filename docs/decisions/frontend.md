# Frontend decisions

One line of reasoning per decision. Newest at the bottom of each section.

## Design language
- **Neutral primary, one accent**: white/ink fill for the main action; glide blue only for progress, focus and selection, so the UI stays calm and the one colour always means "live".
- **Hero text lives in a `data-theme="dark"` scope**: arbitrary key art only keeps AA against a dark, art-independent scrim, so the hero is dark in light theme too (Apple TV / Steam pattern).
- **Surfaces are layered translucency, never opaque greys**: the same tokens work on Mica and on solid Windows 10, and Mica shows through the rail and panel edges.
- **Rail with icon + label, 72 px**: labels beat icon-only wayfinding; 64 px clipped "Downloads".
- **Active job lives in the title bar pill**: persistent on every page and costs no layout; the rail's Downloads icon carries a breathing dot.
- **No emoji, no gradients on controls, no glow on the logo**: brand rules, and restraint.

## Motion
- **Critically damped springs for anything touched, bounce only for celebration**: no overshoot on menus/morphs; the only decorative moment is the one-off "Ready to play" check + sheen.
- **Only transform/opacity animate**; Radix enter/exit are CSS keyframes (Presence waits on animations, not transitions); state changes are transitions so they retarget when interrupted.
- **Command palette and page switches via keyboard get no/near-no animation**: they happen hundreds of times a day.
- **Motion loaded as `LazyMotion`/`m`**: the animation feature bundle (`domMax`, layout projection) loads after first paint, off the critical path.
- **10 Hz progress never re-renders pages**: it lives in its own store, sparklines draw to the DOM from a rAF-coalesced subscription, and the hero re-renders only on whole-percent/phase change.

## State
- **Structural sharing on every snapshot**: the backend pushes the whole snapshot; unchanged branches keep identity, so selectors only fire for real changes.
- **Optimistic UI only where it can't lie**: pause/resume/cancel/dismiss and settings roll back via a fresh snapshot on error; installs and launches show a pending button instead.
- **Settings writes are serialized and shield in-flight values from stale snapshots**: `settings.update` takes the whole object, so racing toggles would otherwise clobber each other.
- **Errors have a home**: inline callout on the surface that failed (hero, downloads, dialogs), toast only for background events; every `FixAction` maps to one handler (`runFix`).
- **Step state in onboarding is derived from real state**, only "welcome" is local; signing in, linking and installing advance it.

## Mock backend
- **Time-scaled simulation (default 12×) with truthful ETA**: a 28 GB install finishes in ~25 s of real time; `?timescale=1` for realistic ETAs, `?freeze=1` for stable screenshots.
- **`window.__glider` test handle exists only in mock mode**: Playwright uses it to reach states that would need long click paths.

## Tooling
- **`@testing-library/user-event` + `jest-dom` added**: Radix keyboard behaviour (roving focus, dialogs) needs real event sequences.
- **Screenshots wait on `html[data-ready]`**, not sleeps; Mica is simulated by a blurred gradient behind the translucent page.

## Delivery
- **Only the 1280×800 dark and light solid screenshot sets are committed**: all 8 variants plus break-UI are ~120 MB of PNG; they regenerate in ~4 minutes and are git-ignored (`docs/screenshots/.gitignore`).
- **`#root`, not `body`, paints `--g-bg`**: the host requires transparent `html`/`body` so Mica shows through.
- **Focus rings come from one global `:focus-visible` rule**: components never use `outline-none` on interactive elements (an e2e test walks the Tab order and fails on a missing ring).
- **`pnpm build` still emits the mock chunk** (needed for `vite preview` and screenshots) but nothing in the hosted entry statically imports it; `index.html` preloads only the app chunks.
