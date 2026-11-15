// Downloads page review shots: every job phase, the speed status, failures and queue states, dark + light, 1280x800 and 960x600.
//   node e2e/tools/downloads-shots.mjs <outDir> [filter]      (needs the dev server: pnpm exec vite --port $GLIDER_E2E_PORT)
import { mkdirSync } from "node:fs";
import { chromium } from "@playwright/test";
import { E2E_PORT } from "./port.mjs";

const out = process.argv[2];
const only = process.argv[3];
if (!out) throw new Error("usage: downloads-shots.mjs <outDir> [filter]");

const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const go = (p) => p.evaluate(() => window.__glider.ui.getState().go("downloads"));

const SCENES = [
  { name: "worst-card", q: "scenario=downloading&freeze=1&seek=0.42&timescale=1&data=worst", settle: 1500 },
  { name: "worst-queue", q: "scenario=queue&freeze=1&seek=0.42&timescale=1&data=worst", settle: 1500 },
  { name: "worst-history", q: "scenario=returning&data=worst", settle: 800 },
  { name: "huge-queue", q: "scenario=queue&freeze=1&data=huge", settle: 1500 },
  { name: "downloading", q: "scenario=downloading&freeze=1&seek=0.42&timescale=1", settle: 1500 },
  { name: "queue", q: "scenario=queue&freeze=1&seek=0.42&timescale=1", settle: 1500 },
  { name: "preparing", q: "scenario=frozenPreparing", settle: 900 },
  { name: "verifying", q: "scenario=frozenVerifying", settle: 900 },
  { name: "repairing", q: "scenario=frozenRepairing", settle: 900 },
  { name: "installing", q: "scenario=frozenInstalling", settle: 900 },
  { name: "finalizing", q: "scenario=frozenFinalizing", settle: 900 },
  { name: "moving", q: "scenario=frozenMoving", settle: 900 },
  { name: "removing", q: "scenario=frozenRemoving", settle: 900 },
  { name: "importing", q: "scenario=frozenImporting", settle: 900 },
  { name: "history", q: "scenario=returning", settle: 600 },
  { name: "errors", q: "scenario=errors", settle: 600 },
  // The failed update's drive is short on space; after "freeing" it outside Glider the row says so and Try again leads.
  {
    name: "errors-freed",
    q: "scenario=errors&freeze=1",
    settle: 900,
    setup: async (p) => {
      await p.evaluate(() => {
        for (const l of ["C", "D", "E", "F"]) window.__glider.mock.setFreeSpace(l, 60 * 1024 ** 3);
        window.dispatchEvent(new Event("focus"));
      });
      await wait(900);
    },
  },
  // What holds the speed back: a slow drive (amber, reason on hover, full reason on click), then a limit the user chose.
  { name: "slow", q: "scenario=downloading&freeze=1&seek=0.42&timescale=1&bottleneck=disk", settle: 900 },
  {
    name: "slow-tooltip",
    q: "scenario=downloading&freeze=1&seek=0.42&timescale=1&bottleneck=disk",
    settle: 700,
    setup: async (p) => {
      await p.getByRole("button", { name: "Slow download" }).hover();
      await wait(900);
    },
  },
  {
    name: "slow-dialog",
    q: "scenario=downloading&freeze=1&seek=0.42&timescale=1&bottleneck=disk",
    settle: 700,
    setup: async (p) => {
      await p.getByRole("button", { name: "Slow download" }).click();
    },
  },
  {
    name: "limited",
    q: "scenario=downloading&freeze=1&seek=0.42&timescale=1",
    settle: 700,
    setup: async (p) => {
      await p.evaluate(() => window.__glider.rpc("settings.update", { ...window.__glider.app.getState().snapshot.settings, speedLimitBytesPerSecond: 8 * 1024 * 1024 }));
      await wait(900);
    },
  },
  {
    name: "queue-paused",
    q: "scenario=queue&freeze=1&seek=0.42&timescale=1",
    settle: 700,
    setup: async (p) => {
      await p.evaluate(() => window.__glider.rpc("queue.pauseAll"));
      await wait(500);
    },
  },
  { name: "empty", q: "scenario=firstRunSignedIn", settle: 600, setup: async (p) => { await p.evaluate(() => window.__glider.rpc("onboarding.complete")); await wait(400); } },
  { name: "offline", q: "scenario=queue&freeze=1&seek=0.42&timescale=1", settle: 900, setup: async (p) => { await wait(400); await p.evaluate(() => window.__glider.mock.setConnectivity("offline")); await wait(600); } },
  { name: "paused", q: "scenario=downloading&freeze=1&seek=0.42&timescale=1", settle: 900, setup: async (p) => { await wait(500); await p.getByRole("button", { name: "Pause", exact: true }).click(); await wait(400); } },
  { name: "playing", q: "scenario=playing", settle: 600, setup: async (p) => { await p.evaluate(() => window.__glider.rpc("library.updateAll").catch(() => {})); await wait(600); } },
  { name: "eglbusy", q: "scenario=eglBusy&freeze=1", settle: 700 },
];

const browser = await chromium.launch();
for (const theme of ["dark", "light"]) {
  for (const [w, h] of (process.env.SIZES ?? "1280x800,960x600").split(",").map((x) => x.split("x").map(Number))) {
    const ctx = await browser.newContext({ viewport: { width: w, height: h }, deviceScaleFactor: 1 });
    for (const s of SCENES) {
      if (only && !s.name.includes(only)) continue;
      const page = await ctx.newPage();
      const errs = [];
      page.on("pageerror", (e) => errs.push(e.message));
      page.on("console", (m) => m.type() === "error" && errs.push(m.text()));
      await page.goto(`http://localhost:${E2E_PORT}/?${s.q}&theme=${theme}&backdrop=solid&dev=0`);
      await page.waitForSelector("html[data-ready='1']", { timeout: 20000 });
      await go(page);
      await wait(400);
      await s.setup?.(page);
      await wait(s.settle ?? 600);
      const dir = `${out}/${theme}-${w}x${h}`;
      mkdirSync(dir, { recursive: true });
      await page.screenshot({ path: `${dir}/${s.name}.png`, fullPage: false, animations: "disabled" });
      // A tall capture too, so everything below the fold is reviewable.
      await page.evaluate(() => { const sc = document.querySelector("div.overflow-y-auto"); if (sc) { sc.style.height = "auto"; sc.style.overflow = "visible"; } });
      if (errs.length) console.log(`[${theme} ${w}x${h}] ${s.name}: ${errs.slice(0, 3).join(" | ")}`);
      await page.close();
    }
    await ctx.close();
  }
}
await browser.close();
console.log("done");
