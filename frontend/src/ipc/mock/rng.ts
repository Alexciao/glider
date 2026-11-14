/** Small deterministic PRNG helpers shared by the mock catalog, art and job simulation. */

export function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** FNV-1a, for turning a string into a stable seed. */
export function hashString(s: string): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return h >>> 0;
}

export type Rand = () => number;

export const pick = <T>(rand: Rand, items: readonly T[]): T => items[Math.floor(rand() * items.length)]!;
export const between = (rand: Rand, lo: number, hi: number): number => lo + rand() * (hi - lo);
export const chance = (rand: Rand, p: number): boolean => rand() < p;
export const clamp = (v: number, lo: number, hi: number): number => Math.min(hi, Math.max(lo, v));

/** Deterministic Fisher-Yates over a copy. */
export function shuffled<T>(rand: Rand, items: readonly T[]): T[] {
  const out = [...items];
  for (let i = out.length - 1; i > 0; i--) {
    const j = Math.floor(rand() * (i + 1));
    [out[i], out[j]] = [out[j]!, out[i]!];
  }
  return out;
}
