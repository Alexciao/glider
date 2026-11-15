import type { ReactNode } from "react";
import { Select as S } from "radix-ui";
import { Check, ChevronDown } from "lucide-react";
import { cx } from "./cx";

export interface SelectOption<T extends string> {
  value: T;
  label: ReactNode;
  hint?: string;
}

export interface SelectProps<T extends string> {
  value: T;
  onValueChange: (v: T) => void;
  options: readonly SelectOption<T>[];
  "aria-label"?: string;
  id?: string;
  disabled?: boolean;
  className?: string;
  /** Minimum trigger width in px. */
  minWidth?: number;
}

export function Select<T extends string>({
  value,
  onValueChange,
  options,
  disabled,
  className,
  minWidth = 160,
  ...aria
}: SelectProps<T>) {
  return (
    <S.Root value={value} onValueChange={(v) => onValueChange(v as T)} disabled={disabled}>
      <S.Trigger
        {...aria}
        style={{ minWidth }}
        className={cx(
          "group inline-flex h-8 items-center justify-between gap-2 rounded-md border border-border-strong bg-surface-2 pl-3 pr-2 text-sm text-fg",
          "transition-[background-color,transform,scale] duration-150 ease-out hover:bg-surface-3 press:scale-[0.98] data-[state=open]:bg-surface-3 disabled:pointer-events-none disabled:opacity-45",
          className,
        )}
      >
        <S.Value />
        <S.Icon className="text-fg-subtle transition-transform duration-150 ease-out group-data-[state=open]:rotate-180">
          <ChevronDown size={14} />
        </S.Icon>
      </S.Trigger>
      <S.Portal>
        <S.Content
          position="popper"
          sideOffset={6}
          collisionPadding={12}
          className="g-pop g-select-pop z-50 max-h-72 min-w-[var(--radix-select-trigger-width)] overflow-hidden rounded-lg border border-border-strong bg-popover p-1 shadow-lg"
        >
          <S.Viewport>
            {options.map((o) => (
              <S.Item
                key={o.value}
                value={o.value}
                className="relative flex h-8 cursor-default select-none items-center gap-2 rounded-md pl-7 pr-3 text-sm text-fg outline-none data-[highlighted]:bg-surface-hover data-[disabled]:opacity-45"
              >
                <S.ItemIndicator className="absolute left-2 text-accent-text">
                  <Check size={14} strokeWidth={2.25} />
                </S.ItemIndicator>
                <S.ItemText>{o.label}</S.ItemText>
                {o.hint && <span className="ml-auto pl-4 text-xs text-fg-subtle">{o.hint}</span>}
              </S.Item>
            ))}
          </S.Viewport>
        </S.Content>
      </S.Portal>
    </S.Root>
  );
}
