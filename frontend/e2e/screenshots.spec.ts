import { mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test, type Page } from "@playwright/test";
import { STATES } from "./states";

/**
 * Captures every state in dark/light, solid and Mica-simulated, at 1280×800 and 960×600 into
 * docs/screenshots/<theme>-<backdrop>-<WxH>/<state>.png.
 *
 * Filters (regex / comma lists), handy while iterating:
 *   SHOT_ONLY=library,dialog  SHOT_VARIANTS=dark-solid-1280x800
 */
const OUT = fileURLToPath(new URL("../../docs/screenshots/", import.meta.url));

const THEMES = ["dark", "light"] as const;
const BACKDROPS = ["solid", "mica"] as const;
const SIZES = [
  { w: 1280, h: 800 },
  { w: 960, h: 600 },
] as const;

const only = process.env.SHOT_ONLY?.split(",").filter(Boolean);
const variantFilter = process.env.SHOT_VARIANTS?.split(",").filter(Boolean);

/** A blurred, desaturated "wallpaper" behind the page so translucent Mica surfaces read like the real thing. */
const WALLPAPER: Record<(typeof THEMES)[number], string> = {
  dark: `radial-gradient(900px 600px at 12% 18%, #4b3a8f 0%, transparent 62%),
         radial-gradient(800px 560px at 88% 82%, #146a85 0%, transparent 60%),
         radial-gradient(700px 480px at 62% 8%, #8a2f63 0%, transparent 58%), #1c2140`,
  light: `radial-gradient(900px 600px at 12% 18%, #b9c6ff 0%, transparent 62%),
          radial-gradient(800px 560px at 88% 82%, #9fe0ee 0%, transparent 60%),
          radial-gradient(700px 480px at 62% 8%, #ffd0c2 0%, transparent 58%), #e9ecf6`,
};

async function ready(page: Page) {
  await page.waitForSelector("html[data-ready='1']", { timeout: 20_000 });
}

for (const theme of THEMES) {
  for (const backdrop of BACKDROPS) {
    for (const { w, h } of SIZES) {
      const variant = `${theme}-${backdrop}-${w}x${h}`;
      if (variantFilter && !variantFilter.includes(variant)) continue;
      mkdirSync(`${OUT}${variant}`, { recursive: true });

      test.describe(variant, () => {
        test.use({ viewport: { width: w, height: h } });
        for (const state of STATES) {
          if (only && !only.some((o) => state.name.includes(o))) continue;
          test(state.name, async ({ page }) => {
            const errors: string[] = [];
            page.on("pageerror", (e) => errors.push(e.message));
            await page.goto(`/?${state.query}&theme=${theme}&backdrop=${backdrop}&dev=0`);
            await ready(page);
            if (backdrop === "mica") await page.addStyleTag({ content: `html{background:${WALLPAPER[theme]} !important}` });
            await state.setup?.(page);
            await page.waitForTimeout(state.settle ?? 350);
            await page.screenshot({
              path: `${OUT}${variant}/${state.name}.png`,
              animations: state.live ? "allow" : "disabled",
              caret: "hide",
            });
            if (errors.length) throw new Error(`Page errors in ${state.name}:\n${errors.join("\n")}`);
          });
        }
      });
    }
  }
}
