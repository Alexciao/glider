import type { ComponentProps } from "react";
import { Switch as S } from "radix-ui";
import { cx } from "./cx";

export function Switch({ className, ...rest }: ComponentProps<typeof S.Root>) {
  return (
    <S.Root
      className={cx(
        "peer relative inline-flex h-5 w-9 shrink-0 items-center rounded-full border border-border-strong bg-surface-3 p-0.5",
        "transition-colors duration-150 ease-out data-[state=checked]:border-transparent data-[state=checked]:bg-accent",
        "disabled:pointer-events-none disabled:opacity-45",
        className,
      )}
      {...rest}
    >
      <S.Thumb
        className={cx(
          "block size-3.5 rounded-full bg-fg-muted shadow-sm transition-[transform,background-color] duration-200 ease-out",
          "data-[state=checked]:translate-x-4 data-[state=checked]:bg-accent-fg",
        )}
      />
    </S.Root>
  );
}
