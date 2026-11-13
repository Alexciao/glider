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
): GliderError => ({ code, title, message, fix, detail });

/** Mirrors every constructor in `src/Glider.Core/Contracts/Errors.cs` (same code, title, message, fix). */
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
  authCodeNotRecognized: () =>
    err("authFailed", "That isn't a sign-in code", "Paste the code from Epic's page, or everything on it, including the curly brackets.", null),
  authCancelled: () =>
    err("authCancelled", "Sign-in cancelled", "The sign-in window was closed before finishing.", null),
  insufficientSpace: (requiredBytes: number, freeBytes: number, detail: string | null = null) =>
    err(
      "insufficientSpace",
      "Not enough disk space",
      `This needs ${formatBytes(requiredBytes)} but only ${formatBytes(freeBytes)} is free. Free up space or choose another drive.`,
      "freeSpace",
      detail,
    ),
  network: (detail: string | null = null) =>
    err("network", "Can't reach Epic", "Check your internet connection, then retry. Progress is kept.", "retry", detail),
  cdnUnavailable: (detail: string | null = null) =>
    err("cdnUnavailable", "Download server unavailable", "Epic's download servers didn't respond. Retry, or pick another CDN in Settings.", "retry", detail),
  filesLocked: (detail: string | null = null) =>
    err("filesLocked", "Game files are in use", "Close Fortnite and any program using its files, then retry.", "retry", detail),
  permissionDenied: (detail: string | null = null) =>
    err("permissionDenied", "Access denied", "Glider can't write to this folder. Choose a different location.", "chooseFolder", detail),
  eglBusy: (detail: string | null = null) =>
    err("eglBusy", "Epic Games Launcher is busy", "Epic Games Launcher is updating Fortnite, so Glider will wait and continue automatically.", "waitForEgl", detail),
  gameRunning: () => err("gameRunning", "Fortnite is running", "Close the game first, then try again.", "closeGame"),
  notInstalled: () =>
    err("notInstalled", "Fortnite isn't installed", "Install Fortnite or locate an existing install first.", null),
  alreadyInstalled: () =>
    err("alreadyInstalled", "Already installed", "Fortnite is already installed in Glider.", null),
  invalidFolder: (reason: string) => err("invalidFolder", "Not a Fortnite folder", reason, "chooseFolder"),
  corrupted: (detail: string | null = null) =>
    err("corrupted", "Game files need repair", "Some files are missing or damaged. Repair downloads only what's broken.", "repair", detail),
  busy: (what: string) =>
    err("busy", "Another task is running", `Wait for ${what} to finish, or cancel it first.`, null),
  legendaryMissing: (detail: string | null = null) =>
    err("legendaryMissing", "Glider is damaged", "A required component is missing. Reinstall Glider to fix it.", "openLogs", detail),
  legendaryFailed: (detail: string | null = null) =>
    err("legendaryFailed", "The task failed", "The operation stopped unexpectedly. Retry; progress is kept.", "retry", detail),
  eacSetupFailed: (detail: string | null = null) =>
    err("eacSetupFailed", "Anti-cheat setup failed", "Easy Anti-Cheat must be installed to play. Retry and accept the Windows prompt.", "retry", detail),
  launchFailed: (detail: string | null = null) =>
    err("launchFailed", "Couldn't start Fortnite", "The game didn't start. Retry, or repair the install if it keeps happening.", "repair", detail),
  cancelled: () => err("cancelled", "Cancelled", "The task was cancelled.", null),
  timeout: (what: string) =>
    err("timeout", "Timed out", `${what} took too long to respond. Check your connection and retry.`, "retry"),
} as const;

/** A representative error for any code, used by `fail=` / `failNext` / `failActiveJob`. */
export function errorForCode(code: ErrorCode): GliderError {
  switch (code) {
    case "unknown": return Errors.unknown("mock: injected failure");
    case "invalidRequest": return Errors.invalidRequest("mock: injected failure");
    case "notSignedIn": return Errors.notSignedIn();
    case "authExpired": return Errors.authExpired();
    case "authFailed": return Errors.authFailed();
    case "authCancelled": return Errors.authCancelled();
    case "insufficientSpace": return Errors.insufficientSpace(Math.round(4.8 * GiB), Math.round(0.9 * GiB));
    case "network": return Errors.network("HTTPSConnectionPool(host='download.epicgames.com', port=443): Max retries exceeded");
    case "cdnUnavailable": return Errors.cdnUnavailable("HTTP 503 from epicgames-download1.akamaized.net");
    case "filesLocked": return Errors.filesLocked("PermissionError: [WinError 32] The process cannot access the file");
    case "permissionDenied": return Errors.permissionDenied("PermissionError: [WinError 5] Access is denied");
    case "eglBusy": return Errors.eglBusy();
    case "gameRunning": return Errors.gameRunning();
    case "notInstalled": return Errors.notInstalled();
    case "alreadyInstalled": return Errors.alreadyInstalled();
    case "invalidFolder": return Errors.invalidFolder("This folder doesn't contain Fortnite. Choose the folder that contains FortniteGame and Engine.");
    case "corrupted": return Errors.corrupted("142 files failed verification");
    case "busy": return Errors.busy("the current task");
    case "legendaryMissing": return Errors.legendaryMissing("legendary.exe not found");
    case "legendaryFailed": return Errors.legendaryFailed("legendary exited unexpectedly");
    case "eacSetupFailed": return Errors.eacSetupFailed("EasyAntiCheat_EOS_Setup.exe exited with code 5");
    case "launchFailed": return Errors.launchFailed("Process exited with code -1073740791 after 2.1 s");
    case "cancelled": return Errors.cancelled();
    case "timeout": return Errors.timeout("Epic's servers");
  }
}

export function toGliderError(e: unknown): GliderError {
  if (e instanceof RpcFailure) return e.error;
  return Errors.unknown(e instanceof Error ? e.message : String(e));
}
