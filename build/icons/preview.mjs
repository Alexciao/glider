// Contact sheet: every size on dark + light surfaces, tray icons on taskbar
// strips, a pixel-magnified row of the small sizes and the lockups.
// Frames are read back out of the .ico files so the sheet shows what Windows
// will actually draw (BMP frames included). Run after generate.mjs.
//   pnpm preview

import { Resvg } from '@resvg/resvg-js';
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { PNG } from 'pngjs';

const brand = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'assets', 'brand');
const uri = (buf) => 'data:image/png;base64,' + buf.toString('base64');
const FONT = 'Segoe UI, Inter, Arial, sans-serif';
const W = 1240;
const panelH = 470;

function icoFrames(file) {
  const b = readFileSync(join(brand, file));
  const n = b.readUInt16LE(4);
  const out = [];
  for (let i = 0; i < n; i++) {
    const o = 6 + i * 16;
    const w = b[o] || 256;
    const len = b.readUInt32LE(o + 8);
    const off = b.readUInt32LE(o + 12);
    const data = b.subarray(off, off + len);
    let img;
    if (data.subarray(1, 4).toString() === 'PNG') {
      img = PNG.sync.read(data);
    } else {
      img = new PNG({ width: w, height: w });
      for (let y = 0; y < w; y++)
        for (let x = 0; x < w; x++) {
          const s = 40 + ((w - 1 - y) * w + x) * 4; // skip BITMAPINFOHEADER, bottom-up BGRA
          const d = (y * w + x) * 4;
          img.data[d] = data[s + 2];
          img.data[d + 1] = data[s + 1];
          img.data[d + 2] = data[s];
          img.data[d + 3] = data[s + 3];
        }
    }
    out.push({ w, buf: PNG.sync.write(img), storage: data.subarray(1, 4).toString() === 'PNG' ? 'png' : 'bmp' });
  }
  return out;
}

const appFrames = icoFrames('glider.ico');
const trayFrames = icoFrames('tray.ico');
const pngFile = (n) => readFileSync(join(brand, 'png', n));

const text = (x, y, s, fill, size = 12, weight = 400) =>
  `<text x="${x}" y="${y}" font-family="${FONT}" font-size="${size}" font-weight="${weight}" fill="${fill}">${s}</text>`;

function panel(y, { bg, fg, sub, taskbar, label }) {
  let s = `<rect x="0" y="${y}" width="${W}" height="${panelH}" fill="${bg}"/>`;
  s += text(32, y + 34, label, fg, 15, 600);

  const rowY = y + 80;
  s += text(32, rowY - 14, 'App icon, 1x. Frames read from glider.ico: 16 20 24 32 40 48 64, then PNG 96 / 128', sub);
  let x = 32;
  for (const fr of appFrames.filter((f) => f.w <= 64)) {
    s += `<image x="${x}" y="${rowY}" width="${fr.w}" height="${fr.w}" href="${uri(fr.buf)}"/>`;
    x += fr.w + 20;
  }
  for (const sz of [96, 128]) {
    s += `<image x="${x}" y="${rowY}" width="${sz}" height="${sz}" href="${uri(pngFile(`glider-${sz}.png`))}"/>`;
    x += sz + 20;
  }

  // tray on a taskbar-coloured strip
  const tx = 820;
  const stripW = trayFrames.reduce((a, f) => a + f.w + 18, 14);
  s += text(tx, rowY - 14, 'Tray, 1x on a taskbar strip: 16 20 24 32 48', sub);
  s += `<rect x="${tx}" y="${rowY - 4}" width="${stripW}" height="64" rx="8" fill="${taskbar}"/>`;
  let ttx = tx + 14;
  for (const fr of trayFrames) {
    s += `<image x="${ttx}" y="${rowY + 28 - fr.w / 2}" width="${fr.w}" height="${fr.w}" href="${uri(fr.buf)}"/>`;
    ttx += fr.w + 18;
  }

  // magnified, nearest-neighbour
  const mY = y + 245;
  s += text(32, mY - 12, 'Magnified, nearest-neighbour: app 16 (10x), 24 (7x), 32 (5x); tray 16 (10x), 24 (7x)', sub);
  let mx = 32;
  const mags = [[appFrames[0], 10], [appFrames[2], 7], [appFrames[3], 5], [trayFrames[0], 10], [trayFrames[2], 7]];
  for (const [fr, k] of mags) {
    s += `<image x="${mx}" y="${mY}" width="${fr.w * k}" height="${fr.w * k}" image-rendering="optimizeSpeed" href="${uri(fr.buf)}"/>`;
    mx += fr.w * k + 28;
  }
  return s;
}

const svgFile = (n) => readFileSync(join(brand, n), 'utf8');
const inner = (svg) => svg.replace(/^<svg[^>]*>/, '').replace(/<\/svg>\s*$/, '');
const vbH = (svg) => Number(svg.match(/viewBox="0 0 [\d.]+ ([\d.]+)"/)[1]);
const wordmark = (svg, x, y, h) => `<g transform="translate(${x} ${y}) scale(${h / vbH(svg)})">${inner(svg)}</g>`;
const mark = (svg, x, y, size) => `<g transform="translate(${x} ${y}) scale(${size / 64})">${inner(svg)}</g>`;

const dark = '#0d0e11';
const light = '#f3f4f6';
let body = '';
body += panel(0, { bg: dark, fg: '#f3f4f6', sub: '#8b909a', taskbar: '#202020', label: 'Dark surface' });
body += panel(panelH, { bg: light, fg: '#1a1c20', sub: '#5b606a', taskbar: '#eef0f4', label: 'Light surface' });

const ly = panelH * 2;
body += `<rect x="0" y="${ly}" width="${W / 2}" height="210" fill="${dark}"/><rect x="${W / 2}" y="${ly}" width="${W / 2}" height="210" fill="${light}"/>`;
for (const [ox, tone, mk, wm] of [
  [0, 'dark', 'glider-mark.svg', 'glider-wordmark.svg'],
  [W / 2, 'light', 'glider-mark-onlight.svg', 'glider-wordmark-onlight.svg'],
]) {
  const m = svgFile(mk);
  body += mark(m, ox + 32, ly + 36, 96) + wordmark(svgFile(wm), ox + 170, ly + 52, 64);
  body += mark(m, ox + 32, ly + 160, 32) + mark(m, ox + 80, ly + 164, 24) + mark(m, ox + 120, ly + 168, 16);
  body += text(ox + 170, ly + 190, `Mark + wordmark on ${tone} surfaces, mark at 32 / 24 / 16`, tone === 'dark' ? '#8b909a' : '#5b606a');
}

const H = panelH * 2 + 210;
const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}" viewBox="0 0 ${W} ${H}">${body}</svg>`;
writeFileSync(
  join(brand, 'preview.png'),
  new Resvg(svg, { fitTo: { mode: 'original' }, font: { loadSystemFonts: true } }).render().asPng(),
);
console.log('preview.png written');
