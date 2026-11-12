import { useMemo } from "react";
import type { AppSnapshot, JobProgress } from "../ipc/contract.gen";
import { rpc } from "../ipc/rpc";
import { hasSettingsInFlight } from "./actions";
import { deriveHero, type HeroModel } from "./derive";
import { pushNotice } from "./notices";
import { ingestProgress, pruneProgress, useProgress } from "./progress";
import { currentJob, isActiveJob, useApp } from "./store";
import { useUi } from "./ui";

/** Detects "an install/update/import that was running just completed" → Library's ready moment. */
export function detectReadyMoment(prev: AppSnapshot | null, next: AppSnapshot): boolean {
  if (!prev) return false;
  const before = new Set(prev.jobs.filter(isActiveJob).map((j) => j.id));
  return next.jobs.some(
    (j) => before.has(j.id) && j.status === "completed" && (j.kind === "install" || j.kind === "update" || j.kind === "import"),
  );
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
  if (detectReadyMoment(prev, merged)) useUi.getState().markReady();
  pruneProgress(new Set(merged.jobs.map((j) => j.id)));
}

/** Wires backend events into the stores and loads the first snapshot. Returns an unsubscribe. */
export async function startBridge(): Promise<() => void> {
  const c = rpc();
  let sawState = false;
  const offs = [
    c.on("state", (s) => {
      sawState = true;
      applySnapshot(s);
    }),
    c.on("progress", (p) => ingestProgress(progressTransform ? progressTransform(p) : p)),
    c.on("notice", pushNotice),
    c.on("window", (w) => useApp.getState().setWindow(w)),
    c.on("caption", (e) => {
      if (e.button === "max") useApp.getState().setMaxButton(e.state === "leave" ? "idle" : e.state);
    }),
  ];
  const [snap, win] = await Promise.all([c.call("app.getSnapshot"), c.call("window.getInfo")]);
  if (!sawState) applySnapshot(snap);
  useApp.getState().setWindow(win);
  return () => offs.forEach((off) => off());
}

/** The hero model, re-derived only when the snapshot or whole-percent/phase changes. */
export function useHero(): HeroModel {
  const snap = useApp((s) => s.snapshot as AppSnapshot);
  const job = currentJob(snap.jobs);
  const jobId = job?.id;
  const phase = useProgress((s) => (jobId ? s.latest[jobId]?.phase : undefined));
  const percent = useProgress((s) => (jobId ? Math.floor(s.latest[jobId]?.percent ?? job?.progress?.percent ?? 0) : 0));
  return useMemo(() => deriveHero(snap, { phase: phase ?? job?.progress?.phase, percent }), [snap, phase, percent, job?.progress?.phase]);
}
