// Glider brand asset generator.
//   pnpm install && pnpm build
// Writes SVG sources, PNG rasters, glider.ico and tray.ico into assets/brand/.

import { Resvg } from '@resvg/resvg-js';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildIco } from './lib/ico.mjs';
import { appIconSvg, markSvg, smallIconSvg } from './lib/svg.mjs';
import { wordmarkSvg } from './lib/wordmark.mjs';

const here = dirname(fileURLToPath(import.meta.url));
export const brandDir = join(here, '..', '..', 'assets', 'brand');
const pngDir = join(brandDir, 'png');
mkdirSync(pngDir, { recursive: true });

const write = (name, data) => writeFileSync(join(brandDir, name), data);

export const render = (svg, size) =>
  new Resvg(svg, { fitTo: { mode: 'width', value: size } }).render().asPng();

// ---- vector sources -------------------------------------------------------
const appSvg = appIconSvg();
const smallSvg = smallIconSvg();
const traySvg = appIconSvg({ variant: 'tray', markSize: 46 });
const traySmallSvg = smallIconSvg({ variant: 'tray' });

write('glider-mark.svg', markSvg('dark'));
write('glider-mark-onlight.svg', markSvg('light'));
write('glider-mark-mono.svg', markSvg('mono'));
write('glider-app-icon.svg', appSvg);
write('glider-icon-16.svg', smallSvg);
write('glider-tray.svg', traySvg);
write('glider-tray-16.svg', traySmallSvg);
write('glider-wordmark.svg', wordmarkSvg('dark'));
write('glider-wordmark-onlight.svg', wordmarkSvg('light'));

// ---- rasters --------------------------------------------------------------
// <= 24 px uses the small-size optimised drawing; larger sizes the full one.
const SMALL_MAX = 24;
const pick = (size, big, small) => (size <= SMALL_MAX ? small : big);

const PNG_SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256, 512];
for (const s of PNG_SIZES) {
  writeFileSync(join(pngDir, `glider-${s}.png`), render(pick(s, appSvg, smallSvg), s));
}

const frames = (sizes, big, small) =>
  sizes.map((size) => ({ size, png: render(pick(size, big, small), size) }));

write('glider.ico', buildIco(frames([16, 20, 24, 32, 40, 48, 64, 256], appSvg, smallSvg)));
write('tray.ico', buildIco(frames([16, 20, 24, 32, 48], traySvg, traySmallSvg)));

console.log('Glider brand assets written to', brandDir);
