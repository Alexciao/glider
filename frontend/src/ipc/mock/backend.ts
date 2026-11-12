import type {
  AppSnapshot,
  ErrorCode,
  FolderValidation,
  GliderError,
  InstallPlan,
  JobInfo,
  JobKind,
  Notice,
  RpcEventMap,
  RpcEventName,
  RpcMethodName,
  RpcMethods,
  RunState,
  UpdateCheckResult,
} from "../contract.gen";
import {
  ACCOUNT,
  DEFAULT_INSTALL_BASE,
  DEFAULT_SETTINGS,
  EGL_BLOCKED_REASON,
  FULL_DOWNLOAD_BYTES,
  FULL_INSTALL_BYTES,
  GiB,
  INSTALL_MARGIN_BYTES,
  INVALID_FOLDER_REASON,
  LATEST_VERSION,
  OLD_VERSION,
  STALE_CANDIDATE_REASON,
  UPDATE_DOWNLOAD_BYTES,
  UPDATE_HEADROOM_BYTES,
  clone,
  iso,
  shortVersion,
} from "./constants";
import { freeBytes, joinPath, samePath, trimSlashes } from "./disk";
import { Errors, errorForCode, fail, toGliderError } from "./errors";
import { LICENSES } from "./licenses";
import { generateLogs, logFileName } from "./logs";
import type { MockController, MockOptions, ScenarioName } from "./options";
import { SCENARIOS, buildScenario, type JobMeta } from "./scenarios";
import { JobSim, TICK_MS, TICK_S, buildJobSim } from "./sim";

type Handlers = {
  [M in RpcMethodName]: (params: RpcMethods[M]["params"]) => Promise<RpcMethods[M]["result"]>;
};

interface ActiveJob {
  info: JobInfo;
  sim: JobSim;
}

interface Waiter {
  resolve: (job: JobInfo) => void;
  reject: (e: unknown) => void;
}

type Timer = ReturnType<typeof setTimeout>;

interface Resolved {
  scenario: ScenarioName;
  seek: number | null;
  timeScale: number;
  timeScaleExplicit: boolean;
  freeze: boolean;
  art: "default" | "none";
  backdrop: "mica" | "solid";
  latency: number;
  seed: number;
  eacInstalled: boolean;
}

const MAX_FINISHED_JOBS = 8;
const EGL_AUTO_RESUME_MS = 14_000;
const GAME_AUTO_EXIT_MS = 120_000;

function resolveOptions(o: MockOptions, prev?: Resolved): Resolved {
  const scenario = o.scenario ?? prev?.scenario ?? "fresh";
  const seek = o.seek ?? (o.scenario !== undefined || !prev ? (scenario === "downloading" ? 0.42 : null) : prev.seek);
  return {
    scenario,
    seek,
    timeScale: o.timeScale ?? prev?.timeScale ?? 12,
    timeScaleExplicit: o.timeScale !== undefined || (prev?.timeScaleExplicit ?? false),
    freeze: o.freeze ?? prev?.freeze ?? false,
    art: o.art ?? prev?.art ?? "default",
    backdrop: o.backdrop ?? prev?.backdrop ?? "solid",
    latency: o.latency ?? prev?.latency ?? 1,
    seed: o.seed ?? prev?.seed ?? 1337,
    eacInstalled: o.eacInstalled ?? prev?.eacInstalled ?? true,
  };
}

const isRequest = (m: unknown): m is { type: "request"; id: number; method: string; params: unknown } =>
  typeof m === "object" && m !== null && (m as { type?: unknown }).type === "request" && typeof (m as { id?: unknown }).id === "number";

export class MockBackend {
  private cfg: Resolved;
  private s!: AppSnapshot;
  private metas = new Map<string, JobMeta>();
  private active: ActiveJob | null = null;
  private waiters = new Map<string, Waiter[]>();

  private ticker: ReturnType<typeof setInterval> | null = null;
  private readonly timers = new Set<Timer>();
  /** Timers that must survive a scenario reset (in-flight call latencies); they reject as cancelled when they fire. */
  private readonly stickyTimers = new Set<Timer>();
  private eglResumeTimer: Timer | null = null;
  private autoExitTimer: Timer | null = null;

  private epoch = 0;
  private jobSeq = 0;
  private noticeSeq = 0;
  private failCode: ErrorCode | null = null;
  private eacInstalled = true;
  private maximized = false;
  /** Logs are generated relative to this, so repeated reads return identical text. */
  private logClock = Date.now();
  private flushQueued = false;
  private snapshotServed = false;
  private replayPending = true;
  private pickLocate = 0;
  private pickBase = 0;
  private readonly listeners = new Set<() => void>();
  private out: (message: unknown) => void = () => {};

  constructor(opts: MockOptions = {}) {
    this.cfg = resolveOptions(opts);
    this.failCode = opts.fail ?? null;
    this.load();
  }

  setOutput(out: (message: unknown) => void): void {
    this.out = out;
  }

  // ───────────────────────────── transport plumbing ─────────────────────────────

  handle(message: unknown): void {
    let m: unknown = message;
    if (typeof m === "string") {
      try {
        m = JSON.parse(m);
      } catch {
        return;
      }
    }
    if (!isRequest(m)) return;
    const { id, method, params } = m;
    void this.dispatch(method, params).then(
      (result) => {
        this.out({ type: "response", id, result: result ?? null, error: null });
        if (method === "app.getSnapshot") this.afterSnapshotServed();
      },
      (e: unknown) => this.out({ type: "response", id, result: null, error: toGliderError(e) }),
    );
  }

  private async dispatch(method: string, params: unknown): Promise<unknown> {
    if (!Object.hasOwn(this.handlers, method)) throw fail(Errors.invalidRequest(`Unknown method: ${method}`));
    const handler = this.handlers[method as RpcMethodName] as (p: unknown) => Promise<unknown>;
    return handler(params);
  }

  private event<E extends RpcEventName>(event: E, data: RpcEventMap[E]): void {
    this.out({ type: "event", event, data });
  }

  /** Full snapshot on every change; same-tick changes coalesce into one push. */
  private emitState(): void {
    if (this.flushQueued) return;
    this.flushQueued = true;
    queueMicrotask(() => {
      this.flushQueued = false;
      this.event("state", this.snapshot());
      this.listeners.forEach((l) => l());
    });
  }

  private snapshot(): AppSnapshot {
    return clone(this.s);
  }

  private notice(n: Omit<Notice, "id">): void {
    this.event("notice", { id: `n-${++this.noticeSeq}`, ...n });
  }

  // ───────────────────────────── timing helpers ─────────────────────────────

  private timer(ms: number, fn: () => void, sticky = false): Timer {
    const t = setTimeout(() => {
      this.timers.delete(t);
      this.stickyTimers.delete(t);
      fn();
    }, ms);
    this.timers.add(t);
    if (sticky) this.stickyTimers.add(t);
    return t;
  }

  private wait(ms: number): Promise<void> {
    const epoch = this.epoch;
    return new Promise((resolve, reject) => {
      this.timer(
        ms,
        () => (epoch === this.epoch ? resolve() : reject(fail(Errors.cancelled()))),
        true,
      );
    });
  }

  /** A simulated latency (scaled by `latency`). Rejects as `cancelled` if the scenario was reset meanwhile. */
  private sleep(ms: number): Promise<void> {
    return this.wait(ms * this.cfg.latency);
  }

  /** With `freeze`, simulated progress holds here until the scenario is reloaded. */
  private async holdWhileFrozen(): Promise<void> {
    while (this.cfg.freeze) await this.wait(200);
  }

  // ───────────────────────────── scenario lifecycle ─────────────────────────────

  private clearSimTimers(): void {
    for (const t of this.timers) {
      if (!this.stickyTimers.has(t)) {
        clearTimeout(t);
        this.timers.delete(t);
      }
    }
    this.eglResumeTimer = null;
    this.autoExitTimer = null;
    if (this.ticker !== null) clearInterval(this.ticker);
    this.ticker = null;
  }

  /** Build the scenario in `cfg` and hydrate its running job. Does not emit. */
  private load(): void {
    this.epoch++;
    this.clearSimTimers();
    for (const list of this.waiters.values()) list.forEach((w) => w.reject(fail(Errors.cancelled())));
    this.waiters.clear();

    const built = buildScenario(this.cfg.scenario, { backdrop: this.cfg.backdrop, art: this.cfg.art }, Date.now());
    this.s = built.snapshot;
    this.metas = built.metas;
    this.active = null;
    this.eacInstalled = this.cfg.eacInstalled;
    this.maximized = false;
    this.logClock = Date.now();
    this.jobSeq = this.s.jobs.reduce((n, j) => Math.max(n, Number(/(\d+)$/.exec(j.id)?.[1] ?? 0)), 0);

    const live = this.s.jobs.find((j) => j.status === "running" || j.status === "paused" || j.status === "waiting");
    if (live) {
      const sim = this.makeSim(live);
      this.active = { info: live, sim };
      if (live.id === built.seekJobId && this.cfg.seek !== null) sim.seek(this.cfg.seek);
      if (live.status === "running") {
        live.progress = sim.progress();
        this.ensureTicker();
      } else if (live.status === "waiting") {
        this.scheduleEglResume();
      }
    }
    if (this.s.game.runState === "running") this.scheduleAutoExit();
  }

  loadScenario(name: ScenarioName, opts: Partial<MockOptions> = {}): void {
    this.cfg = resolveOptions({ ...opts, scenario: name }, this.cfg);
    this.failCode = opts.fail ?? null;
    this.replayPending = true;
    this.load();
    this.emitState();
    this.listeners.forEach((l) => l());
    if (this.snapshotServed) {
      this.replayPending = false;
      queueMicrotask(() => this.replayProgress());
    }
  }

  dispose(): void {
    this.epoch++;
    for (const t of this.timers) clearTimeout(t);
    this.timers.clear();
    this.stickyTimers.clear();
    if (this.ticker !== null) clearInterval(this.ticker);
    this.ticker = null;
    this.listeners.clear();
  }

  private afterSnapshotServed(): void {
    this.snapshotServed = true;
    if (!this.replayPending) return;
    this.replayPending = false;
    this.replayProgress();
  }

  /** Pre-seed a late-joining UI's sparkline with ~90 synthetic past samples (oldest first). */
  private replayProgress(): void {
    const a = this.active;
    if (!a || a.info.status !== "running") return;
    for (const p of a.sim.history(90, this.cfg.seed)) this.event("progress", p);
  }

  // ───────────────────────────── jobs ─────────────────────────────

  private makeSim(info: JobInfo): JobSim {
    const installBytes = this.s.game.installed?.installSizeBytes ?? FULL_INSTALL_BYTES;
    const uninstallSeconds = this.metas.get(info.id)?.uninstallMode === "deleteFiles" ? 1.8 : 0.6;
    return buildJobSim(
      info.id,
      info.kind,
      { installBytes, uninstallSeconds },
      this.cfg.timeScale,
      this.cfg.timeScaleExplicit,
      this.cfg.seed + Number(/(\d+)$/.exec(info.id)?.[1] ?? 0),
    );
  }

  private createJob(kind: JobKind, extra: Partial<JobInfo> = {}, meta: JobMeta = {}): JobInfo {
    const info: JobInfo = {
      id: `job-${++this.jobSeq}`,
      kind,
      status: "queued",
      progress: null,
      error: null,
      createdAt: iso(),
      finishedAt: null,
      targetPath: null,
      fromVersion: null,
      toVersion: null,
      blockedReason: null,
      ...extra,
    };
    this.s.jobs.push(info);
    this.metas.set(info.id, meta);
    this.pump();
    this.emitState();
    return info;
  }

  /** Start the next queued job if nothing is active (the queue is strictly serial). */
  private pump(): void {
    if (this.active) return;
    const next = this.s.jobs.find((j) => j.status === "queued");
    if (!next) return;
    const sim = this.makeSim(next);
    this.active = { info: next, sim };
    next.progress = sim.progress();
    if (this.s.egl.busyWithGame) {
      next.status = "waiting";
      next.blockedReason = EGL_BLOCKED_REASON;
      this.scheduleEglResume();
    } else {
      next.status = "running";
      this.ensureTicker();
    }
  }

  private ensureTicker(): void {
    if (this.ticker !== null) return;
    this.ticker = setInterval(() => this.tick(), TICK_MS);
  }

  private stopTickerIfIdle(): void {
    if (this.ticker !== null && this.active?.info.status !== "running") {
      clearInterval(this.ticker);
      this.ticker = null;
    }
  }

  private tick(): void {
    const a = this.active;
    if (!a || a.info.status !== "running") {
      this.stopTickerIfIdle();
      return;
    }
    const result = this.cfg.freeze ? "tick" : a.sim.advance(TICK_S);
    const progress = a.sim.progress();
    a.info.progress = progress;
    this.event("progress", progress);
    if (result === "done") this.finish(a.info, "completed");
    else if (result === "phase") this.emitState();
  }

  private scheduleEglResume(): void {
    if (this.eglResumeTimer) clearTimeout(this.eglResumeTimer);
    this.eglResumeTimer = null;
    if (this.cfg.freeze) return;
    this.eglResumeTimer = this.timer(EGL_AUTO_RESUME_MS, () => this.setEglBusy(false));
  }

  setEglBusy(busy: boolean): void {
    this.s.egl.busyWithGame = busy;
    if (busy) this.s.egl.launcherRunning = true;
    const a = this.active;
    if (busy && a?.info.status === "running") {
      a.info.status = "waiting";
      a.info.blockedReason = EGL_BLOCKED_REASON;
      this.stopTickerIfIdle();
      this.scheduleEglResume();
    } else if (!busy && a?.info.status === "waiting") {
      a.info.status = "running";
      a.info.blockedReason = null;
      if (this.eglResumeTimer) clearTimeout(this.eglResumeTimer);
      this.eglResumeTimer = null;
      this.ensureTicker();
    }
    this.emitState();
  }

  /** Move a job to a terminal state, apply its effect on the game, and start the next queued job. */
  private finish(info: JobInfo, status: "completed" | "failed" | "cancelled", error: GliderError | null = null): void {
    const wasActive = this.active?.info.id === info.id;
    info.status = status;
    info.finishedAt = iso();
    info.error = error;
    info.blockedReason = null;
    if (wasActive) {
      if (status === "completed" && info.progress) info.progress = this.active!.sim.progress();
      this.active = null;
      if (this.eglResumeTimer) clearTimeout(this.eglResumeTimer);
      this.eglResumeTimer = null;
    }
    this.stopTickerIfIdle();
    const meta = this.metas.get(info.id) ?? {};
    if (status === "completed") this.onCompleted(info, meta);
    else if (status === "failed") this.onFailed(info, error);
    else this.onCancelled(info);
    this.trimHistory();
    const list = this.waiters.get(info.id);
    if (list) {
      this.waiters.delete(info.id);
      list.forEach((w) => w.resolve(clone(info)));
    }
    this.pump();
    this.emitState();
  }

  private trimHistory(): void {
    const finished = this.s.jobs.filter((j) => j.status === "completed" || j.status === "failed" || j.status === "cancelled");
    const drop = new Set(finished.slice(0, Math.max(0, finished.length - MAX_FINISHED_JOBS)).map((j) => j.id));
    if (drop.size) this.s.jobs = this.s.jobs.filter((j) => !drop.has(j.id));
  }

  private awaitJob(id: string): Promise<JobInfo> {
    return new Promise((resolve, reject) => {
      const list = this.waiters.get(id) ?? [];
      list.push({ resolve, reject });
      this.waiters.set(id, list);
    });
  }

  private onCompleted(info: JobInfo, meta: JobMeta): void {
    const g = this.s.game;
    const egl = this.s.egl;
    switch (info.kind) {
      case "install":
        g.installStatus = "installed";
        g.installed = {
          installPath: info.targetPath ?? joinPath(DEFAULT_INSTALL_BASE, "Fortnite"),
          version: LATEST_VERSION,
          installSizeBytes: FULL_INSTALL_BYTES,
          sharedWithEgl: false,
          needsVerification: false,
          installedAt: iso(),
        };
        g.latestVersion = LATEST_VERSION;
        g.updateAvailable = false;
        g.downloadSizeBytes = g.installSizeBytes = null;
        this.notice({ level: "success", title: "Fortnite is ready to play", message: `Version ${shortVersion(LATEST_VERSION)} is installed.`, action: null });
        break;

      case "update": {
        if (g.installed) {
          g.installed.version = LATEST_VERSION;
          g.installed.installSizeBytes = FULL_INSTALL_BYTES;
          const c = egl.candidates.find((x) => samePath(x.installPath, g.installed!.installPath));
          if (c && this.s.settings.eglSync) c.version = LATEST_VERSION;
        }
        g.updateAvailable = false;
        g.downloadSizeBytes = null;
        this.notice({ level: "success", title: "Fortnite is ready to play", message: `Updated to ${shortVersion(LATEST_VERSION)}.`, action: null });
        break;
      }

      case "import": {
        const version = meta.adoptVersion ?? LATEST_VERSION;
        const fromEgl = meta.adoptFromEgl === true;
        const path = meta.adoptPath ?? joinPath(DEFAULT_INSTALL_BASE, "Fortnite");
        const needsVerify = meta.verifyMode === "background" || meta.verifyMode === "full";
        g.installStatus = "installed";
        g.installed = {
          installPath: path,
          version,
          installSizeBytes: Math.round(94.8 * GiB),
          sharedWithEgl: fromEgl,
          needsVerification: needsVerify,
          installedAt: iso(),
        };
        g.latestVersion = g.latestVersion ?? LATEST_VERSION;
        g.updateAvailable = version !== (g.latestVersion ?? LATEST_VERSION);
        g.downloadSizeBytes = g.updateAvailable ? UPDATE_DOWNLOAD_BYTES : null;
        g.installSizeBytes = g.updateAvailable ? FULL_INSTALL_BYTES : null;
        egl.syncAvailable = fromEgl;
        egl.manifestPath = fromEgl ? egl.manifestPath ?? "C:\\ProgramData\\Epic\\EpicGamesLauncher\\Data\\Manifests\\3F2A91C4B7E04D11A6C0D8E5F27B9A13.item" : null;
        this.s.onboarding = { ...this.s.onboarding, step: "done" };
        this.notice({ level: "success", title: "Fortnite is ready to play", message: `Using the existing install at ${path}.`, action: null });
        if (needsVerify) {
          const v = this.createJob("verify", { targetPath: path, toVersion: version }, {});
          meta.followUpJobId = v.id;
        }
        break;
      }

      case "verify":
      case "repair":
        if (g.installed) g.installed.needsVerification = false;
        if (g.installStatus === "broken") g.installStatus = "installed";
        this.notice(
          info.kind === "repair"
            ? { level: "success", title: "Repair finished", message: "Fortnite's files are in good shape.", action: null }
            : { level: "success", title: "Files verified", message: "No problems found.", action: null },
        );
        break;

      case "move": {
        const g2 = g.installed;
        if (g2 && info.targetPath) {
          const old = g2.installPath;
          g2.installPath = info.targetPath;
          const c = egl.candidates.find((x) => samePath(x.installPath, old));
          if (c) {
            if (this.s.settings.eglSync) {
              c.installPath = info.targetPath;
            } else {
              // EGL still points at the old folder and no longer finds the game.
              Object.assign(c, { isStale: true, folderExists: false, executableFound: false, sizeBytes: null });
              g2.sharedWithEgl = false;
              egl.syncAvailable = false;
            }
          }
        }
        this.notice({ level: "success", title: "Install moved", message: info.targetPath, action: null });
        break;
      }

      case "uninstall": {
        const old = g.installed?.installPath ?? null;
        g.installed = null;
        g.installStatus = "notInstalled";
        g.updateAvailable = false;
        g.downloadSizeBytes = FULL_DOWNLOAD_BYTES;
        g.installSizeBytes = FULL_INSTALL_BYTES;
        egl.syncAvailable = false;
        if (meta.uninstallMode === "deleteFiles" && old) egl.candidates = egl.candidates.filter((c) => !samePath(c.installPath, old));
        this.notice(
          meta.uninstallMode === "deleteFiles"
            ? { level: "success", title: "Fortnite uninstalled", message: "The game files were deleted.", action: null }
            : { level: "success", title: "Removed from Glider", message: "Your files and the Epic Games Launcher install were left untouched.", action: null },
        );
        break;
      }
    }
  }

  private onFailed(info: JobInfo, error: GliderError | null): void {
    const g = this.s.game;
    if (info.kind === "install") g.installStatus = "notInstalled";
    if (error?.code === "corrupted" && g.installed) g.installStatus = "broken";
    if (error) this.notice({ level: "error", title: error.title, message: error.message, action: error.fix });
  }

  private onCancelled(info: JobInfo): void {
    if (info.kind === "install") this.s.game.installStatus = "notInstalled";
  }

  // ───────────────────────────── controller ─────────────────────────────

  failActiveJob(code: ErrorCode): void {
    const a = this.active;
    if (!a) return;
    this.finish(a.info, "failed", errorForCode(code));
  }

  emitNotice(n: Omit<Notice, "id">): void {
    this.notice(n);
  }

  endGame(): void {
    const g = this.s.game;
    if (g.runState !== "running") return;
    if (this.autoExitTimer) clearTimeout(this.autoExitTimer);
    this.autoExitTimer = null;
    this.setRunState("closing");
    this.timer(700 * this.cfg.latency, () => {
      this.setRunState("idle");
      this.notice({ level: "info", title: "Fortnite closed", message: null, action: null });
    });
  }

  private scheduleAutoExit(): void {
    if (this.autoExitTimer) clearTimeout(this.autoExitTimer);
    this.autoExitTimer = null;
    if (this.cfg.freeze) return;
    this.autoExitTimer = this.timer(GAME_AUTO_EXIT_MS, () => this.endGame());
  }

  private setRunState(state: RunState): void {
    const g = this.s.game;
    g.runState = state;
    if (state === "idle") g.runningSince = null;
    this.emitState();
  }

  createController(): MockController {
    return {
      scenarios: SCENARIOS,
      getScenario: () => this.cfg.scenario,
      loadScenario: (name, opts) => this.loadScenario(name, opts),
      failNext: (code) => {
        this.failCode = code;
      },
      failActiveJob: (code) => this.failActiveJob(code),
      emitNotice: (n) => this.emitNotice(n),
      endGame: () => this.endGame(),
      setEglBusy: (busy) => this.setEglBusy(busy),
      subscribe: (l) => {
        this.listeners.add(l);
        return () => {
          this.listeners.delete(l);
        };
      },
      dispose: () => this.dispose(),
    };
  }

  // ───────────────────────────── guards ─────────────────────────────

  /** Consume a pending injected failure (`fail=` / `failNext`). */
  private takeFail(): ErrorCode | null {
    const code = this.failCode;
    this.failCode = null;
    return code;
  }

  private consumeFail(): void {
    const code = this.takeFail();
    if (code) throw fail(errorForCode(code));
  }

  private requireAuth(): void {
    switch (this.s.auth.status) {
      case "signedIn":
        return;
      case "expired":
        throw fail(Errors.authExpired());
      default:
        throw fail(Errors.notSignedIn());
    }
  }

  private requireInstalled(): NonNullable<AppSnapshot["game"]["installed"]> {
    const installed = this.s.game.installed;
    if (!installed) throw fail(Errors.notInstalled());
    return installed;
  }

  private requireGameClosed(): void {
    if (this.s.game.runState !== "idle") throw fail(Errors.gameRunning());
  }

  private diskFull(): boolean {
    return this.cfg.scenario === "diskFull";
  }

  // ───────────────────────────── install planning ─────────────────────────────

  private plan(basePath: string | null): InstallPlan {
    const base = trimSlashes(basePath ?? this.s.settings.defaultInstallBase ?? DEFAULT_INSTALL_BASE);
    const gamePath = joinPath(base, "Fortnite");
    const free = freeBytes(base, this.diskFull());
    const existing =
      this.s.egl.candidates.some((c) => samePath(c.installPath, gamePath) && c.folderExists && c.executableFound && !c.isStale) ||
      (this.s.game.installed !== null && samePath(this.s.game.installed.installPath, gamePath));
    return {
      basePath: base,
      gamePath,
      version: LATEST_VERSION,
      downloadSizeBytes: FULL_DOWNLOAD_BYTES,
      installSizeBytes: FULL_INSTALL_BYTES,
      freeBytes: free,
      hasEnoughSpace: free >= FULL_INSTALL_BYTES + INSTALL_MARGIN_BYTES,
      existingInstallFound: existing,
    };
  }

  private validate(path: string): FolderValidation {
    const clean = trimSlashes(path.trim());
    if (/fortnite$/i.test(clean)) return { valid: true, gamePath: clean, version: OLD_VERSION, reason: null };
    return { valid: false, gamePath: null, version: null, reason: INVALID_FOLDER_REASON };
  }

  private onSignedIn(): void {
    const g = this.s.game;
    g.latestVersion = g.latestVersion ?? LATEST_VERSION;
    if (g.installed) {
      g.updateAvailable = g.installed.version !== g.latestVersion;
      g.downloadSizeBytes = g.updateAvailable ? UPDATE_DOWNLOAD_BYTES : null;
    } else {
      g.downloadSizeBytes = FULL_DOWNLOAD_BYTES;
      g.installSizeBytes = FULL_INSTALL_BYTES;
    }
  }

  // ───────────────────────────── RPC handlers ─────────────────────────────

  private readonly handlers: Handlers = {
    // ---- app ----
    "app.completeOnboarding": async () => {
      await this.sleep(40);
      this.s.onboarding = { completed: true, step: "done" };
      this.emitState();
    },
    "app.copyLogs": async () => {
      await this.sleep(30);
      console.debug("[mock] app.copyLogs");
    },
    "app.getLicenses": async () => {
      await this.sleep(60);
      return { items: clone(LICENSES) };
    },
    "app.getLogs": async ({ maxLines }) => {
      await this.sleep(90);
      return { text: generateLogs(this.logClock, maxLines, this.cfg.seed + 20261112), logFile: logFileName(this.logClock) };
    },
    "app.getSnapshot": async () => {
      await this.sleep(30);
      return this.snapshot();
    },
    "app.openExternal": async ({ url }) => {
      await this.sleep(30);
      console.debug("[mock] app.openExternal", url);
    },
    "app.openPath": async ({ path }) => {
      await this.sleep(30);
      console.debug("[mock] app.openPath", path);
    },
    "app.pickFolder": async ({ title }) => {
      await this.sleep(500);
      const locate = /locate|existing/i.test(title ?? "");
      const choices = locate ? ["F:\\Games\\Fortnite", "C:\\Users\\Public\\Documents"] : ["D:\\Games", "E:\\Games", "C:\\Games"];
      const i = locate ? this.pickLocate++ : this.pickBase++;
      return { path: choices[i % choices.length] ?? null };
    },
    "app.quit": async () => {
      await this.sleep(10);
      console.debug("[mock] app.quit");
    },
    "app.ready": async () => {
      await this.sleep(10);
    },

    // ---- auth ----
    "auth.signIn": async () => {
      await this.sleep(60);
      const previous = this.s.auth;
      const failure = this.takeFail();
      this.s.auth = { status: "signingIn", account: previous.account };
      this.emitState();
      await this.sleep(1400);
      if (failure) {
        this.s.auth = previous;
        this.emitState();
        throw fail(errorForCode(failure));
      }
      this.s.auth = { status: "signedIn", account: { ...ACCOUNT } };
      if (!this.s.onboarding.completed) this.s.onboarding = { completed: false, step: "chooseInstall" };
      this.onSignedIn();
      this.emitState();
      return clone(this.s.auth);
    },
    "auth.signOut": async () => {
      await this.sleep(80);
      this.s.auth = { status: "signedOut", account: null };
      this.emitState();
      return clone(this.s.auth);
    },

    // ---- egl ----
    "egl.rescan": async () => {
      await this.sleep(700);
      this.s.egl.lastScan = iso();
      this.emitState();
      return clone(this.s.egl);
    },

    // ---- game ----
    "game.adopt": async (req) => {
      await this.sleep(60);
      this.consumeFail();
      this.requireAuth();
      const g = this.s.game;
      if (g.installed) throw fail(Errors.alreadyInstalled());
      if (g.installStatus === "installing") throw fail(Errors.busy("the current install"));

      let path: string;
      let version: string | null;
      let fromEgl = false;
      if (req.candidateId) {
        const c = this.s.egl.candidates.find((x) => x.id === req.candidateId);
        if (!c) throw fail(Errors.invalidRequest(`Unknown candidate: ${req.candidateId}`));
        if (c.isStale || !c.folderExists || !c.executableFound) throw fail(Errors.invalidFolder(STALE_CANDIDATE_REASON));
        path = c.installPath;
        version = c.version;
        fromEgl = c.source === "epicGamesLauncher";
      } else if (req.path) {
        const v = this.validate(req.path);
        if (!v.valid || !v.gamePath) throw fail(Errors.invalidFolder(v.reason ?? INVALID_FOLDER_REASON));
        path = v.gamePath;
        version = v.version;
      } else {
        throw fail(Errors.invalidRequest("Adopt needs a candidateId or a path."));
      }

      const importJob = this.createJob("import", { targetPath: path, toVersion: version }, {
        candidateId: req.candidateId,
        adoptPath: path,
        adoptVersion: version,
        adoptFromEgl: fromEgl,
        verifyMode: req.verify,
      });
      const done = await this.awaitJob(importJob.id);
      if (done.status !== "completed") throw fail(done.error ?? Errors.cancelled());

      let verifyJob: JobInfo | null = null;
      const followUp = this.metas.get(importJob.id)?.followUpJobId;
      if (followUp) {
        if (req.verify === "full") {
          verifyJob = await this.awaitJob(followUp);
          if (verifyJob.status !== "completed") throw fail(verifyJob.error ?? Errors.cancelled());
        } else {
          const live = this.s.jobs.find((j) => j.id === followUp);
          verifyJob = live ? clone(live) : null;
        }
      }
      return { game: clone(this.s.game), verifyJob };
    },
    "game.checkForUpdates": async () => {
      await this.sleep(40);
      this.requireAuth();
      const g = this.s.game;
      g.checkingForUpdates = true;
      this.emitState();
      await this.sleep(900);
      g.checkingForUpdates = false;
      g.latestVersion = LATEST_VERSION;
      g.lastUpdateCheck = iso();
      g.updateAvailable = g.installed !== null && g.installed.version !== LATEST_VERSION;
      g.downloadSizeBytes = g.updateAvailable ? UPDATE_DOWNLOAD_BYTES : g.installed ? null : FULL_DOWNLOAD_BYTES;
      this.emitState();
      const result: UpdateCheckResult = {
        updateAvailable: g.updateAvailable,
        installedVersion: g.installed?.version ?? null,
        latestVersion: g.latestVersion,
      };
      return result;
    },
    "game.install": async ({ basePath }) => {
      await this.sleep(80);
      this.consumeFail();
      this.requireAuth();
      const g = this.s.game;
      if (g.installed) throw fail(Errors.alreadyInstalled());
      if (g.installStatus === "installing") throw fail(Errors.busy("the current install"));
      const plan = this.plan(basePath);
      if (!plan.hasEnoughSpace) throw fail(Errors.insufficientSpace(plan.installSizeBytes, plan.freeBytes));
      g.installStatus = "installing";
      const info = this.createJob("install", { targetPath: plan.gamePath, toVersion: plan.version });
      return clone(info);
    },
    "game.prepareLaunch": async () => undefined,
    "game.launch": async () => {
      await this.sleep(40);
      this.consumeFail();
      this.requireAuth();
      this.requireInstalled();
      const g = this.s.game;
      if (g.runState !== "idle") throw fail(Errors.gameRunning());
      if (this.s.egl.busyWithGame) throw fail(Errors.eglBusy());
      if (this.active?.info.kind === "install" || this.active?.info.kind === "update" || this.active?.info.kind === "repair") {
        throw fail(Errors.busy(this.active.info.kind === "update" ? "the update" : "the current task"));
      }

      this.setRunState("preparing");
      await this.sleep(300);
      if (!this.eacInstalled) {
        this.setRunState("installingPrerequisites");
        await this.sleep(1200);
        this.eacInstalled = true;
      }
      this.setRunState("launching");
      await this.sleep(1600);
      await this.holdWhileFrozen();
      g.runningSince = iso();
      this.setRunState("running");
      this.scheduleAutoExit();
      return { started: true, eacInstalled: this.eacInstalled };
    },
    "game.move": async ({ destinationBase }) => {
      await this.sleep(80);
      this.consumeFail();
      const installed = this.requireInstalled();
      this.requireGameClosed();
      const target = joinPath(destinationBase, "Fortnite");
      if (samePath(target, installed.installPath)) throw fail(Errors.invalidFolder("Fortnite is already in this folder."));
      const free = freeBytes(destinationBase, this.diskFull());
      if (free < installed.installSizeBytes + INSTALL_MARGIN_BYTES) throw fail(Errors.insufficientSpace(installed.installSizeBytes, free));
      return clone(this.createJob("move", { targetPath: target, fromVersion: installed.version }));
    },
    "game.openInstallFolder": async () => {
      await this.sleep(30);
      console.debug("[mock] game.openInstallFolder", this.s.game.installed?.installPath);
    },
    "game.planInstall": async ({ basePath }) => {
      await this.sleep(350);
      this.requireAuth();
      return this.plan(basePath);
    },
    "game.uninstall": async ({ mode }) => {
      await this.sleep(80);
      this.consumeFail();
      const installed = this.requireInstalled();
      this.requireGameClosed();
      return clone(this.createJob("uninstall", { targetPath: installed.installPath, fromVersion: installed.version }, { uninstallMode: mode }));
    },
    "game.update": async () => {
      await this.sleep(80);
      this.consumeFail();
      this.requireAuth();
      const installed = this.requireInstalled();
      this.requireGameClosed();
      if (!this.s.game.updateAvailable) throw fail(Errors.invalidRequest("Fortnite is already up to date."));
      const free = this.diskFull() ? Math.round(0.9 * GiB) : freeBytes(installed.installPath, false);
      if (free < UPDATE_HEADROOM_BYTES) throw fail(Errors.insufficientSpace(UPDATE_HEADROOM_BYTES, free));
      return clone(
        this.createJob("update", {
          targetPath: installed.installPath,
          fromVersion: installed.version,
          toVersion: this.s.game.latestVersion ?? LATEST_VERSION,
        }),
      );
    },
    "game.validateFolder": async ({ path }) => {
      await this.sleep(120);
      return this.validate(path);
    },
    "game.verify": async () => {
      await this.sleep(80);
      this.consumeFail();
      this.requireAuth();
      const installed = this.requireInstalled();
      this.requireGameClosed();
      const kind: JobKind = this.s.game.installStatus === "broken" ? "repair" : "verify";
      return clone(this.createJob(kind, { targetPath: installed.installPath, fromVersion: installed.version, toVersion: installed.version }));
    },

    // ---- jobs ----
    "jobs.cancel": async ({ jobId }) => {
      await this.sleep(40);
      const info = this.findJob(jobId);
      if (info.status !== "queued" && info.status !== "running" && info.status !== "paused" && info.status !== "waiting") {
        throw fail(Errors.invalidRequest(`Job ${jobId} already finished.`));
      }
      this.finish(info, "cancelled", null);
      return clone(info);
    },
    "jobs.dismiss": async ({ jobId }) => {
      await this.sleep(30);
      const info = this.findJob(jobId);
      if (info.status === "queued" || info.status === "running" || info.status === "paused" || info.status === "waiting") {
        throw fail(Errors.busy("this task"));
      }
      this.s.jobs = this.s.jobs.filter((j) => j.id !== jobId);
      this.metas.delete(jobId);
      this.emitState();
    },
    "jobs.pause": async ({ jobId }) => {
      await this.sleep(40);
      const info = this.findJob(jobId);
      if (info.status === "paused") return clone(info);
      if (info.status !== "running") throw fail(Errors.invalidRequest(`Job ${jobId} is not running.`));
      info.status = "paused";
      this.stopTickerIfIdle();
      this.emitState();
      return clone(info);
    },
    "jobs.resume": async ({ jobId }) => {
      await this.sleep(40);
      const info = this.findJob(jobId);
      if (info.status === "running") return clone(info);
      if (info.status !== "paused" && info.status !== "waiting") throw fail(Errors.invalidRequest(`Job ${jobId} is not paused.`));
      info.status = "running";
      info.blockedReason = null;
      if (this.eglResumeTimer) clearTimeout(this.eglResumeTimer);
      this.eglResumeTimer = null;
      this.ensureTicker();
      this.emitState();
      return clone(info);
    },

    // ---- settings ----
    "settings.reset": async () => {
      await this.sleep(60);
      this.s.settings = { ...DEFAULT_SETTINGS };
      this.s.egl.syncEnabled = this.s.settings.eglSync;
      this.emitState();
      return clone(this.s.settings);
    },
    "settings.update": async (settings) => {
      await this.sleep(60);
      this.consumeFail();
      this.s.settings = { ...settings };
      this.s.egl.syncEnabled = settings.eglSync;
      this.emitState();
      return clone(this.s.settings);
    },

    // ---- window ----
    "window.close": async () => {
      await this.sleep(4);
    },
    "window.getInfo": async () => {
      await this.sleep(4);
      return { maximized: this.maximized, focused: true };
    },
    "window.minimize": async () => {
      await this.sleep(4);
    },
    "window.toggleMaximize": async () => {
      await this.sleep(4);
      this.maximized = !this.maximized;
      this.event("window", { maximized: this.maximized, focused: true });
    },
  };

  private findJob(jobId: string): JobInfo {
    const info = this.s.jobs.find((j) => j.id === jobId);
    if (!info) throw fail(Errors.invalidRequest(`Unknown job: ${jobId}`));
    return info;
  }
}
