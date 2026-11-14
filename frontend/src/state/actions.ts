import { RpcError } from "../ipc/client";
import type {
  EglImportResult,
  EglReadiness,
  FixAction,
  FolderValidation,
  GliderError,
  OnboardingStep,
  InstallPlan,
  JobInfo,
  JobStatus,
  LaunchMode,
  LaunchResult,
  SaveSide,
  Settings,
  UninstallMode,
  VerifyMode,
} from "../ipc/contract.gen";
import { rpc } from "../ipc/rpc";
import { driveOf, dirname } from "./format";
import { getGame, useLibrary } from "./library";
import { toast } from "./notices";
import { goGame } from "./router";
import { getSnapshot, historyJobs, isActiveJob, useApp } from "./store";
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
    appName: null,
  };
}

/** Errors that are the user's own choice, not failures. (A closed sign-in window is not silent: Epic may have shown an error the user never reported.) */
const SILENT = new Set(["cancelled"]);

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

/** Pending-state key for a per-game operation, e.g. `launch:Fortnite`. */
export const pendingKey = (op: string, appName: string): string => `${op}:${appName}`;

const inline =
  (surface: ErrorSurface, retry?: () => void, path?: string | null, appName?: string | null) =>
  (error: GliderError): void =>
    useUi.getState().setError(surface, { error, retry, path, appName: appName ?? error.appName });

const asToast =
  (retry?: () => void) =>
  (error: GliderError): void => {
    toast({ level: "error", title: error.title, message: error.message, action: error.fix, appName: error.appName });
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

const patchQueue = (fn: (jobs: JobInfo[]) => JobInfo[]): void =>
  useApp.getState().patch((s) => ({ ...s, queue: { ...s.queue, jobs: fn(s.queue.jobs) } }));

/**
 * Where a job lands when moved to `toIndex` among the unfinished jobs (the backend's meaning of `jobs.reorder`): the
 * optimistic local order, so a drop does not flash back to the old order until the next snapshot.
 */
export function reorderActive(jobs: readonly JobInfo[], id: string, toIndex: number): JobInfo[] {
  const active = jobs.filter(isActiveJob);
  const rest = jobs.filter((j) => !isActiveJob(j));
  const from = active.findIndex((j) => j.id === id);
  if (from < 0) return [...jobs];
  const [moved] = active.splice(from, 1);
  active.splice(Math.max(0, Math.min(active.length, toIndex)), 0, moved!);
  return [...active, ...rest];
}

const patchJob = (id: string, status: JobStatus): void => patchQueue((jobs) => jobs.map((j) => (j.id === id ? { ...j, status } : j)));

const titleOf = (appName: string): string => getGame(appName)?.title ?? appName;

export const actions = {
  signIn(surface: ErrorSurface = "hero"): Promise<unknown> {
    clear(surface);
    return run("signIn", () => rpc().call("auth.signIn"), inline(surface, () => void actions.signIn(surface)));
  },

  async signOut(): Promise<void> {
    const ok = await run("signOut", () => rpc().call("auth.signOut"), asToast());
    if (ok) toast({ level: "info", title: "Signed out", duration: 3500 });
  },

  planInstall(appName: string, basePath: string | null): Promise<InstallPlan | undefined> {
    return run(pendingKey("plan", appName), () => rpc().call("game.planInstall", { appName, basePath, components: null, dlc: null }), inline("install", undefined, undefined, appName));
  },

  install(appName: string, basePath: string, surface: ErrorSurface = "hero"): Promise<JobInfo | undefined> {
    clear(surface);
    return run(
      pendingKey("install", appName),
      () => rpc().call("game.install", { appName, basePath, components: null, dlc: null, priority: false }),
      inline(surface, () => void actions.install(appName, basePath, surface), undefined, appName),
    );
  },

  update(appName: string): Promise<JobInfo | undefined> {
    clear("hero");
    return run(pendingKey("update", appName), () => rpc().call("game.update", { appName }), inline("hero", () => void actions.update(appName), undefined, appName));
  },

  verify(appName: string, surface: ErrorSurface = "hero"): Promise<JobInfo | undefined> {
    clear(surface);
    return run(pendingKey("verify", appName), () => rpc().call("game.verify", { appName }), inline(surface, () => void actions.verify(appName, surface), undefined, appName));
  },

  /** Fire-and-forget: backend prefetches the launch command so Play is near-instant. Never surfaces errors. */
  prepareLaunch(appName: string): Promise<void> {
    return rpc().call("game.prepareLaunch", { appName }).catch(() => undefined);
  },

  async launch(appName: string, opts: { mode?: LaunchMode | null; skipSaveSync?: boolean } = {}): Promise<LaunchResult | undefined> {
    clear("hero");
    const r = await run(
      pendingKey("launch", appName),
      () => rpc().call("game.launch", { appName, mode: opts.mode ?? null, skipSaveSync: opts.skipSaveSync ?? false }),
      inline("hero", () => void actions.launch(appName, opts), undefined, appName),
    );
    if (r) announceLaunch(appName, r);
    return r;
  },

  async closeGame(appName: string): Promise<void> {
    await run(pendingKey("close", appName), () => rpc().call("game.close", { appName }), asToast());
  },

  /** Link a third-party-store game (Ubisoft Connect / EA app) once. */
  async activate(appName: string): Promise<void> {
    const ok = await run(pendingKey("activate", appName), async () => (await rpc().call("game.activate", { appName }), true), inline("hero", undefined, undefined, appName));
    if (ok) toast({ level: "success", title: `${titleOf(appName)} is linked`, duration: 3500 });
  },

  async installPrerequisites(appName: string): Promise<void> {
    await run(pendingKey("prereq", appName), async () => (await rpc().call("game.installPrerequisites", { appName }), true), inline("hero", undefined, undefined, appName));
  },

  async resolveSaves(appName: string, keep: SaveSide, thenLaunch: boolean): Promise<void> {
    const r = await run(pendingKey("saves", appName), () => rpc().call("saves.resolve", { appName, keep, thenLaunch }), inline("hero", undefined, undefined, appName));
    if (r) announceLaunch(appName, r);
  },

  async setFavorite(appName: string, value: boolean): Promise<void> {
    useLibrary.getState().patchGame(appName, { favorite: value });
    await run(pendingKey("favorite", appName), () => rpc().call("library.setFavorite", { appName, value }), asToast());
  },

  async setHidden(appName: string, value: boolean): Promise<void> {
    useLibrary.getState().patchGame(appName, { hidden: value });
    await run(pendingKey("hidden", appName), () => rpc().call("library.setHidden", { appName, value }), asToast());
  },

  async refreshLibrary(): Promise<void> {
    await run("refreshLibrary", () => rpc().call("library.refresh"), asToast(() => void actions.refreshLibrary()));
  },

  async checkForUpdates(): Promise<void> {
    await run("check", () => rpc().call("library.checkUpdates"), asToast(() => void actions.checkForUpdates()));
  },

  async updateAll(): Promise<void> {
    const r = await run("updateAll", () => rpc().call("library.updateAll"), asToast(() => void actions.updateAll()));
    if (r) toast({ level: "info", title: r.jobs.length === 0 ? "Nothing to update" : `Updating ${r.jobs.length} ${r.jobs.length === 1 ? "game" : "games"}`, duration: 3500 });
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
    patchQueue((jobs) => jobs.filter((j) => j.id !== id));
    const r = await run(`dismiss:${id}`, async () => (await rpc().call("jobs.dismiss", { jobId: id }), true), asToast());
    if (!r) void refreshSnapshot();
  },

  async moveJobToTop(id: string): Promise<void> {
    patchQueue((jobs) => reorderActive(jobs, id, jobs[0]?.status === "running" && jobs[0].id !== id ? 1 : 0));
    const q = await run(`job:${id}`, () => rpc().call("jobs.moveToTop", { jobId: id }), inline("downloads"));
    if (q) void refreshSnapshot();
  },

  async reorderJob(id: string, toIndex: number): Promise<void> {
    patchQueue((jobs) => reorderActive(jobs, id, toIndex));
    const q = await run(`job:${id}`, () => rpc().call("jobs.reorder", { jobId: id, toIndex }), inline("downloads"));
    if (q) void refreshSnapshot();
  },

  async pauseQueue(): Promise<void> {
    await run("queue", () => rpc().call("queue.pauseAll"), inline("downloads"));
  },

  async resumeQueue(): Promise<void> {
    await run("queue", () => rpc().call("queue.resumeAll"), inline("downloads"));
  },

  async clearFinished(): Promise<void> {
    await run("queue", () => rpc().call("queue.clearFinished"), asToast());
  },

  async uninstall(appName: string, mode: UninstallMode): Promise<boolean> {
    clear("uninstall");
    const job = await run(pendingKey("uninstall", appName), () => rpc().call("game.uninstall", { appName, mode }), inline("uninstall", () => void actions.uninstall(appName, mode), undefined, appName));
    if (job) useUi.getState().closeDialog();
    return !!job;
  },

  async move(appName: string, destinationBase: string): Promise<boolean> {
    clear("move");
    const job = await run(pendingKey("move", appName), () => rpc().call("game.move", { appName, destinationBase }), inline("move", () => void actions.move(appName, destinationBase), undefined, appName));
    if (job) {
      useUi.getState().closeDialog();
      useUi.getState().go("downloads");
    }
    return !!job;
  },

  validateFolder(path: string): Promise<FolderValidation | undefined> {
    return run("validate", () => rpc().call("egl.validateFolder", { path }), inline("onboarding", undefined, path));
  },

  async importInstalls(ids: string[], verify: VerifyMode = "auto", surface: ErrorSurface = "onboarding"): Promise<EglImportResult | undefined> {
    clear(surface);
    return run("import", () => rpc().call("egl.import", { ids, verify }), inline(surface, () => void actions.importInstalls(ids, verify, surface)));
  },

  importFolder(path: string, verify: VerifyMode = "auto", surface: ErrorSurface = "onboarding"): Promise<JobInfo | undefined> {
    clear(surface);
    return run("import", () => rpc().call("egl.importFolder", { path, verify }), inline(surface, () => void actions.importFolder(path, verify, surface), path));
  },

  async pickFolder(initialPath: string | null, title: string | null): Promise<string | null> {
    const r = await run("pick", () => rpc().call("app.pickFolder", { initialPath, title }), asToast());
    return r?.path ?? null;
  },

  async rescanEgl(drives = false): Promise<void> {
    const r = await run("rescan", () => rpc().call(drives ? "egl.quickScan" : "egl.rescan"), asToast(() => void actions.rescanEgl(drives)));
    if (r) {
      const n = r.installs.filter((i) => i.status === "importable").length;
      toast({
        level: n > 0 ? "success" : "info",
        title: n > 0 ? `Found ${n} ${n === 1 ? "install" : "installs"} to import` : "No new installs found",
        message: n > 0 ? "Epic Games Launcher has games Glider can take over without downloading." : r.launcherInstalled ? "Epic Games Launcher has nothing new to import." : "Epic Games Launcher isn't installed.",
        duration: 3500,
      });
    }
  },

  async installOverlay(): Promise<void> {
    await run("overlay", () => rpc().call("overlay.install"), asToast(() => void actions.installOverlay()));
  },

  async setOverlayEnabled(value: boolean): Promise<void> {
    await run("overlay", () => rpc().call("overlay.setEnabled", { appName: "eos-overlay", value }), asToast());
  },

  checkEglReadiness(): Promise<EglReadiness | undefined> {
    return run("readiness", () => rpc().call("egl.checkReadiness"), asToast());
  },

  async openInstallFolder(appName: string): Promise<void> {
    await run(pendingKey("openFolder", appName), () => rpc().call("game.openFolder", { appName }), asToast());
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

  async setOnboardingStep(step: OnboardingStep, skipped = false): Promise<void> {
    await run("onboarding", () => rpc().call("onboarding.setStep", { step, skipped }), asToast());
  },

  async completeOnboarding(): Promise<void> {
    await run("onboarding", () => rpc().call("onboarding.complete"), asToast());
  },

  async restartOnboarding(): Promise<void> {
    await run("onboarding", () => rpc().call("onboarding.restart"), asToast());
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
    const failed = s ? historyJobs(s.queue.jobs).find((j) => j.status === "failed") : undefined;
    if (failed) void actions.retryJob(failed);
    else if (lastRetry) lastRetry();
    else void actions.checkForUpdates();
  },

  retryJob(job: JobInfo): Promise<unknown> {
    switch (job.kind) {
      case "install":
        return job.targetPath ? actions.install(job.appName, dirname(job.targetPath)) : Promise.resolve();
      case "update":
        return actions.update(job.appName);
      case "repair":
      case "verify":
        return actions.verify(job.appName);
      case "move":
        return job.targetPath ? actions.move(job.appName, dirname(job.targetPath)) : Promise.resolve();
      case "overlay":
        return run("overlay", () => rpc().call("overlay.install"), asToast());
      default:
        return actions.checkForUpdates();
    }
  },
};

/** What a launch/resolve result means for the user when nothing started. (The real dialogs come with the game page.) */
function announceLaunch(appName: string, r: LaunchResult): void {
  switch (r.outcome) {
    case "saveConflict":
      toast({ level: "warning", title: "Your saves differ", message: `${titleOf(appName)}'s cloud and local saves both changed. Choose which to keep.`, action: "resolveSaves", appName });
      return;
    case "needsActivation":
      toast({ level: "warning", title: "Link your account first", message: r.message ?? undefined, action: "activate", appName });
      return;
    case "thirdPartyLauncher":
      toast({ level: "info", title: `Opening ${getGame(appName)?.thirdPartyName ?? "the launcher"}`, message: r.message ?? undefined, duration: 3500 });
      return;
    case "started":
      return;
  }
}

/* -------------------------------------------------------------- fix actions */

export interface FixContext {
  retry?: () => void;
  /** A path relevant to the failure (used by freeSpace). */
  path?: string | null;
  /** The game the failure is about. */
  appName?: string | null;
}

/** Runs the one-click fix attached to a GliderError. */
export function runFix(fix: FixAction, ctx: FixContext = {}): void {
  const ui = useUi.getState();
  const snap = getSnapshot();
  const game = ctx.appName ? getGame(ctx.appName) : undefined;
  switch (fix) {
    case "signIn":
      void actions.signIn(snap?.onboarding.completed === false ? "onboarding" : "hero");
      return;
    case "repair":
      if (ctx.appName) void actions.verify(ctx.appName);
      return;
    case "retry":
    case "closeGame":
      if (ctx.retry) ctx.retry();
      else actions.retryLast();
      return;
    case "chooseFolder":
      if (game && game.installStatus === "installed") ui.openDialog({ kind: "move", appName: game.appName });
      else ui.go("settings", "library");
      return;
    case "freeSpace": {
      const target = ctx.path ?? game?.installPath ?? snap?.settings.defaultInstallBase;
      const drive = driveOf(target);
      if (drive) void actions.openPath(`${drive}\\`);
      else ui.go("settings", "library");
      return;
    }
    case "openLogs":
      ui.go("settings", "advanced");
      ui.focusLogs();
      return;
    case "waitForEgl":
      ui.go("downloads");
      return;
    case "activate":
      if (ctx.appName) void actions.activate(ctx.appName);
      return;
    case "resolveSaves":
      if (ctx.appName) goGame(ctx.appName);
      return;
    case "installPrerequisites":
      if (ctx.appName) void actions.installPrerequisites(ctx.appName);
      return;
    case "playOffline":
      if (ctx.appName) void actions.launch(ctx.appName, { mode: "offline" });
      return;
  }
}

