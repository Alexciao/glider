import { GiB, TiB } from "./constants";

export function driveLetter(path: string): string | null {
  const m = /^([a-zA-Z]):/.exec(path);
  return m ? m[1]!.toUpperCase() : null;
}

export const trimSlashes = (p: string): string => p.replace(/[\\/]+$/, "");
export const joinPath = (base: string, name: string): string => `${trimSlashes(base)}\\${name}`;
export const samePath = (a: string, b: string): boolean => trimSlashes(a).toLowerCase() === trimSlashes(b).toLowerCase();
export const dirname = (p: string): string => p.replace(/[\\/][^\\/]*$/, "");

export interface MockDrive {
  letter: string;
  label: string | null;
  totalBytes: number;
  freeBytes: number;
  isSsd: boolean;
}

/** The mock PC: a small system SSD, a fast games SSD, a big HDD and a nearly full external drive. */
export function defaultDrives(diskFull = false): MockDrive[] {
  return [
    { letter: "C", label: "Windows", totalBytes: Math.round(0.93 * TiB), freeBytes: Math.round((diskFull ? 1.1 : 212) * GiB), isSsd: true },
    { letter: "D", label: "Games", totalBytes: Math.round(1.82 * TiB), freeBytes: Math.round((diskFull ? 2.1 : 640) * GiB), isSsd: true },
    { letter: "E", label: "Archive", totalBytes: Math.round(3.64 * TiB), freeBytes: Math.round((diskFull ? 3.4 : 1.9 * 1024) * GiB), isSsd: false },
    { letter: "F", label: "Backup", totalBytes: Math.round(0.93 * TiB), freeBytes: Math.round((diskFull ? 0.9 : 18.4) * GiB), isSsd: false },
  ];
}
