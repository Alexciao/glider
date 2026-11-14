import type { ComponentProps, ReactNode } from "react";
import { AnimatePresence, m } from "motion/react";
import { ChevronDown } from "lucide-react";
import { cx } from "./cx";
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from "./DropdownMenu";
import { labelSwap, spring } from "./motion";
import { Spinner } from "./Spinner";

/** Heights and radii line up with Button: sm = md button (32 / 8), md = lg (40 / 12), lg = xl (48 / 16). */
export type SplitButtonSize = "sm" | "md" | "lg";
/** primary: the neutral high-contrast fill. quiet: a glass fill with a hairline ring, for secondary actions and work in progress. */
export type SplitButtonTone = "primary" | "quiet";

const sizes: Record<SplitButtonSize, { height: string; radius: number; menu: string; text: string; pad: string; gap: string; chevron: number }> = {
  sm: { height: "h-8", radius: 8, menu: "w-8", text: "text-sm font-medium", pad: "px-3", gap: "gap-2", chevron: 14 },
  md: { height: "h-10", radius: 12, menu: "w-10", text: "text-base font-medium", pad: "px-4", gap: "gap-2", chevron: 16 },
  lg: { height: "h-12", radius: 16, menu: "w-12", text: "text-md font-semibold", pad: "px-6", gap: "gap-2.5", chevron: 18 },
};

export interface SplitButtonProps {
  /** The primary action's visible text. */
  label: string;
  icon?: ReactNode;
  onPress: () => void;
  size?: SplitButtonSize;
  tone?: SplitButtonTone;
  /** The click was heard and the system is catching up: spinner, no further presses, still focusable. */
  busy?: boolean;
  disabled?: boolean;
  /** Menu items (`DropdownMenuItem`s). Omit for a plain button with every corner rounded. */
  menu?: ReactNode;
  /** Accessible name of the menu segment. */
  menuLabel?: string;
  menuDisabled?: boolean;
  /**
   * Identity of the current face. Label and icon crossfade only when this changes (defaults to the label), so
   * "Launching…" → "Launching…" never flickers while the state behind it moves on.
   */
  faceKey?: string;
  /** Extra props for the primary segment (e.g. hover/focus prefetch handlers, data attributes). */
  primaryProps?: Omit<ComponentProps<"button">, "children" | "onClick" | "disabled" | "type" | "className">;
  "aria-label"?: string;
  className?: string;
}

/**
 * One primary action with a menu, drawn as one object: a single rounded shape (only the outer corners are round,
 * the segments are flat), one fill, a 1 px divider, and hover / press / focus that read the same on both halves.
 * The focus ring wraps the whole group, each segment stays its own tab stop, and the focused one lifts a shade so
 * you can still tell which. Pressing either half squeezes the whole group, never half of it.
 */
export function SplitButton({
  label,
  icon,
  onPress,
  size = "md",
  tone = "primary",
  busy,
  disabled,
  menu,
  menuLabel = "More actions",
  menuDisabled,
  faceKey,
  primaryProps,
  className,
  ...rest
}: SplitButtonProps) {
  const s = sizes[size];
  const inert = busy || disabled;
  return (
    <m.div
      layout
      transition={spring.snappy}
      role="group"
      aria-label={rest["aria-label"] ?? label}
      data-tone={tone}
      data-split=""
      style={{ borderRadius: s.radius }}
      className={cx("g-split", s.height, className)}
    >
      <m.button
        layout="position"
        {...primaryProps}
        type="button"
        disabled={disabled}
        aria-busy={busy || undefined}
        onClick={busy ? undefined : onPress}
        className={cx("g-split-seg min-w-[8.5rem] justify-center", s.pad, s.text, s.gap, inert && "g-split-inert")}
      >
        <AnimatePresence mode="popLayout" initial={false}>
          <m.span
            key={faceKey ?? label}
            initial={labelSwap.initial}
            animate={labelSwap.animate}
            exit={labelSwap.exit}
            transition={{ duration: 0.18, ease: [0.23, 1, 0.32, 1] }}
            className={cx("flex items-center whitespace-nowrap", s.gap)}
          >
            {busy ? <Spinner size={size === "lg" ? 16 : 14} /> : icon}
            {label}
          </m.span>
        </AnimatePresence>
      </m.button>
      {menu && (
        <DropdownMenu>
          <m.span aria-hidden layout="position" className="g-split-divider" />
          <DropdownMenuTrigger asChild>
            <m.button layout="position" type="button" aria-label={menuLabel} disabled={menuDisabled} className={cx("g-split-seg group justify-center", s.menu)}>
              <ChevronDown size={s.chevron} className="transition-transform duration-150 ease-out group-data-[state=open]:rotate-180" />
            </m.button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" className="w-60">
            {menu}
          </DropdownMenuContent>
        </DropdownMenu>
      )}
    </m.div>
  );
}
