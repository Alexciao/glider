import type { GameSummary } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { JOB_VERB } from "../../state/derive";
import { toast } from "../../state/notices";
import { openInstall } from "../../state/installPlan";
import { useUi } from "../../state/ui";
import type { BadgeTone } from "../../ui";
import { statusTone } from "../game/statusLevels";

/** The one-line status a list row shows for a game. */
export function rowStatus(g: GameSummary): { tone: BadgeTone; text: string } {
  if (g.runState !== "idle") return { tone: statusTone("running"), text: g.runState === "running" ? "Running" : "Starting" };
  if (g.activeJobKind) return { tone: statusTone("working"), text: JOB_VERB[g.activeJobKind] };
  switch (g.installStatus) {
    case "installed":
      return g.updateAvailable ? { tone: statusTone("updateAvailable"), text: "Update available" } : { tone: statusTone("installed"), text: "Installed" };
    case "broken":
      return { tone: statusTone("needsRepair"), text: "Needs repair" };
    case "notInstallable":
      return { tone: statusTone("installed"), text: g.thirdPartyName ?? "Included" };
    case "notInstalled":
      return { tone: statusTone("notInstalled"), text: "Not installed" };
  }
}

export type QuickKind = "play" | "install" | "repair";

export interface QuickAction {
  kind: QuickKind;
  label: string;
}

/**
 * The one button a tile or row offers on hover. Busy games (running, or with a job) offer none: their state is already on
 * the cover. Updates are not the quick action: Play stays Play (the update badge and the menu carry Update).
 */
export function quickAction(g: GameSummary): QuickAction | null {
  if (g.kind !== "game" || g.runState !== "idle" || g.activeJobId) return null;
  switch (g.installStatus) {
    case "installed":
      return { kind: "play", label: "Play" };
    case "broken":
      return { kind: "repair", label: "Repair" };
    case "notInstalled":
      return { kind: "install", label: "Install" };
    case "notInstallable":
      return null;
  }
}

/**
 * Errors from play/update/repair normally land in the game page's inline callout. From a list nobody is looking at that
 * page, so they are moved into a toast instead (and the page's copy is cleared so it doesn't show twice later).
 */
function surfaceHeroError(appName: string): void {
  const ui = useUi.getState();
  const e = ui.errors.hero;
  if (!e || (e.appName && e.appName !== appName)) return;
  ui.setError("hero", undefined);
  toast({ level: "error", title: e.error.title, message: e.error.message, action: e.error.fix, appName });
}

export async function runQuick(kind: QuickKind, appName: string): Promise<void> {
  switch (kind) {
    case "play":
      await actions.launch(appName);
      break;
    case "install":
      await openInstall(appName);
      return;
    case "repair":
      await actions.verify(appName);
      break;
  }
  surfaceHeroError(appName);
}

export async function runUpdate(appName: string): Promise<void> {
  await actions.update(appName);
  surfaceHeroError(appName);
}

/** What the status means for a screen reader, since the badges are icons. */
export function describeState(g: GameSummary): string {
  const parts: string[] = [rowStatus(g).text];
  if (g.cloudSaves) parts.push("cloud saves");
  if (g.favorite) parts.push("favorite");
  return parts.join(", ");
}
