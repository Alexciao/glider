import type { GameArt } from "../contract.gen";
import { mulberry32 } from "./sim";

/**
 * Original, procedural key art for the mock: a dusk-to-dawn sky over low-poly ridgelines.
 * No third-party imagery, no text. Delivered as SVG data URIs so the mock needs no assets.
 */

type Rand = () => number;

/** Midpoint-displacement ridge: returns n+1 heights in 0..1. */
function ridge(rand: Rand, n: number, roughness: number): number[] {
  const ys = new Array<number>(n + 1).fill(0);
  ys[0] = rand() * 0.5;
  ys[n] = rand() * 0.5;
  let step = n;
  let disp = 1;
  while (step > 1) {
    const half = step / 2;
    for (let i = half; i < n; i += step) ys[i] = ((ys[i - half] ?? 0) + (ys[i + half] ?? 0)) / 2 + (rand() - 0.5) * disp;
    disp *= roughness;
    step = half;
  }
  const lo = Math.min(...ys);
  const hi = Math.max(...ys);
  return ys.map((y) => (y - lo) / (hi - lo || 1));
}

function layer(rand: Rand, w: number, h: number, baseY: number, amp: number, id: string, facet: number): string {
  const n = 16;
  const ys = ridge(rand, n, 0.58);
  const pts = ys.map((y, i) => [Math.round((i * w) / n), Math.round(baseY - y * amp)] as const);
  const outline = pts.map(([x, y]) => `${x},${y}`).join(" ");
  let facets = "";
  for (let i = 1; i < n; i++) {
    const p = pts[i]!;
    const l = pts[i - 1]!;
    const r = pts[i + 1]!;
    if (p[1] < l[1] && p[1] < r[1]) {
      // Lit slope on the sun side of every peak.
      const mx = Math.round(p[0] + (r[0] - p[0]) * 0.55);
      const my = Math.round(r[1] + (p[1] - r[1]) * 0.2);
      facets += `<path d="M${p[0]},${p[1]}L${r[0]},${r[1]}L${mx},${my}Z"/>`;
    }
  }
  return (
    `<polygon fill="url(#${id})" points="0,${h} ${outline} ${w},${h}"/>` +
    `<g fill="#ffb59a" opacity="${facet}">${facets}</g>`
  );
}

function stars(rand: Rand, w: number, limitY: number, count: number): string {
  let out = "";
  for (let i = 0; i < count; i++) {
    const x = Math.round(rand() * w);
    const y = Math.round(Math.pow(rand(), 1.6) * limitY);
    const r = (0.7 + rand() * 1.3).toFixed(1);
    const o = (0.25 + rand() * 0.6).toFixed(2);
    out += `<circle cx="${x}" cy="${y}" r="${r}" opacity="${o}"/>`;
  }
  return `<g fill="#fff">${out}</g>`;
}

function clouds(rand: Rand, w: number, y: number, spread: number, count: number, fill: string, opacity: number): string {
  let out = "";
  for (let i = 0; i < count; i++) {
    const cx = Math.round(((i + rand() * 0.8) / count) * w);
    const cy = Math.round(y + (rand() - 0.5) * spread);
    const rx = Math.round(w * (0.1 + rand() * 0.1));
    const ry = Math.round(w * (0.012 + rand() * 0.014));
    out += `<ellipse cx="${cx}" cy="${cy}" rx="${rx}" ry="${ry}"/>`;
  }
  return `<g fill="${fill}" opacity="${opacity}" filter="url(#b)">${out}</g>`;
}

function scene(w: number, h: number, seed: number, horizon: number, sunX: number): string {
  const rand = mulberry32(seed);
  const hy = Math.round(h * horizon);
  const sunY = Math.round(hy - h * 0.045);
  const unit = Math.min(w, h);
  return (
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${w} ${h}" preserveAspectRatio="xMidYMid slice">` +
    `<defs>` +
    `<linearGradient id="s" x1="0" y1="0" x2="0" y2="1">` +
    `<stop offset="0" stop-color="#0b0a26"/><stop offset=".38" stop-color="#241a5e"/>` +
    `<stop offset=".62" stop-color="#5f3a9c"/><stop offset=".82" stop-color="#cf5f86"/>` +
    `<stop offset="1" stop-color="#ff9d70"/></linearGradient>` +
    `<radialGradient id="g" cx="${sunX}" cy="${horizon - 0.05}" r=".55" gradientUnits="objectBoundingBox">` +
    `<stop offset="0" stop-color="#ffc08a" stop-opacity=".85"/><stop offset=".35" stop-color="#ff8a78" stop-opacity=".35"/>` +
    `<stop offset="1" stop-color="#ff8a78" stop-opacity="0"/></radialGradient>` +
    `<linearGradient id="m1" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#8a5aa8"/><stop offset="1" stop-color="#3a2a78"/></linearGradient>` +
    `<linearGradient id="m2" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#41307f"/><stop offset="1" stop-color="#1c1650"/></linearGradient>` +
    `<linearGradient id="m3" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#1b1548"/><stop offset="1" stop-color="#0a0820"/></linearGradient>` +
    `<linearGradient id="hz" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ff9d70" stop-opacity="0"/><stop offset="1" stop-color="#ff9d70" stop-opacity=".38"/></linearGradient>` +
    `<filter id="b" x="-10%" y="-80%" width="120%" height="260%"><feGaussianBlur stdDeviation="${Math.round(unit * 0.014)}"/></filter>` +
    `</defs>` +
    `<rect width="${w}" height="${h}" fill="url(#s)"/>` +
    stars(rand, w, Math.round(h * 0.45), Math.round(unit / 28)) +
    `<rect width="${w}" height="${h}" fill="url(#g)"/>` +
    `<circle cx="${Math.round(sunX * w)}" cy="${sunY}" r="${Math.round(unit * 0.034)}" fill="#ffd9b0" opacity=".92"/>` +
    clouds(rand, w, Math.round(h * (horizon - 0.2)), h * 0.05, 5, "#b98ad0", 0.28) +
    clouds(rand, w, Math.round(h * (horizon - 0.1)), h * 0.04, 6, "#ff9f90", 0.34) +
    layer(rand, w, h, Math.round(hy - h * 0.02), Math.round(h * 0.16), "m1", 0.1) +
    `<rect y="${Math.round(hy - h * 0.12)}" width="${w}" height="${Math.round(h * 0.2)}" fill="url(#hz)"/>` +
    layer(rand, w, h, Math.round(hy + h * 0.07), Math.round(h * 0.19), "m2", 0.07) +
    clouds(rand, w, Math.round(hy + h * 0.02), h * 0.03, 5, "#ffb4a0", 0.2) +
    layer(rand, w, h, Math.round(hy + h * 0.2), Math.round(h * 0.17), "m3", 0.04) +
    `</svg>`
  );
}

const toDataUri = (svg: string): string => `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;

/** 16:9, 1920×1080. */
export const heroArt: string = toDataUri(scene(1920, 1080, 4217, 0.66, 0.64));
/** 3:4, 1200×1600. */
export const tallArt: string = toDataUri(scene(1200, 1600, 9031, 0.72, 0.5));

export const GAME_ART: GameArt = { heroUrl: heroArt, tallUrl: tallArt, logoUrl: null };
