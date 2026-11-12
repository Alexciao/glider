// Contact sheets for design review: tiles a set of screenshots into one labelled image.
//
//   node e2e/tools/sheet.mjs <out.png> <cols> <tileWidth> <img>[=label] [<img>[=label] …]
//   node e2e/tools/sheet.mjs --dir <variantDir> <outDir> [cols=2] [tileWidth=780] [filter]
//
// The --dir form writes <outDir>/<variant>-NN.png, one sheet per cols×2 tiles, in file-name order.
import { chromium } from "@playwright/test";
import { mkdirSync, readFileSync, readdirSync } from "node:fs";
import { basename, join, resolve } from "node:path";

const args = process.argv.slice(2);

async function sheet(browser, out, cols, tile, items) {
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  const html = `<!doctype html><style>
    body{margin:0;background:#0b0b0c;font:12px/1.2 system-ui;color:#c9c9cf}
    .g{display:grid;grid-template-columns:repeat(${cols},${tile}px);gap:10px;padding:10px;width:max-content}
    figure{margin:0} img{display:block;width:${tile}px;height:auto;outline:1px solid #2a2a2e}
    figcaption{padding:4px 2px 0;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
  </style><div class="g">${items
    .map(({ file, label }) => `<figure><img src="data:image/png;base64,${readFileSync(resolve(file)).toString("base64")}"><figcaption>${label}</figcaption></figure>`)
    .join("")}</div>`;
  await page.setContent(html, { waitUntil: "load" });
  await page.locator(".g").screenshot({ path: out });
  await page.close();
}

const browser = await chromium.launch();
try {
  if (args[0] === "--dir") {
    const [, dir, outDir, colsArg = "2", tileArg = "780", filter = ""] = args;
    const cols = Number(colsArg);
    const tile = Number(tileArg);
    const per = cols * 2;
    mkdirSync(outDir, { recursive: true });
    const files = readdirSync(dir)
      .filter((f) => f.endsWith(".png") && (!filter || new RegExp(filter).test(f)))
      .sort();
    for (let i = 0; i < files.length; i += per) {
      const items = files.slice(i, i + per).map((f) => ({ file: join(dir, f), label: f.replace(/\.png$/, "") }));
      const out = join(outDir, `${basename(dir)}-${String(i / per + 1).padStart(2, "0")}.png`);
      await sheet(browser, out, cols, tile, items);
      console.log(out);
    }
  } else {
    const [out, cols, tile, ...rest] = args;
    const items = rest.map((a) => {
      const [file, label] = a.split("=");
      return { file, label: label ?? basename(file).replace(/\.png$/, "") };
    });
    await sheet(browser, out, Number(cols), Number(tile), items);
    console.log(out);
  }
} finally {
  await browser.close();
}
