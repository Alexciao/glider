/**
 * Most-recently-used ids for the command palette, in localStorage. Storage can be missing or blocked (private window,
 * cleared site data), so every access is guarded and the palette works without it.
 */
const KEY = "glider.palette.recent.v1";
const MAX = 6;

function read(): string[] {
  try {
    const raw = localStorage.getItem(KEY);
    const list: unknown = raw ? JSON.parse(raw) : [];
    return Array.isArray(list) ? list.filter((x): x is string => typeof x === "string").slice(0, MAX) : [];
  } catch {
    return [];
  }
}

export const getRecents = (): string[] => read();

/** Move `id` to the front. Returns the new list. */
export function pushRecent(id: string): string[] {
  const next = [id, ...read().filter((x) => x !== id)].slice(0, MAX);
  try {
    localStorage.setItem(KEY, JSON.stringify(next));
  } catch {
    /* storage unavailable: recents just don't persist */
  }
  return next;
}

export function clearRecents(): void {
  try {
    localStorage.removeItem(KEY);
  } catch {
    /* nothing to clear */
  }
}
