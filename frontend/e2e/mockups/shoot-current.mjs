// Design review of the live active-download card (docs/design-download-card.md): every phase and state, viewport + card crop,
// dark + light, 1280x800 and 960x600. Needs the production build served on this checkout's e2e port:
//   pnpm exec vite build && pnpm exec vite preview --port <E2E_PORT> --strictPort
//   node e2e/mockups/shoot-current.mjs <outDir> [filter]
// Mockups: GLIDER_E2E_PORT=<mockup server port> CARD=a|b|c node e2e/mockups/shoot-current.mjs <outDir> [filter]
import { mkdirSync } from "node:fs";
import { chromium } from "@playwright/test";
import { E2E_PORT } from "../tools/port.mjs";

const out = process.argv[2];
const only = process.argv[3];
if (!out) throw new Error("usage: shoot-current.mjs <outDir> [filter]");
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const DL = "scenario=downloading&freeze=1&seek=0.42&timescale=1";
const settings = (p, patch) =>
  p.evaluate((patch) => window.__glider.rpc("settings.update", { ...window.__glider.app.getState().snapshot.settings, ...patch }), patch);

export const SCENES = [
  { name: "downloading", q: DL },
  { name: "slow-disk", q: `${DL}&bottleneck=disk` },
  { name: "slow-cpu", q: `${DL}&bottleneck=cpu` },
  { name: "slow-cdn", q: `${DL}&bottleneck=cdn` },
  { name: "slow-tooltip", q: `${DL}&bottleneck=disk`, setup: async (p) => { await p.getByRole("button", { name: "Slow download" }).hover(); await wait(900); } },
  { name: "slow-dialog", q: `${DL}&bottleneck=disk`, full: true, setup: async (p) => { await p.getByRole("button", { name: "Slow download" }).click(); await wait(500); } },
  { name: "limited", q: DL, setup: async (p) => { await settings(p, { speedLimitBytesPerSecond: 8 * 1024 * 1024 }); await wait(1200); } },
  { name: "background", q: DL, setup: async (p) => { await settings(p, { downloadMode: "background" }); await wait(1200); } },
  { name: "paused", q: DL, setup: async (p) => { await wait(500); await p.getByRole("button", { name: "Pause", exact: true }).click(); await wait(600); } },
  { name: "young", q: "scenario=downloading&freeze=1&seek=0.01&timescale=1" },
  { name: "preparing", q: "scenario=frozenPreparing" },
  { name: "verifying", q: "scenario=frozenVerifying" },
  { name: "repairing", q: "scenario=frozenRepairing" },
  { name: "installing", q: "scenario=frozenInstalling" },
  { name: "finalizing", q: "scenario=frozenFinalizing" },
  { name: "moving", q: "scenario=frozenMoving" },
  { name: "queue", q: "scenario=queue&freeze=1&seek=0.42&timescale=1", full: true },
  { name: "waiting", q: "scenario=eglBusy&freeze=1" },
  // Held while a game runs (default "While a game is running: Pause"), and offline.
  { name: "held", q: "scenario=playing", full: true, setup: async (p) => { await p.evaluate(() => window.__glider.rpc("library.updateAll").catch(() => {})); await wait(600); } },
  { name: "offline", q: DL, full: true, setup: async (p) => { await wait(400); await p.evaluate(() => window.__glider.mock.setConnectivity("offline")); await wait(600); } },
  // Mockups only: the disclosure open (B), the big game of the busy queue (realistic sizes and times).
  { name: "slow-open", q: `${DL}&bottleneck=disk&details=1` },
  { name: "open", q: `${DL}&details=1` },
  { name: "big", q: "scenario=queue&freeze=1&seek=0.42&timescale=1" },
  { name: "big-slow", q: "scenario=queue&freeze=1&seek=0.42&timescale=1&bottleneck=disk" },
  { name: "big-slow-open", q: "scenario=queue&freeze=1&seek=0.42&timescale=1&bottleneck=disk&details=1" },
  { name: "big-paused", q: "scenario=queue&freeze=1&seek=0.42&timescale=1", setup: async (p) => { await wait(500); await p.getByRole("button", { name: "Pause", exact: true }).first().click(); await wait(600); } },
  // Holds (Core will send a hold kind + subject; the mockups fake it with ?hold=).
  { name: "m-held", q: "scenario=playing&hold=playing", setup: async (p) => { await p.evaluate(() => window.__glider.rpc("library.updateAll").catch(() => {})); await wait(600); } },
  { name: "m-held-started", q: "scenario=queue&freeze=1&seek=0.42&timescale=1&hold=playing" },
  { name: "m-done", q: `${DL}&done=1` },
  { name: "m-offline", q: "scenario=queue&freeze=1&seek=0.42&timescale=1&hold=offline" },
];

const browser = await chromium.launch();
for (const theme of (process.env.THEMES ?? "dark,light").split(",")) {
  for (const [w, h] of (process.env.SIZES ?? "1280x800,960x600").split(",").map((x) => x.split("x").map(Number))) {
    const ctx = await browser.newContext({ viewport: { width: w, height: h }, deviceScaleFactor: Number(process.env.DPR ?? 1) });
    for (const s of SCENES) {
      if (only && !new RegExp(only).test(s.name)) continue;
      const page = await ctx.newPage();
      const errs = [];
      page.on("pageerror", (e) => errs.push(e.message));
      await page.goto(`http://localhost:${E2E_PORT}/?${s.q}&theme=${theme}&backdrop=solid&dev=0${process.env.CARD ? `&card=${process.env.CARD}` : ""}`);
      await page.waitForSelector("html[data-ready='1']", { timeout: 20000 });
      await page.evaluate(() => window.__glider.ui.getState().go("downloads"));
      await wait(700);
      await s.setup?.(page);
      await wait(s.settle ?? 1400);
      const dir = `${out}/${theme}-${w}x${h}`;
      mkdirSync(dir, { recursive: true });
      await page.screenshot({ path: `${dir}/${s.name}-page.png`, animations: "disabled" });
      const card = page.locator(".\\@container").first();
      if (!s.full && (await card.count())) {
        const box = await card.boundingBox();
        await page.screenshot({ path: `${dir}/${s.name}.png`, animations: "disabled", clip: { x: box.x - 12, y: box.y - 12, width: box.width + 24, height: box.height + 24 } });
      }
      if (errs.length) console.log(`[${theme} ${w}x${h}] ${s.name}: ${errs.slice(0, 3).join(" | ")}`);
      await page.close();
    }
    await ctx.close();
  }
}
await browser.close();
console.log("done");
