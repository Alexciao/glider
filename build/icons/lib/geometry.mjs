// Small geometry helpers. Shapes are chains of line / quadratic edges whose
// corners are filleted. Output is plain filled <path> data, so there are no
// strokes anywhere that could thin out or vanish at 16 px.

const f = (n) => +n.toFixed(3);

export const rotate = (pts, deg, cx = 32, cy = 32) => {
  const a = (deg * Math.PI) / 180;
  const c = Math.cos(a);
  const s = Math.sin(a);
  return pts.map(([x, y]) => [cx + (x - cx) * c - (y - cy) * s, cy + (x - cx) * s + (y - cy) * c]);
};

const sub = (a, b) => [a[0] - b[0], a[1] - b[1]];
const add = (a, b) => [a[0] + b[0], a[1] + b[1]];
const mul = (a, k) => [a[0] * k, a[1] * k];
const lerp = (a, b, t) => add(mul(a, 1 - t), mul(b, t));
const P = (p) => f(p[0]) + ' ' + f(p[1]);

export const quadAt = (p0, c, p1, t) => lerp(lerp(p0, c, t), lerp(c, p1, t), t);

// control point of the sub-curve [t0, t1] of a quadratic Bezier
const quadSub = (p0, c, p1, t0, t1) => {
  const w0 = (1 - t0) * (1 - t1);
  const w1 = t0 * (1 - t1) + t1 * (1 - t0);
  const w2 = t0 * t1;
  return [w0 * p0[0] + w1 * c[0] + w2 * p1[0], w0 * p0[1] + w1 * c[1] + w2 * p1[1]];
};

// An edge is [p0, p1] (line) or [p0, c, p1] (quadratic).
const first = (e) => e[0];
const last = (e) => e[e.length - 1];
const edgeLen = (e) => Math.hypot(...sub(last(e), first(e)));

// trim distance d from the start / end of an edge, returning the new edge
function trim(e, ds, de) {
  const L = edgeLen(e);
  const t0 = Math.min(0.45, ds / L);
  const t1 = 1 - Math.min(0.45, de / L);
  if (e.length === 2) return [lerp(e[0], e[1], t0), lerp(e[0], e[1], t1)];
  return [quadAt(e[0], e[1], e[2], t0), quadSub(e[0], e[1], e[2], t0, t1), quadAt(e[0], e[1], e[2], t1)];
}

/**
 * Closed outline from a chain of edges (each edge starts where the previous one
 * ends). radii[i] is the fillet at the vertex where edge i begins. Fillets are
 * quadratic corners, so the result is pure fill geometry.
 */
export function shapePath(edges, radii) {
  const n = edges.length;
  const trimmed = edges.map((e, i) => trim(e, radii[i], radii[(i + 1) % n]));
  let d = '';
  trimmed.forEach((e, i) => {
    if (i === 0) d += 'M' + P(first(e));
    d += e.length === 2 ? 'L' + P(e[1]) : 'Q' + P(e[1]) + ' ' + P(e[2]);
    const next = trimmed[(i + 1) % n];
    const corner = first(edges[(i + 1) % n]);
    d += radii[(i + 1) % n] > 0 ? 'Q' + P(corner) + ' ' + P(first(next)) : 'L' + P(first(next));
  });
  return d + 'Z';
}
