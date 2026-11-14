import { mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test } from "@playwright/test";
import { STATES } from "./states";

/**
 * The committed README screenshot set: six screens, dark and light, solid backdrop, 1280×800, written to
 * docs/screenshots/<theme>-solid-1280x800/<file>.png. Run with `pnpm screenshots:readme`. (`pnpm screenshots` renders the
 * whole matrix into the same folders; only these files are committed, see docs/screenshots/README.md.)
 */
const OUT = fileURLToPath(new URL("../../docs/screenshots/", import.meta.url));

/** README file name → state in states.ts. */
const SET = {
  home: "home-returning",
  "library-grid": "lib-grid",
  "game-page": "game-installed",
  downloads: "downloads-active",
  "onboarding-scan": "onboarding-3-scan",
  settings: "settings-downloads",
} as const;

for (const theme of ["dark", "light"] as const) {
  const dir = `${OUT}${theme}-solid-1280x800`;
  mkdirSync(dir, { recursive: true });
  test.describe(`readme ${theme}`, () => {
    test.use({ viewport: { width: 1280, height: 800 } });
    for (const [file, name] of Object.entries(SET)) {
      const state = STATES.find((s) => s.name === name);
      if (!state) throw new Error(`readme set: no state "${name}"`);
      test(file, async ({ page }) => {
        await page.goto(`/?${state.query}&theme=${theme}&backdrop=solid&dev=0`);
        await page.waitForSelector("html[data-ready='1']", { timeout: 20_000 });
        await state.setup?.(page);
        await page.waitForTimeout(Math.max(state.settle ?? 350, 900));
        await page.screenshot({ path: `${dir}/${file}.png`, animations: "disabled", caret: "hide" });
      });
    }
  });
}
