// Wordmark = mark + "Glider" set in Inter SemiBold, converted to outlines at
// build time (opentype.js). The resulting SVG has no font dependency.
// Inter is licensed under the SIL OFL 1.1 (outlines may be embedded freely).

import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { palette as C } from './color.mjs';
import { markShapes } from './mark.mjs';

const require = createRequire(import.meta.url);
const opentype = require('opentype.js');

const FONT_PATH = require.resolve('@fontsource/inter/files/inter-latin-600-normal.woff');

function loadFont() {
  const b = readFileSync(FONT_PATH);
  return opentype.parse(b.buffer.slice(b.byteOffset, b.byteOffset + b.byteLength));
}

/**
 * @param {'dark'|'light'} tone surface the wordmark sits on
 */
export function wordmarkSvg(tone = 'dark') {
  const font = loadFont();
  const text = 'Glider';
  const fontSize = 40; // px in the 64-unit mark system: cap height ~ 29
  const tracking = -0.015; // em, slight optical tightening for a display size
  const upm = font.unitsPerEm;

  // Lay out glyphs manually so tracking + kerning are explicit and reproducible.
  const glyphs = font.stringToGlyphs(text);
  let x = 0;
  let d = '';
  glyphs.forEach((g, i) => {
    const p = g.getPath(x, 0, fontSize);
    d += p.toPathData(3);
    const adv = (g.advanceWidth / upm) * fontSize;
    const next = glyphs[i + 1];
    const kern = next ? (font.getKerningValue(g, next) / upm) * fontSize : 0;
    x += adv + kern + tracking * fontSize;
  });
  const textWidth = x - tracking * fontSize;
  const capH = (font.tables.os2.sCapHeight / upm) * fontSize;

  const shapes = markShapes({ size: 52 });
  // mark is 64 units tall; scale so its live area visually matches ~1.5x cap height
  const markScale = (capH * 1.9) / 52;
  const markW = 64 * markScale;
  const gap = capH * 0.55;
  const padY = 6;
  const markH = 64 * markScale;
  const height = Math.ceil(markH + padY * 2);
  const baseline = height / 2 + capH / 2;
  const width = Math.ceil(markW + gap + textWidth + 4);

  const wing = tone === 'dark' ? C.paper : C.graphite;
  const flap = tone === 'dark' ? C.glide : C.glideLo;
  const f = (n) => +n.toFixed(3);

  return (
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${width} ${height}" width="${width * 4}" height="${height * 4}" fill="none" role="img">` +
    `<title>Glider</title>` +
    `<g transform="translate(0 ${f((height - markH) / 2)}) scale(${f(markScale)})">` +
    `<path d="${shapes[0].d}" fill="${wing}"/><path d="${shapes[1].d}" fill="${flap}"/></g>` +
    `<path transform="translate(${f(markW + gap)} ${f(baseline)})" d="${d}" fill="${wing}"/>` +
    '</svg>\n'
  );
}
