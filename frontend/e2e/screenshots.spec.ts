import { mkdirSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { test, type Page } from "@playwright/test";
import { STATES } from "./states";

/**
 * Captures every state in dark/light, solid and Mica-simulated, at 1280×800 and 960×600 into
 * docs/screenshots/<theme>-<backdrop>-<WxH>/<state>.png.
 *
 * Filters (regex / comma lists), handy while iterating:
 *   SHOT_ONLY=library,dialog  SHOT_VARIANTS=dark-solid-1280x800  SHOT_OUT=/abs/dir (default docs/screenshots)
 */
const OUT = process.env.SHOT_OUT
  ? `${process.env.SHOT_OUT.replace(/[\\/]+$/, "")}/`
  : fileURLToPath(new URL("../../docs/screenshots/", import.meta.url));

const THEMES = ["dark", "light"] as const;
const BACKDROPS = ["solid", "mica"] as const;
const SIZES = [
  { w: 1280, h: 800 },
  { w: 960, h: 600 },
] as const;

const only = process.env.SHOT_ONLY?.split(",").filter(Boolean);
const variantFilter = process.env.SHOT_VARIANTS?.split(",").filter(Boolean);

/**
 * Mica, simulated: what the WebView actually sees behind a transparent page is the desktop wallpaper,
 * heavily blurred and laid under the system tint (#202020 dark / #f3f3f3 light). The wallpaper here is
 * deliberately loud (the user picks it, not us) and the tint is lighter than the real material, so a
 * surface that still reads as intentional in these shots has margin on a real desktop.
 */
const WALLPAPER = `radial-gradient(900px 640px at 8% 12%, #ff6a00 0%, transparent 64%),
  radial-gradient(860px 600px at 92% 88%, #00c2a8 0%, transparent 62%),
  radial-gradient(760px 520px at 70% 6%, #e0109a 0%, transparent 60%),
  radial-gradient(700px 520px at 20% 96%, #2f6bff 0%, transparent 60%), #6b2bd6`;
const MICA_TINT: Record<(typeof THEMES)[number], string> = {
  dark: "rgb(32 32 32 / 0.62)",
  light: "rgb(243 243 243 / 0.58)",
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
            if (state.early) await page.waitForSelector("#root *");
            else await ready(page);
            if (backdrop === "mica")
              await page.addStyleTag({ content: `html{background:linear-gradient(${MICA_TINT[theme]},${MICA_TINT[theme]}),${WALLPAPER} !important}` });
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
