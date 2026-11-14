import { useState } from "react";
import { cx } from "./cx";
import { hueOf } from "./Avatar";

const sizes = {
  /** 24×32: the command palette. */
  xs: "h-8 w-6 rounded-[4px]",
  /** 32×44: dense lists. */
  sm: "h-11 w-8 rounded-[5px]",
  /** 40×56: list rows (import list). */
  md: "h-14 w-10 rounded-md",
  /** 48×68 */
  lg: "h-[68px] w-12 rounded-md",
} as const;

const initialOf = (title: string): string => {
  const t = title.trim();
  return t ? ([...t][0] ?? "?").toUpperCase() : "?";
};

export interface ArtThumbProps {
  /** Portrait cover URL (tall art). */
  src: string | null | undefined;
  /** Used for the fallback tile only; the thumb itself is decorative. */
  title: string;
  placeholder?: string | null;
  size?: keyof typeof sizes;
  className?: string;
}

/**
 * A small portrait cover. Decorative (the title is always next to it), lazy and async-decoded, and it degrades to a
 * quiet monogram tile on a hue derived from the title when there is no art or the image fails.
 */
export function ArtThumb({ src, title, placeholder, size = "md", className }: ArtThumbProps) {
  const [failed, setFailed] = useState<string | null>(null);
  const showImage = !!src && failed !== src;
  const hue = hueOf(title);
  return (
    <span
      aria-hidden
      className={cx("relative grid shrink-0 place-items-center overflow-hidden ring-1 ring-inset ring-border-strong", sizes[size], className)}
      style={{ background: showImage ? (placeholder ?? "var(--g-surface-3)") : `linear-gradient(160deg, oklch(0.4 0.07 ${hue}), oklch(0.27 0.05 ${(hue + 40) % 360}))` }}
    >
      {showImage ? (
        <img src={src} alt="" loading="lazy" decoding="async" draggable={false} onError={() => setFailed(src)} className="size-full object-cover" />
      ) : (
        <span className="text-sm font-semibold text-white/80">{initialOf(title)}</span>
      )}
    </span>
  );
}
