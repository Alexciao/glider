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
}

interface NoticeStore {
  items: Toast[];
  push: (t: Toast) => void;
  dismiss: (id: string) => void;
}

const MAX_VISIBLE = 3;

export const useNotices = create<NoticeStore>((set) => ({
  items: [],
  push: (t) =>
    set((s) => {
      const rest = s.items.filter((x) => x.id !== t.id);
      return { items: [...rest, t].slice(-MAX_VISIBLE) };
    }),
  dismiss: (id) => set((s) => ({ items: s.items.filter((x) => x.id !== id) })),
}));

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
