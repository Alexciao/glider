import type { GameArt } from "../contract.gen";
import { mulberry32 } from "./sim";

/**
 * Worst-case key art for the hero (`?art=busy|bright|white`). Real catalog art is arbitrary: loud,
 * saturated, high-key, with lettering baked in exactly where a launcher puts its own title. These are
 * original, procedural stand-ins for that (no third-party imagery), built to defeat a naive scrim:
 *
 *   busy   – saturated shards, hard black/white edges and a fake lockup in the lower-left corner
 *   bright – a high-key daylight scene, near-white where the text block sits
 *   white  – a flat #fff image: the theoretical floor for text contrast
 */
export type StressArt = "busy" | "bright" | "white";

const W = 1920;
const H = 1080;
const uri = (svg: string): string => `data:image/svg+xml;charset=utf-8,${encodeURIComponent(svg)}`;
const svg = (body: string): string => `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${W} ${H}" preserveAspectRatio="xMidYMid slice">${body}</svg>`;

function busy(): string {
  const rand = mulberry32(77031);
  const hues = ["#ffe600", "#ff2e93", "#00e5ff", "#7cff3a", "#ff7a00", "#9b4dff", "#ffffff", "#111111"];
  let shards = "";
  for (let i = 0; i < 150; i++) {
    const cx = rand() * W;
    const cy = rand() * H;
    const r = 60 + rand() * 260;
    const a = rand() * Math.PI * 2;
    const pts = [0, 1, 2]
      .map((k) => {
        const t = a + (k * Math.PI * 2) / 3 + (rand() - 0.5);
        return `${Math.round(cx + Math.cos(t) * r)},${Math.round(cy + Math.sin(t) * r * (0.4 + rand()))}`;
      })
      .join(" ");
    shards += `<polygon points="${pts}" fill="${hues[i % hues.length]}" opacity="${(0.72 + rand() * 0.28).toFixed(2)}"/>`;
  }
  let rays = "";
  for (let i = 0; i < 28; i++) {
    const t0 = (i / 28) * Math.PI * 2;
    const t1 = t0 + Math.PI / 56;
    rays += `<path d="M1240,400L${Math.round(1240 + Math.cos(t0) * 2400)},${Math.round(400 + Math.sin(t0) * 2400)}L${Math.round(1240 + Math.cos(t1) * 2400)},${Math.round(400 + Math.sin(t1) * 2400)}Z"/>`;
  }
  let dots = "";
  for (let y = 0; y < 14; y++) for (let x = 0; x < 22; x++) dots += `<circle cx="${40 + x * 46}" cy="${470 + y * 46}" r="${6 + ((x + y) % 4) * 3}"/>`;
  // A fake logo lockup: fat white slabs with a black keyline, right under where the launcher's own title goes.
  let lockup = "";
  const slabs = [520, 380, 610, 300];
  slabs.forEach((w, i) => {
    lockup += `<rect x="${96 + i * 14}" y="${610 + i * 96}" width="${w}" height="72" rx="10" fill="#fff" stroke="#000" stroke-width="10" transform="rotate(-4 ${96 + w / 2} ${646 + i * 96})"/>`;
  });
  return svg(
    `<rect width="${W}" height="${H}" fill="#19b5ff"/>` +
      `<g fill="#fff36b" opacity=".55">${rays}</g>` +
      shards +
      `<g fill="#000" opacity=".5">${dots}</g>` +
      `<circle cx="1240" cy="400" r="210" fill="#fff"/><circle cx="1240" cy="400" r="150" fill="#ff2e93"/>` +
      lockup,
  );
}

function bright(): string {
  const rand = mulberry32(5150);
  let clouds = "";
  for (let i = 0; i < 26; i++) {
    const cx = Math.round(rand() * W);
    const cy = Math.round(120 + rand() * 520);
    clouds += `<ellipse cx="${cx}" cy="${cy}" rx="${Math.round(120 + rand() * 260)}" ry="${Math.round(36 + rand() * 60)}" fill="#fff" opacity="${(0.75 + rand() * 0.25).toFixed(2)}"/>`;
  }
  let dunes = "";
  const tones = ["#fff3c9", "#ffe9a8", "#fff8e2", "#ffffff"];
  tones.forEach((fill, i) => {
    const base = 640 + i * 96;
    let d = `M0,${H}L0,${base}`;
    for (let x = 0; x <= W; x += 160) d += `Q${x + 80},${Math.round(base - 70 + rand() * 120)} ${x + 160},${Math.round(base - 20 + rand() * 50)}`;
    dunes += `<path d="${d}L${W},${H}Z" fill="${fill}"/>`;
  });
  return svg(
    `<defs><linearGradient id="k" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#8fd6ff"/><stop offset=".55" stop-color="#dff4ff"/><stop offset="1" stop-color="#fffdf2"/></linearGradient></defs>` +
      `<rect width="${W}" height="${H}" fill="url(#k)"/>` +
      `<circle cx="1500" cy="260" r="150" fill="#fffbe0"/>` +
      clouds +
      dunes,
  );
}

const white = (): string => svg(`<rect width="${W}" height="${H}" fill="#fff"/>`);

const BUILDERS: Record<StressArt, () => string> = { busy, bright, white };

export function stressArt(kind: StressArt): GameArt {
  const url = uri(BUILDERS[kind]());
  return { heroUrl: url, tallUrl: url, logoUrl: null };
}
