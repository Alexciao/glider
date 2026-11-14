import type { ComponentProps } from "react";
import { Checkbox as C } from "radix-ui";
import { Check, Minus } from "lucide-react";
import { cx } from "./cx";

export type CheckedState = boolean | "indeterminate";

export interface CheckboxProps extends Omit<ComponentProps<typeof C.Root>, "checked" | "onCheckedChange"> {
  checked: CheckedState;
  onCheckedChange: (checked: boolean) => void;
}

/**
 * 16 px box, accent fill when on. Supports the tri-state "select all" pattern: pass `checked="indeterminate"` and the
 * next click reports `true`. Give it an accessible name (a wrapping label, `aria-label` or `aria-labelledby`).
 */
export function Checkbox({ checked, onCheckedChange, className, ...rest }: CheckboxProps) {
  return (
    <C.Root
      checked={checked}
      onCheckedChange={(v) => onCheckedChange(v === true)}
      className={cx(
        "group/cb relative grid size-4 shrink-0 place-items-center rounded-xs border border-fg-disabled bg-surface-1 text-accent-fg",
        "transition-[background-color,border-color,transform] duration-150 ease-out active:scale-90",
        "hover:border-fg-subtle data-[state=checked]:border-transparent data-[state=checked]:bg-accent data-[state=indeterminate]:border-transparent data-[state=indeterminate]:bg-accent",
        "disabled:pointer-events-none disabled:opacity-45",
        className,
      )}
      {...rest}
    >
      <C.Indicator className="grid place-items-center data-[state=checked]:animate-[g-tick_140ms_var(--ease-out)] data-[state=indeterminate]:animate-[g-tick_140ms_var(--ease-out)]">
        {checked === "indeterminate" ? <Minus size={12} strokeWidth={3.25} /> : <Check size={12} strokeWidth={3.25} />}
      </C.Indicator>
    </C.Root>
  );
}
