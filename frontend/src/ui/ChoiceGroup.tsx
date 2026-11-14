import type { ReactNode } from "react";
import { RadioGroup } from "radix-ui";
import { cx } from "./cx";

export interface ChoiceOption<T extends string> {
  value: T;
  title: ReactNode;
  /** One line (two at most) saying what choosing this does. */
  description: ReactNode;
  /** Leading glyph, 20 px box. */
  icon?: ReactNode;
  /** Short chip after the title, e.g. "Recommended". */
  badge?: ReactNode;
}

export interface ChoiceGroupProps<T extends string> {
  value: T;
  onValueChange: (v: T) => void;
  options: readonly ChoiceOption<T>[];
  "aria-label": string;
  className?: string;
}

/**
 * A short list of mutually exclusive choices that each need a sentence of explanation (download mode, theme).
 * Built on RadioGroup: arrow keys move and select, one tab stop. The selected card gets the accent edge; the radio
 * dot on the right is the shape cue, so selection never relies on colour alone.
 */
export function ChoiceGroup<T extends string>({ value, onValueChange, options, className, ...aria }: ChoiceGroupProps<T>) {
  return (
    <RadioGroup.Root value={value} onValueChange={(v) => onValueChange(v as T)} {...aria} className={cx("flex flex-col gap-2", className)}>
      {options.map((o) => {
        const on = o.value === value;
        return (
          <RadioGroup.Item
            key={o.value}
            value={o.value}
            className={cx(
              "group flex w-full items-center gap-4 rounded-xl border p-4 text-left",
              "transition-[background-color,border-color,box-shadow,transform] duration-150 ease-out active:scale-[0.995]",
              on ? "border-accent bg-surface-2 shadow-[0_0_0_1px_var(--g-accent)]" : "border-border bg-surface-1 hover:border-border-strong hover:bg-surface-2",
            )}
          >
            {o.icon && (
              <span
                aria-hidden
                className={cx(
                  "grid size-10 shrink-0 place-items-center rounded-lg transition-colors duration-150",
                  on ? "bg-accent-soft text-accent-text" : "bg-surface-2 text-fg-muted",
                )}
              >
                {o.icon}
              </span>
            )}
            <span className="min-w-0 flex-1">
              <span className="flex items-center gap-2 text-base font-medium text-fg">
                {o.title}
                {o.badge}
              </span>
              <span className="mt-0.5 block text-sm leading-[1.4] text-fg-muted">{o.description}</span>
            </span>
            <span
              aria-hidden
              className={cx(
                "relative grid size-[18px] shrink-0 place-items-center rounded-full border transition-colors duration-150",
                on ? "border-accent" : "border-fg-disabled group-hover:border-fg-subtle",
              )}
            >
              <span className={cx("size-2 rounded-full bg-accent transition-[transform,opacity] duration-150 ease-out", on ? "scale-100 opacity-100" : "scale-50 opacity-0")} />
            </span>
          </RadioGroup.Item>
        );
      })}
    </RadioGroup.Root>
  );
}
