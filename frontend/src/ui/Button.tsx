import type { ComponentProps, ReactNode } from "react";
import { cx } from "./cx";
import { Spinner } from "./Spinner";
import { Tooltip } from "./Tooltip";

export type ButtonVariant = "primary" | "secondary" | "ghost" | "danger";
export type ButtonSize = "sm" | "md" | "lg" | "xl";

const base =
  "inline-flex shrink-0 select-none items-center justify-center whitespace-nowrap font-medium " +
  "transition-[background-color,color,box-shadow,transform,opacity,border-color] duration-150 ease-out " +
  "active:scale-[0.97] disabled:pointer-events-none disabled:opacity-45 aria-busy:pointer-events-none";

const variants: Record<ButtonVariant, string> = {
  primary: "bg-primary text-primary-fg shadow-sm hover:bg-primary-hover",
  secondary:
    "border border-border-strong bg-surface-2 text-fg hover:bg-surface-3 active:bg-surface-active data-[open]:bg-surface-3",
  ghost: "text-fg-muted hover:bg-surface-hover hover:text-fg active:bg-surface-active data-[open]:bg-surface-hover data-[open]:text-fg",
  danger: "bg-danger text-danger-fg shadow-sm hover:brightness-110",
};

const sizes: Record<ButtonSize, string> = {
  sm: "h-7 gap-1.5 rounded-md px-2.5 text-xs",
  md: "h-8 gap-2 rounded-md px-3 text-sm",
  lg: "h-10 gap-2 rounded-lg px-4 text-base",
  xl: "h-12 gap-2.5 rounded-xl px-6 text-md font-semibold",
};

export interface ButtonProps extends ComponentProps<"button"> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  loading?: boolean;
  icon?: ReactNode;
  iconRight?: ReactNode;
}

export function Button({
  variant = "secondary",
  size = "md",
  loading,
  icon,
  iconRight,
  className,
  children,
  disabled,
  type = "button",
  ...rest
}: ButtonProps) {
  return (
    <button
      type={type}
      disabled={disabled}
      aria-busy={loading || undefined}
      className={cx(base, variants[variant], sizes[size], className)}
      {...rest}
    >
      {loading ? <Spinner size={size === "sm" ? 12 : 14} /> : icon}
      {children}
      {iconRight}
    </button>
  );
}

const iconSizes = { sm: "size-7 rounded-md", md: "size-8 rounded-md", lg: "size-10 rounded-lg" } as const;

export interface IconButtonProps extends Omit<ComponentProps<"button">, "aria-label"> {
  /** Required: becomes the accessible name and the tooltip. */
  label: string;
  variant?: Exclude<ButtonVariant, "primary">;
  size?: keyof typeof iconSizes;
  shortcut?: string;
  tooltip?: boolean;
  tooltipSide?: "top" | "right" | "bottom" | "left";
}

export function IconButton({
  label,
  variant = "ghost",
  size = "md",
  shortcut,
  tooltip = true,
  tooltipSide,
  className,
  children,
  type = "button",
  ...rest
}: IconButtonProps) {
  const button = (
    <button
      type={type}
      aria-label={label}
      className={cx(base, variants[variant], iconSizes[size], className)}
      {...rest}
    >
      {children}
    </button>
  );
  return tooltip ? (
    <Tooltip content={label} shortcut={shortcut} side={tooltipSide}>
      {button}
    </Tooltip>
  ) : (
    button
  );
}
