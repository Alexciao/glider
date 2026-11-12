import { GiB, TiB } from "./constants";

export function driveLetter(path: string): string | null {
  const m = /^([a-zA-Z]):/.exec(path);
  return m ? m[1]!.toUpperCase() : null;
}

export const trimSlashes = (p: string): string => p.replace(/[\\/]+$/, "");
export const joinPath = (base: string, name: string): string => `${trimSlashes(base)}\\${name}`;
export const samePath = (a: string, b: string): boolean => trimSlashes(a).toLowerCase() === trimSlashes(b).toLowerCase();

/** Free space by drive letter. The `diskFull` scenario squeezes C: and D:. */
export function freeBytes(path: string, diskFull: boolean): number {
  switch (driveLetter(path)) {
    case "C": return Math.round((diskFull ? 3 : 95) * GiB);
    case "D": return Math.round((diskFull ? 12.6 : 640) * GiB);
    case "E": return Math.round(18.4 * GiB);
    case "F": return Math.round(1.2 * TiB);
    default: return Math.round(200 * GiB);
  }
}
