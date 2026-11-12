/**
 * Structural sharing for JSON-like data: returns `prev` (same reference) wherever `next` is deeply
 * equal, and rebuilds only the branches that changed. The backend pushes the whole snapshot on
 * every change; with this, a component selecting `snapshot.game` re-renders only when game changed.
 */
export function share<T>(prev: unknown, next: T): T {
  if (prev === next) return next;
  if (typeof prev !== "object" || typeof next !== "object" || prev === null || next === null) return next;

  if (Array.isArray(next)) {
    if (!Array.isArray(prev)) return next;
    let same = prev.length === next.length;
    const out = next.map((item, i) => {
      const s = share(prev[i], item);
      if (s !== prev[i]) same = false;
      return s;
    });
    return (same ? prev : out) as T;
  }
  if (Array.isArray(prev)) return next;

  const p = prev as Record<string, unknown>;
  const n = next as Record<string, unknown>;
  const keys = Object.keys(n);
  let same = Object.keys(p).length === keys.length;
  const out: Record<string, unknown> = {};
  for (const k of keys) {
    const s = share(p[k], n[k]);
    out[k] = s;
    if (s !== p[k]) same = false;
  }
  return (same ? prev : out) as T;
}
