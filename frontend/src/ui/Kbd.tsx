import { cx } from "./cx";

/** A single key cap. Pass "Ctrl+K" to render a combo as separate caps. */
export function Kbd({ children, className }: { children: string; className?: string }) {
  const keys = children.split("+");
  return (
    <span className={cx("inline-flex items-center gap-0.5", className)} aria-label={children.replace("+", " plus ")}>
      {keys.map((k, i) => (
        <kbd
          key={i}
          aria-hidden
          className="inline-flex h-[18px] min-w-[18px] items-center justify-center rounded-xs border border-border-strong bg-surface-2 px-1 font-sans text-2xs font-medium leading-none text-fg-muted shadow-[0_1px_0_var(--g-border)]"
        >
          {k}
        </kbd>
      ))}
    </span>
  );
}
