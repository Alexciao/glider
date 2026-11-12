import { mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test } from "@playwright/test";
import { STATES } from "./states";

/**
 * "Break the UI": the same screens with worst-case / empty / one / huge data (src/dev/stress.ts),
 * at 1280×800, 960×600 and 960×540 (a 1920×1080 display at 200 % zoom). Dark theme; light is
 * covered by the main matrix. Output: docs/screenshots/break-ui/<mode>-<WxH>/<state>.png
 */
const OUT = fileURLToPath(new URL("../../docs/screenshots/break-ui/", import.meta.url));

const MODES = ["worst", "empty", "one", "huge"] as const;
const SIZES = [
  { w: 1280, h: 800 },
  { w: 960, h: 600 },
  { w: 960, h: 540 },
] as const;

/** The screens where data shows through: names, paths, versions, sizes, speeds, errors, lists. */
const KEY = new Set([
  "library-installed",
  "library-downloading",
  "library-waiting-for-egl",
  "library-error-disk-full",
  "library-menu-account",
  "downloads-active",
  "downloads-history",
  "downloads-failed-disk-full",
  "downloads-waiting-for-egl",
  "onboarding-4-found-multiple",
  "onboarding-4-found-one",
  "settings-game",
  "settings-egl",
  "settings-about",
  "dialog-uninstall",
  "dialog-move",
  "dialog-cancel-download",
  "toasts",
]);

for (const mode of MODES) {
  for (const { w, h } of SIZES) {
    // Worst case gets every size; the list-shape modes only need the two main ones.
    if (mode !== "worst" && h === 540) continue;
    const variant = `${mode}-${w}x${h}`;
    mkdirSync(`${OUT}${variant}`, { recursive: true });
    test.describe(`break-ui ${variant}`, () => {
      test.use({ viewport: { width: w, height: h } });
      for (const state of STATES.filter((s) => KEY.has(s.name))) {
        test(state.name, async ({ page }) => {
          const errors: string[] = [];
          page.on("pageerror", (e) => errors.push(e.message));
          await page.goto(`/?${state.query}&data=${mode}&theme=dark&backdrop=solid&dev=0`);
          await page.waitForSelector("html[data-ready='1']");
          await state.setup?.(page);
          await page.waitForTimeout(state.settle ?? 350);
          await page.screenshot({ path: `${OUT}${variant}/${state.name}.png`, animations: state.live ? "allow" : "disabled", caret: "hide" });
          if (errors.length) throw new Error(errors.join("\n"));
        });
      }
    });
  }
}
