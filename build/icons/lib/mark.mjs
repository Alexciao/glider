// The Glider mark: two nested swept wings on a 64 x 64 grid.
//
//   wing - the large leading wing  (paper / currentColor)
//   flap - a crescent that follows the wing's trailing edge at a fixed gap,
//          "the lift" (glide blue)
//
// The wing has a straight leading edge, a concave trailing edge (the lift
// curve) and a flat root. The flap's leading edge is that same curve, offset
// by a constant gap, so the pair reads as one slotted wing. Authored in raw
// units, climbed to a bank angle, then uniformly scaled to fill the live
// area. Output is plain fill paths: no strokes anywhere.

import { quadAt, rotate, shapePath } from './geometry.mjs';

const sub = (a, b) => [a[0] - b[0], a[1] - b[1]];
const add = (a, b) => [a[0] + b[0], a[1] + b[1]];
const mul = (a, k) => [a[0] * k, a[1] * k];
const unit = (a) => mul(a, 1 / Math.hypot(a[0], a[1]));
const mid = (a, b) => mul(add(a, b), 0.5);

// quadratic sub-curve control point
const subCtl = (p0, c, p1, t0, t1) => {
  const w0 = (1 - t0) * (1 - t1);
  const w1 = t0 * (1 - t1) + t1 * (1 - t0);
  const w2 = t0 * t1;
  return [w0 * p0[0] + w1 * c[0] + w2 * p1[0], w0 * p0[1] + w1 * c[1] + w2 * p1[1]];
};

export const DEFAULTS = {
  size: 52, // live-area extent on the 64 grid
  bank: 12, // degrees of climb attitude
  gap: 5.5, // wing -> flap channel, raw units
  flapStart: 0.3, // where the flap's tip sits along the wing's trailing edge
  flapBase: 15, // width of the flap at the root
  flapBelly: 4, // concavity of the flap's own trailing edge
  tip: 3, // fillet radius at the wing tip
  corner: 2, // fillet radius at other corners
  flapTip: 1.6,
};

/** Raw (un-rotated) wing + flap edge chains. */
function rawShapes(o) {
  const R = [2, 58];
  const T = [60, 10];
  const B = [34, 58];
  const C = [41, 34];
  const wing = [[R, T], [T, C, B], [B, R]];

  // flap: leading edge = wing trailing edge translated by `gap` along its normal
  const chord = unit(sub(B, T));
  const v = mul([chord[1], -chord[0]], o.gap); // normal pointing away from the wing
  const [T2, C2, B2] = [add(T, v), add(C, v), add(B, v)];
  // where the offset curve crosses the root line y = 58
  let lo = 0.5;
  let hi = 1;
  for (let i = 0; i < 40; i++) {
    const m = (lo + hi) / 2;
    if (quadAt(T2, C2, B2, m)[1] < 58) lo = m;
    else hi = m;
  }
  const tb = lo;
  const ta = o.flapStart;
  const baseL = quadAt(T2, C2, B2, tb);
  const tip = quadAt(T2, C2, B2, ta);
  const baseR = [baseL[0] + o.flapBase, baseL[1]];
  const n = mul(v, -1 / o.gap); // toward the wing
  const ctlR = add(mid(tip, baseR), mul(n, o.flapBelly));
  const flap = [
    [baseL, subCtl(T2, C2, B2, tb, ta), tip],
    [tip, ctlR, baseR],
    [baseR, baseL],
  ];
  return { wing, flap };
}

const sample = (e) =>
  e.length === 2 ? [e[0], e[1]] : [0, 0.25, 0.5, 0.75, 1].map((t) => quadAt(e[0], e[1], e[2], t));

export function markShapes(opts = {}) {
  const o = { ...DEFAULTS, ...opts };
  const { wing, flap } = rawShapes(o);
  const rot = (e) => rotate(e, o.bank);
  const wingR = wing.map(rot);
  const flapR = flap.map(rot);
  const pts = [...wingR, ...flapR].flatMap(sample);
  const xs = pts.map((p) => p[0]);
  const ys = pts.map((p) => p[1]);
  const x0 = Math.min(...xs);
  const x1 = Math.max(...xs);
  const y0 = Math.min(...ys);
  const y1 = Math.max(...ys);
  const k = o.size / Math.max(x1 - x0, y1 - y0);
  const cx = (x0 + x1) / 2 + (o.nudge?.[0] ?? 0);
  const cy = (y0 + y1) / 2 + (o.nudge?.[1] ?? 0);
  const tf = (e) => e.map((p) => [32 + (p[0] - cx) * k, 32 + (p[1] - cy) * k]);
  const r = o.round ?? 1;
  return [
    { role: 'wing', d: shapePath(wingR.map(tf), [o.corner * r, o.tip * r, o.corner * r]) },
    { role: 'flap', d: shapePath(flapR.map(tf), [o.corner * r, o.flapTip * r, o.corner * r]) },
  ];
}
