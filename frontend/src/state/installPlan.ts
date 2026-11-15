import type { InstallPlan } from "../ipc/contract.gen";
import { rpc } from "../ipc/rpc";
import { pendingKey } from "./actions";
import { useUi } from "./ui";

/**
 * The install dialog should open ready, not open and then resize while Core plans. Pressing (or just hovering) Install asks
 * for the default plan first; the dialog opens with it in hand, or after at most `MAX_WAIT_MS` of a spinner on the button
 * (a slow plan then fills in a dialog that already reserves its height).
 */
const MAX_WAIT_MS = 400;
/** A plan is a snapshot of free space: good for a moment, not for a minute. */
const FRESH_MS = 30_000;

interface Entry {
  at: number;
  promise: Promise<InstallPlan | undefined>;
  plan?: InstallPlan;
}
const cache = new Map<string, Entry>();

/** Start (or reuse) the default-folder plan for a game. Silent: a failure is shown when the dialog asks again itself. */
export function prefetchInstallPlan(appName: string): Promise<InstallPlan | undefined> {
  const hit = cache.get(appName);
  if (hit && Date.now() - hit.at < FRESH_MS) return hit.promise;
  const entry: Entry = {
    at: Date.now(),
    promise: rpc()
      .call("game.planInstall", { appName, basePath: null, components: null, dlc: null })
      .then((p) => {
        entry.plan = p;
        return p;
      })
      .catch(() => {
        cache.delete(appName);
        return undefined;
      }),
  };
  cache.set(appName, entry);
  return entry.promise;
}

/** The default plan if one has already arrived and is still fresh. */
export function peekInstallPlan(appName: string): InstallPlan | undefined {
  const hit = cache.get(appName);
  return hit && Date.now() - hit.at < FRESH_MS ? hit.plan : undefined;
}

/** Open the install dialog once its plan is ready (or after a short, visible wait). */
export async function openInstall(appName: string): Promise<void> {
  const ui = useUi.getState();
  const key = pendingKey("planning", appName);
  if (peekInstallPlan(appName)) {
    ui.openDialog({ kind: "install", appName });
    return;
  }
  ui.begin(key);
  try {
    await Promise.race([prefetchInstallPlan(appName), new Promise((r) => setTimeout(r, MAX_WAIT_MS))]);
  } finally {
    useUi.getState().end(key);
  }
  useUi.getState().openDialog({ kind: "install", appName });
}
