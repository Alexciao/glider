import { mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test } from "@playwright/test";
import { STATES } from "./states";

/**
 * Host surface states, emulated by the attribute the host sets (see ChromeLogic.cs / tokens.css):
 *   mica-active   data-backdrop=mica            (Windows 11, window active)
 *   mica-solid    data-backdrop=mica + data-surface=solid (inactive, transparency off, Battery Saver)
 *   win10         data-backdrop=solid           (Windows 10 fallback)
 * Out: SHOT_OUT (default docs/screenshots)/surface-<theme>-<surface>/<state>.png
 */
const OUT = process.env.SHOT_OUT
  ? `${process.env.SHOT_OUT.replace(/[\\/]+$/, "")}/`
  : fileURLToPath(new URL("../../docs/screenshots/", import.meta.url));
const NAMES = ["library-installed", "library-downloading", "settings-startup", "dialog-uninstall", "toasts"];
const SURFACES = {
  "mica-active": { backdrop: "mica", solid: false },
  "mica-solid": { backdrop: "mica", solid: true },
  win10: { backdrop: "solid", solid: false },
} as const;

for (const theme of ["dark", "light"] as const) {
  for (const [surface, cfg] of Object.entries(SURFACES)) {
    const dir = `${OUT}surface-${theme}-${surface}`;
    mkdirSync(dir, { recursive: true });
    test.describe(`surface-${theme}-${surface}`, () => {
      test.use({ viewport: { width: 1280, height: 800 } });
      for (const state of STATES.filter((s) => NAMES.includes(s.name))) {
        test(state.name, async ({ page }) => {
          await page.goto(`/?${state.query}&theme=${theme}&backdrop=${cfg.backdrop}&dev=0`);
          await page.waitForSelector("html[data-ready='1']", { timeout: 20_000 });
          if (cfg.solid) await page.evaluate(() => (document.documentElement.dataset.surface = "solid"));
          await state.setup?.(page);
          await page.waitForTimeout(state.settle ?? 350);
          await page.screenshot({ path: `${dir}/${state.name}.png`, animations: state.live ? "allow" : "disabled", caret: "hide" });
        });
      }
    });
  }
}
