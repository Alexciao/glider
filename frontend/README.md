# Glider frontend

React 19 + TypeScript + Vite 8 + Tailwind v4 + Radix + Motion + Zustand. pnpm only.

```sh
pnpm install
pnpm dev                 # http://localhost:5173, mock backend (no host needed)
pnpm lint && pnpm typecheck && pnpm test && pnpm build
pnpm screenshots         # every screen/state, see ../docs/screenshots/README.md
pnpm exec playwright test e2e/a11y.spec.ts    # axe-core over every state, dark + light
pnpm exec playwright test e2e/focus.spec.ts   # every Tab stop shows a focus ring
```

## Mock backend (when not inside the WebView2 host)

`src/ipc/mock` implements the whole generated contract. Pick a scenario and options by URL:

| Param | Values |
|---|---|
| `scenario` | `fresh` `eglSignedOut` `signedIn` `eglDetected` `eglMultiple` `installed` `updateAvailable` `downloading` `eglBusy` `diskFull` `authExpired` `playing` |
| `theme` / `backdrop` | `dark` `light` `system` / `solid` `mica` |
| `seek` `timescale` `freeze` | start a running job at 0..1 / simulation speed (default 12, `1` = realistic ETAs) / hold the clock |
| `fail` | fail the next action with an `ErrorCode` |
| `art` | `none` to see the key-art fallback |
| `data` | `worst` `empty` `one` `huge` (dev stress data, `src/dev/stress.ts`) |
| `dev` | `0` hides the dev switchers |

In mock mode a scenario menu (bottom-left) and a Demo / Worst case / Empty / One / Huge toggle
(bottom-centre) appear, and `window.__glider` exposes the stores, `rpc` and the mock controller to tests.

## Layout

`src/ui` design system · `src/state` stores, actions, formatters · `src/app` shell ·
`src/features` Library / Downloads / Settings / Onboarding / dialogs · `src/ipc` typed client + mock ·
`e2e` Playwright (screenshots, a11y, focus, break-UI).
