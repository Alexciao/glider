import type { AppSnapshot, JobProgress, LibraryDelta } from "../ipc/contract.gen";
import { rpc } from "../ipc/rpc";
import { hasSettingsInFlight } from "./actions";
import { useLibrary } from "./library";
import { pushNotice, useNotices } from "./notices";
import { ingestProgress, pruneProgress, seedProgress } from "./progress";
import { installIntent, navigate } from "./router";
import { isActiveJob, useApp } from "./store";
import { useUi } from "./ui";

/**
 * Detects "an install/update/import that was running just completed" and returns its game, for the game page's
 * ready moment. (Only for jobs we watched run: a finished job in the first snapshot is old news.)
 */
export function detectReadyMoment(prev: AppSnapshot | null, next: AppSnapshot): string | null {
  if (!prev) return null;
  const before = new Set(prev.queue.jobs.filter(isActiveJob).map((j) => j.id));
  const done = next.queue.jobs.find(
    (j) => before.has(j.id) && j.status === "completed" && (j.kind === "install" || j.kind === "update" || j.kind === "import"),
  );
  return done?.appName ?? null;
}

/** Dev-only hook (mock mode): the stress toggle rewrites progress at the same boundary the backend uses. */
let progressTransform: ((p: JobProgress) => JobProgress) | null = null;
export const setProgressTransform = (fn: typeof progressTransform): void => void (progressTransform = fn);

function applySnapshot(next: AppSnapshot): void {
  const store = useApp.getState();
  const prev = store.snapshot;
  // An optimistic settings write is still in flight: keep the local value so toggles don't flicker back.
  const merged = prev && hasSettingsInFlight() ? { ...next, settings: prev.settings } : next;
  store.setSnapshot(merged);
  // The connection is back: "Can't reach Epic" is over (the jobs it stopped resume on their own).
  if (prev?.connectivity === "offline" && merged.connectivity === "online") useNotices.getState().dismissTitled("Can't reach Epic");
  const readyApp = detectReadyMoment(prev, merged);
  if (readyApp) useUi.getState().markReady(readyApp);
  pruneProgress(new Set(merged.queue.jobs.map((j) => j.id)));
  seedProgress(merged.queue.jobs.map((j) => j.progress));
}

/**
 * Keeps the library store in step with the backend: one `library.get` at boot, then deltas in order. Deltas that
 * arrive while a (re)load is in flight are buffered and replayed on top of it; a revision gap triggers a reload.
 */
export class LibrarySync {
  private loading: Promise<void> | null = null;
  /** True from the moment a load starts (before its request is even sent) until it has been applied. */
  private busy = false;
  private buffer: LibraryDelta[] = [];
  private disposed = false;

  /** Fetch the whole library (reset) and replay any buffered deltas newer than it. */
  load(): Promise<void> {
    if (this.loading) return this.loading;
    this.busy = true;
    this.loading = this.fetchAndReplay().finally(() => {
      this.busy = false;
      this.loading = null;
    });
    return this.loading;
  }

  private async fetchAndReplay(): Promise<void> {
    // A replayed delta can itself reveal a gap (more were lost while loading): look again, a few times at most.
    for (let attempt = 0; attempt < 3; attempt++) {
      const full = await rpc().call("library.get");
      if (this.disposed) return;
      const store = useLibrary.getState();
      store.apply(full);
      const pending = this.buffer.filter((d) => d.reset || d.revision > full.revision).sort((x, y) => x.revision - y.revision);
      this.buffer = [];
      let gap = false;
      for (const d of pending) if (store.apply(d) === "gap") gap = true;
      if (!gap) return;
    }
  }

  onDelta = (delta: LibraryDelta): void => {
    if (this.busy) {
      this.buffer.push(delta);
      return;
    }
    const result = useLibrary.getState().apply(delta);
    if (result === "gap") void this.load();
  };

  dispose(): void {
    this.disposed = true;
  }
}

/** Wires backend events into the stores and loads the first snapshot and library. Returns an unsubscribe. */
export async function startBridge(): Promise<() => void> {
  const c = rpc();
  let sawState = false;
  const sync = new LibrarySync();
  useLibrary.getState().reset();
  const offs = [
    c.on("state", (s) => {
      sawState = true;
      applySnapshot(s);
    }),
    c.on("library", sync.onDelta),
    c.on("progress", (p) => ingestProgress(progressTransform ? progressTransform(p) : p)),
    c.on("notice", pushNotice),
    c.on("navigate", (e) => {
      navigate(e.route);
      const install = installIntent(e.route);
      if (install) useUi.getState().openDialog({ kind: "install", appName: install });
    }),
    c.on("window", (w) => useApp.getState().setWindow(w)),
    c.on("caption", (e) => {
      if (e.button === "max") useApp.getState().setMaxButton(e.state === "leave" ? "idle" : e.state);
    }),
  ];
  const [snap, win] = await Promise.all([c.call("app.getSnapshot"), c.call("window.getInfo"), sync.load()]);
  if (!sawState) applySnapshot(snap);
  useApp.getState().setWindow(win);
  return () => {
    sync.dispose();
    offs.forEach((off) => off());
  };
}
