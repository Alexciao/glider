/**
 * Small fuzzy matcher for the command palette and Settings search. No dependency, and fast enough to run on every
 * keystroke over a few hundred titles.
 *
 * Every whitespace-separated word of the query must match somewhere in the text. A word matches, best first, as: the
 * whole text, a prefix of the text, a prefix of any word in it, a substring, or (typo-tolerant) a subsequence whose
 * letters appear in order. Case and diacritics are ignored. `ranges` are [start, end) indices into the *original* text,
 * for highlighting.
 */
export interface FuzzyMatch {
  score: number;
  ranges: [number, number][];
}

/** Lower-case and strip accents, one UTF-16 unit in → one out, so indices stay valid for the original text. */
function fold(s: string): string {
  let out = "";
  for (const ch of s) {
    const base = ch.normalize("NFD")[0] ?? ch;
    const lower = base.toLowerCase();
    out += lower.length === ch.length ? lower : ch.padEnd(ch.length, " ").slice(0, ch.length);
  }
  return out;
}

const isWordChar = (c: string | undefined): boolean => !!c && /[a-z0-9]/.test(c);

function wordStarts(t: string): number[] {
  const out: number[] = [];
  for (let i = 0; i < t.length; i++) if (isWordChar(t[i]) && !isWordChar(t[i - 1])) out.push(i);
  return out;
}

function subsequence(w: string, t: string, starts: number[]): { score: number; ranges: [number, number][] } | null {
  if (w.length < 2) return null;
  const ranges: [number, number][] = [];
  let ti = 0;
  let gaps = 0;
  let boundary = 0;
  for (let wi = 0; wi < w.length; wi++) {
    const c = w[wi]!;
    // Prefer a word start carrying this letter so "ffx" finds "Final Fantasy X" ahead of "stuff fix".
    let found = starts.find((s) => s >= ti && t[s] === c) ?? -1;
    const plain = t.indexOf(c, ti);
    if (found < 0 || (plain >= 0 && plain === ti)) found = plain;
    if (found < 0) return null;
    if (starts.includes(found)) boundary++;
    const last = ranges[ranges.length - 1];
    if (last && last[1] === found) last[1] = found + 1;
    else {
      if (last) gaps++;
      ranges.push([found, found + 1]);
    }
    ti = found + 1;
  }
  // Typo tolerance, not a free pass: either every letter starts a word ("ffx" → Final Fantasy X) or the letters sit
  // close together (at most two stray characters between the first and last). Without this, "fold" would match
  // "Check for updates, interval, schedule" by picking letters from three different words.
  const span = ranges[ranges.length - 1]![1] - ranges[0]![0];
  if (boundary < w.length && span > w.length + 2) return null;
  return { score: Math.max(4, 30 - gaps * 4 + boundary * 3), ranges };
}

function matchWord(w: string, t: string, starts: number[]): { score: number; ranges: [number, number][] } | null {
  if (t === w) return { score: 120, ranges: [[0, t.length]] };
  if (t.startsWith(w)) return { score: 100, ranges: [[0, w.length]] };
  for (let k = 0; k < starts.length; k++) {
    const s = starts[k]!;
    if (t.startsWith(w, s)) return { score: 85 - Math.min(k, 8) * 2, ranges: [[s, s + w.length]] };
  }
  const at = t.indexOf(w);
  if (at >= 0) return { score: 60 - Math.min(at, 40) * 0.5, ranges: [[at, at + w.length]] };
  return subsequence(w, t, starts);
}

function merge(ranges: [number, number][]): [number, number][] {
  const sorted = ranges.slice().sort((a, b) => a[0] - b[0]);
  const out: [number, number][] = [];
  for (const r of sorted) {
    const last = out[out.length - 1];
    if (last && r[0] <= last[1]) last[1] = Math.max(last[1], r[1]);
    else out.push([r[0], r[1]]);
  }
  return out;
}

/** Null when any query word fails to match. An empty query matches everything with a neutral score. */
export function fuzzyMatch(query: string, text: string): FuzzyMatch | null {
  const q = fold(query).trim().replace(/\s+/g, " ");
  const words = q.split(" ").filter(Boolean);
  if (words.length === 0) return { score: 1, ranges: [] };
  const t = fold(text);
  // The whole phrase, typed as it reads, beats the same words matched one by one.
  if (words.length > 1 && t.startsWith(q)) return { score: (t === q ? 120 : 100) - Math.min(text.length, 80) * 0.05, ranges: [[0, q.length]] };
  const starts = wordStarts(t);
  let total = 0;
  const ranges: [number, number][] = [];
  for (const w of words) {
    const m = matchWord(w, t, starts);
    if (!m) return null;
    total += m.score;
    ranges.push(...m.ranges);
  }
  // Shorter texts win ties: "Home" over "Home and other long things".
  return { score: total / words.length - Math.min(text.length, 80) * 0.05, ranges: merge(ranges) };
}

/**
 * Best match over a label and extra search terms. Only the label's own matches produce highlight ranges; a match that
 * needed a keyword scores lower, so a command whose label says it wins over one that only mentions it.
 */
export function fuzzyMatchAny(query: string, label: string, ...others: (string | undefined)[]): FuzzyMatch | null {
  const main = fuzzyMatch(query, label);
  if (main) return main;
  const extra = others.filter((x): x is string => !!x);
  for (const o of extra) {
    const m = fuzzyMatch(query, o);
    if (m) return { score: m.score - 25, ranges: [] };
  }
  if (extra.length) {
    // The query may span the label and a keyword ("update all" over label "Update games", keyword "all").
    const m = fuzzyMatch(query, `${label} ${extra.join(" ")}`);
    if (m) return { score: m.score - 35, ranges: m.ranges.filter((r) => r[1] <= label.length) };
  }
  return null;
}
