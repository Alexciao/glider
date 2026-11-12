import { RpcError } from "../ipc/client";
import type {
  AdoptRequest,
  AdoptResult,
  FixAction,
  FolderValidation,
  GliderError,
  InstallPlan,
  JobInfo,
  JobStatus,
  Settings,
  UninstallMode,
} from "../ipc/contract.gen";
import { rpc } from "../ipc/rpc";
import { driveOf } from "./format";
import { toast } from "./notices";
import { getSnapshot, useApp } from "./store";
import { useUi, type ErrorSurface } from "./ui";

/* ------------------------------------------------------------------ errors */

export function toGliderError(e: unknown): GliderError {
  if (e instanceof RpcError) return e.error;
  const message = e instanceof Error ? e.message : String(e);
  return {
    code: "unknown",
    title: "Something went wrong",
    message: "An unexpected error occurred. Try again, or copy the logs if it keeps happening.",
    fix: "retry",
    detail: message,
  };
}

/** Errors that are the user's own choice, not failures. */
const SILENT = new Set(["authCancelled", "cancelled"]);

async function run<T>(key: string, fn: () => Promise<T>, onError: (e: GliderError) => void): Promise<T | undefined> {
  const ui = useUi.getState();
  ui.begin(key);
  try {
    return await fn();
  } catch (e) {
    const err = toGliderError(e);
    if (!SILENT.has(err.code)) onError(err);
    return undefined;
  } finally {
    useUi.getState().end(key);
  }
}

const inline =
  (surface: ErrorSurface, retry?: () => void, path?: string | null) =>
  (error: GliderError): void =>
    useUi.getState().setError(surface, { error, retry, path });

const asToast =
  (retry?: () => void) =>
  (error: GliderError): void => {
    toast({ level: "error", title: error.title, message: error.message, action: error.fix });
    if (retry) lastRetry = retry;
  };

let lastRetry: (() => void) | undefined;

const clear = (surface: ErrorSurface): void => useUi.getState().setError(surface, undefined);

/* ----------------------------------------------------------- settings sync */

/** While a settings write is in flight, incoming snapshots must not roll the optimistic value back. */
let settingsInFlight = 0;
export const hasSettingsInFlight = (): boolean => settingsInFlight > 0;
let settingsChain: Promise<unknown> = Promise.resolve();

/* ----------------------------------------------------------------- actions */

export async function refreshSnapshot(): Promise<void> {
  const s = await rpc().call("app.getSnapshot");
  useApp.getState().setSnapshot(s);
}

const patchJob = (id: string, status: JobStatus): void =>
  useApp.getState().patch((s) => ({ ...s, jobs: s.jobs.map((j) => (j.id === id ? { ...j, status } : j)) }));

const dirname = (p: string): string => p.replace(/[\\/][^\\/]*$/, "");

export const actions = {
  signIn(surface: ErrorSurface = "hero"): Promise<unknown> {
    clear(surface);
    return run("signIn", () => rpc().call("auth.signIn"), inline(surface, () => void actions.signIn(surface)));
  },

  async signOut(): Promise<void> {
    const ok = await run("signOut", () => rpc().call("auth.signOut"), asToast());
    if (ok) toast({ level: "info", title: "Signed out", duration: 3500 });
  },

  install(basePath: string, surface: ErrorSurface = "hero"): Promise<JobInfo | undefined> {
    clear(surface);
    return run("install", () => rpc().call("game.install", { basePath }), inline(surface, () => void actions.install(basePath, surface)));
  },

  update(): Promise<JobInfo | undefined> {
    clear("hero");
    return run("update", () => rpc().call("game.update"), inline("hero", () => void actions.update()));
  },

  verify(surface: ErrorSurface = "hero"): Promise<JobInfo | undefined> {
    clear(surface);
    return run("verify", () => rpc().call("game.verify"), inline(surface, () => void actions.verify(surface)));
  },

  /** Fire-and-forget: backend prefetches `launch --json` so Play is near-instant. Never surfaces errors. */
  prepareLaunch(): Promise<void> {
    return rpc().call("game.prepareLaunch").catch(() => undefined);
  },

  launch(): Promise<unknown> {
    clear("hero");
    return run("launch", () => rpc().call("game.launch"), inline("hero", () => void actions.launch()));
  },

  checkForUpdates(quiet = false): Promise<unknown> {
    return run(
      "check",
      async () => {
        const r = await rpc().call("game.checkForUpdates");
        if (!quiet && !r.updateAvailable) toast({ level: "success", title: "Fortnite is up to date", duration: 3000 });
        return r;
      },
      asToast(),
    );
  },

  async pauseJob(id: string): Promise<void> {
    patchJob(id, "paused");
    const r = await run(`job:${id}`, () => rpc().call("jobs.pause", { jobId: id }), inline("downloads"));
    if (!r) void refreshSnapshot();
  },

  async resumeJob(id: string): Promise<void> {
    clear("downloads");
    patchJob(id, "running");
    const r = await run(`job:${id}`, () => rpc().call("jobs.resume", { jobId: id }), inline("downloads"));
    if (!r) void refreshSnapshot();
  },

  async cancelJob(id: string): Promise<void> {
    patchJob(id, "cancelled");
    const r = await run(`job:${id}`, () => rpc().call("jobs.cancel", { jobId: id }), inline("downloads"));
    if (!r) void refreshSnapshot();
  },

  async dismissJob(id: string): Promise<void> {
    useApp.getState().patch((s) => ({ ...s, jobs: s.jobs.filter((j) => j.id !== id) }));
    const r = await run(`dismiss:${id}`, async () => (await rpc().call("jobs.dismiss", { jobId: id }), true), asToast());
    if (!r) void refreshSnapshot();
  },

  async uninstall(mode: UninstallMode): Promise<boolean> {
    clear("uninstall");
    const job = await run("uninstall", () => rpc().call("game.uninstall", { mode }), inline("uninstall", () => void actions.uninstall(mode)));
    if (job) useUi.getState().closeDialog();
    return !!job;
  },

  async move(destinationBase: string): Promise<boolean> {
    clear("move");
    const job = await run("move", () => rpc().call("game.move", { destinationBase }), inline("move", () => void actions.move(destinationBase)));
    if (job) {
      useUi.getState().closeDialog();
      useUi.getState().go("downloads");
    }
    return !!job;
  },

  adopt(req: AdoptRequest, surface: ErrorSurface = "onboarding"): Promise<AdoptResult | undefined> {
    clear(surface);
    return run("adopt", () => rpc().call("game.adopt", req), inline(surface, () => void actions.adopt(req, surface)));
  },

  planInstall(basePath: string | null): Promise<InstallPlan | undefined> {
    return run("plan", () => rpc().call("game.planInstall", { basePath }), inline("onboarding"));
  },

  validateFolder(path: string): Promise<FolderValidation | undefined> {
    return run("validate", () => rpc().call("game.validateFolder", { path }), inline("onboarding", undefined, path));
  },

  async pickFolder(initialPath: string | null, title: string | null): Promise<string | null> {
    const r = await run("pick", () => rpc().call("app.pickFolder", { initialPath, title }), asToast());
    return r?.path ?? null;
  },

  async rescanEgl(drives = false): Promise<void> {
    const r = await run("rescan", () => rpc().call(drives ? "egl.quickScan" : "egl.rescan"), asToast(() => void actions.rescanEgl(drives)));
    if (r) {
      const n = r.candidates.filter((c) => !c.isStale).length;
      toast({
        level: n > 0 ? "success" : "info",
        title: n > 0 ? `Found ${n} ${n === 1 ? "install" : "installs"}` : "No existing installs found",
        message: n > 0 ? "Epic Games Launcher has Fortnite on this PC." : r.launcherInstalled ? "Epic Games Launcher has no Fortnite install." : "Epic Games Launcher isn't installed.",
        duration: 3500,
      });
    }
  },

  async openInstallFolder(): Promise<void> {
    await run("openFolder", () => rpc().call("game.openInstallFolder"), asToast());
  },

  async openPath(path: string): Promise<void> {
    await run("openPath", () => rpc().call("app.openPath", { path }), asToast());
  },

  async openExternal(url: string): Promise<void> {
    await run("openExternal", () => rpc().call("app.openExternal", { url }), asToast());
  },

  async copyLogs(): Promise<void> {
    const r = await run("copyLogs", async () => (await rpc().call("app.copyLogs"), true), asToast());
    if (r) toast({ level: "success", title: "Logs copied", message: "Paste them into your bug report.", duration: 3000 });
  },

  async completeOnboarding(): Promise<void> {
    await run("onboarding", () => rpc().call("app.completeOnboarding"), asToast());
  },

  /** Optimistic: the switch moves instantly, writes are serialized, and a failure rolls back with a fix. */
  updateSettings(patch: Partial<Settings>): Promise<void> {
    const before = getSnapshot()?.settings;
    if (!before) return Promise.resolve();
    useApp.getState().patch((s) => ({ ...s, settings: { ...s.settings, ...patch } }));
    settingsInFlight++;
    settingsChain = settingsChain.then(async () => {
      try {
        const current = getSnapshot()?.settings;
        if (current) await rpc().call("settings.update", current);
      } catch (e) {
        const err = toGliderError(e);
        asToast()(err);
        void refreshSnapshot();
      } finally {
        settingsInFlight--;
      }
    });
    return settingsChain as Promise<void>;
  },

  async resetSettings(): Promise<void> {
    const r = await run("resetSettings", () => rpc().call("settings.reset"), asToast());
    if (r) toast({ level: "success", title: "Settings reset", duration: 3000 });
  },

  retryLast(): void {
    const s = getSnapshot();
    const failed = s?.jobs.filter((j) => j.status === "failed").sort((a, b) => (b.finishedAt ?? "").localeCompare(a.finishedAt ?? ""))[0];
    if (failed) void actions.retryJob(failed);
    else if (lastRetry) lastRetry();
    else void actions.checkForUpdates();
  },

  retryJob(job: JobInfo): Promise<unknown> {
    switch (job.kind) {
      case "install":
        return job.targetPath ? actions.install(dirname(job.targetPath)) : Promise.resolve();
      case "update":
        return actions.update();
      case "repair":
      case "verify":
        return actions.verify();
      case "move":
        return job.targetPath ? actions.move(dirname(job.targetPath)) : Promise.resolve();
      default:
        return actions.checkForUpdates(true);
    }
  },
};

/* -------------------------------------------------------------- fix actions */

export interface FixContext {
  retry?: () => void;
  /** A path relevant to the failure (used by freeSpace). */
  path?: string | null;
}

/** Runs the one-click fix attached to a GliderError. */
export function runFix(fix: FixAction, ctx: FixContext = {}): void {
  const ui = useUi.getState();
  const snap = getSnapshot();
  switch (fix) {
    case "signIn":
      void actions.signIn(snap?.onboarding.completed === false ? "onboarding" : "hero");
      return;
    case "repair":
      void actions.verify();
      return;
    case "retry":
    case "closeGame":
      if (ctx.retry) ctx.retry();
      else actions.retryLast();
      return;
    case "chooseFolder":
      if (snap?.game.installed) ui.openDialog({ kind: "move" });
      else ui.go("settings", "game");
      return;
    case "freeSpace": {
      const target = ctx.path ?? snap?.game.installed?.installPath ?? snap?.settings.defaultInstallBase;
      const drive = driveOf(target);
      if (drive) void actions.openPath(`${drive}\\`);
      else ui.go("settings", "game");
      return;
    }
    case "openLogs":
      ui.go("settings", "advanced");
      ui.focusLogs();
      return;
    case "waitForEgl":
      ui.go("downloads");
      return;
  }
}
