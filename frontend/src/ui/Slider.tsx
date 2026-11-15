import { Slider as S } from "radix-ui";
import { cx } from "./cx";

export interface SliderProps {
  value: number;
  min: number;
  max: number;
  step?: number;
  onValueChange?: (v: number) => void;
  /** Fires on release / keyboard step: the moment to persist. */
  onValueCommit?: (v: number) => void;
  "aria-label": string;
  disabled?: boolean;
  className?: string;
}

export function Slider({ value, min, max, step = 1, onValueChange, onValueCommit, disabled, className, ...aria }: SliderProps) {
  return (
    <S.Root
      value={[value]}
      min={min}
      max={max}
      step={step}
      disabled={disabled}
      onValueChange={([v]) => v != null && onValueChange?.(v)}
      onValueCommit={([v]) => v != null && onValueCommit?.(v)}
      className={cx("relative flex h-5 w-44 touch-none select-none items-center data-[disabled]:opacity-45", className)}
      {...aria}
    >
      <S.Track className="relative h-1 grow overflow-hidden rounded-full bg-surface-3">
        <S.Range className="absolute h-full rounded-full bg-accent" />
      </S.Track>
      <S.Thumb
        aria-label={aria["aria-label"]}
        className="block size-4 rounded-full border border-border-strong bg-fg shadow-md transition-[transform,scale] duration-150 ease-out hover:scale-110 press:scale-95"
      />
    </S.Root>
  );
}
