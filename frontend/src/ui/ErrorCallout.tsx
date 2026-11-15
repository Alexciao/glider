import { useState, type ReactNode } from "react";
import { ChevronRight, CircleAlert, TriangleAlert, X } from "lucide-react";
import { cx } from "./cx";
import { Button, IconButton } from "./Button";

export interface ErrorCalloutProps {
  title: string;
  message: string;
  /** "error" is red; "warning" is amber (for waiting-on-something states). */
  tone?: "error" | "warning";
  /** Label + handler for the one-click fix, if the error has one. */
  fixLabel?: string;
  onFix?: () => void;
  fixLoading?: boolean;
  /** Secondary action rendered beside the fix. */
  secondary?: ReactNode;
  /** Technical detail, collapsed by default. */
  detail?: string | null;
  onDismiss?: () => void;
  className?: string;
}

/** Inline failure: what happened, why it matters, and the one-click fix. Never apologises. */
export function ErrorCallout({
  title,
  message,
  tone = "error",
  fixLabel,
  onFix,
  fixLoading,
  secondary,
  detail,
  onDismiss,
  className,
}: ErrorCalloutProps) {
  const [open, setOpen] = useState(false);
  const Icon = tone === "error" ? CircleAlert : TriangleAlert;
  return (
    <div
      role={tone === "error" ? "alert" : "status"}
      className={cx(
        "flex gap-3 rounded-lg border p-3.5",
        tone === "error" ? "border-danger/25 bg-danger-soft" : "border-warning/25 bg-warning-soft",
        className,
      )}
    >
      <Icon size={16} className={cx("mt-0.5 shrink-0", tone === "error" ? "text-danger-text" : "text-warning-text")} aria-hidden />
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium text-fg">{title}</p>
        <p className="mt-0.5 text-xs leading-[1.5] text-fg-muted">{message}</p>
        {(fixLabel || secondary || detail) && (
          <div className="mt-3 flex flex-wrap items-center gap-2">
            {fixLabel && (
              <Button size="sm" variant="secondary" onClick={onFix} loading={fixLoading}>
                {fixLabel}
              </Button>
            )}
            {secondary}
            {detail && (
              <button
                type="button"
                onClick={() => setOpen((o) => !o)}
                aria-expanded={open}
                className="inline-flex h-7 items-center gap-1 rounded-md px-1.5 text-xs text-fg-muted transition-colors duration-150 hover:text-fg"
              >
                <ChevronRight size={12} className={cx("transition-transform duration-150 ease-out", open && "rotate-90")} />
                Details
              </button>
            )}
          </div>
        )}
        {open && detail && (
          <pre className="selectable mt-2 max-h-28 overflow-auto whitespace-pre-wrap break-all rounded-md bg-surface-2 p-2 font-mono text-2xs leading-[1.5] text-fg-muted">
            {detail}
          </pre>
        )}
      </div>
      {onDismiss && (
        <IconButton label="Dismiss" size="sm" onClick={onDismiss} className="-mr-1 -mt-1" tooltip={false}>
          <X size={14} />
        </IconButton>
      )}
    </div>
  );
}
