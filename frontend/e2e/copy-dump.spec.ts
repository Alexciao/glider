import { mkdirSync, writeFileSync } from "node:fs";
import { test } from "@playwright/test";
import { STATES } from "./states";

/**
 * Copy audit aid: dumps every visible string of every state (text lines plus aria-label / title / placeholder) into one
 * text file per state, so wording, capitalization and repeated facts can be reviewed without reading pixels.
 * Runs only when COPY_OUT=/abs/dir is set:  COPY_OUT=… pnpm exec playwright test e2e/copy-dump.spec.ts
 */
const OUT = process.env.COPY_OUT;

test.describe("copy dump", () => {
  test.skip(!OUT, "set COPY_OUT to write the copy dump");
  test.use({ viewport: { width: 1280, height: 800 } });
  for (const state of STATES) {
    test(state.name, async ({ page }) => {
      mkdirSync(OUT!, { recursive: true });
      await page.goto(`/?${state.query}&theme=dark&backdrop=solid&dev=0`);
      if (state.early) await page.waitForSelector("#root *");
      else await page.waitForSelector("html[data-ready='1']", { timeout: 20_000 });
      await state.setup?.(page);
      await page.waitForTimeout(state.settle ?? 350);
      const text = await page.evaluate(() => {
        const out: string[] = [];
        const root = document.body;
        const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
        let n: Node | null;
        let line = "";
        let lastBlock: Element | null = null;
        while ((n = walker.nextNode())) {
          const t = n.textContent?.replace(/\s+/g, " ").trim();
          if (!t) continue;
          const el = n.parentElement;
          if (!el || el.closest("script,style")) continue;
          const block = el.closest("p,h1,h2,h3,li,button,label,div,span,a,td,th");
          if (block !== lastBlock) {
            if (line) out.push(line);
            line = t;
            lastBlock = block;
          } else line += ` ${t}`;
        }
        if (line) out.push(line);
        const attrs: string[] = [];
        for (const el of document.querySelectorAll("[aria-label],[title],[placeholder]")) {
          for (const a of ["aria-label", "title", "placeholder"]) {
            const v = el.getAttribute(a);
            if (v) attrs.push(`@${a}: ${v}`);
          }
        }
        return `${out.join("\n")}\n--- attributes\n${[...new Set(attrs)].join("\n")}`;
      });
      writeFileSync(`${OUT}/${state.name}.txt`, text);
    });
  }
});
