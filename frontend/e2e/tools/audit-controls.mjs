// Control audit: measures height / radius / icon sizes of every interactive element on key screens, so siblings that should
// match (buttons, selects, inputs, segmented controls, split buttons side by side) can be compared in one table.
//
//   node e2e/tools/audit-controls.mjs [baseUrl]      (a dev server must be listening; default: this checkout's e2e port)
import { chromium } from "@playwright/test";
import { E2E_PORT } from "./port.mjs";

const base = process.argv[2] ?? `http://localhost:${E2E_PORT}`;
const SCREENS = [
  ["library", "scenario=returning#/library"],
  ["library-list", "scenario=returning#/library"],
  ["game", "scenario=returning#/game/Fortnite"],
  ["downloads", "scenario=queue&freeze=1&seek=0.42&timescale=1#/downloads"],
  ["settings-downloads", "scenario=returning#/settings"],
  ["home", "scenario=returning#/home"],
  ["store", "scenario=returning#/store"],
];

const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
for (const [name, q] of SCREENS) {
  const [query, hash] = q.split("#");
  await page.goto(`${base}/?${query}&theme=dark&backdrop=solid&dev=0#${hash}`);
  await page.waitForSelector("html[data-ready='1']");
  await page.waitForTimeout(900);
  const rows = await page.evaluate(() => {
    const sel = "button,[role=combobox],input,[role=radiogroup],[role=switch],[data-split]";
    const out = [];
    for (const el of document.querySelectorAll(sel)) {
      const r = el.getBoundingClientRect();
      if (r.width < 4 || r.height < 4 || r.bottom < 0 || r.top > innerHeight) continue;
      const cs = getComputedStyle(el);
      const target = el.closest("[data-split]") === el && el.hasAttribute("data-split") ? el : el;
      out.push({
        y: Math.round(r.top / 8) * 8,
        x: Math.round(r.left),
        tag: el.getAttribute("role") ?? el.tagName.toLowerCase(),
        label: (el.getAttribute("aria-label") ?? el.textContent ?? "").trim().slice(0, 26),
        h: Math.round(r.height * 10) / 10,
        w: Math.round(r.width),
        radius: cs.borderTopLeftRadius,
        svg: [...new Set([...target.querySelectorAll("svg")].map((s) => Math.round(s.getBoundingClientRect().width)))].join("/"),
      });
    }
    return out;
  });
  console.log(`\n== ${name}`);
  const byRow = new Map();
  for (const r of rows) (byRow.get(r.y) ?? byRow.set(r.y, []).get(r.y)).push(r);
  for (const [y, rs] of [...byRow].sort((a, b) => a[0] - b[0])) {
    const hs = new Set(rs.map((r) => r.h));
    const flag = rs.length > 1 && hs.size > 1 ? "  <-- heights differ" : "";
    console.log(`y≈${y}${flag}`);
    for (const r of rs.sort((a, b) => a.x - b.x)) console.log(`   ${r.tag.padEnd(9)} h=${String(r.h).padEnd(5)} w=${String(r.w).padEnd(4)} r=${r.radius.padEnd(6)} svg=${(r.svg || "-").padEnd(5)} ${r.label}`);
  }
}
await browser.close();
