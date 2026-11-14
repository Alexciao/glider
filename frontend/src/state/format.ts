/**
 * Formatters. Bytes are 1024-based with KB/MB/GB labels, exactly like the backend's Format.Bytes,
 * so numbers in the UI match the ones inside backend error copy. Components render these with
 * `tabular-nums` so changing digits never shift the layout.
 */

const UNITS = ["B", "KB", "MB", "GB", "TB"] as const;

function scale(bytes: number): { v: number; u: number } {
  let v = Math.abs(bytes);
  let u = 0;
  while (v >= 1024 && u < UNITS.length - 1) {
    v /= 1024;
    u++;
  }
  return { v: bytes < 0 ? -v : v, u };
}

/** "98.4 GB", "28 GB", "512 B" (one decimal, trailing zero trimmed). */
export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes)) return "—";
  const { v, u } = scale(bytes);
  if (u === 0) return `${Math.round(v)} B`;
  return `${Number(v.toFixed(1))} ${UNITS[u]}`;
}

/** Fixed one decimal ("48.0 MB/s"): live numbers keep a stable width. */
export function formatSpeed(bytesPerSecond: number): string {
  if (!Number.isFinite(bytesPerSecond) || bytesPerSecond < 0) return "—";
  const { v, u } = scale(bytesPerSecond);
  if (u === 0) return `${Math.round(v)} B/s`;
  return `${v.toFixed(1)} ${UNITS[u]}/s`;
}

/** "9 min", "1 h 12 min", "Under a minute", "—" when unknown. */
export function formatEta(seconds: number | null | undefined): string {
  if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return "—";
  if (seconds < 45) return "Under a minute";
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min`;
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  if (h < 24) return m === 0 ? `${h} h` : `${h} h ${m} min`;
  const d = Math.floor(h / 24);
  return `${d} d ${h % 24} h`;
}

/** Clock style, "4:12" or "1:02:09". */
export function formatClock(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  const mm = h > 0 ? String(m).padStart(2, "0") : String(m);
  return `${h > 0 ? `${h}:` : ""}${mm}:${String(sec).padStart(2, "0")}`;
}

/** Compact spans, "1 h 12 min", "4 min 10 s", "38 s". */
export function formatDuration(totalSeconds: number): string {
  const s = Math.max(0, Math.round(totalSeconds));
  if (s < 60) return `${s} s`;
  const m = Math.floor(s / 60);
  if (m < 60) return s % 60 === 0 ? `${m} min` : `${m} min ${s % 60} s`;
  const h = Math.floor(m / 60);
  return m % 60 === 0 ? `${h} h` : `${h} h ${m % 60} min`;
}

/** Total time played, "42 h 17 min"; under an hour "17 min"; under a minute "< 1 min". */
export function formatPlaytime(totalSeconds: number): string {
  const minutes = Math.floor(Math.max(0, totalSeconds) / 60);
  if (minutes < 1) return "< 1 min";
  const h = Math.floor(minutes / 60);
  return h === 0 ? `${minutes} min` : `${h} h ${minutes % 60} min`;
}

/** Whole percent, floored: never shows 100% before the job is actually done. */
export function formatPercent(percent: number): string {
  const p = Math.min(100, Math.max(0, percent));
  return `${Math.floor(p)}%`;
}

export function formatRelativeTime(iso: string | null | undefined, now: number = Date.now()): string {
  if (!iso) return "—";
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return "—";
  const diff = Math.max(0, now - t) / 1000;
  if (diff < 45) return "Just now";
  if (diff < 3600) return `${Math.round(diff / 60)} min ago`;
  if (diff < 86400) return `${Math.round(diff / 3600)} h ago`;
  const days = Math.floor(diff / 86400);
  if (days === 1) return "Yesterday";
  if (days < 14) return `${days} days ago`;
  return new Date(t).toLocaleDateString(undefined, { month: "short", day: "numeric", year: days > 300 ? "numeric" : undefined });
}

/**
 * Legendary versions look like "++Fortnite+Release-40.20-CL-47120934-Windows"; test branches add
 * segments ("Release-Cert-Next-40.20-…"). Humans want "40.20": the first dotted number wins (the
 * changelist is a plain integer). Shapes with no dotted number pass through, capped at 28 characters.
 */
export function prettyVersion(raw: string | null | undefined): string {
  if (!raw) return "—";
  const m = /(\d+\.\d+(?:\.\d+)*)/.exec(raw);
  if (m) return m[1]!;
  return raw.length > 28 ? `${raw.slice(0, 27)}…` : raw;
}

/** Changelist, for the technical line under a version when it adds information. */
export function buildOf(raw: string | null | undefined): string | null {
  const m = raw ? /-CL-(\d+)/i.exec(raw) : null;
  return m ? m[1]! : null;
}

export function driveOf(path: string | null | undefined): string {
  const m = path ? /^([A-Za-z]:)/.exec(path) : null;
  return m ? m[1]!.toUpperCase() : "";
}

/** Splits a Windows path so the last segments never truncate: head can ellipsize, tail stays. */
export function splitPath(path: string, tailSegments = 2): { head: string; tail: string } {
  const parts = path.split(/[\\/]/);
  if (parts.length <= tailSegments + 1) return { head: "", tail: path };
  const tail = parts.slice(-tailSegments).join("\\");
  const head = parts.slice(0, -tailSegments).join("\\") + "\\";
  return { head, tail };
}

/** The parent folder of a Windows path. */
export const dirname = (p: string): string => p.replace(/[\\/][^\\/]*$/, "");

export function joinPath(base: string, leaf: string): string {
  return base.replace(/[\\/]+$/, "") + "\\" + leaf;
}

export function plural(n: number, one: string, many = `${one}s`): string {
  return `${n} ${n === 1 ? one : many}`;
}
