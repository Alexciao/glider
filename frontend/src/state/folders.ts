import type { DriveInfoDto, LibraryFolder, Settings } from "../ipc/contract.gen";
import { actions } from "./actions";
import { toast } from "./notices";

const trimSlashes = (p: string): string => p.replace(/[\\/]+$/, "");
/** Windows paths compare case-insensitively and ignore a trailing slash. */
export const samePath = (a: string, b: string): boolean => trimSlashes(a).toLowerCase() === trimSlashes(b).toLowerCase();

/**
 * Library-folder editing shared by onboarding and Settings. The pure patch builders decide what changes; the three
 * action functions pick a folder (or take a path) and write the patch through the one optimistic settings writer.
 */

type FolderSettings = Pick<Settings, "libraryFolders" | "defaultInstallBase">;

/** The drive a folder lives on, for free-space figures. Matches by drive letter ("D:" ↔ "D:\"). */
export function driveFor(folder: Pick<LibraryFolder, "drive" | "path">, drives: readonly DriveInfoDto[]): DriveInfoDto | undefined {
  const letter = (folder.drive || folder.path).slice(0, 1).toLowerCase();
  return drives.find((d) => d.root.slice(0, 1).toLowerCase() === letter);
}

/** Adds a folder; the first one becomes the default. Null when it is already there. */
export function addFolderPatch(s: FolderSettings, path: string): FolderSettings | null {
  if (s.libraryFolders.some((f) => samePath(f, path))) return null;
  return { libraryFolders: [...s.libraryFolders, path], defaultInstallBase: s.defaultInstallBase ?? path };
}

/** Removes a folder; if it was the default, the first remaining one takes over. */
export function removeFolderPatch(s: FolderSettings, path: string): FolderSettings {
  const left = s.libraryFolders.filter((f) => !samePath(f, path));
  const wasDefault = s.defaultInstallBase !== null && samePath(s.defaultInstallBase, path);
  return { libraryFolders: left, defaultInstallBase: wasDefault ? (left[0] ?? null) : s.defaultInstallBase };
}

/** Why a folder can't be removed, or null when it can. Games installed there stay where they are, so they block it. */
export function removeBlockedReason(f: Pick<LibraryFolder, "gameCount">): string | null {
  return f.gameCount > 0 ? `${f.gameCount} ${f.gameCount === 1 ? "game is" : "games are"} installed here. Move or uninstall ${f.gameCount === 1 ? "it" : "them"} first.` : null;
}

export async function addLibraryFolder(current: FolderSettings, title = "Choose a library folder"): Promise<void> {
  const path = await actions.pickFolder(current.defaultInstallBase, title);
  if (!path) return;
  const patch = addFolderPatch(current, path);
  if (!patch) {
    toast({ level: "info", title: "That folder is already in your library", duration: 3000 });
    return;
  }
  await actions.updateSettings(patch);
}

export const removeLibraryFolder = (current: FolderSettings, path: string): Promise<void> => actions.updateSettings(removeFolderPatch(current, path));

export const makeDefaultFolder = (path: string): Promise<void> => actions.updateSettings({ defaultInstallBase: path });
