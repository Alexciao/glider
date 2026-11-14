import type { DownloadMode } from "../ipc/contract.gen";

/** One term, one sentence per download mode: onboarding, Settings and the palette all say the same thing. */
export const DOWNLOAD_MODES: readonly { value: DownloadMode; label: string; summary: string }[] = [
  { value: "background", label: "Background", summary: "Low priority and fewer connections. Best while you work or play." },
  { value: "balanced", label: "Balanced", summary: "Full network speed, low disk and CPU priority. Keeps your PC smooth." },
  { value: "full", label: "Full", summary: "Uses everything for the fastest installs. Best when you're away." },
];

export const modeLabel = (m: DownloadMode): string => DOWNLOAD_MODES.find((x) => x.value === m)?.label ?? m;
export const modeSummary = (m: DownloadMode): string => DOWNLOAD_MODES.find((x) => x.value === m)?.summary ?? "";
