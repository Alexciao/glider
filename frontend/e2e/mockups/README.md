# Download card mockups (scratch, not product code)

Mockups and review tooling behind `docs/design-download-card.md`. Nothing here ships. The components are `.jsx`, so `tsc -b`
and ESLint ignore them.

- `vite.config.mjs`: the real app and mock backend with one swap: `Downloads.tsx`'s card resolves to `CardSwitch.jsx`.
- `CardSwitch.jsx`: `?card=a|b|c` picks a direction; anything else renders the live card.
- `CardA.jsx` (Tuned), `CardB.jsx` (Cover and one line, recommended), `CardC.jsx` (Hero band); `model.js` and `parts.jsx` are shared.
- `shoot-current.mjs`: every state, dark and light, 1280×800 and 960×600 (live card, or a mockup with `CARD=`).
- `film-card.mjs`: filmstrips of pause, resume and a phase change.

```sh
pnpm exec vite --config e2e/mockups/vite.config.mjs --port 5988
# http://localhost:5988/?scenario=queue&freeze=1&seek=0.42&timescale=1&bottleneck=disk&card=b
#   details=1 opens B's panel · hold=playing|offline fakes a hold · done=1 shows the Ready state
GLIDER_E2E_PORT=5988 CARD=b node e2e/mockups/shoot-current.mjs <outDir> [filter]
```
