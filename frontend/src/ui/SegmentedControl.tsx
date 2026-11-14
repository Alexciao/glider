import type { ReactNode } from "react";
import { RadioGroup } from "radix-ui";
import { cx } from "./cx";

export interface SegmentedOption<T extends string> {
  value: T;
  label: ReactNode;
  /** Accessible name when `label` is only an icon. */
  ariaLabel?: string;
}

export interface SegmentedControlProps<T extends string> {
  value: T;
  onValueChange: (v: T) => void;
  options: readonly SegmentedOption<T>[];
  "aria-label": string;
  size?: "sm" | "md";
  className?: string;
}

/**
 * Equal-width segments with a thumb that slides by translateX (compositor-only).
 * Built on RadioGroup, so arrow keys and roving focus come for free.
 */
export function SegmentedControl<T extends string>({
  value,
  onValueChange,
  options,
  size = "md",
  className,
  ...aria
}: SegmentedControlProps<T>) {
  const index = Math.max(0, options.findIndex((o) => o.value === value));
  const n = options.length;
  return (
    <RadioGroup.Root
      value={value}
      onValueChange={(v) => onValueChange(v as T)}
      orientation="horizontal"
      {...aria}
      className={cx(
        "relative grid rounded-lg border border-border bg-surface-1 p-0.5",
        size === "sm" ? "h-7" : "h-8",
        className,
      )}
      style={{ gridTemplateColumns: `repeat(${n}, minmax(0, 1fr))` }}
    >
      <span
        aria-hidden
        className="pointer-events-none absolute inset-y-0.5 left-0.5 rounded-md border border-border-strong bg-surface-3 shadow-sm transition-transform duration-200 ease-out"
        style={{ width: `calc((100% - 4px) / ${n})`, transform: `translateX(${index * 100}%)` }}
      />
      {options.map((o) => (
        <RadioGroup.Item
          key={o.value}
          value={o.value}
          aria-label={o.ariaLabel}
          className={cx(
            "relative z-10 whitespace-nowrap rounded-md px-3 text-center font-medium transition-colors duration-150 ease-out",
            size === "sm" ? "text-xs" : "text-sm",
            o.value === value ? "text-fg" : "text-fg-muted hover:text-fg",
          )}
        >
          {o.label}
        </RadioGroup.Item>
      ))}
    </RadioGroup.Root>
  );
}
