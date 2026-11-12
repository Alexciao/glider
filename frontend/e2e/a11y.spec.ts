import { appendFileSync, mkdirSync } from "node:fs";
import { createRequire } from "node:module";
import { expect, test } from "@playwright/test";
import { STATES } from "./states";

/**
 * Accessibility audit: axe-core over every state, dark and light (solid backdrop, where contrast can
 * be computed). Fails on serious/critical violations. Mica is audited via the solid tokens: the
 * translucent layers only ever sit over a darker/lighter plate than the solid fallback.
 */
const require = createRequire(import.meta.url);
const axePath = require.resolve("axe-core/axe.min.js");

interface AxeViolation {
  id: string;
  impact: string | null;
  help: string;
  nodes: { target: string[]; html: string }[];
}

for (const theme of ["dark", "light"] as const) {
  test.describe(`a11y ${theme}`, () => {
    test.use({ viewport: { width: 1280, height: 800 } });
    for (const state of STATES) {
      // Mid-animation captures and the mock-only toast pair are not stable audit targets.
      if (state.live) continue;
      test(state.name, async ({ page }) => {
        await page.goto(`/?${state.query}&theme=${theme}&backdrop=solid&dev=0`);
        await page.waitForSelector("html[data-ready='1']");
        await state.setup?.(page);
        await page.waitForTimeout(state.settle ?? 350);
        await page.addScriptTag({ path: axePath });
        const violations = await page.evaluate(async () => {
          const axe = (window as unknown as { axe: { run: (ctx: unknown, opts: unknown) => Promise<{ violations: AxeViolation[] }> } }).axe;
          const res = await axe.run(document, { runOnly: { type: "tag", values: ["wcag2a", "wcag2aa", "wcag21aa", "best-practice"] } });
          return res.violations.map((v) => ({
            id: v.id,
            impact: v.impact,
            help: v.help,
            nodes: v.nodes.slice(0, 3).map((n) => ({ target: n.target, html: n.html.slice(0, 140) })),
          }));
        });
        if (process.env.A11Y_LOG) {
          mkdirSync("test-results", { recursive: true });
          appendFileSync(process.env.A11Y_LOG, `${JSON.stringify({ theme, state: state.name, violations })}\n`);
        }
        // Radix marks everything outside an open menu/dialog aria-hidden (hideOthers) while focus is
        // trapped inside it; axe flags the still-focusable background as a violation. That is the
        // intended modal pattern, not a defect.
        const blocking = violations
          .filter((v) => v.impact === "serious" || v.impact === "critical")
          .filter((v) => !(v.id === "aria-hidden-focus" && v.nodes.every((n) => n.html.includes("data-aria-hidden"))));
        expect(blocking, JSON.stringify(blocking, null, 2)).toEqual([]);
      });
    }
  });
}
