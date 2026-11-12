// SVG document builders for every brand asset. All outputs are pure vector
// (fills + gradients), with no fonts, filters, masks or strokes in the mark.

import { palette as C } from './color.mjs';
import { markShapes } from './mark.mjs';

const head = (viewBox, extra = '') =>
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="${viewBox}" fill="none"${extra}>`;

const place = (size, cx, cy, baseSize = 52) => {
  const s = size / baseSize;
  return `translate(${cx} ${cy}) scale(${s}) translate(-32 -32)`;
};

/** Symbol on a 64-unit square. `tone` picks the colour set. */
export function markSvg(tone = 'dark') {
  const [wing, flap] = markShapes({ size: 52 });
  const fills =
    tone === 'dark'
      ? { wing: C.paper, flap: C.glide }
      : tone === 'light'
        ? { wing: C.graphite, flap: C.glideLo }
        : { wing: 'currentColor', flap: 'currentColor' };
  const title =
    tone === 'mono' ? 'Glider mark (currentColor)' : `Glider mark (${tone === 'dark' ? 'for dark surfaces' : 'for light surfaces'})`;
  return (
    head('0 0 64 64', ' role="img"') +
    `<title>${title}</title>` +
    `<path d="${wing.d}" fill="${fills.wing}"/>` +
    `<path d="${flap.d}" fill="${fills.flap}"/>` +
    '</svg>\n'
  );
}

/**
 * Windows app icon: the mark on a rounded graphite tile. 256 unit viewBox.
 * `variant: 'tray'` keeps the tile and adds a glide-blue rim so the edge reads
 * on both dark and light taskbars.
 */
export function appIconSvg({ variant = 'app', markSize = 45, shapes } = {}) {
  const [wing, flap] = shapes ?? markShapes({ size: 52 });
  const tray = variant === 'tray';
  const r = tray ? 60 : 58;
  const t = place(markSize * 4, 128, 128);
  return (
    head('0 0 256 256', ' role="img"') +
    '<title>Glider</title>' +
    '<defs>' +
    `<linearGradient id="tile" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${C.graphiteHi}"/><stop offset="1" stop-color="${C.graphiteLo}"/></linearGradient>` +
    `<radialGradient id="glow" cx="0.72" cy="0.78" r="0.7"><stop offset="0" stop-color="${C.glide}" stop-opacity="${tray ? 0.18 : 0.2}"/><stop offset="1" stop-color="${C.glide}" stop-opacity="0"/></radialGradient>` +
    `<linearGradient id="rim" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ffffff" stop-opacity="0.18"/><stop offset="0.5" stop-color="#ffffff" stop-opacity="0.04"/><stop offset="1" stop-color="#ffffff" stop-opacity="0.08"/></linearGradient>` +
    `<linearGradient id="wing" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ffffff"/><stop offset="1" stop-color="#d9dee8"/></linearGradient>` +
    `<linearGradient id="flap" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="${C.glideHi}"/><stop offset="1" stop-color="${C.glideLo}"/></linearGradient>` +
    '</defs>' +
    `<rect width="256" height="256" rx="${r}" fill="url(#tile)"/>` +
    `<rect width="256" height="256" rx="${r}" fill="url(#glow)"/>` +
    `<rect x="1.5" y="1.5" width="253" height="253" rx="${r - 1.5}" stroke="${tray ? C.glide : 'url(#rim)'}" stroke-opacity="${tray ? 0.55 : 1}" stroke-width="${tray ? 6 : 3}"/>` +
    `<g transform="${t}">` +
    `<path d="${wing.d}" fill="url(#wing)"/>` +
    `<path d="${flap.d}" fill="url(#flap)"/>` +
    '</g></svg>\n'
  );
}

/**
 * Small-size icon (16-24 px): flat fills, no glow, a wider wing/flap gap, a
 * bigger mark relative to the tile and tighter fillets so edges stay crisp.
 */
export function smallIconSvg({ variant = 'app' } = {}) {
  const tray = variant === 'tray';
  const [wing, flap] = markShapes({ size: 52, round: 0.6, gap: 7.5, flapBase: 18, flapStart: 0.24, bank: 10, tip: 3.8 });
  const live = tray ? 13.2 : 14;
  const s = +(live / 52).toFixed(4);
  return (
    head('0 0 16 16', ' role="img"') +
    '<title>Glider (small)</title>' +
    `<rect width="16" height="16" rx="${tray ? 3.6 : 3.4}" fill="${C.graphite}"/>` +
    (tray
      ? `<rect x="0.5" y="0.5" width="15" height="15" rx="3.1" stroke="${C.glide}" stroke-opacity="0.5"/>`
      : '') +
    `<g transform="translate(8 8) scale(${s}) translate(-32 -32)">` +
    `<path d="${wing.d}" fill="${C.paper}"/><path d="${flap.d}" fill="${C.glide}"/></g></svg>\n`
  );
}
