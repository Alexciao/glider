import { useCallback, useMemo, useState } from "react";
import type { EglInstall, JobInfo } from "../../ipc/contract.gen";
import { actions } from "../../state/actions";
import { useLibrary } from "../../state/library";
import { useApp } from "../../state/store";
import { useUi, usePending, type ErrorSurface } from "../../state/ui";
import { useImportBatch, wireImportBatch, type BatchItem } from "./batch";
import { groupInstalls, rowResult, summarize, sumBytes, type BatchSummary, type InstallGroups, type RowResult } from "./model";

export type ImportPhase =
  /** Library sync or the scan is still running: nothing honest to list yet. */
  | "scanning"
  /** Scan finished and found nothing at all. */
  | "empty"
  /** Pick what to import. */
  | "list"
  /** A batch is running. */
  | "importing"
  /** Every job in the batch has a result. */
  | "done";

export interface ImportRowModel {
  install: EglInstall;
  job: JobInfo | null;
  result: RowResult;
}

export interface ImportSession {
  phase: ImportPhase;
  groups: InstallGroups;
  signedIn: boolean;
  /** Selected installs (always a subset of `groups.ready`). */
  selectedIds: ReadonlySet<string>;
  selectedCount: number;
  selectedBytes: number;
  allSelected: boolean | "indeterminate";
  toggle: (id: string) => void;
  selectAll: () => void;
  selectNone: () => void;
  rows: ImportRowModel[];
  summary: BatchSummary;
  starting: boolean;
  locating: boolean;
  scanning: boolean;
  /** Import the selected installs. */
  start: () => Promise<void>;
  /** Pick a folder, check it is one of the account's games, import it. */
  locate: () => Promise<void>;
  /** Try one failed row again. */
  retry: (installId: string) => Promise<void>;
  retryAllFailed: () => Promise<void>;
  /** Scan every drive (not just where Epic Games Launcher says games are). */
  scanDrives: () => Promise<void>;
  /** Forget the finished batch (leaving the step, closing the dialog after it finished). */
  reset: () => void;
}

/**
 * Everything the import list needs, on top of the snapshot's `EglState` and the import batch store. Selection is kept as
 * a set of *deselected* ids, so installs found later (a scan of all drives) arrive selected, as the first batch did.
 */
export function useImportSession(surface: ErrorSurface = "onboarding"): ImportSession {
  wireImportBatch();
  const egl = useApp((s) => s.snapshot!.egl);
  const jobs = useApp((s) => s.snapshot!.queue.jobs);
  const signedIn = useApp((s) => s.snapshot!.auth.status === "signedIn");
  const libraryReady = useLibrary((s) => s.loaded);
  const items = useImportBatch((s) => s.items);
  const finished = useImportBatch((s) => s.finished);
  const starting = usePending("import");
  const picking = usePending("pick");
  const validating = usePending("validate");
  const rescanning = usePending("rescan");
  const [deselected, setDeselected] = useState<ReadonlySet<string>>(() => new Set());

  const groups = useMemo(() => groupInstalls(egl.installs), [egl.installs]);
  const selectedIds = useMemo(() => new Set(groups.ready.filter((i) => !deselected.has(i.id)).map((i) => i.id)), [groups.ready, deselected]);
  const selectedBytes = useMemo(() => sumBytes(groups.ready.filter((i) => selectedIds.has(i.id))), [groups.ready, selectedIds]);

  const rows = useMemo<ImportRowModel[]>(
    () =>
      items.map((item) => {
        const job = item.jobId ? (finished[item.jobId] ?? jobs.find((j) => j.id === item.jobId) ?? null) : null;
        const result: RowResult = item.jobId === null ? { state: "failed", message: "Glider couldn't start this import.", error: null } : rowResult(job);
        return { install: item.install, job, result };
      }),
    [items, finished, jobs],
  );
  const summary = useMemo(() => summarize(rows.map((r) => r.result.state)), [rows]);

  const scanning = egl.scanning || rescanning || !libraryReady || (egl.lastScan === null && egl.installs.length === 0);
  const phase: ImportPhase = rows.length > 0 ? (summary.settled ? "done" : "importing") : scanning ? "scanning" : egl.installs.length === 0 ? "empty" : "list";

  const toggle = useCallback(
    (id: string) =>
      setDeselected((cur) => {
        const next = new Set(cur);
        if (next.has(id)) next.delete(id);
        else next.add(id);
        return next;
      }),
    [],
  );
  const selectAll = useCallback(() => setDeselected(new Set()), []);
  const selectNone = useCallback(() => setDeselected(new Set(groups.ready.map((i) => i.id))), [groups.ready]);

  const begin = useCallback((installs: EglInstall[], jobsBack: JobInfo[]) => {
    const byApp = new Map(jobsBack.map((j) => [j.appName, j] as const));
    const batch: BatchItem[] = installs.map((install) => ({ install, jobId: byApp.get(install.appName)?.id ?? null }));
    useImportBatch.getState().start(batch);
  }, []);

  const start = useCallback(async () => {
    const picked = groups.ready.filter((i) => selectedIds.has(i.id));
    if (picked.length === 0) return;
    const r = await actions.importInstalls(
      picked.map((i) => i.id),
      "auto",
      surface,
    );
    if (r) begin(picked, r.jobs);
  }, [groups.ready, selectedIds, surface, begin]);

  async function locate(): Promise<void> {
    const ui = useUi.getState();
    ui.setError(surface, undefined);
    const path = await actions.pickFolder(null, "Locate a game folder");
    if (!path) return;
    const v = await actions.validateFolder(path);
    if (!v) return;
    if (!v.valid || !v.gamePath) {
      ui.setError(surface, {
        error: { code: "invalidFolder", title: "Not a game folder", message: v.reason ?? "This folder doesn't contain a game from your library.", fix: "chooseFolder", detail: path, appName: null, requiredBytes: null, drive: null },
        retry: () => void locate(),
      });
      return;
    }
    const job = await actions.importFolder(v.gamePath, "auto", surface);
    if (!job) return;
    const known = useApp.getState().snapshot?.egl.installs.find((i) => i.appName === job.appName && i.installPath === v.gamePath);
    const install: EglInstall = known ?? {
      id: `manual:${job.appName}`,
      source: "manual",
      appName: job.appName,
      title: v.title ?? job.title,
      installPath: v.gamePath,
      drive: `${v.gamePath.slice(0, 1).toUpperCase()}:\\`,
      version: v.version,
      sizeBytes: null,
      status: "importable",
      drift: "none",
      reason: null,
      artUrl: null,
    };
    useImportBatch.getState().start([{ install, jobId: job.id }]);
  }

  const retry = useCallback(
    async (installId: string) => {
      const item = useImportBatch.getState().items.find((x) => x.install.id === installId);
      if (!item) return;
      const r = await actions.importInstalls([installId], "auto", surface);
      const job = r?.jobs.find((j) => j.appName === item.install.appName);
      if (job) useImportBatch.getState().restart(installId, job.id);
    },
    [surface],
  );

  const retryAllFailed = useCallback(async () => {
    const failed = rows.filter((r) => r.result.state === "failed");
    for (const r of failed) await retry(r.install.id);
  }, [rows, retry]);

  const scanDrives = useCallback(() => actions.rescanEgl(true), []);
  const reset = useCallback(() => useImportBatch.getState().clear(), []);

  const n = groups.ready.length;
  const allSelected: boolean | "indeterminate" = n === 0 ? false : selectedIds.size === n ? true : selectedIds.size === 0 ? false : "indeterminate";

  return {
    phase,
    groups,
    signedIn,
    selectedIds,
    selectedCount: selectedIds.size,
    selectedBytes,
    allSelected,
    toggle,
    selectAll,
    selectNone,
    rows,
    summary,
    starting,
    locating: picking || validating,
    scanning,
    start,
    locate,
    retry,
    retryAllFailed,
    scanDrives,
    reset,
  };
}
