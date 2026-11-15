import { create } from "zustand";
import type { FixAction, Notice, NoticeLevel } from "../ipc/contract.gen";

/** Toast queue. Backend `notice` events and local confirmations share one region. */
export interface Toast {
  id: string;
  level: NoticeLevel;
  title: string;
  message?: string | null;
  action?: FixAction | null;
  /** Local handler for non-FixAction toasts (e.g. Undo). */
  actionLabel?: string;
  onAction?: () => void;
  duration?: number;
  /** The game the toast is about (for fix actions that need one). */
  appName?: string | null;
  /** The failed job the toast is about (a backend failure notice). */
  jobId?: string | null;
  /** How many identical toasts this one stands for (see `mergeInto`); absent = one. */
  merged?: number;
}

interface NoticeStore {
  items: Toast[];
  push: (t: Toast) => void;
  dismiss: (id: string) => void;
  /** Remove every toast with this title (the situation it reported is over). */
  dismissTitled: (title: string) => void;
}

const MAX_VISIBLE = 3;

/** Warnings and failures a backend raises once per job: ten jobs failing for the same reason say it once, not ten times. */
const mergeable = (t: Toast): boolean => (t.level === "error" || t.level === "warning") && !t.onAction && !t.actionLabel;

export const useNotices = create<NoticeStore>((set) => ({
  items: [],
  push: (t) =>
    set((s) => {
      const twin = mergeable(t) ? s.items.find((x) => x.id !== t.id && mergeable(x) && x.level === t.level && x.title === t.title && x.message === t.message) : undefined;
      if (twin) {
        // The same sentence again: keep the toast on screen and count it, so a whole queue failing reads as one toast. A fix button would only fit one of them, so it goes.
        return { items: s.items.map((x) => (x === twin ? { ...x, merged: (x.merged ?? 1) + 1, action: null } : x)) };
      }
      const rest = s.items.filter((x) => x.id !== t.id);
      return { items: [...rest, t].slice(-MAX_VISIBLE) };
    }),
  dismiss: (id) => set((s) => ({ items: s.items.filter((x) => x.id !== id) })),
  dismissTitled: (title) => set((s) => (s.items.some((x) => x.title === title) ? { items: s.items.filter((x) => x.title !== title) } : s)),
}));

/** "Can't reach Epic · 3 downloads": the title with how many identical toasts it stands for. */
export const toastTitle = (t: Pick<Toast, "title" | "merged" | "jobId">): string => (t.merged && t.merged > 1 ? `${t.title} · ${t.merged} ${t.jobId ? "downloads" : "times"}` : t.title);

let seq = 0;
export function toast(t: Omit<Toast, "id"> & { id?: string }): string {
  const id = t.id ?? `local-${++seq}`;
  useNotices.getState().push({ ...t, id });
  return id;
}

const suppressors = new Set<(n: Notice) => boolean>();

/**
 * Register a predicate: backend notices it returns true for are not toasted, because a screen already shows that
 * outcome inline (the onboarding import list shows every game's result; a toast per game would say it twice).
 * Returns the unregister function.
 */
export function suppressNoticesWhen(fn: (n: Notice) => boolean): () => void {
  suppressors.add(fn);
  return () => void suppressors.delete(fn);
}

export const pushNotice = (n: Notice): void => {
  for (const suppress of suppressors) if (suppress(n)) return;
  useNotices.getState().push({ ...n });
};
