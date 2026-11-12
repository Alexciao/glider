import { useEffect, useState, type ComponentProps, type ReactNode } from "react";
import { Minus, Plus } from "lucide-react";
import { cx } from "./cx";

const field =
  "flex h-8 items-center gap-2 rounded-md border bg-surface-1 px-2.5 text-sm text-fg transition-[border-color,box-shadow,background-color] duration-150 ease-out " +
  "focus-within:border-accent focus-within:bg-surface-2 focus-within:shadow-[0_0_0_3px_var(--g-accent-soft)] has-[:disabled]:opacity-45";

export interface TextFieldProps extends Omit<ComponentProps<"input">, "prefix"> {
  prefix?: ReactNode;
  suffix?: ReactNode;
  invalid?: boolean;
  wrapperClassName?: string;
}

export function TextField({ prefix, suffix, invalid, className, wrapperClassName, ...rest }: TextFieldProps) {
  return (
    <div className={cx(field, invalid ? "border-danger" : "border-border-strong hover:border-fg-disabled", wrapperClassName)}>
      {prefix && <span className="shrink-0 text-fg-subtle">{prefix}</span>}
      <input
        aria-invalid={invalid || undefined}
        spellCheck={false}
        autoComplete="off"
        className={cx("min-w-0 flex-1 bg-transparent outline-none placeholder:text-fg-subtle", className)}
        {...rest}
      />
      {suffix && <span className="shrink-0 text-xs text-fg-subtle">{suffix}</span>}
    </div>
  );
}

export interface NumberFieldProps {
  value: number;
  min: number;
  max: number;
  step?: number;
  suffix?: string;
  onCommit: (v: number) => void;
  "aria-label": string;
  disabled?: boolean;
  className?: string;
}

/** Stepper + typed input. Commits on blur/Enter/step, clamps to range, ArrowUp/Down step. */
export function NumberField({ value, min, max, step = 1, suffix, onCommit, disabled, className, ...aria }: NumberFieldProps) {
  const [text, setText] = useState(String(value));
  useEffect(() => setText(String(value)), [value]);

  const clamp = (n: number) => Math.min(max, Math.max(min, n));
  const commit = (raw: string) => {
    const n = Number(raw);
    const next = Number.isFinite(n) && raw.trim() !== "" ? clamp(Math.round(n / step) * step) : value;
    setText(String(next));
    if (next !== value) onCommit(next);
  };
  const bump = (dir: 1 | -1) => {
    const next = clamp(value + dir * step);
    setText(String(next));
    if (next !== value) onCommit(next);
  };

  return (
    <div className={cx("inline-flex h-8 items-stretch overflow-hidden rounded-md border border-border-strong bg-surface-1", "focus-within:border-accent focus-within:shadow-[0_0_0_3px_var(--g-accent-soft)]", disabled && "pointer-events-none opacity-45", className)}>
      <button
        type="button"
        aria-label={`Decrease ${aria["aria-label"]}`}
        tabIndex={-1}
        disabled={value <= min}
        onClick={() => bump(-1)}
        className="grid w-7 place-items-center text-fg-muted transition-colors duration-150 hover:bg-surface-hover hover:text-fg disabled:opacity-35"
      >
        <Minus size={13} />
      </button>
      <input
        inputMode="numeric"
        role="spinbutton"
        aria-valuemin={min}
        aria-valuemax={max}
        aria-valuenow={value}
        value={text}
        disabled={disabled}
        onChange={(e) => setText(e.target.value.replace(/[^\d]/g, ""))}
        onBlur={(e) => commit(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter") commit(e.currentTarget.value);
          else if (e.key === "ArrowUp") (e.preventDefault(), bump(1));
          else if (e.key === "ArrowDown") (e.preventDefault(), bump(-1));
        }}
        className="w-14 bg-transparent text-center text-sm tabular-nums outline-none"
        {...aria}
      />
      {suffix && <span className="flex items-center pr-1.5 text-xs text-fg-subtle">{suffix}</span>}
      <button
        type="button"
        aria-label={`Increase ${aria["aria-label"]}`}
        tabIndex={-1}
        disabled={value >= max}
        onClick={() => bump(1)}
        className="grid w-7 place-items-center text-fg-muted transition-colors duration-150 hover:bg-surface-hover hover:text-fg disabled:opacity-35"
      >
        <Plus size={13} />
      </button>
    </div>
  );
}
