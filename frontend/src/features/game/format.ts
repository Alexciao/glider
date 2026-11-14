/** Time formatting for the game page: a relative phrase for scanning, the absolute moment for certainty. */

const DATE = new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short", year: "numeric" });
const DATE_TIME = new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short", year: "numeric", hour: "numeric", minute: "2-digit" });

const parse = (iso: string | null | undefined): number | null => {
  if (!iso) return null;
  const t = Date.parse(iso);
  return Number.isNaN(t) ? null : t;
};

/** "14 Nov 2026", or null when unknown. */
export function formatDate(iso: string | null | undefined): string | null {
  const t = parse(iso);
  return t === null ? null : DATE.format(t);
}

/** "14 Nov 2026, 18:42", or null when unknown. */
export function formatDateTime(iso: string | null | undefined): string | null {
  const t = parse(iso);
  return t === null ? null : DATE_TIME.format(t);
}

/** "Playing for 1 h 12 min"-style span: under a minute reads "less than a minute" instead of a bare "< 1 min". */
export function formatPlayingFor(sinceIso: string | null | undefined, now: number = Date.now()): string {
  const t = parse(sinceIso);
  if (t === null) return "a moment";
  const minutes = Math.floor(Math.max(0, now - t) / 60_000);
  if (minutes < 1) return "less than a minute";
  const h = Math.floor(minutes / 60);
  return h === 0 ? `${minutes} min` : `${h} h ${String(minutes % 60).padStart(2, "0")} min`;
}
