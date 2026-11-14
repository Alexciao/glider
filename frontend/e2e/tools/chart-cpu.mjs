// Chart CPU cost on the live Downloads card, headless Chromium + CDP.
//  1. Page-level: main-thread busy time (TaskDuration) over a fixed window for Downloads (chart visible), Downloads with the
//     window hidden (chart paused) and Library (same job running, no chart), three runs each, medians.
//  2. Chart-only: a sampling profile of the Downloads run; inclusive time of the chart's draw() and of the glide animation setup.
//   node e2e/tools/chart-cpu.mjs [seconds]      (needs the dev server: pnpm exec vite --port $GLIDER_E2E_PORT)
import { chromium } from "@playwright/test";
import { E2E_PORT } from "./port.mjs";

const SECONDS = Number(process.argv[2] ?? 30);
const wait = (ms) => new Promise((r) => setTimeout(r, ms));
const median = (xs) => [...xs].sort((a, b) => a - b)[Math.floor(xs.length / 2)];

async function run(browser, { page: where, hidden, profile }) {
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const page = await ctx.newPage();
  await page.goto(`http://localhost:${E2E_PORT}/?scenario=downloading&timescale=1&seek=0.05&dev=0&theme=dark`);
  await page.waitForSelector("html[data-ready='1']");
  await page.evaluate((w) => window.__glider.ui.getState().go(w), where);
  await wait(5000);
  if (hidden) await page.evaluate(() => window.__glider.app.getState().setWindow({ maximized: false, focused: false, visible: false }));
  const cdp = await ctx.newCDPSession(page);
  await cdp.send("Performance.enable");
  if (profile) {
    await cdp.send("Profiler.enable");
    await cdp.send("Profiler.setSamplingInterval", { interval: 100 });
    await cdp.send("Profiler.start");
  }
  const read = async () => Object.fromEntries((await cdp.send("Performance.getMetrics")).metrics.map((m) => [m.name, m.value]));
  const a = await read();
  await wait(SECONDS * 1000);
  const b = await read();
  let chart = null;
  if (profile) {
    const { profile: p } = await cdp.send("Profiler.stop");
    const byId = new Map(p.nodes.map((n) => [n.id, n]));
    const self = new Map();
    p.samples.forEach((id, i) => self.set(id, (self.get(id) ?? 0) + (p.timeDeltas[i] ?? 0)));
    const total = (n) => (self.get(n.id) ?? 0) + (n.children ?? []).reduce((s, c) => s + total(byId.get(c)), 0);
    let inclusive = 0;
    for (const n of p.nodes) {
      const f = n.callFrame;
      if (f.functionName === "draw" && /ThroughputChart/.test(f.url)) inclusive += total(n);
    }
    chart = { "chart draw() ms/s": +(inclusive / 1000 / SECONDS).toFixed(3) };
    if (process.env.TOP) {
      const agg = new Map();
      for (const n of p.nodes) { const k = `${n.callFrame.functionName || "(anon)"} ${n.callFrame.url.split("/").slice(-2).join("/")}:${n.callFrame.lineNumber}`; agg.set(k, (agg.get(k) ?? 0) + (self.get(n.id) ?? 0)); }
      console.log([...agg].sort((x, y) => y[1] - x[1]).slice(0, 18).map(([k, v]) => `${(v / 1000 / SECONDS).toFixed(2)} ms/s  ${k}`).join(String.fromCharCode(10)));
    }
  }
  const d = (k) => (b[k] - a[k]) * 1000;
  await ctx.close();
  return {
    busy: (d("TaskDuration") / (SECONDS * 1000)) * 100,
    script: d("ScriptDuration") / SECONDS,
    layout: d("LayoutDuration") / SECONDS,
    style: d("RecalcStyleDuration") / SECONDS,
    chart,
  };
}

const browser = await chromium.launch();
const cases = [
  ["Downloads (live chart)", { page: "downloads" }],
  ["Downloads (window hidden, chart paused)", { page: "downloads", hidden: true }],
  ["Library (baseline: same job, no chart)", { page: "library" }],
];
const table = [];
if (process.env.TOP) {
  await run(browser, { page: process.env.TOP === "lib" ? "library" : "downloads", profile: true });
  await browser.close();
  process.exit(0);
}
for (const [label, opts] of cases) {
  const runs = [];
  for (let i = 0; i < 3; i++) runs.push(await run(browser, opts));
  table.push({
    scenario: label,
    "main thread busy %": +median(runs.map((r) => r.busy)).toFixed(2),
    "script ms/s": +median(runs.map((r) => r.script)).toFixed(1),
    "layout ms/s": +median(runs.map((r) => r.layout)).toFixed(2),
    "style ms/s": +median(runs.map((r) => r.style)).toFixed(1),
  });
}
const prof = await run(browser, { page: "downloads", profile: true });
await browser.close();
console.table(table);
console.log("Chart only (sampling profile, Downloads page):", prof.chart);
