import type {
  AppSnapshot,
  AuthState,
  Collection,
  Connectivity,
  DownloadMode,
  EglImportSkip,
  EglReadiness,
  EglState,
  ErrorCode,
  FolderValidation,
  FreeGamesResult,
  GameDetail,
  GameSummary,
  GliderError,
  InstallPlan,
  JobHistory,
  JobInfo,
  JobKind,
  JobOutcome,
  JobSample,
  LaunchRequest,
  LaunchResult,
  LibraryDelta,
  LibraryFolder,
  Notice,
  OnboardingState,
  OverlayState,
  QueueState,
  ReadinessItem,
  RpcEventMap,
  RpcEventName,
  RpcMethodName,
  RpcMethods,
  RunState,
  RunningGame,
  SaveState,
  SaveSyncStatus,
  Settings,
  StoreState,
  ThirdPartyStatus,
  VerifyMode,
} from "../contract.gen";
import { wideArt } from "./art";
import type { MockGame } from "./catalog";
import {
  ACCOUNT,
  DAY,
  DATA_DIRECTORY,
  GiB,
  HOUR,
  LOG_DIRECTORY,
  MiB,
  OVERLAY_APP,
  OVERLAY_LATEST,
  DEFAULT_SETTINGS,
  clone,
  iso,
} from "./constants";
import { dirname, driveLetter, joinPath, samePath, trimSlashes, type MockDrive } from "./disk";
import { Errors, errorForCode, fail, toGliderError } from "./errors";
import { LICENSES } from "./licenses";
import { generateLogs, logFileName } from "./logs";
import type { MockController, MockOptions, ScenarioName } from "./options";
import { mulberry32 } from "./rng";
import { SCENARIOS, buildWorld, type SeedJob, type World } from "./scenarios";
import { JobSim, TICK_MS, TICK_S, buildJobSim, type JobEnv, type JobSimInit } from "./sim";

type Handlers = {
  [M in RpcMethodName]: (params: RpcMethods[M]["params"]) => Promise<RpcMethods[M]["result"]>;
};

type Timer = ReturnType<typeof setTimeout>;

interface JobMeta {
  uninstallMode?: "gliderOnly" | "deleteFiles";
  destinationBase?: string;
  basePath?: string;
  components?: string[];
  dlc?: string[];
  eglInstallId?: string;
  verifyMode?: VerifyMode;
  /** Set by "resume" on a waiting job: run anyway. */
  forced?: boolean;
}

interface JobEntry {
  info: JobInfo;
  sim: JobSim;
  meta: JobMeta;
}

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
  games: number | undefined;
  eacInstalled: boolean;
}

const MAX_FINISHED_JOBS = 20;
const EGL_AUTO_RESUME_MS = 14_000;
const GAME_AUTO_EXIT_MS = 120_000;
const INSTALL_MARGIN_BYTES = 1 * GiB;
const WRITE_KINDS: readonly JobKind[] = ["install", "update", "repair", "verify", "modify", "move", "uninstall", "import", "overlay"];
const NETWORK_KINDS: readonly JobKind[] = ["install", "update", "repair", "modify", "overlay"];
const PAUSABLE: readonly JobKind[] = ["install", "update", "repair", "verify", "modify", "overlay"];

/** Calls that only read: they never consume an injected failure. */
const READ_ONLY = new Set<string>([
  "app.getSnapshot", "app.getLogs", "app.getLicenses", "app.ready", "window.getInfo", "library.get", "game.getDetail",
  "game.planInstall", "game.prepareLaunch", "saves.get", "jobs.getHistory", "freeGames.get", "egl.validateFolder",
  "egl.checkReadiness", "store.setBounds", "app.openPath", "app.openExternal", "app.copyLogs", "game.openFolder", "saves.openBackups",
  "window.minimize", "window.toggleMaximize", "window.close", "app.quit", "app.pickFolder",
]);

function resolveOptions(o: MockOptions, prev?: Resolved): Resolved {
  const scenario = o.scenario ?? prev?.scenario ?? "returning";
  const fresh = o.scenario !== undefined || !prev;
  const liveSeek: Partial<Record<ScenarioName, number>> = { queue: 0.42, downloading: 0.42, eglBusy: 0.2 };
  return {
    scenario,
    seek: o.seek ?? (fresh ? (liveSeek[scenario] ?? null) : (prev?.seek ?? null)),
    timeScale: o.timeScale ?? prev?.timeScale ?? 12,
    timeScaleExplicit: o.timeScale !== undefined || (prev?.timeScaleExplicit ?? false),
    freeze: o.freeze ?? (scenario.startsWith("frozen") ? true : fresh ? false : (prev?.freeze ?? false)),
    art: o.art ?? prev?.art ?? "default",
    backdrop: o.backdrop ?? prev?.backdrop ?? "solid",
    latency: o.latency ?? prev?.latency ?? 1,
    seed: o.seed ?? prev?.seed ?? 1337,
    games: o.games ?? (fresh ? undefined : prev?.games),
    eacInstalled: o.eacInstalled ?? prev?.eacInstalled ?? true,
  };
}

const isoTime = (v: string | null): number => (v ? Date.parse(v) : 0);

const isRequest = (m: unknown): m is { type: "request"; id: number; method: string; params: unknown } =>
  typeof m === "object" && m !== null && (m as { type?: unknown }).type === "request" && typeof (m as { id?: unknown }).id === "number";

export class MockBackend {
  private cfg: Resolved;
  private world!: World;

  // ----- state -----
  private games = new Map<string, MockGame>();
  private deferred: MockGame[] = [];
  private rev = 1;
  private dirty = new Set<string>();
  private libFlushQueued = false;
  private dropLibraryEvents = 0;

  private auth!: AuthState;
  private connectivity: Connectivity = "online";
  private settings!: Settings;
  private drives: MockDrive[] = [];
  private collections: Collection[] = [];
  private onboarding!: OnboardingState;
  private overlay!: OverlayState;
  private sessions: RunningGame[] = [];
  private egl!: World["egl"];
  private eglScanning = false;
  private syncing = false;
  private syncError: GliderError | null = null;
  private lastSyncedAt: string | null = null;
  private store: StoreState = { created: false, visible: false, loading: false, canGoBack: false, title: null };

  private queue: JobEntry[] = [];
  private finished: JobInfo[] = [];
  private histories = new Map<string, JobSample[]>();
  private queuePaused = false;

  private ticker: ReturnType<typeof setInterval> | null = null;
  private readonly timers = new Set<Timer>();
  /** Timers that must survive a scenario reset (in-flight call latencies); they reject as cancelled when they fire. */
  private readonly stickyTimers = new Set<Timer>();
  private sessionTimers = new Map<string, Timer>();

  private epoch = 0;
  private jobSeq = 0;
  private noticeSeq = 0;
  private failCode: ErrorCode | null = null;
  private maximized = false;
  private logClock = Date.now();
  private flushQueued = false;
  private pickLocate = 0;
  private pickBase = 0;
  private checkRound = 0;
  private refreshRound = 0;
  private quickScanned = false;
  private activated = new Set<string>();
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
      (result) => this.out({ type: "response", id, result: result ?? null, error: null }),
      (e: unknown) => this.out({ type: "response", id, result: null, error: toGliderError(e) }),
    );
  }

  private async dispatch(method: string, params: unknown): Promise<unknown> {
    if (!Object.hasOwn(this.handlers, method)) throw fail(Errors.invalidRequest(`Unknown method: ${method}`));
    if (this.failCode && !READ_ONLY.has(method)) {
      const code = this.failCode;
      this.failCode = null;
      const appName = (params as { appName?: string } | null)?.appName ?? null;
      throw fail(errorForCode(code, appName ? this.games.get(appName)?.summary.title : undefined, appName));
    }
    const handler = this.handlers[method as RpcMethodName] as (p: unknown) => Promise<unknown>;
    return handler(params);
  }

  private event<E extends RpcEventName>(event: E, data: RpcEventMap[E]): void {
    this.out({ type: "event", event, data });
  }

  /** Full (small) snapshot on every change; same-tick changes coalesce into one push. */
  private emitState(): void {
    if (this.flushQueued) return;
    this.flushQueued = true;
    queueMicrotask(() => {
      this.flushQueued = false;
      this.event("state", this.snapshot());
      this.listeners.forEach((l) => l());
    });
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
      this.timer(ms, () => (epoch === this.epoch ? resolve() : reject(fail(Errors.cancelled()))), true);
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
    this.sessionTimers.clear();
    if (this.ticker !== null) clearInterval(this.ticker);
    this.ticker = null;
  }

  /** Build the scenario in `cfg` and hydrate its jobs. Does not emit. */
  private load(): void {
    this.epoch++;
    this.clearSimTimers();
    const now = Date.now();
    this.world = buildWorld(this.cfg.scenario, {
      seed: this.cfg.seed,
      now,
      games: this.cfg.games,
      art: this.cfg.art !== "none",
      eacInstalled: this.cfg.eacInstalled,
    });
    const w = this.world;
    this.games = new Map(w.games.map((g) => [g.summary.appName, g]));
    this.deferred = w.deferred;
    this.auth = clone(w.auth);
    this.connectivity = w.connectivity;
    this.settings = clone(w.settings);
    this.drives = w.drives.map((d) => ({ ...d }));
    this.collections = clone(w.collections);
    this.onboarding = clone(w.onboarding);
    this.overlay = clone(w.overlay);
    this.sessions = clone(w.sessions);
    this.egl = clone(w.egl);
    this.eglScanning = false;
    this.syncing = false;
    this.syncError = w.syncError;
    this.lastSyncedAt = w.lastSyncedAt;
    this.store = { created: false, visible: false, loading: false, canGoBack: false, title: null };
    this.queue = [];
    this.finished = [];
    this.histories = new Map();
    this.queuePaused = false;
    this.dirty.clear();
    this.maximized = false;
    this.logClock = now;
    this.checkRound = 0;
    this.refreshRound = 0;
    this.quickScanned = false;
    this.activated = new Set();
    this.jobSeq = 0;
    this.cfg = { ...this.cfg, freeze: this.cfg.freeze || w.freeze };

    // Games that are running at load.
    for (const s of this.sessions) {
      this.patch(s.appName, { runState: s.state });
      if (!this.cfg.freeze) this.scheduleAutoExit(s.appName);
    }
    for (const seed of w.jobs) this.hydrate(seed, now);
    this.finished.sort((x, y) => isoTime(y.finishedAt) - isoTime(x.finishedAt));
    this.dirty.clear();
    this.reconcile(false);
    for (const e of this.queue) this.refreshActive(e.info.appName);
    this.dirty.clear();
    if (this.queue.some((e) => e.info.status === "running")) this.ensureTicker();
    if (this.egl.busyApps.length > 0 && !this.cfg.freeze) this.timer(EGL_AUTO_RESUME_MS, () => this.setEglBusy(false));
  }

  loadScenario(name: ScenarioName, opts: Partial<MockOptions> = {}): void {
    this.cfg = resolveOptions({ ...opts, scenario: name }, this.cfg);
    this.failCode = opts.fail ?? null;
    this.load();
    this.rev++;
    this.event("library", this.resetDelta());
    this.emitState();
    this.listeners.forEach((l) => l());
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

  // ───────────────────────────── library ─────────────────────────────

  private rec(appName: string): MockGame {
    const g = this.games.get(appName);
    if (!g) throw fail(Errors.notFound());
    return g;
  }

  private titleOf(appName: string): string {
    return appName === OVERLAY_APP ? "Epic overlay" : (this.games.get(appName)?.summary.title ?? appName);
  }

  private resetDelta(): LibraryDelta {
    return { revision: this.rev, reset: true, upserts: [...this.games.values()].map((g) => g.summary), removes: [] };
  }

  /** Replace fields of a summary (immutably) and queue a library delta. */
  private patch(appName: string, p: Partial<GameSummary>): void {
    const g = this.games.get(appName);
    if (!g) return;
    g.summary = { ...g.summary, ...p };
    this.touch(appName);
  }

  private touch(appName: string): void {
    this.dirty.add(appName);
    if (this.libFlushQueued) return;
    this.libFlushQueued = true;
    queueMicrotask(() => {
      this.libFlushQueued = false;
      this.flushLibrary();
    });
  }

  private flushLibrary(): void {
    if (this.dirty.size === 0) return;
    const upserts: GameSummary[] = [];
    const removes: string[] = [];
    for (const a of this.dirty) {
      const g = this.games.get(a);
      if (g) upserts.push(g.summary);
      else removes.push(a);
    }
    this.dirty.clear();
    const delta: LibraryDelta = { revision: ++this.rev, reset: false, upserts, removes };
    if (this.dropLibraryEvents > 0) {
      this.dropLibraryEvents--;
    } else {
      this.event("library", delta);
    }
    this.emitState();
  }

  private baseOf(appName: string): string {
    return this.games.get(appName)?.summary.baseAppName ?? appName;
  }

  private libraryStatus(): AppSnapshot["library"] {
    let gameCount = 0;
    let updates = 0;
    for (const g of this.games.values()) {
      if (g.summary.kind === "game") gameCount++;
      if (g.summary.kind === "game" && g.summary.installStatus === "installed" && g.summary.updateAvailable) updates++;
    }
    return { revision: this.rev, gameCount, syncing: this.syncing, lastSyncedAt: this.lastSyncedAt, syncError: this.syncError, updatesAvailable: updates };
  }

  private revealLibrary(): void {
    if (this.deferred.length === 0) return;
    for (const g of this.deferred) this.games.set(g.summary.appName, g);
    this.deferred = [];
    this.collections = this.collections.length > 0 ? this.collections : [];
    this.rev++;
    this.event("library", this.resetDelta());
  }

  private clearLibrary(): void {
    this.deferred = [...this.games.values()];
    this.games = new Map();
    this.rev++;
    this.event("library", this.resetDelta());
  }

  // ───────────────────────────── snapshot ─────────────────────────────

  private free(path: string): number {
    const letter = driveLetter(path);
    return this.drives.find((d) => d.letter === letter)?.freeBytes ?? Math.round(200 * GiB);
  }

  private storage(): AppSnapshot["storage"] {
    const folders: LibraryFolder[] = this.settings.libraryFolders.map((p) => ({
      path: p,
      isDefault: this.settings.defaultInstallBase !== null && samePath(p, this.settings.defaultInstallBase),
      exists: true,
      drive: `${driveLetter(p) ?? "C"}:\\`,
      freeBytes: this.free(p),
      gameCount: [...this.games.values()].filter((g) => g.summary.installPath && samePath(dirname(g.summary.installPath), p)).length,
    }));
    return {
      folders,
      drives: this.drives.map((d) => ({ root: `${d.letter}:\\`, label: d.label, freeBytes: d.freeBytes, totalBytes: d.totalBytes, isSsd: d.isSsd })),
    };
  }

  private effectiveMode(): { mode: DownloadMode; reason: string | null } {
    const running = this.sessions.find((s) => s.state === "running");
    if (running && this.settings.whilePlaying === "throttle") return { mode: "background", reason: `Throttled while ${running.title} is running` };
    if (running && this.settings.whilePlaying === "pause") return { mode: this.settings.downloadMode, reason: `Paused while ${running.title} is running` };
    return { mode: this.settings.downloadMode, reason: null };
  }

  private queueState(): QueueState {
    const { mode, reason } = this.effectiveMode();
    return { jobs: [...this.queue.map((e) => e.info), ...this.finished.slice(0, MAX_FINISHED_JOBS)], paused: this.queuePaused, effectiveMode: mode, modeReason: reason };
  }

  private eglState(): EglState {
    return {
      launcherInstalled: this.egl.launcherInstalled,
      launcherRunning: this.egl.launcherRunning,
      scanning: this.eglScanning,
      installs: this.egl.revealed ? this.egl.installs : [],
      busyApps: this.egl.busyApps,
      syncEnabled: this.settings.eglSync,
      lastScan: this.egl.lastScan,
    };
  }

  private overlayState(): OverlayState {
    return { ...this.overlay, busy: this.queue.some((e) => e.info.kind === "overlay") };
  }

  private snapshot(): AppSnapshot {
    return clone({
      app: {
        version: "0.2.0",
        legendaryVersion: "0.21.1",
        isWindows11: true,
        backdrop: this.cfg.backdrop,
        dataDirectory: DATA_DIRECTORY,
        logDirectory: LOG_DIRECTORY,
        cpuCount: 16,
        totalMemoryBytes: 32 * GiB,
        autoWorkers: 12,
        autoSharedMemoryMb: 2048,
        warm: true,
        migratedFrom: null,
      },
      auth: this.auth,
      connectivity: this.connectivity,
      library: this.libraryStatus(),
      queue: this.queueState(),
      sessions: this.sessions,
      storage: this.storage(),
      egl: this.eglState(),
      overlay: this.overlayState(),
      collections: this.collections,
      settings: this.settings,
      onboarding: this.onboarding,
    } satisfies AppSnapshot);
  }

  // ───────────────────────────── guards ─────────────────────────────

  private requireAuth(): void {
    if (this.auth.status === "expired") throw fail(Errors.authExpired());
    if (this.auth.status !== "signedIn") throw fail(Errors.notSignedIn());
  }

  private requireOnline(what: string): void {
    if (this.connectivity === "offline") throw fail(Errors.offline(what));
  }

  private isRunning(appName: string): boolean {
    const base = this.baseOf(appName);
    return this.sessions.some((s) => s.appName === appName || s.appName === base);
  }

  private unfinishedFor(appName: string): JobEntry | undefined {
    const base = this.baseOf(appName);
    return this.queue.find((e) => e.info.appName === appName || e.info.appName === base);
  }

  private requireIdle(appName: string): void {
    const g = this.rec(appName);
    if (this.isRunning(appName)) throw fail(Errors.gameRunning(g.summary.title, appName));
  }

  // ───────────────────────────── jobs ─────────────────────────────

  private jobEnv(): JobEnv {
    return { mode: this.effectiveMode().mode, speedLimit: this.settings.speedLimitBytesPerSecond, cdn: this.settings.preferredCdn, paused: false };
  }

  private simInit(kind: JobKind, appName: string, id: string, meta: JobMeta, seed: SeedJob | null): Omit<JobSimInit, "scale"> {
    const g = appName === OVERLAY_APP ? null : this.games.get(appName);
    const installed = g?.summary.installedSizeBytes ?? g?.fullInstallBytes ?? 0;
    const base = {
      jobId: id,
      appName,
      kind,
      seed: this.cfg.seed + Number(/(\d+)$/.exec(id)?.[1] ?? 0),
      installBytes: installed,
      downloadBytes: 0,
      writeBytes: 0,
      badFiles: 0,
      badBytes: 0,
    };
    switch (kind) {
      case "install": {
        const extra = (meta.dlc ?? []).reduce((s, a) => s + (this.games.get(a)?.fullDownloadBytes ?? 0), 0);
        const dl = (g?.fullDownloadBytes ?? 0) + extra;
        const wr = (g?.fullInstallBytes ?? 0) + (meta.dlc ?? []).reduce((s, a) => s + (this.games.get(a)?.fullInstallBytes ?? 0), 0);
        return { ...base, installBytes: wr, downloadBytes: dl, writeBytes: wr };
      }
      case "update":
        return { ...base, downloadBytes: g?.updateDownloadBytes ?? 0, writeBytes: g?.updateWriteBytes ?? 0 };
      case "modify":
        return { ...base, downloadBytes: Math.round(installed * 0.08), writeBytes: Math.round(installed * 0.12) };
      case "overlay":
        return { ...base, installBytes: 420 * MiB, downloadBytes: 180 * MiB, writeBytes: 420 * MiB };
      case "verify":
      case "repair":
        return { ...base, badFiles: g?.badFiles ?? 0, badBytes: g?.badBytes ?? 0 };
      case "uninstall":
        return { ...base, uninstallSeconds: meta.uninstallMode === "deleteFiles" ? (seed?.uninstallSeconds ?? 1.8) : 0.6 };
      case "import": {
        const inst = meta.eglInstallId ? this.egl.installs.find((i) => i.id === meta.eglInstallId) : undefined;
        return { ...base, installBytes: inst?.sizeBytes ?? g?.fullInstallBytes ?? 8 * GiB, importVerify: meta.verifyMode === "full" };
      }
      default:
        return base;
    }
  }

  private newInfo(kind: JobKind, appName: string, id: string, sim: JobSim, extra: Partial<JobInfo>): JobInfo {
    return {
      id,
      appName,
      title: this.titleOf(appName),
      kind,
      status: "queued",
      phases: sim.plannedPhases(),
      phase: null,
      progress: null,
      error: null,
      createdAt: iso(),
      startedAt: null,
      finishedAt: null,
      targetPath: null,
      fromVersion: null,
      toVersion: null,
      blockedReason: null,
      verify: null,
      outcome: null,
      ...extra,
    };
  }

  private versionsFor(kind: JobKind, appName: string): Pick<JobInfo, "fromVersion" | "toVersion"> {
    const s = this.games.get(appName)?.summary;
    if (!s) return { fromVersion: null, toVersion: null };
    if (kind === "update") {
      // Seeded history may update a game that has nothing pending now: show a plausible step up, not "1.2.3 → 1.2.3".
      const from = s.installedVersion;
      const parts = (from ?? "").split(".").map(Number);
      const stepped = parts.length > 1 && parts.every(Number.isFinite) ? [...parts.slice(0, -1), parts.at(-1)! + 1].join(".") : s.latestVersion;
      return { fromVersion: from, toVersion: s.latestVersion !== from ? s.latestVersion : stepped };
    }
    if (kind === "install") return { fromVersion: null, toVersion: s.latestVersion };
    return { fromVersion: null, toVersion: null };
  }

  private targetFor(kind: JobKind, appName: string, meta: JobMeta): string | null {
    const g = this.games.get(appName);
    if (!g) return null;
    switch (kind) {
      case "install":
        return joinPath(meta.basePath ?? this.settings.defaultInstallBase ?? "D:\\Games", g.folderName);
      case "move":
        return joinPath(meta.destinationBase ?? "D:\\Games", g.folderName);
      case "import":
        return this.egl.installs.find((i) => i.id === meta.eglInstallId)?.installPath ?? g.summary.installPath;
      default:
        return g.summary.installPath;
    }
  }

  /** Create (or return the existing, for idempotency) unfinished job keyed by (appName, kind). */
  private createJob(kind: JobKind, appName: string, meta: JobMeta = {}, opts: { front?: boolean } = {}): JobInfo {
    const existing = this.queue.find((e) => e.info.appName === appName && e.info.kind === kind);
    if (existing) {
      if (opts.front) this.moveJob(existing.info.id, this.queue[0]?.info.status === "running" ? 1 : 0);
      return existing.info;
    }
    const id = `job-${++this.jobSeq}`;
    const init = this.simInit(kind, appName, id, meta, null);
    const sim = buildJobSim(init, this.cfg.timeScale, this.cfg.timeScaleExplicit);
    const info = this.newInfo(kind, appName, id, sim, { targetPath: this.targetFor(kind, appName, meta), ...this.versionsFor(kind, appName) });
    const entry: JobEntry = { info, sim, meta };
    if (opts.front) {
      const at = this.queue[0]?.info.status === "running" ? 1 : 0;
      this.queue.splice(at, 0, entry);
    } else {
      this.queue.push(entry);
    }
    this.refreshActive(appName);
    this.reconcile();
    return entry.info;
  }

  private entry(jobId: string): JobEntry | undefined {
    return this.queue.find((e) => e.info.id === jobId);
  }

  private moveJob(jobId: string, toIndex: number): void {
    const from = this.queue.findIndex((e) => e.info.id === jobId);
    if (from < 0) return;
    const [e] = this.queue.splice(from, 1);
    this.queue.splice(Math.max(0, Math.min(this.queue.length, toIndex)), 0, e!);
    this.emitState();
  }

  /** The activeJobId/Kind a game's summary carries follows its unfinished jobs. */
  private refreshActive(appName: string): void {
    const g = this.games.get(appName);
    if (!g) return;
    const e = this.queue.find((x) => x.info.appName === appName && x.info.status === "running") ?? this.queue.find((x) => x.info.appName === appName);
    const id = e?.info.id ?? null;
    const kind = e?.info.kind ?? null;
    if (g.summary.activeJobId !== id || g.summary.activeJobKind !== kind) this.patch(appName, { activeJobId: id, activeJobKind: kind });
  }

  private blockReason(e: JobEntry): string | null {
    if (e.meta.forced) return null;
    const { info } = e;
    if (this.connectivity === "offline" && NETWORK_KINDS.includes(info.kind)) return "You're offline. Glider continues when you're back online.";
    if (this.egl.busyApps.includes(info.appName)) return `Epic Games Launcher is updating ${info.title}`;
    const playing = this.sessions.find((s) => s.state === "running");
    if (playing && this.settings.whilePlaying === "pause" && WRITE_KINDS.includes(info.kind) && info.kind !== "uninstall") return `Paused while ${playing.title} is running`;
    return null;
  }

  /** Re-evaluate external blockers for every unfinished job, then start whatever can start. */
  private reconcile(emit = true): void {
    let changed = false;
    for (const e of this.queue) {
      const s = e.info.status;
      if (s === "paused") continue;
      const reason = this.blockReason(e);
      if (reason && s !== "waiting") {
        e.info = { ...e.info, status: "waiting", blockedReason: reason };
        changed = true;
      } else if (reason && e.info.blockedReason !== reason) {
        e.info = { ...e.info, blockedReason: reason };
        changed = true;
      } else if (!reason && s === "waiting") {
        e.info = { ...e.info, status: "queued", blockedReason: null };
        changed = true;
      }
    }
    if (this.queue.some((e) => e.info.status === "running") === false && this.ticker !== null) {
      clearInterval(this.ticker);
      this.ticker = null;
    }
    this.pump();
    if (changed || emit) this.emitState();
  }

  /** Start the next runnable job (one writer at a time). A paused/waiting job blocks only its own game. */
  private pump(): void {
    if (this.queuePaused) return;
    if (this.queue.some((e) => e.info.status === "running")) return;
    const blocked = new Set(this.queue.filter((e) => e.info.status === "paused" || e.info.status === "waiting").map((e) => e.info.appName));
    const next = this.queue.find((e) => e.info.status === "queued" && !blocked.has(e.info.appName));
    if (next) this.startJob(next);
  }

  private startJob(e: JobEntry): void {
    e.sim.setEnv(this.jobEnv());
    const progress = e.sim.progress();
    e.info = { ...e.info, status: "running", startedAt: e.info.startedAt ?? iso(), blockedReason: null, phase: progress.phase, progress, phases: e.sim.plannedPhases() };
    // Running job first.
    this.queue = [e, ...this.queue.filter((x) => x !== e)];
    this.refreshActive(e.info.appName);
    this.ensureTicker();
    this.emitState();
  }

  private ensureTicker(): void {
    if (this.ticker !== null) return;
    this.ticker = setInterval(() => this.tick(), TICK_MS);
  }

  private tick(): void {
    const e = this.queue.find((x) => x.info.status === "running");
    if (!e) {
      if (this.ticker !== null) clearInterval(this.ticker);
      this.ticker = null;
      return;
    }
    if (!this.cfg.freeze) {
      e.sim.setEnv(this.jobEnv());
      const r = e.sim.advance(TICK_S);
      if (r === "done") {
        this.completeJob(e);
        return;
      }
      if (r === "phase") {
        const progress = e.sim.progress();
        e.info = { ...e.info, phase: progress.phase, phases: e.sim.plannedPhases(), verify: e.sim.report() ?? e.info.verify, progress };
        this.emitState();
      }
    }
    const p = e.sim.progress();
    e.info.progress = p;
    this.event("progress", p);
  }

  private finishInfo(e: JobEntry, status: "completed" | "failed" | "cancelled", extra: Partial<JobInfo> = {}): JobInfo {
    const progress = e.sim.progress();
    return {
      ...e.info,
      status,
      phase: status === "completed" ? (e.sim.plannedPhases().at(-1) ?? e.info.phase) : e.info.phase,
      progress: status === "completed" ? { ...progress, percent: 100, phasePercent: 100, etaSeconds: 0 } : e.info.progress,
      finishedAt: iso(),
      blockedReason: null,
      verify: e.sim.report() ?? e.info.verify,
      ...extra,
    };
  }

  private retire(e: JobEntry, info: JobInfo): void {
    this.queue = this.queue.filter((x) => x !== e);
    this.finished = [info, ...this.finished].slice(0, MAX_FINISHED_JOBS);
    this.histories.set(info.id, e.sim.history());
    this.refreshActive(info.appName);
  }

  private completeJob(e: JobEntry): void {
    const outcome = this.completionOutcome(e);
    const info = this.finishInfo(e, "completed", { outcome });
    this.retire(e, info);
    this.applyCompletion(e, info);
    const verbs: Record<JobKind, string> = {
      install: "Installed", update: "Updated", repair: "Repaired", verify: "Verified", move: "Moved", uninstall: "Removed", import: "Added", modify: "Updated", overlay: "Epic overlay installed",
    };
    this.notice({
      level: outcome?.kind === "needsRepair" ? "warning" : "success",
      title: info.kind === "overlay" ? verbs.overlay : `${verbs[info.kind]} ${info.title}`,
      message: outcome?.message ?? null,
      action: outcome?.kind === "needsRepair" ? "repair" : null,
      appName: info.appName,
      jobId: info.id,
    });
    this.reconcile();
  }

  private completionOutcome(e: JobEntry): JobOutcome | null {
    if (e.info.kind !== "import") return null;
    const inst = this.egl.installs.find((i) => i.id === e.meta.eglInstallId);
    const outdated = inst?.drift === "eglNewer" && e.meta.verifyMode !== "full";
    return outdated
      ? { kind: "needsRepair", message: `Epic's copy is an older version (${inst?.version ?? "unknown"}). Glider will fix it with an update.` }
      : { kind: "success", message: "Imported without downloading anything." };
  }

  private applyCompletion(e: JobEntry, info: JobInfo): void {
    const { appName, kind } = info;
    const g = this.games.get(appName);
    switch (kind) {
      case "install": {
        if (!g) break;
        const dlcApps = e.meta.dlc ?? [];
        this.patch(appName, {
          installStatus: "installed",
          installedVersion: g.summary.latestVersion,
          installPath: info.targetPath,
          installedSizeBytes: Math.round(g.fullInstallBytes * 0.99),
          downloadSizeBytes: null,
          updateAvailable: false,
          needsVerification: false,
          sharedWithEgl: false,
        });
        g.installedAt = iso();
        g.executable = `${g.folderName}\\Binaries\\Win64\\${g.folderName}.exe`;
        for (const d of dlcApps) this.patch(d, { installStatus: "installed", installPath: info.targetPath, installedVersion: g.summary.latestVersion });
        if (g.saves && g.saves.status === "unknown") this.setSaveStatus(appName, "inSync");
        break;
      }
      case "update":
        if (g) this.patch(appName, { installedVersion: g.summary.latestVersion, updateAvailable: false, downloadSizeBytes: null, installedSizeBytes: Math.round(g.fullInstallBytes * 0.99) });
        break;
      case "verify":
      case "repair":
        if (g) {
          g.badFiles = 0;
          g.badBytes = 0;
          this.patch(appName, { installStatus: "installed", needsVerification: false });
        }
        break;
      case "move":
        if (g) this.patch(appName, { installPath: info.targetPath, sharedWithEgl: g.summary.sharedWithEgl && this.settings.eglSync });
        break;
      case "uninstall":
        if (!g) break;
        this.patch(appName, {
          installStatus: "notInstalled",
          installedVersion: null,
          installPath: null,
          installedSizeBytes: null,
          updateAvailable: false,
          downloadSizeBytes: g.fullDownloadBytes,
          needsVerification: false,
          sharedWithEgl: false,
        });
        for (const d of g.dlc) this.patch(d, { installStatus: this.games.get(d)?.summary.installStatus === "notInstallable" ? "notInstallable" : "notInstalled", installPath: null, installedVersion: null });
        if (e.meta.uninstallMode === "deleteFiles") this.egl.installs = this.egl.installs.filter((i) => i.appName !== appName);
        else this.egl.installs = this.egl.installs.map((i) => (i.appName === appName ? { ...i, status: "importable" } : i));
        break;
      case "import": {
        const inst = this.egl.installs.find((i) => i.id === e.meta.eglInstallId);
        if (!g || !inst) break;
        const outdated = inst.drift === "eglNewer";
        this.patch(appName, {
          installStatus: "installed",
          installedVersion: inst.version,
          installPath: inst.installPath,
          installedSizeBytes: inst.sizeBytes ?? g.fullInstallBytes,
          updateAvailable: outdated,
          downloadSizeBytes: outdated ? g.updateDownloadBytes : null,
          sharedWithEgl: inst.source !== "manual",
          needsVerification: e.meta.verifyMode === "background",
        });
        this.egl.installs = this.egl.installs.map((i) => (i.id === inst.id ? { ...i, status: "imported" } : i));
        if (e.meta.verifyMode === "background") this.createJob("verify", appName);
        break;
      }
      case "modify":
        if (g) {
          g.components = g.components.map((c) => ({ ...c, selected: c.required || (e.meta.components ?? []).includes(c.id) }));
          // The chosen DLC are on disk afterwards, the others are not (entitlement-only content never changes).
          const wanted = e.meta.dlc ?? [];
          for (const d of g.dlc) {
            const rec = this.games.get(d);
            if (!rec || rec.summary.installStatus === "notInstallable") continue;
            const on = wanted.includes(d);
            this.patch(d, { installStatus: on ? "installed" : "notInstalled", installedVersion: on ? g.summary.installedVersion : null, installedSizeBytes: on ? rec.fullInstallBytes : null });
          }
          this.patch(appName, { installedSizeBytes: Math.round(g.fullInstallBytes * 0.99) });
        }
        break;
      case "overlay":
        this.overlay = { ...this.overlay, installed: true, enabled: true, version: OVERLAY_LATEST, updateAvailable: false, installPath: "C:\\ProgramData\\Glider\\EOSOverlay", managed: true };
        break;
    }
    this.emitState();
  }

  private failJob(e: JobEntry, error: GliderError): void {
    const info = this.finishInfo(e, "failed", { error: { ...error, appName: error.appName ?? e.info.appName } });
    this.retire(e, info);
    if (error.code === "corrupted" && this.games.get(info.appName)?.summary.installStatus === "installed") {
      const g = this.rec(info.appName);
      g.badFiles = 142;
      g.badBytes = Math.round(0.4 * GiB);
      this.patch(info.appName, { installStatus: "broken" });
    }
    this.notice({ level: "error", title: error.title, message: error.message, action: error.fix, appName: info.appName, jobId: info.id });
    this.reconcile();
  }

  private cancelEntry(e: JobEntry): JobInfo {
    const info = this.finishInfo(e, "cancelled");
    this.retire(e, info);
    this.reconcile();
    return info;
  }

  /** Seeded jobs (scenarios): running ones are fast-forwarded, finished ones go straight to history. */
  private hydrate(seed: SeedJob, now: number): void {
    const id = `job-${++this.jobSeq}`;
    const meta: JobMeta = { uninstallMode: seed.kind === "uninstall" ? "deleteFiles" : undefined, destinationBase: seed.kind === "move" ? (seed.targetPath ?? "E:\\Epic Games") : undefined };
    if (!this.games.has(seed.appName)) return;
    const init = this.simInit(seed.kind, seed.appName, id, meta, seed);
    const sim = buildJobSim(init, this.cfg.timeScale, this.cfg.timeScaleExplicit);
    const base = this.newInfo(seed.kind, seed.appName, id, sim, { targetPath: this.targetFor(seed.kind, seed.appName, meta), ...this.versionsFor(seed.kind, seed.appName) });
    const terminal = seed.status === "completed" || seed.status === "failed" || seed.status === "cancelled";
    if (terminal) {
      const finishedAt = now - (seed.finishedAgoMs ?? HOUR);
      const dur = (seed.durationS ?? 300) * 1000;
      const error = seed.error ? errorForCode(seed.error, base.title, seed.appName) : null;
      const info: JobInfo = {
        ...base,
        status: seed.status,
        phase: seed.status === "completed" ? (sim.plannedPhases().at(-1) ?? null) : sim.plannedPhases()[1] ?? null,
        progress: null,
        error: seed.status === "failed" ? error : null,
        createdAt: iso(finishedAt - dur - 20_000),
        startedAt: iso(finishedAt - dur),
        finishedAt: iso(finishedAt),
        verify: seed.kind === "verify" ? { filesChecked: 18_422, filesBad: 0, filesFixed: 0, verified: true, bytesToFix: 0 } : null,
        outcome: seed.outcome ?? (seed.kind === "import" && seed.status === "completed" ? { kind: "success", message: "Imported without downloading anything." } : null),
      };
      this.finished.push(info);
      this.histories.set(id, this.syntheticHistory(id, Math.min(600, Math.round(dur / 1000))));
      return;
    }
    const entry: JobEntry = { info: base, sim, meta };
    if (seed.status !== "queued") {
      const frac = this.cfg.seek ?? seed.seek ?? 0.4;
      sim.seek(seed.phase ? (seed.seek ?? 0.5) : frac, seed.phase);
      const progress = sim.progress();
      entry.info = {
        ...base,
        status: seed.status,
        phase: progress.phase,
        progress,
        startedAt: iso(now - progress.elapsedSeconds * 1000),
        createdAt: iso(now - progress.elapsedSeconds * 1000 - 15_000),
        verify: sim.report(),
        blockedReason: seed.status === "waiting" ? (seed.blockedReason ?? null) : null,
      };
    }
    this.queue.push(entry);
  }

  private syntheticHistory(id: string, seconds: number): JobSample[] {
    const rand = mulberry32(this.cfg.seed ^ Number(/(\d+)$/.exec(id)?.[1] ?? 1));
    let v = 55e6;
    return Array.from({ length: seconds }, (_, i) => {
      v = Math.min(95e6, Math.max(20e6, v + (rand() - 0.5) * 9e6));
      const warm = Math.min(1, (i + 1) / 4);
      return { t: i + 1, networkBytesPerSecond: Math.round(v * warm), diskBytesPerSecond: Math.round(v * 3.2 * warm) };
    });
  }

  // ───────────────────────────── runtime / launch ─────────────────────────────

  private setRun(appName: string, state: RunState, session?: Partial<RunningGame>): void {
    const g = this.games.get(appName);
    const idx = this.sessions.findIndex((s) => s.appName === appName);
    if (state === "idle") {
      if (idx >= 0) this.sessions.splice(idx, 1);
    } else if (idx >= 0) {
      this.sessions[idx] = { ...this.sessions[idx]!, state, ...session };
    } else {
      this.sessions.push({ appName, title: g?.summary.title ?? appName, state, since: null, offline: false, untracked: false, ...session });
    }
    this.patch(appName, { runState: state });
    this.emitState();
  }

  private scheduleAutoExit(appName: string): void {
    const t = this.timer(GAME_AUTO_EXIT_MS, () => this.endGameInternal(appName));
    this.sessionTimers.set(appName, t);
  }

  private endGameInternal(appName: string): void {
    const s = this.sessions.find((x) => x.appName === appName);
    if (!s || s.state === "closing") return;
    this.setRun(appName, "closing");
    this.timer(600, () => {
      const since = s.since ? Date.parse(s.since) : Date.now();
      const played = Math.max(60, Math.round((Date.now() - since) / 1000));
      const g = this.games.get(appName);
      this.setRun(appName, "idle");
      this.sessionTimers.delete(appName);
      if (g) this.patch(appName, { playtimeSeconds: g.summary.playtimeSeconds + played, lastPlayedAt: iso() });
      this.notice({ level: "info", title: `${s.title} closed`, message: null, action: null, appName, jobId: null });
      this.reconcile();
    });
  }

  private async launch(req: LaunchRequest): Promise<LaunchResult> {
    const g = this.rec(req.appName);
    const s = g.summary;
    if (s.kind === "dlc") throw fail(Errors.invalidRequest("Content DLC can't be launched"));
    if (s.installStatus === "broken") throw fail(Errors.corrupted(s.title, null, s.appName));
    if (s.thirdParty !== "none" && s.installStatus === "installed" && g.needsActivation && !this.activated.has(s.appName)) {
      return { outcome: "needsActivation", conflict: null, offline: false, message: `Link ${s.title} with ${s.thirdPartyName ?? "its store"} once, then Play starts it.` };
    }
    if (s.installStatus === "notInstallable" && s.thirdParty !== "none") {
      this.requireOnline("Opening " + (s.thirdPartyName ?? "the launcher"));
      return { outcome: "thirdPartyLauncher", conflict: null, offline: false, message: `Opened ${s.thirdPartyName ?? "the launcher"}.` };
    }
    if (s.installStatus !== "installed") throw fail(Errors.notInstalled(s.title, s.appName));
    if (this.isRunning(s.appName)) throw fail(Errors.gameRunning(s.title, s.appName));
    if (this.unfinishedFor(s.appName)) throw fail(Errors.gameBusy(s.title, s.appName));
    if (this.egl.busyApps.includes(s.appName)) throw fail(Errors.eglBusy(s.title, null, s.appName));
    if (s.thirdParty === "none") {
      if (this.auth.status === "signedOut") throw fail(Errors.notSignedIn());
    }
    const mode = req.mode ?? g.settings.launchMode;
    const wantOffline = mode === "offline" || (mode === "auto" && this.connectivity === "offline");
    if (mode === "online" && this.connectivity === "offline") throw fail(Errors.offline("Playing online"));
    if (wantOffline && !s.canRunOffline) throw fail(Errors.offline(`${s.title} can't run offline, so it`));
    if (!wantOffline && this.auth.status === "expired" && s.thirdParty === "none") throw fail(Errors.authExpired());

    const cloud = g.saves?.supported && g.saves.enabled && this.settings.cloudSaves && g.settings.cloudSaves && !wantOffline;
    if (cloud && s.saveStatus === "conflict" && !req.skipSaveSync) {
      return { outcome: "saveConflict", conflict: g.conflict ?? this.conflictFor(g), offline: false, message: null };
    }

    this.setRun(s.appName, "preparing");
    await this.sleep(400);
    if (cloud && !req.skipSaveSync && (s.saveStatus === "localNewer" || s.saveStatus === "cloudNewer" || s.saveStatus === "unknown")) {
      this.setRun(s.appName, "syncingSaves");
      await this.sleep(700);
      this.setSaveStatus(s.appName, "inSync", { lastSyncedAt: iso() });
    }
    if (g.prerequisites.some((p) => !p.installed)) {
      this.setRun(s.appName, "installingPrerequisites");
      await this.sleep(1800);
      g.prerequisites = g.prerequisites.map((p) => ({ ...p, installed: true }));
    }
    this.setRun(s.appName, "launching");
    await this.sleep(900);
    await this.holdWhileFrozen();
    if (s.thirdParty !== "none") {
      this.setRun(s.appName, "running", { since: iso(), offline: wantOffline, untracked: true });
    } else {
      this.setRun(s.appName, "running", { since: iso(), offline: wantOffline, untracked: false });
    }
    this.patch(s.appName, { lastPlayedAt: iso() });
    if (!this.cfg.freeze) this.scheduleAutoExit(s.appName);
    this.reconcile();
    return { outcome: s.thirdParty !== "none" ? "thirdPartyLauncher" : "started", conflict: null, offline: wantOffline, message: null };
  }

  private conflictFor(g: MockGame): LaunchResult["conflict"] {
    return {
      appName: g.summary.appName,
      title: g.summary.title,
      localModifiedAt: g.saves?.localModifiedAt ?? null,
      cloudModifiedAt: g.saves?.cloudModifiedAt ?? null,
      localSizeBytes: 3 * MiB,
      cloudSizeBytes: 4 * MiB,
      lastSyncedAt: g.saves?.lastSyncedAt ?? null,
    };
  }

  private setSaveStatus(appName: string, status: SaveSyncStatus, extra: Partial<SaveState> = {}): void {
    const g = this.games.get(appName);
    if (!g?.saves) return;
    g.saves = { ...g.saves, status, ...extra };
    if (status !== "conflict") g.conflict = null;
    this.patch(appName, { saveStatus: status });
  }

  // ───────────────────────────── controller (dev switcher + tests) ─────────────────────────────

  createController(): MockController {
    return {
      scenarios: SCENARIOS,
      getScenario: () => this.cfg.scenario,
      loadScenario: (name, opts) => this.loadScenario(name, opts),
      failNext: (code) => {
        this.failCode = code;
      },
      failActiveJob: (code) => {
        const e = this.queue.find((x) => x.info.status === "running" || x.info.status === "paused" || x.info.status === "waiting");
        if (!e) return;
        this.failJob(e, errorForCode(code, e.info.title, e.info.appName));
      },
      emitNotice: (n) => this.notice(n),
      endGame: (appName) => {
        const s = appName ? this.sessions.find((x) => x.appName === appName) : this.sessions.find((x) => x.state === "running");
        if (s) this.endGameInternal(s.appName);
      },
      setEglBusy: (busy) => this.setEglBusy(busy),
      setConnectivity: (c) => {
        this.connectivity = c;
        this.syncError = c === "offline" ? Errors.offline("Syncing your library") : null;
        this.reconcile();
      },
      dropNextLibraryDelta: () => {
        this.dropLibraryEvents++;
      },
      subscribe: (l) => {
        this.listeners.add(l);
        return () => {
          this.listeners.delete(l);
        };
      },
      dispose: () => this.dispose(),
    };
  }

  setEglBusy(busy: boolean): void {
    const target = this.queue.find((e) => WRITE_KINDS.includes(e.info.kind) && e.info.kind !== "overlay");
    if (busy) {
      if (target) this.egl.busyApps = [target.info.appName];
    } else {
      this.egl.busyApps = [];
    }
    this.reconcile();
  }

  // ───────────────────────────── install planning ─────────────────────────────

  private planFor(appName: string, basePath: string | null, componentIds: readonly string[] | null, dlcIds: readonly string[] | null): InstallPlan {
    const g = this.rec(appName);
    const s = g.summary;
    const base = basePath ?? this.settings.defaultInstallBase ?? this.settings.libraryFolders[0] ?? "D:\\Games";
    const gamePath = joinPath(base, g.folderName);
    const comps = g.components.map((c) => ({ ...c, selected: c.required || (componentIds ? componentIds.includes(c.id) : c.selected) }));
    const dlcOptions = g.dlc.map((a) => {
      const d = this.rec(a);
      const installable = d.summary.installStatus !== "notInstallable";
      return {
        appName: a,
        title: d.summary.title,
        downloadSizeBytes: installable ? d.fullDownloadBytes : null,
        installSizeBytes: installable ? d.fullInstallBytes : null,
        installable,
        selected: installable && (dlcIds ? dlcIds.includes(a) : true),
      };
    });
    const unselected = comps.filter((c) => !c.selected).reduce((n, c) => n + (c.sizeBytes ?? 0), 0);
    const selDlc = dlcOptions.filter((d) => d.selected);
    const install = Math.max(0, g.fullInstallBytes - unselected) + selDlc.reduce((n, d) => n + (d.installSizeBytes ?? 0), 0);
    const download = Math.max(0, Math.round(g.fullDownloadBytes * (install / Math.max(1, g.fullInstallBytes + selDlc.reduce((n, d) => n + (d.installSizeBytes ?? 0), 0))))) + selDlc.reduce((n, d) => n + (d.downloadSizeBytes ?? 0), 0);
    const free = this.free(base);
    const existing = this.egl.installs.some((i) => samePath(i.installPath, gamePath) && i.status === "importable") || (s.installStatus === "installed" && samePath(s.installPath ?? "", gamePath));
    return {
      appName,
      title: s.title,
      basePath: base,
      gamePath,
      version: s.latestVersion ?? "unknown",
      downloadSizeBytes: download,
      installSizeBytes: install,
      freeBytes: free,
      hasEnoughSpace: free >= install + INSTALL_MARGIN_BYTES,
      existingInstallFound: existing,
      components: comps,
      dlc: dlcOptions,
      prerequisites: g.prerequisites,
      thirdParty: s.thirdParty,
      thirdPartyName: s.thirdPartyName,
    };
  }

  // ───────────────────────────── handlers ─────────────────────────────

  private thirdPartyInfo(g: MockGame): ThirdPartyStatus | null {
    const s = g.summary;
    if (s.thirdParty === "none") return null;
    const activated = !g.needsActivation || this.activated.has(s.appName);
    return {
      store: s.thirdParty,
      name: s.thirdPartyName ?? "its store",
      launcherInstalled: true,
      activated,
      needsActivation: s.thirdParty === "ubisoft" && !activated,
      managedExternally: s.installStatus === "notInstallable",
    };
  }

  private detail(appName: string): GameDetail {
    const g = this.rec(appName);
    return {
      summary: g.summary,
      description: g.description,
      publisher: g.publisher,
      releaseDate: g.releaseDate,
      dlc: g.dlc,
      components: g.components,
      prerequisites: g.prerequisites,
      saves: g.saves,
      settings: g.settings,
      launchOptions: g.launchOptions,
      shortcuts: g.shortcuts,
      quirks: g.quirks,
      antiCheat: g.antiCheat,
      executable: g.executable,
      installedAt: g.installedAt,
      eglManifestPath: g.eglManifestPath,
      thirdPartyInfo: this.thirdPartyInfo(g),
    };
  }

  private updateSettingsInternal(next: Settings): Settings {
    this.settings = { ...next };
    this.egl = { ...this.egl };
    this.reconcile();
    return clone(this.settings);
  }

  private folderGame(path: string): { appName: string; title: string; gamePath: string; version: string | null } | null {
    const leaf = trimSlashes(path).split(/[\\/]/).pop()?.toLowerCase() ?? "";
    if (!leaf) return null;
    const inst = this.egl.installs.find((i) => i.status === "importable" && samePath(i.installPath, path));
    if (inst) return { appName: inst.appName, title: inst.title, gamePath: inst.installPath, version: inst.version };
    for (const g of this.games.values()) {
      if (g.summary.kind === "game" && g.summary.installStatus === "notInstalled" && g.summary.thirdParty === "none" && g.folderName.toLowerCase() === leaf) {
        return { appName: g.summary.appName, title: g.summary.title, gamePath: path, version: g.summary.latestVersion };
      }
    }
    return null;
  }

  private readonly handlers: Handlers = {
    // ---- app ----
    "app.getSnapshot": async () => {
      await this.sleep(60);
      return this.snapshot();
    },
    "app.openExternal": async () => {},
    "app.openPath": async () => {},
    "app.pickFolder": async (r) => {
      await this.sleep(500);
      const located = /locate/i.test(r.title ?? "");
      if (located) {
        const free = [...this.games.values()].find((g) => g.summary.kind === "game" && g.summary.installStatus === "notInstalled" && g.summary.thirdParty === "none");
        const pool = [free ? `E:\\Old Library\\${free.folderName}` : "E:\\Old Library\\Game", "D:\\Downloads\\NotAGame"];
        return { path: pool[this.pickLocate++ % pool.length]! };
      }
      const bases = ["E:\\Epic Games", "F:\\Library", "D:\\Games", "C:\\Games"];
      return { path: bases[this.pickBase++ % bases.length]! };
    },
    "app.getLogs": async (r) => {
      await this.sleep(120);
      return { text: generateLogs(this.logClock, r.maxLines), logFile: logFileName(this.logClock) };
    },
    "app.copyLogs": async () => {},
    "app.getLicenses": async () => ({ items: LICENSES }),
    "app.ready": async () => {},
    "app.quit": async () => {},

    // ---- onboarding ----
    "onboarding.setStep": async (r) => {
      const prev = this.onboarding.step;
      const skipped = r.skipped && !this.onboarding.skipped.includes(prev) ? [...this.onboarding.skipped, prev] : this.onboarding.skipped;
      this.onboarding = { ...this.onboarding, step: r.step, skipped };
      this.emitState();
      return clone(this.onboarding);
    },
    "onboarding.complete": async () => {
      this.onboarding = { completed: true, step: "done", skipped: this.onboarding.skipped };
      this.emitState();
      return clone(this.onboarding);
    },
    "onboarding.restart": async () => {
      this.onboarding = { completed: false, step: "welcome", skipped: [] };
      this.emitState();
      return clone(this.onboarding);
    },

    // ---- window ----
    "window.minimize": async () => {},
    "window.toggleMaximize": async () => {
      this.maximized = !this.maximized;
      this.event("window", { maximized: this.maximized, focused: true, visible: true });
    },
    "window.close": async () => {},
    "window.getInfo": async () => ({ maximized: this.maximized, focused: true, visible: true }),

    // ---- auth ----
    "auth.signIn": async () => {
      if (this.auth.status === "signedIn") return clone(this.auth);
      const prev = this.auth;
      this.auth = { status: "signingIn", account: null };
      this.emitState();
      try {
        await this.sleep(1400);
        await this.holdWhileFrozen();
      } catch (e) {
        this.auth = prev;
        throw e;
      }
      this.auth = { status: "signedIn", account: ACCOUNT };
      this.syncError = null;
      this.syncing = true;
      this.emitState();
      this.timer(1200, () => {
        this.syncing = false;
        this.lastSyncedAt = iso();
        this.revealLibrary();
        // EGL scan follows the first sign-in, like onboarding's Scan step expects.
        if (!this.egl.revealed) {
          this.eglScanning = true;
          this.emitState();
          this.timer(1100, () => {
            this.eglScanning = false;
            this.egl = { ...this.egl, revealed: true, lastScan: iso() };
            this.emitState();
          });
        } else {
          this.emitState();
        }
      });
      return clone(this.auth);
    },
    "auth.signOut": async () => {
      await this.sleep(150);
      this.auth = { status: "signedOut", account: null };
      this.clearLibrary();
      this.emitState();
      return clone(this.auth);
    },

    // ---- library ----
    "library.get": async () => {
      await this.sleep(40);
      return this.resetDelta();
    },
    "library.refresh": async () => {
      if (this.syncing) return;
      this.syncing = true;
      this.emitState();
      const round = ++this.refreshRound;
      this.timer(1600, () => {
        this.syncing = false;
        if (this.connectivity === "offline") {
          this.syncError = Errors.offline("Syncing your library");
        } else if (this.cfg.scenario === "syncError" && round === 1) {
          this.syncError = Errors.network("GET https://library-service.live.use1a.on.epicgames.com/library/api/public/items: connection reset");
        } else {
          this.syncError = null;
          this.lastSyncedAt = iso();
          const fresh = [...this.games.values()].find((g) => g.summary.kind === "game" && g.summary.installStatus === "notInstalled" && !g.summary.hidden);
          if (fresh && round === 1) this.patch(fresh.summary.appName, { addedAt: iso() });
        }
        this.emitState();
      });
    },
    "library.checkUpdates": async () => {
      const round = ++this.checkRound;
      this.timer(1200, () => {
        let found = 0;
        if (round === 1 && this.connectivity === "online") {
          const g = [...this.games.values()].find((x) => x.summary.kind === "game" && x.summary.installStatus === "installed" && !x.summary.updateAvailable && x.summary.thirdParty === "none");
          if (g) {
            this.patch(g.summary.appName, { updateAvailable: true, latestVersion: `${g.summary.installedVersion ?? "1.0.0"}.1`, downloadSizeBytes: g.updateDownloadBytes });
            found = 1;
          }
        }
        this.notice({ level: found > 0 ? "info" : "success", title: found > 0 ? "An update is ready" : "Everything is up to date", message: null, action: null, appName: null, jobId: null });
        this.emitState();
      });
    },
    "library.updateAll": async () => {
      this.requireAuth();
      this.requireOnline("Updating");
      const jobs = [...this.games.values()]
        .filter((g) => g.summary.kind === "game" && g.summary.installStatus === "installed" && g.summary.updateAvailable && g.settings.autoUpdate !== "never" && !this.isRunning(g.summary.appName))
        .map((g) => this.createJob("update", g.summary.appName));
      return { jobs: clone(jobs) };
    },
    "library.setFavorite": async (r) => {
      this.rec(r.appName);
      this.patch(r.appName, { favorite: r.value });
    },
    "library.setHidden": async (r) => {
      this.rec(r.appName);
      this.patch(r.appName, { hidden: r.value });
    },

    // ---- collections ----
    "collections.create": async (r) => {
      const name = r.name.trim();
      if (!name) throw fail(Errors.invalidRequest("A collection needs a name"));
      const c: Collection = { id: `col-${this.collections.length + 1}-${name.toLowerCase().replace(/[^a-z0-9]+/g, "-")}`, name, appNames: [] };
      this.collections = [...this.collections, c];
      this.emitState();
      return clone(c);
    },
    "collections.rename": async (r) => {
      const c = this.collections.find((x) => x.id === r.id);
      if (!c) throw fail(Errors.invalidRequest("Unknown collection"));
      const next = { ...c, name: r.name.trim() || c.name };
      this.collections = this.collections.map((x) => (x.id === r.id ? next : x));
      this.emitState();
      return clone(next);
    },
    "collections.delete": async (r) => {
      this.collections = this.collections.filter((x) => x.id !== r.id);
      this.emitState();
    },
    "collections.setMembership": async (r) => {
      const c = this.collections.find((x) => x.id === r.id);
      if (!c) throw fail(Errors.invalidRequest("Unknown collection"));
      this.rec(r.appName);
      const appNames = r.member ? (c.appNames.includes(r.appName) ? c.appNames : [...c.appNames, r.appName]) : c.appNames.filter((a) => a !== r.appName);
      const next = { ...c, appNames };
      this.collections = this.collections.map((x) => (x.id === r.id ? next : x));
      this.emitState();
      return clone(next);
    },

    // ---- game ----
    "game.getDetail": async (r) => {
      await this.sleep(80);
      return clone(this.detail(r.appName));
    },
    "game.updateSettings": async (r) => {
      const g = this.rec(r.appName);
      if (r.settings.launchOption && !g.launchOptions.some((o) => o.name === r.settings.launchOption)) throw fail(Errors.invalidRequest("Unknown launch option"));
      g.settings = { ...r.settings };
      if (g.saves) {
        const enabled = g.settings.cloudSaves;
        g.saves = { ...g.saves, enabled };
        if (!enabled) this.setSaveStatus(r.appName, "disabled");
        else if (g.summary.saveStatus === "disabled") this.setSaveStatus(r.appName, g.summary.installStatus === "installed" ? "unknown" : "unknown");
      }
      this.emitState();
      return clone(g.settings);
    },
    "game.planInstall": async (r) => {
      this.requireAuth();
      await this.sleep(150);
      return this.planFor(r.appName, r.basePath, r.components, r.dlc);
    },
    "game.install": async (r) => {
      this.requireAuth();
      const g = this.rec(r.appName);
      const s = g.summary;
      if (s.installStatus === "installed" || s.installStatus === "broken") throw fail(Errors.alreadyInstalled(s.title, s.appName));
      if (s.installStatus === "notInstallable") throw fail(Errors.invalidRequest(`${s.title} can't be installed through Epic`));
      if (s.kind !== "game") {
        const base = this.rec(s.baseAppName ?? "");
        if (base.summary.installStatus !== "installed") throw fail(Errors.notInstalled(base.summary.title, base.summary.appName));
      }
      this.requireOnline("Installing");
      const plan = this.planFor(r.appName, r.basePath, r.components, r.dlc);
      const target = r.basePath || plan.basePath;
      if (this.free(target) < plan.installSizeBytes + INSTALL_MARGIN_BYTES) {
        throw fail(Errors.insufficientSpace(plan.installSizeBytes + INSTALL_MARGIN_BYTES, this.free(target), null, s.appName));
      }
      await this.sleep(200);
      const dlc = plan.dlc.filter((d) => d.selected).map((d) => d.appName);
      return clone(this.createJob("install", r.appName, { basePath: target, components: plan.components.filter((c) => c.selected).map((c) => c.id), dlc }, { front: r.priority }));
    },
    "game.modify": async (r) => {
      this.requireAuth();
      const g = this.rec(r.appName);
      if (g.summary.installStatus !== "installed") throw fail(Errors.notInstalled(g.summary.title, g.summary.appName));
      this.requireIdle(r.appName);
      this.requireOnline("Changing components");
      await this.sleep(150);
      return clone(this.createJob("modify", r.appName, { components: [...r.components], dlc: [...r.dlc] }));
    },
    "game.update": async (r) => {
      this.requireAuth();
      const g = this.rec(r.appName);
      const s = g.summary;
      if (s.installStatus !== "installed" && s.installStatus !== "broken") throw fail(Errors.notInstalled(s.title, s.appName));
      if (!s.updateAvailable) throw fail(Errors.invalidRequest(`${s.title} is up to date`));
      this.requireIdle(r.appName);
      this.requireOnline("Updating");
      const need = g.updateWriteBytes + Math.round(0.7 * GiB);
      const where = s.installPath ?? "D:\\";
      if (this.free(where) < need) throw fail(Errors.insufficientSpace(need, this.free(where), null, s.appName));
      await this.sleep(150);
      return clone(this.createJob("update", r.appName));
    },
    "game.verify": async (r) => {
      const g = this.rec(r.appName);
      const s = g.summary;
      if (s.installStatus !== "installed" && s.installStatus !== "broken") throw fail(Errors.notInstalled(s.title, s.appName));
      this.requireIdle(r.appName);
      await this.sleep(120);
      return clone(this.createJob("verify", r.appName));
    },
    "game.move": async (r) => {
      const g = this.rec(r.appName);
      const s = g.summary;
      if (s.installStatus !== "installed") throw fail(Errors.notInstalled(s.title, s.appName));
      this.requireIdle(r.appName);
      const dest = joinPath(r.destinationBase, g.folderName);
      if (samePath(dest, s.installPath ?? "")) throw fail(Errors.invalidFolder(`${s.title} is already in this folder.`));
      const size = s.installedSizeBytes ?? g.fullInstallBytes;
      if (this.free(r.destinationBase) < size) throw fail(Errors.insufficientSpace(size, this.free(r.destinationBase), null, s.appName));
      await this.sleep(150);
      return clone(this.createJob("move", r.appName, { destinationBase: r.destinationBase }));
    },
    "game.uninstall": async (r) => {
      const g = this.rec(r.appName);
      const s = g.summary;
      if (s.installStatus !== "installed" && s.installStatus !== "broken") throw fail(Errors.notInstalled(s.title, s.appName));
      this.requireIdle(r.appName);
      await this.sleep(120);
      return clone(this.createJob("uninstall", r.appName, { uninstallMode: r.mode }));
    },
    "game.launch": async (r) => this.launch(r),
    "game.prepareLaunch": async () => {},
    "game.close": async (r) => {
      this.rec(r.appName);
      if (this.sessions.some((s) => s.appName === r.appName)) this.endGameInternal(r.appName);
    },
    "game.openFolder": async (r) => {
      if (!this.rec(r.appName).summary.installPath) throw fail(Errors.notInstalled());
    },
    "game.activate": async (r) => {
      const g = this.rec(r.appName);
      if (g.summary.thirdParty === "none") throw fail(Errors.invalidRequest(`${g.summary.title} doesn't need activation`));
      this.requireOnline("Linking your account");
      await this.sleep(1500);
      this.activated.add(r.appName);
      g.needsActivation = false;
      this.notice({ level: "success", title: `${g.summary.title} is linked`, message: `Linked with ${g.summary.thirdPartyName ?? "its store"}.`, action: null, appName: r.appName, jobId: null });
    },
    "game.skipPrerequisites": async (r) => {
      this.rec(r.appName);
    },
    "game.installPrerequisites": async (r) => {
      const g = this.rec(r.appName);
      if (g.summary.runState === "idle") {
        this.setRun(r.appName, "installingPrerequisites");
        try {
          await this.sleep(1800);
        } finally {
          this.setRun(r.appName, "idle");
        }
      } else {
        await this.sleep(1800);
      }
      g.prerequisites = g.prerequisites.map((p) => ({ ...p, installed: true }));
      this.emitState();
    },
    "game.setShortcut": async (r) => {
      const g = this.rec(r.appName);
      g.shortcuts = { ...g.shortcuts, [r.location === "desktop" ? "desktop" : "startMenu"]: r.create };
      this.emitState();
      return clone(g.shortcuts);
    },

    // ---- cloud saves ----
    "saves.get": async (r) => {
      const g = this.rec(r.appName);
      await this.sleep(80);
      if (!g.saves) {
        return { supported: false, enabled: false, status: "notSupported", savePath: null, localModifiedAt: null, cloudModifiedAt: null, lastSyncedAt: null, lastError: null, backups: 0, conflict: null, note: null };
      }
      // Like the real backend: a conflicting game carries the details the resolve dialog shows.
      return clone(g.saves.status === "conflict" ? { ...g.saves, conflict: g.conflict ?? this.conflictFor(g) } : g.saves);
    },
    "saves.sync": async (r) => {
      const g = this.rec(r.appName);
      if (!g.saves) throw fail(Errors.invalidRequest(`${g.summary.title} has no cloud saves`));
      if (!g.saves.enabled) return clone(g.saves);
      this.requireOnline("Syncing saves");
      const before = g.saves.status;
      this.setSaveStatus(r.appName, "syncing");
      await this.sleep(900);
      if (before === "conflict" && r.direction === "auto") {
        this.setSaveStatus(r.appName, "conflict");
        g.conflict = g.conflict ?? this.conflictFor(g);
        return clone(g.saves!);
      }
      this.setSaveStatus(r.appName, "inSync", { lastSyncedAt: iso(), lastError: null, backups: g.saves!.backups + (before === "inSync" ? 0 : 1) });
      return clone(g.saves!);
    },
    "saves.resolve": async (r) => {
      const g = this.rec(r.appName);
      if (!g.saves) throw fail(Errors.invalidRequest(`${g.summary.title} has no cloud saves`));
      this.requireOnline("Resolving saves");
      this.setSaveStatus(r.appName, "syncing");
      await this.sleep(900);
      this.setSaveStatus(r.appName, "inSync", { lastSyncedAt: iso(), lastError: null, backups: g.saves!.backups + 1 });
      if (!r.thenLaunch) return null;
      return this.launch({ appName: r.appName, mode: null, skipSaveSync: true });
    },
    "saves.openBackups": async (r) => {
      this.rec(r.appName);
    },

    // ---- jobs / queue ----
    "jobs.pause": async (r) => {
      const e = this.entry(r.jobId);
      if (!e) throw fail(Errors.invalidRequest("That task has already finished"));
      if (!PAUSABLE.includes(e.info.kind)) throw fail(Errors.invalidRequest(`${e.info.kind} can't be paused`));
      if (e.info.status === "paused") return clone(e.info);
      e.info = { ...e.info, status: "paused", blockedReason: null };
      this.reconcile();
      return clone(e.info);
    },
    "jobs.resume": async (r) => {
      const e = this.entry(r.jobId);
      if (!e) throw fail(Errors.invalidRequest("That task has already finished"));
      if (e.info.status === "waiting") e.meta.forced = true;
      e.info = { ...e.info, status: "queued", blockedReason: null };
      this.queuePaused = false;
      this.reconcile();
      return clone(e.info);
    },
    "jobs.cancel": async (r) => {
      const e = this.entry(r.jobId);
      if (!e) throw fail(Errors.invalidRequest("That task has already finished"));
      if (e.info.kind === "uninstall" && e.info.status === "running") throw fail(Errors.invalidRequest("Removal can't be cancelled once it started"));
      return clone(this.cancelEntry(e));
    },
    "jobs.dismiss": async (r) => {
      if (this.entry(r.jobId)) throw fail(Errors.invalidRequest("Cancel the task before removing it"));
      this.finished = this.finished.filter((j) => j.id !== r.jobId);
      this.histories.delete(r.jobId);
      this.emitState();
    },
    "jobs.reorder": async (r) => {
      if (!this.entry(r.jobId)) throw fail(Errors.invalidRequest("That task has already finished"));
      const running = this.queue[0]?.info.status === "running";
      this.moveJob(r.jobId, running ? Math.max(1, r.toIndex) : r.toIndex);
      this.reconcile();
      return this.queueState();
    },
    "jobs.moveToTop": async (r) => {
      if (!this.entry(r.jobId)) throw fail(Errors.invalidRequest("That task has already finished"));
      this.moveJob(r.jobId, this.queue[0]?.info.status === "running" && this.queue[0].info.id !== r.jobId ? 1 : 0);
      this.reconcile();
      return this.queueState();
    },
    "jobs.getHistory": async (r) => {
      const e = this.entry(r.jobId);
      const samples = e ? e.sim.history() : this.histories.get(r.jobId);
      if (!samples) throw fail(Errors.invalidRequest("Unknown task"));
      return { jobId: r.jobId, samples } satisfies JobHistory;
    },
    "queue.pauseAll": async () => {
      this.queuePaused = true;
      for (const e of this.queue) if (e.info.status === "running") e.info = { ...e.info, status: "queued" };
      this.reconcile();
      return this.queueState();
    },
    "queue.resumeAll": async () => {
      this.queuePaused = false;
      for (const e of this.queue) if (e.info.status === "paused") e.info = { ...e.info, status: "queued" };
      this.reconcile();
      return this.queueState();
    },
    "queue.clearFinished": async () => {
      this.finished = [];
      this.emitState();
      return this.queueState();
    },

    // ---- egl ----
    "egl.rescan": async () => {
      this.eglScanning = true;
      this.emitState();
      await this.sleep(700);
      this.eglScanning = false;
      this.egl = { ...this.egl, revealed: true, lastScan: iso() };
      this.emitState();
      return clone(this.eglState());
    },
    "egl.quickScan": async () => {
      this.eglScanning = true;
      this.emitState();
      await this.sleep(1600);
      this.eglScanning = false;
      if (!this.quickScanned) {
        this.quickScanned = true;
        const g = [...this.games.values()].find((x) => x.summary.kind === "game" && x.summary.installStatus === "notInstalled" && x.summary.thirdParty === "none" && !this.egl.installs.some((i) => i.appName === x.summary.appName));
        if (g) {
          this.egl.installs = [
            ...this.egl.installs,
            {
              id: `manual:${g.summary.appName}`,
              source: "manual",
              appName: g.summary.appName,
              title: g.summary.title,
              installPath: `F:\\Old Library\\${g.folderName}`,
              drive: "F:\\",
              version: g.summary.latestVersion,
              sizeBytes: Math.round(g.fullInstallBytes * 0.98),
              status: "importable",
              drift: "none",
              reason: null,
              artUrl: g.summary.art.tallThumb,
            },
          ];
        }
      }
      this.egl = { ...this.egl, revealed: true, lastScan: iso() };
      this.emitState();
      return clone(this.eglState());
    },
    "egl.validateFolder": async (r) => {
      await this.sleep(200);
      const hit = this.folderGame(r.path);
      const v: FolderValidation = hit
        ? { valid: true, appName: hit.appName, title: hit.title, gamePath: hit.gamePath, version: hit.version, reason: null, sizeBytes: Math.round(this.rec(hit.appName).fullInstallBytes * 0.98) }
        : { valid: false, appName: null, title: null, gamePath: null, version: null, reason: "This folder doesn't contain a game from your library. Choose the folder that holds the game's files.", sizeBytes: null };
      return v;
    },
    "egl.import": async (r) => {
      this.requireAuth();
      const picked = this.egl.installs.filter((i) => r.ids.includes(i.id));
      if (picked.length === 0) throw fail(Errors.invalidRequest("Nothing selected to import"));
      const blocked = picked.find((i) => i.status === "busy");
      const importable = picked.filter((i) => i.status === "importable");
      if (importable.length === 0) {
        throw fail(blocked ? Errors.eglBusy(blocked.title, null, blocked.appName) : Errors.invalidRequest(picked[0]?.reason ?? "These installs can't be imported"));
      }
      await this.sleep(150);
      const jobs = importable.map((i) => this.createJob("import", i.appName, { eglInstallId: i.id, verifyMode: r.verify }));
      // Every other requested id comes back with its reason, like the real Core.
      const skipped: EglImportSkip[] = r.ids.flatMap((id): EglImportSkip[] => {
        const i = this.egl.installs.find((x) => x.id === id);
        if (!i) return [{ id, appName: null, title: null, reason: "This install isn't in the list anymore. Scan again." }];
        if (i.status === "importable") return [];
        return [{ id, appName: i.appName, title: i.title, reason: i.status === "imported" ? `${i.title} is already in Glider.` : (i.reason ?? `${i.title} can't be imported right now.`) }];
      });
      return { jobs: clone(jobs), skipped };
    },
    "egl.importFolder": async (r) => {
      this.requireAuth();
      const hit = this.folderGame(r.path);
      if (!hit) throw fail(Errors.invalidFolder("This folder doesn't contain a game from your library. Choose the folder that holds the game's files."));
      const g = this.rec(hit.appName);
      const id = `manual:${hit.appName}`;
      if (!this.egl.installs.some((i) => i.id === id)) {
        this.egl.installs = [
          ...this.egl.installs,
          { id, source: "manual", appName: hit.appName, title: hit.title, installPath: hit.gamePath, drive: `${driveLetter(hit.gamePath) ?? "C"}:\\`, version: hit.version, sizeBytes: Math.round(g.fullInstallBytes * 0.98), status: "importable", drift: "none", reason: null, artUrl: g.summary.art.tallThumb },
        ];
      }
      await this.sleep(150);
      return clone(this.createJob("import", hit.appName, { eglInstallId: id, verifyMode: r.verify }));
    },
    "egl.checkReadiness": async () => {
      await this.sleep(400);
      const items: ReadinessItem[] = [];
      for (const g of this.games.values()) {
        const s = g.summary;
        if (s.kind !== "game") continue;
        if (g.needsActivation && !this.activated.has(s.appName)) items.push({ appName: s.appName, title: s.title, severity: "blocker", message: `Link ${s.title} with ${s.thirdPartyName ?? "its store"} first. It can't start without that.`, fix: "launch" });
        else if (s.sharedWithEgl && s.installStatus === "installed") items.push({ appName: s.appName, title: s.title, severity: "warning", message: "Epic Games Launcher also lists this install. Glider keeps managing it after the launcher is gone.", fix: "none" });
        else if (g.prerequisites.some((p) => !p.installed)) items.push({ appName: s.appName, title: s.title, severity: "warning", message: "Needs a one-time setup before first launch. Glider handles that on Play.", fix: "launch" });
      }
      for (const a of this.egl.busyApps) items.push({ appName: a, title: this.titleOf(a), severity: "blocker", message: "Epic Games Launcher is still installing or updating this.", fix: "waitForEgl" });
      if (items.length === 0) items.push({ appName: null, title: "Everything checks out", severity: "ok", message: "No game depends on Epic Games Launcher.", fix: "none" });
      const r: EglReadiness = { ready: !items.some((i) => i.severity === "blocker"), items: items.slice(0, 40) };
      return r;
    },

    // ---- EOS overlay ----
    "overlay.install": async () => {
      this.requireAuth();
      this.requireOnline("Installing the overlay");
      await this.sleep(150);
      return clone(this.createJob("overlay", OVERLAY_APP));
    },
    "overlay.setEnabled": async (r) => {
      if (!this.overlay.installed) throw fail(Errors.invalidRequest("The overlay isn't installed"));
      this.overlay = { ...this.overlay, enabled: r.value };
      this.emitState();
      return clone(this.overlayState());
    },
    "overlay.remove": async () => {
      this.overlay = { installed: false, enabled: false, version: null, latestVersion: OVERLAY_LATEST, updateAvailable: false, installPath: null, busy: false, managed: false, externalPath: null, note: null };
      this.emitState();
      return clone(this.overlayState());
    },

    // ---- store / free games ----
    "store.show": async (r) => {
      this.store = { created: true, visible: true, loading: true, canGoBack: r.target !== null && r.target.kind !== "home", title: r.target?.kind === "freeGames" ? "Free Games" : "Store" };
      this.event("store", { ...this.store });
      this.timer(700, () => {
        this.store = { ...this.store, loading: false };
        this.event("store", { ...this.store });
      });
      return { ...this.store };
    },
    "store.setBounds": async () => {},
    "store.hide": async () => {
      this.store = { ...this.store, visible: false, loading: false };
      this.event("store", { ...this.store });
      return { ...this.store };
    },
    "store.back": async () => {
      this.store = { ...this.store, canGoBack: false, title: "Store" };
      this.event("store", { ...this.store });
      return { ...this.store };
    },
    "store.reload": async () => {
      this.store = { ...this.store, loading: true };
      this.event("store", { ...this.store });
      this.timer(500, () => {
        this.store = { ...this.store, loading: false };
        this.event("store", { ...this.store });
      });
      return { ...this.store };
    },
    "freeGames.get": async () => {
      this.requireOnline("Loading free games");
      await this.sleep(250);
      const now = Date.now();
      const owned = [...this.games.values()].find((g) => g.summary.kind === "game" && g.summary.installStatus !== "notInstallable");
      const r: FreeGamesResult = {
        fetchedAt: iso(),
        items: [
          { id: "free-1", title: "Lanternfall", imageUrl: wideArt("free-1"), slug: "lanternfall", startsAt: iso(now - 2 * DAY), endsAt: iso(now + 4 * DAY), status: "freeNow", owned: false },
          { id: "free-2", title: owned?.summary.title ?? "Orchard Tactics", imageUrl: wideArt("free-2"), slug: "orchard-tactics", startsAt: iso(now - 1 * DAY), endsAt: iso(now + 5 * DAY), status: "freeNow", owned: owned !== undefined },
          { id: "free-3", title: "Saltmarsh Courier", imageUrl: wideArt("free-3"), slug: "saltmarsh-courier", startsAt: iso(now + 4 * DAY), endsAt: iso(now + 11 * DAY), status: "upcoming", owned: false },
        ],
      };
      return r;
    },

    // ---- settings ----
    "settings.update": async (s) => {
      await this.sleep(60);
      const out = this.updateSettingsInternal(s);
      this.emitState();
      return out;
    },
    "settings.reset": async () => {
      await this.sleep(60);
      const out = this.updateSettingsInternal({ ...DEFAULT_SETTINGS, libraryFolders: this.settings.libraryFolders, defaultInstallBase: this.settings.defaultInstallBase });
      this.emitState();
      return out;
    },
  };

  // The progress helper used by tests/sim consumers.
  progressOf(jobId: string): JobSim | undefined {
    return this.entry(jobId)?.sim;
  }
}
