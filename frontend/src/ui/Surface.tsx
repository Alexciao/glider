import type { ComponentProps, ReactNode } from "react";
import { cx } from "./cx";

type Tone = "flat" | "raised";

const tones: Record<Tone, string> = {
  flat: "bg-surface-1 border border-border",
  raised: "bg-surface-2 border border-border-strong shadow-sm [box-shadow:inset_0_1px_0_var(--g-highlight),var(--g-shadow-sm)]",
};

export interface SurfaceProps extends ComponentProps<"div"> {
  tone?: Tone;
  padded?: boolean;
}

/** The one container: translucent layered fill, hairline border, 12px radius. Works on Mica and solid. */
export function Surface({ tone = "flat", padded = false, className, ...rest }: SurfaceProps) {
  return <div className={cx("rounded-lg", tones[tone], padded && "p-4", className)} {...rest} />;
}

export const Card = Surface;

/** A titled group of rows, the building block of Settings. Rows are divided by hairlines. */
export function Section({
  title,
  description,
  action,
  children,
  className,
}: {
  title?: string;
  description?: ReactNode;
  action?: ReactNode;
  children: ReactNode;
  className?: string;
}) {
  return (
    <section className={cx("flex flex-col gap-2", className)}>
      {(title || action) && (
        <div className="flex items-end justify-between gap-4 px-1">
          <div className="min-w-0">
            {title && <h3 className="text-sm font-semibold text-fg">{title}</h3>}
            {description && <p className="mt-0.5 text-xs text-fg-muted">{description}</p>}
          </div>
          {action}
        </div>
      )}
      <Surface className="divide-y divide-border overflow-hidden">{children}</Surface>
    </section>
  );
}

/** A label + helper text on the left, a control on the right. */
export function Row({
  label,
  description,
  control,
  htmlFor,
  labelId,
  className,
  children,
}: {
  label: ReactNode;
  description?: ReactNode;
  control?: ReactNode;
  htmlFor?: string;
  labelId?: string;
  className?: string;
  children?: ReactNode;
}) {
  return (
    <div className={cx("px-4 py-3", className)}>
      <div className="flex min-h-8 items-center justify-between gap-6">
        <div className="min-w-0 flex-1">
          <label id={labelId} htmlFor={htmlFor} className="block text-sm font-medium text-fg">
            {label}
          </label>
          {description && <p className="mt-0.5 max-w-prose text-xs leading-[1.4] text-fg-muted">{description}</p>}
        </div>
        {control && <div className="flex shrink-0 items-center gap-2">{control}</div>}
      </div>
      {children}
    </div>
  );
}
