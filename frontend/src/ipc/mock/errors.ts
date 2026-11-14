import type { ErrorCode, GliderError } from "../contract.gen";
import { GiB } from "./constants";

/** Thrown inside the mock; the dispatcher turns it into the `error` field of a response. */
export class RpcFailure extends Error {
  readonly error: GliderError;
  constructor(error: GliderError) {
    super(error.message);
    this.name = "RpcFailure";
    this.error = error;
  }
}

export const fail = (error: GliderError): RpcFailure => new RpcFailure(error);

const UNITS = ["B", "KB", "MB", "GB", "TB"] as const;

/** Same as `Format.Bytes` in Errors.cs: 1024-based, one decimal at most, units B/KB/MB/GB/TB. */
export function formatBytes(bytes: number): string {
  let v = bytes;
  let u = 0;
  while (Math.abs(v) >= 1024 && u < UNITS.length - 1) {
    v /= 1024;
    u++;
  }
  if (u === 0) return `${bytes} B`;
  return `${Math.round(v * 10) / 10} ${UNITS[u]}`;
}

const err = (
  code: ErrorCode,
  title: string,
  message: string,
  fix: GliderError["fix"],
  detail: string | null = null,
  appName: string | null = null,
): GliderError => ({ code, title, message, fix, detail, appName });

const game = (name?: string): string => name ?? "the game";

/**
 * Mirrors the constructors in `src/Glider.Core/Contracts/Errors.cs`, with the game's name filled in where v0.1 said
 * "Fortnite" (the multi-game Core takes the title the same way).
 */
export const Errors = {
  unknown: (detail: string | null = null) =>
    err("unknown", "Something went wrong", "An unexpected error occurred. Try again, or copy the logs if it keeps happening.", "retry", detail),
  invalidRequest: (detail: string) =>
    err("invalidRequest", "Invalid request", "The app sent a request the backend did not understand.", null, detail),
  notSignedIn: (detail: string | null = null) =>
    err("notSignedIn", "Sign in required", "Sign in with your Epic Games account to continue.", "signIn", detail),
  authExpired: (detail: string | null = null) =>
    err("authExpired", "Your session expired", "Sign in again to keep downloading and playing.", "signIn", detail),
  authFailed: (detail: string | null = null) =>
    err("authFailed", "Sign-in failed", "Epic didn't accept the sign-in. Try again.", "signIn", detail),
  authCancelled: () =>
    err("authCancelled", "Sign-in was canceled", "The sign-in window was closed before you finished. If Epic showed an error, copy the diagnostic logs so it can be looked into.", null),
  insufficientSpace: (requiredBytes: number, freeBytes: number, detail: string | null = null, appName: string | null = null) =>
    err(
      "insufficientSpace",
      "Not enough disk space",
      `This needs ${formatBytes(requiredBytes)} but only ${formatBytes(freeBytes)} is free. Free up space or choose another drive.`,
      "freeSpace",
      detail,
      appName,
    ),
  network: (detail: string | null = null, appName: string | null = null) =>
    err("network", "Can't reach Epic", "Check your internet connection, then try again. Progress is kept.", "retry", detail, appName),
  offline: (what = "This") =>
    err("offline", "You're offline", `${what} needs an internet connection. Glider will pick up again when you're back online.`, "retry"),
  cdnUnavailable: (detail: string | null = null, appName: string | null = null) =>
    err("cdnUnavailable", "Download server unavailable", "Epic's download servers didn't respond. Try again, or pick another CDN in Settings.", "retry", detail, appName),
  filesLocked: (title?: string, detail: string | null = null, appName: string | null = null) =>
    err("filesLocked", "Game files are in use", `Close ${game(title)} and any program using its files, then try again.`, "retry", detail, appName),
  permissionDenied: (detail: string | null = null, appName: string | null = null) =>
    err("permissionDenied", "Access denied", "Glider can't write to this folder. Choose a different location.", "chooseFolder", detail, appName),
  eglBusy: (title?: string, detail: string | null = null, appName: string | null = null) =>
    err("eglBusy", "Epic Games Launcher is busy", `Epic Games Launcher is updating ${game(title)}, so Glider will wait and continue automatically.`, "waitForEgl", detail, appName),
  gameRunning: (title?: string, appName: string | null = null) =>
    err("gameRunning", `${title ?? "The game"} is running`, "Close the game first, then try again.", "closeGame", null, appName),
  gameBusy: (title?: string, appName: string | null = null) =>
    err("gameBusy", "Another task is using this game", `${game(title)} is being installed, updated or repaired. Wait for it to finish, or cancel it first.`, null, null, appName),
  notFound: (what = "That game") => err("notFound", "Not found", `${what} isn't in your library.`, null),
  notOwned: (title?: string, appName: string | null = null) =>
    err("notOwned", "Not in your library", `${game(title)} isn't owned by this Epic account.`, null, null, appName),
  notInstalled: (title?: string, appName: string | null = null) =>
    err("notInstalled", `${title ?? "The game"} isn't installed`, "Install it or locate an existing install first.", null, null, appName),
  alreadyInstalled: (title?: string, appName: string | null = null) =>
    err("alreadyInstalled", "Already installed", `${game(title)} is already installed in Glider.`, null, null, appName),
  invalidFolder: (reason: string) => err("invalidFolder", "Not a game folder", reason, "chooseFolder"),
  corrupted: (title?: string, detail: string | null = null, appName: string | null = null) =>
    err("corrupted", "Game files need repair", `Some files of ${game(title)} are missing or damaged. Repair downloads only what's broken.`, "repair", detail, appName),
  busy: (what: string) => err("busy", "Another task is running", `Wait for ${what} to finish, or cancel it first.`, null),
  legendaryMissing: (detail: string | null = null) =>
    err("legendaryMissing", "Glider is damaged", "A required component is missing. Reinstall Glider to fix it.", "openLogs", detail),
  legendaryFailed: (detail: string | null = null, appName: string | null = null) =>
    err("legendaryFailed", "The task failed", "The operation stopped unexpectedly. Try again; progress is kept.", "retry", detail, appName),
  legendaryLocked: () =>
    err("legendaryLocked", "Epic's data is busy", "Another Glider task is using Epic's account data. Glider will retry in a moment.", "retry"),
  eacSetupFailed: (detail: string | null = null, appName: string | null = null) =>
    err("eacSetupFailed", "Anti-cheat setup failed", "Easy Anti-Cheat must be installed to play. Try again and accept the Windows prompt.", "retry", detail, appName),
  prerequisiteFailed: (title?: string, detail: string | null = null, appName: string | null = null) =>
    err("prerequisiteFailed", "Setup didn't finish", `${game(title)} needs a one-time setup (runtimes) before it can start. Try again and accept the Windows prompt.`, "installPrerequisites", detail, appName),
  launchFailed: (title?: string, detail: string | null = null, appName: string | null = null) =>
    err("launchFailed", `Couldn't start ${title ?? "the game"}`, "The game didn't start. Try again, or repair the install if it keeps happening.", "repair", detail, appName),
  thirdPartyRequired: (store: string, appName: string | null = null) =>
    err("thirdPartyRequired", `${store} is required`, `This game starts through ${store}. Install it, then try again.`, null, null, appName),
  activationRequired: (store: string, appName: string | null = null) =>
    err("activationRequired", "Link your account first", `Link this game with ${store} once, then Play will start it.`, "activate", null, appName),
  saveConflict: (title?: string, appName: string | null = null) =>
    err("saveConflict", "Your saves differ", `${game(title)}'s cloud and local saves both changed. Choose which to keep.`, "resolveSaves", null, appName),
  saveSyncFailed: (title?: string, detail: string | null = null, appName: string | null = null) =>
    err("saveSyncFailed", "Cloud saves didn't sync", `Glider couldn't sync ${game(title)}'s saves with Epic. You can play without syncing.`, "retry", detail, appName),
  overlayFailed: (detail: string | null = null) =>
    err("overlayFailed", "Overlay setup failed", "The Epic overlay couldn't be installed. Try again; the games work without it.", "retry", detail),
  cancelled: () => err("cancelled", "Canceled", "The task was canceled.", null),
  timeout: (what: string) => err("timeout", "Timed out", `${what} took too long to respond. Check your connection and retry.`, "retry"),
} as const;

/** A representative error for any code, used by `fail=` / `failNext` / `failActiveJob`. */
export function errorForCode(code: ErrorCode, title?: string, appName: string | null = null): GliderError {
  switch (code) {
    case "unknown": return Errors.unknown("mock: injected failure");
    case "invalidRequest": return Errors.invalidRequest("mock: injected failure");
    case "notSignedIn": return Errors.notSignedIn();
    case "authExpired": return Errors.authExpired();
    case "authFailed": return Errors.authFailed();
    case "authCancelled": return Errors.authCancelled();
    case "insufficientSpace": return Errors.insufficientSpace(Math.round(4.8 * GiB), Math.round(0.9 * GiB), null, appName);
    case "network": return Errors.network("HTTPSConnectionPool(host='download.epicgames.com', port=443): Max retries exceeded", appName);
    case "offline": return Errors.offline();
    case "cdnUnavailable": return Errors.cdnUnavailable("HTTP 503 from epicgames-download1.akamaized.net", appName);
    case "filesLocked": return Errors.filesLocked(title, "PermissionError: [WinError 32] The process cannot access the file", appName);
    case "permissionDenied": return Errors.permissionDenied("PermissionError: [WinError 5] Access is denied", appName);
    case "eglBusy": return Errors.eglBusy(title, null, appName);
    case "gameRunning": return Errors.gameRunning(title, appName);
    case "gameBusy": return Errors.gameBusy(title, appName);
    case "notFound": return Errors.notFound();
    case "notOwned": return Errors.notOwned(title, appName);
    case "notInstalled": return Errors.notInstalled(title, appName);
    case "alreadyInstalled": return Errors.alreadyInstalled(title, appName);
    case "invalidFolder": return Errors.invalidFolder("This folder doesn't contain a game Glider recognises. Choose the folder that holds the game's files.");
    case "corrupted": return Errors.corrupted(title, "142 files failed verification", appName);
    case "busy": return Errors.busy("the current task");
    case "legendaryMissing": return Errors.legendaryMissing("legendary.exe not found");
    case "legendaryFailed": return Errors.legendaryFailed("legendary exited unexpectedly", appName);
    case "legendaryLocked": return Errors.legendaryLocked();
    case "eacSetupFailed": return Errors.eacSetupFailed("EasyAntiCheat_EOS_Setup.exe exited with code 5", appName);
    case "prerequisiteFailed": return Errors.prerequisiteFailed(title, "vc_redist.x64.exe exited with code 1603", appName);
    case "launchFailed": return Errors.launchFailed(title, "Process exited with code -1073740791 after 2.1 s", appName);
    case "thirdPartyRequired": return Errors.thirdPartyRequired("Ubisoft Connect", appName);
    case "activationRequired": return Errors.activationRequired("Ubisoft Connect", appName);
    case "saveConflict": return Errors.saveConflict(title, appName);
    case "saveSyncFailed": return Errors.saveSyncFailed(title, "HTTP 500 from datastorage-public-service-live.ol.epicgames.com", appName);
    case "overlayFailed": return Errors.overlayFailed("EOSOverlayRenderer: download interrupted");
    case "cancelled": return Errors.cancelled();
    case "timeout": return Errors.timeout("Epic's servers");
  }
}

export function toGliderError(e: unknown): GliderError {
  if (e instanceof RpcFailure) return e.error;
  return Errors.unknown(e instanceof Error ? e.message : String(e));
}
