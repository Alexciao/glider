import { Toast as T } from "radix-ui";
import { AlertTriangle, CheckCircle2, CircleAlert, Info, X } from "lucide-react";
import { cx } from "./cx";
import { Button } from "./Button";

export type ToastLevel = "info" | "success" | "warning" | "error";

export interface ToastItem {
  id: string;
  level: ToastLevel;
  title: string;
  message?: string | null;
  actionLabel?: string;
  onAction?: () => void;
  /** ms; 0 = sticky until dismissed. */
  duration?: number;
}

const icons = {
  info: <Info size={16} className="text-accent-text" />,
  success: <CheckCircle2 size={16} className="text-success-text" />,
  warning: <AlertTriangle size={16} className="text-warning-text" />,
  error: <CircleAlert size={16} className="text-danger-text" />,
} satisfies Record<ToastLevel, React.ReactNode>;

const defaultDuration: Record<ToastLevel, number> = { info: 5000, success: 5000, warning: 8000, error: 10000 };

export function ToastRegion({ toasts, onDismiss }: { toasts: readonly ToastItem[]; onDismiss: (id: string) => void }) {
  return (
    <T.Provider swipeDirection="right" swipeThreshold={60} label="Notifications">
      {toasts.map((t) => (
        <T.Root
          key={t.id}
          duration={t.duration === 0 ? Infinity : (t.duration ?? defaultDuration[t.level])}
          onOpenChange={(open) => !open && onDismiss(t.id)}
          type={t.level === "error" ? "foreground" : "background"}
          className={cx(
            "g-toast pointer-events-auto flex w-[360px] max-w-[calc(100vw-32px)] gap-3 rounded-xl border border-border-strong bg-popover p-3.5 shadow-lg",
            "[box-shadow:inset_0_1px_0_var(--g-highlight),var(--g-shadow-lg)]",
          )}
        >
          <span className="mt-0.5 shrink-0" aria-hidden>
            {icons[t.level]}
          </span>
          <div className="min-w-0 flex-1">
            <T.Title className="text-sm font-medium text-fg">{t.title}</T.Title>
            {t.message && <T.Description className="mt-0.5 text-xs leading-[1.5] text-fg-muted">{t.message}</T.Description>}
            {t.actionLabel && (
              <T.Action altText={t.actionLabel} asChild>
                <Button size="sm" className="mt-2.5" onClick={t.onAction}>
                  {t.actionLabel}
                </Button>
              </T.Action>
            )}
          </div>
          <T.Close
            aria-label="Dismiss notification"
            className="-mr-1 -mt-1 grid size-6 shrink-0 place-items-center rounded-md text-fg-subtle transition-colors duration-150 hover:bg-surface-hover hover:text-fg"
          >
            <X size={14} />
          </T.Close>
        </T.Root>
      ))}
      <T.Viewport className="pointer-events-none fixed right-4 top-[calc(var(--titlebar-height)+12px)] z-[60] m-0 flex list-none flex-col gap-2 p-0 outline-none" />
    </T.Provider>
  );
}
