// Downloads page review shots: every state of the active card (docs/design-download-card.md §7), the queue, failures and history,
// dark + light, 1280x800 and 960x600 (SIZES=900x700 for the window minimum). Each scene saves the whole viewport and, when the
// page has a card, a crop of it (`<name>-card.png`).
//   pnpm exec vite build && pnpm exec vite preview --port $GLIDER_E2E_PORT --strictPort     (production build, mock backend)
//   node e2e/tools/downloads-shots.mjs <outDir> [filter-regex]       THEMES=dark,light  SIZES=1280x800,960x600  DPR=1
import { mkdirSync } from "node:fs";
import { chromium } from "@playwright/test";
import { E2E_PORT } from "./port.mjs";

const out = process.argv[2];
const only = process.argv[3] ? new RegExp(process.argv[3]) : null;
if (!out) throw new Error("usage: downloads-shots.mjs <outDir> [filter-regex]");

const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const go = (p) => p.evaluate(() => window.__glider.ui.getState().go("downloads"));
const DL = "scenario=downloading&freeze=1&seek=0.42&timescale=1";
const QUEUE = "scenario=queue&freeze=1&seek=0.42&timescale=1";
const settings = (p, patch) => p.evaluate((patch) => window.__glider.rpc("settings.update", { ...window.__glider.app.getState().snapshot.settings, ...patch }), patch);
const rpc = (p, method, params) => p.evaluate(([m, a]) => window.__glider.rpc(m, a), [method, params]);

/**
 * `open`: Details expanded (the card remembers it per viewer). `full`: the page, no card crop. `focus`: Tab to Pause (or to the
 * disclosure when "details"); `paint`: a flat colour over the band's art.
 * Names are stable so the docs can point at them.
 */
const SCENES = [
  { name: "downloading", q: DL },
  { name: "downloading-open", q: DL, open: true },
  { name: "slow-disk", q: `${DL}&bottleneck=disk` },
  { name: "slow-disk-open", q: `${DL}&bottleneck=disk`, open: true },
  { name: "slow-cpu", q: `${DL}&bottleneck=cpu` },
  { name: "slow-cdn", q: `${DL}&bottleneck=cdn`, open: true },
  { name: "limited", q: DL, setup: async (p) => { await settings(p, { speedLimitBytesPerSecond: 8 * 1024 * 1024 }); await wait(1200); } },
  { name: "limited-open", q: DL, open: true, setup: async (p) => { await settings(p, { speedLimitBytesPerSecond: 8 * 1024 * 1024 }); await wait(1200); } },
  { name: "background", q: DL, setup: async (p) => { await settings(p, { downloadMode: "background" }); await wait(1200); } },
  { name: "paused", q: DL, setup: async (p) => { await wait(500); await p.getByRole("button", { name: "Pause", exact: true }).click(); await wait(600); } },
  { name: "paused-open", q: DL, open: true, setup: async (p) => { await wait(500); await p.getByRole("button", { name: "Pause", exact: true }).click(); await wait(600); } },
  { name: "young", q: "scenario=downloading&freeze=1&seek=0.01&timescale=1" },
  { name: "preparing", q: "scenario=frozenPreparing" },
  { name: "verifying", q: "scenario=frozenVerifying" },
  { name: "repairing", q: "scenario=frozenRepairing", open: true },
  { name: "installing", q: "scenario=frozenInstalling", open: true },
  { name: "moving", q: "scenario=frozenMoving" },
  { name: "finalizing", q: "scenario=frozenFinalizing" },
  { name: "removing", q: "scenario=frozenRemoving" },
  { name: "importing", q: "scenario=frozenImporting" },
  { name: "waiting-egl", q: "scenario=eglBusy&freeze=1" },
  // "While a game is running: Pause" (the default) and an update started meanwhile: never started, so Core still says queued.
  { name: "held-by-game", q: "scenario=playing", full: true, setup: async (p) => { await rpc(p, "library.updateAll").catch(() => {}); await wait(800); } },
  { name: "held-by-game-open", q: "scenario=playing", open: true, setup: async (p) => { await rpc(p, "library.updateAll").catch(() => {}); await wait(800); } },
  { name: "offline-started", q: QUEUE, setup: async (p) => { await wait(400); await p.evaluate(() => window.__glider.mock.setConnectivity("offline")); await wait(900); } },
  {
    name: "offline-fresh",
    q: "scenario=offline",
    setup: async (p) => {
      const app = await p.evaluate(`(() => [...window.__glider.library.getState().games.values()].find((g) => g.kind==='game'&&g.installStatus==='notInstalled'&&g.thirdParty==='none'))()?.appName ?? null`);
      await rpc(p, "game.install", { appName: app, basePath: "D:\\Games", components: null, dlc: [], priority: false });
      await wait(900);
    },
  },
  { name: "ready", q: "scenario=downloading&seek=0.97&timescale=40", settle: 5500 },
  { name: "queue", q: QUEUE, full: true, settle: 1200 },
  { name: "queue-paused", q: QUEUE, setup: async (p) => { await rpc(p, "queue.pauseAll"); await wait(600); } },
  // Art fallbacks and the worst key art: no wide art (blurred cover), no art (hue gradient), flat white, bright, busy.
  { name: "art-nowide", q: `${DL}&art=noWide` },
  { name: "art-none", q: `${DL}&art=none` },
  { name: "art-white", q: `${DL}&art=white` },
  { name: "art-bright", q: `${DL}&art=bright&bottleneck=disk` },
  { name: "art-busy", q: `${DL}&art=busy` },
  // Keyboard focus on the card's controls over green, purple, blue, white and bright art (the dark halo under the ring).
  { name: "focus-green", q: DL, focus: true },
  { name: "focus-purple", q: QUEUE, focus: true },
  { name: "focus-blue", q: DL, focus: true, paint: "#3b82f6" },
  { name: "focus-white", q: `${DL}&art=white`, focus: true },
  { name: "focus-bright", q: `${DL}&art=bright`, focus: true },
  { name: "focus-details", q: DL, focus: "details" },
  // The page around the card.
  { name: "worst-card", q: `${DL}&data=worst`, settle: 1500 },
  { name: "worst-queue", q: `${QUEUE}&data=worst`, full: true, settle: 1500 },
  { name: "huge-queue", q: "scenario=queue&freeze=1&data=huge", full: true, settle: 1500 },
  { name: "history", q: "scenario=returning", full: true },
  { name: "errors", q: "scenario=errors", full: true },
  {
    name: "errors-freed",
    q: "scenario=errors&freeze=1",
    full: true,
    settle: 900,
    setup: async (p) => {
      await p.evaluate(() => {
        for (const l of ["C", "D", "E", "F"]) window.__glider.mock.setFreeSpace(l, 60 * 1024 ** 3);
        window.dispatchEvent(new Event("focus"));
      });
      await wait(900);
    },
  },
  { name: "empty", q: "scenario=returning", full: true, setup: async (p) => { await rpc(p, "queue.clearFinished"); await wait(400); } },
];

const browser = await chromium.launch();
for (const theme of (process.env.THEMES ?? "dark,light").split(",")) {
  for (const [w, h] of (process.env.SIZES ?? "1280x800,960x600").split(",").map((x) => x.split("x").map(Number))) {
    for (const s of SCENES) {
      if (only && !only.test(s.name)) continue;
      const ctx = await browser.newContext({ viewport: { width: w, height: h }, deviceScaleFactor: Number(process.env.DPR ?? 1) });
      if (s.open) await ctx.addInitScript(() => localStorage.setItem("glider.downloads.details", "1"));
      const page = await ctx.newPage();
      const errs = [];
      page.on("pageerror", (e) => errs.push(e.message));
      page.on("console", (m) => m.type() === "error" && errs.push(m.text()));
      await page.goto(`http://localhost:${E2E_PORT}/?${s.q}&theme=${theme}&backdrop=solid&dev=0`);
      await page.waitForSelector("html[data-ready='1']", { timeout: 20000 });
      await go(page);
      await wait(600);
      await s.setup?.(page);
      if (s.paint) await page.evaluate((c) => ((document.querySelector("[data-band] > div")).style.cssText = `position:absolute;inset:0;background:${c}`), s.paint);
      if (s.focus) {
        const target = page.getByRole("button", s.focus === "details" ? { name: /show details$/ } : { name: "Pause", exact: true });
        for (let i = 0; i < 12 && !(await target.evaluate((el) => el === document.activeElement)); i++) await page.keyboard.press("Tab");
      }
      await wait(s.settle ?? 1000);
      const dir = `${out}/${theme}-${w}x${h}`;
      mkdirSync(dir, { recursive: true });
      await page.screenshot({ path: `${dir}/${s.name}.png`, animations: "disabled" });
      const card = page.locator("section:has([role=progressbar])").first();
      if (!s.full && (await card.count())) {
        const box = await card.boundingBox();
        await page.screenshot({ path: `${dir}/${s.name}-card.png`, animations: "disabled", clip: { x: box.x - 12, y: box.y - 12, width: box.width + 24, height: box.height + 24 } });
      }
      if (errs.length) console.log(`[${theme} ${w}x${h}] ${s.name}: ${errs.slice(0, 3).join(" | ")}`);
      await ctx.close();
    }
  }
}
await browser.close();
console.log("done");
