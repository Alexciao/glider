import { useMemo } from "react";
import { runFix } from "../state/actions";
import { useNotices } from "../state/notices";
import { FIX_LABELS } from "../state/ui";
import { ToastRegion, type ToastItem } from "../ui";

/** Backend `notice` events and local confirmations share one region. */
export function Notices() {
  const items = useNotices((s) => s.items);
  const dismiss = useNotices((s) => s.dismiss);
  const toasts = useMemo<ToastItem[]>(
    () =>
      items.map((t) => {
        const fix = t.action;
        return {
          id: t.id,
          level: t.level,
          title: t.title,
          message: t.message,
          duration: t.duration,
          actionLabel: t.actionLabel ?? (fix ? FIX_LABELS[fix] : undefined),
          onAction: t.onAction ?? (fix ? () => runFix(fix) : undefined),
        };
      }),
    [items],
  );
  return <ToastRegion toasts={toasts} onDismiss={dismiss} />;
}
