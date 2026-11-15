import type { BadgeTone } from "../../ui";

/**
 * The one status colour system, read by every surface (hero line, library list and tiles, saves row, import results,
 * settings checks). A level says how much attention a state deserves, never how it is drawn:
 *
 *   danger   something failed, or data is at risk (a failed job, a launch that failed, a sync that failed)
 *   warning  needs your action but nothing is broken (needs repair, saves differ, a readiness blocker, low space)
 *   accent   informational (an update is available, something is working)
 *   live     a game is running: the only use of success green, and only as a dot
 *   normal   everything ordinary (installed, ready to play, in sync): uncoloured
 */
export type StatusLevel = "danger" | "warning" | "accent" | "live" | "normal";

export const STATUS_LEVEL = {
  failed: "danger",
  syncFailed: "danger",
  needsRepair: "warning",
  savesDiffer: "warning",
  needsSignIn: "warning",
  needsInternet: "warning",
  offline: "warning",
  paused: "warning",
  waiting: "warning",
  blocker: "warning",
  lowSpace: "warning",
  updateAvailable: "accent",
  working: "accent",
  savesNewer: "accent",
  running: "live",
  ready: "normal",
  installed: "normal",
  inSync: "normal",
  queued: "normal",
  notInstalled: "normal",
  signedOut: "normal",
} as const satisfies Record<string, StatusLevel>;

export type StatusKind = keyof typeof STATUS_LEVEL;

/** The shared Badge/dot tone of a level. `live` is the green dot; `normal` is neutral. */
const LEVEL_TONE: Record<StatusLevel, BadgeTone> = { danger: "danger", warning: "warning", accent: "accent", live: "success", normal: "neutral" };

export const statusTone = (kind: StatusKind): BadgeTone => LEVEL_TONE[STATUS_LEVEL[kind]];

/** Text colour per tone. `success` is only ever a dot or a one-off check, so its text is as calm as normal text. */
export const TONE_TEXT: Record<BadgeTone, string> = {
  neutral: "text-fg-muted",
  accent: "text-accent-text",
  success: "text-fg-muted",
  warning: "text-warning-text",
  danger: "text-danger-text",
};
