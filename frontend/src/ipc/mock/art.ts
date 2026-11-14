import type { GameArt } from "../contract.gen";
import { hashString, mulberry32, type Rand } from "./rng";

/**
 * Original, procedural key art for the mock: a dusk-to-dawn sky over low-poly ridgelines.
 * No third-party imagery, no text. Delivered as SVG data URIs so the mock needs no assets.
 */



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


/** Fortnite is the user's real v0.1 game: it keeps the original procedural dusk scene (no third-party imagery). */
export const FORTNITE_ART: GameArt = { tall: tallArt, tallThumb: tallArt, wide: heroArt, logo: null, placeholder: "#2a1d5e" };

/* ------------------------------------------------------------------ generated art for the rest of the library */

const hsl = (h: number, s: number, l: number): string => `hsl(${Math.round(h) % 360} ${Math.round(s)}% ${Math.round(l)}%)`;

interface Palette {
  a: string;
  b: string;
  c: string;
  accent: string;
  /** A flat colour close to the average, for blur-up placeholders. */
  flat: string;
  pattern: number;
}

function palette(rand: Rand): Palette {
  const h = rand() * 360;
  const spread = 30 + rand() * 90;
  const dark = rand() < 0.7;
  return {
    a: hsl(h, 55 + rand() * 25, dark ? 14 + rand() * 8 : 62 + rand() * 12),
    b: hsl(h + spread, 55 + rand() * 25, dark ? 28 + rand() * 14 : 48 + rand() * 12),
    c: hsl(h - spread * 0.6, 50 + rand() * 30, dark ? 40 + rand() * 18 : 36 + rand() * 12),
    accent: hsl(h + 180 + rand() * 40, 70 + rand() * 20, 62 + rand() * 14),
    flat: hsl(h + spread * 0.4, 45, dark ? 22 : 54),
    pattern: Math.floor(rand() * 6),
  };
}

function shapes(rand: Rand, w: number, h: number, p: Palette, detail: number): string {
  const unit = Math.min(w, h);
  let out = "";
  switch (p.pattern) {
    case 0: // concentric rings around an off-centre sun
      for (let i = 0; i < 5 * detail; i++) {
        out += `<circle cx="${Math.round(w * 0.62)}" cy="${Math.round(h * 0.36)}" r="${Math.round(unit * (0.12 + i * 0.1))}" fill="none" stroke="${p.accent}" stroke-opacity="${(0.55 - i * 0.07).toFixed(2)}" stroke-width="${Math.max(1, Math.round(unit * 0.012))}"/>`;
      }
      break;
    case 1: // diagonal bands
      for (let i = 0; i < 6 * detail; i++) {
        const x = Math.round(rand() * w * 1.4 - w * 0.2);
        out += `<polygon points="${x},0 ${x + Math.round(unit * 0.16)},0 ${x - Math.round(h * 0.4)},${h} ${x - Math.round(h * 0.4) - Math.round(unit * 0.16)},${h}" fill="${i % 2 ? p.c : p.accent}" opacity="${(0.12 + rand() * 0.2).toFixed(2)}"/>`;
      }
      break;
    case 2: // low ridgelines
      for (let l = 0; l < 3; l++) {
        const base = h * (0.55 + l * 0.14);
        let pts = `0,${h}`;
        for (let i = 0; i <= 8 * detail; i++) pts += ` ${Math.round((i * w) / (8 * detail))},${Math.round(base - rand() * h * 0.2)}`;
        out += `<polygon points="${pts} ${w},${h}" fill="${l === 0 ? p.c : l === 1 ? p.b : p.a}" opacity="${(0.55 + l * 0.2).toFixed(2)}"/>`;
      }
      break;
    case 3: // scattered orbs
      for (let i = 0; i < 9 * detail; i++) {
        out += `<circle cx="${Math.round(rand() * w)}" cy="${Math.round(rand() * h)}" r="${Math.round(unit * (0.03 + rand() * 0.14))}" fill="${i % 3 ? p.c : p.accent}" opacity="${(0.15 + rand() * 0.35).toFixed(2)}"/>`;
      }
      break;
    case 4: // diamond lattice
      for (let i = 0; i < 6 * detail; i++) {
        const cx = Math.round(rand() * w);
        const cy = Math.round(rand() * h);
        const r = Math.round(unit * (0.06 + rand() * 0.16));
        out += `<polygon points="${cx},${cy - r} ${cx + r},${cy} ${cx},${cy + r} ${cx - r},${cy}" fill="${i % 2 ? p.accent : p.c}" opacity="${(0.18 + rand() * 0.3).toFixed(2)}"/>`;
      }
      break;
    default: // horizon stripes
      for (let i = 0; i < 7 * detail; i++) {
        out += `<rect x="0" y="${Math.round(h * (0.3 + i * (0.6 / (7 * detail))))}" width="${w}" height="${Math.max(1, Math.round(unit * 0.02 * (1 + i / 4)))}" fill="${p.accent}" opacity="${(0.1 + i * 0.04).toFixed(2)}"/>`;
      }
  }
  return out;
}

function svgArt(p: Palette, w: number, h: number, seed: number, detail: number): string {
  const rand = mulberry32(seed);
  const svg =
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${w} ${h}" preserveAspectRatio="xMidYMid slice">` +
    `<defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="${p.a}"/><stop offset=".6" stop-color="${p.b}"/><stop offset="1" stop-color="${p.a}"/></linearGradient></defs>` +
    `<rect width="${w}" height="${h}" fill="url(#g)"/>${shapes(rand, w, h, p, detail)}</svg>`;
  return toDataUri(svg);
}

const artCache = new Map<string, GameArt>();

/**
 * Deterministic, original art for a mock game: a gradient plus a seeded pattern, in a tall 3:4 box, a wide 16:9 hero,
 * a tiny thumbnail and a flat placeholder colour. No text, no third-party imagery.
 */
export function generatedArt(key: string, opts: { tall?: boolean; wide?: boolean } = {}): GameArt {
  const cacheKey = `${key}|${opts.tall === false ? 0 : 1}${opts.wide === false ? 0 : 1}`;
  const hit = artCache.get(cacheKey);
  if (hit) return hit;
  const seed = hashString(key);
  const p = palette(mulberry32(seed));
  const art: GameArt = {
    tall: opts.tall === false ? null : svgArt(p, 360, 480, seed ^ 0x51ed, 2),
    tallThumb: opts.tall === false ? null : svgArt(p, 90, 120, seed ^ 0x51ed, 1),
    wide: opts.wide === false ? null : svgArt(p, 640, 360, seed ^ 0xa11ce, 2),
    logo: null,
    placeholder: p.flat,
  };
  artCache.set(cacheKey, art);
  return art;
}

/** Wide image for a free-game promo / store tile. */
export const wideArt = (key: string): string => generatedArt(key).wide ?? "";
