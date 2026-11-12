import { cx } from "./cx";

/** Epic offers no avatar URL, so identity is initials on a hue derived from the account id. */
const segmenter = typeof Intl !== "undefined" && "Segmenter" in Intl ? new Intl.Segmenter(undefined, { granularity: "grapheme" }) : null;
const graphemes = (text: string): string[] => (segmenter ? Array.from(segmenter.segment(text), (x) => x.segment) : [...text]);

export function initialsOf(name: string): string {
  const parts = name
    .trim()
    .split(/[\s._-]+/)
    .filter(Boolean);
  if (parts.length === 0) return "?";
  const first = graphemes(parts[0]!)[0] ?? "?";
  const second = parts.length > 1 ? graphemes(parts[parts.length - 1]!)[0] : graphemes(parts[0]!)[1];
  return (first + (second ?? "")).toUpperCase();
}

export function hueOf(seed: string): number {
  let h = 2166136261;
  for (let i = 0; i < seed.length; i++) h = Math.imul(h ^ seed.charCodeAt(i), 16777619);
  return Math.abs(h) % 360;
}

const sizes = {
  sm: "size-6 text-2xs",
  md: "size-8 text-xs",
  lg: "size-10 text-sm",
  xl: "size-14 text-lg",
} as const;

export function Avatar({
  name,
  seed,
  src,
  size = "md",
  className,
}: {
  name: string;
  seed?: string;
  src?: string | null;
  size?: keyof typeof sizes;
  className?: string;
}) {
  const hue = hueOf(seed ?? name);
  return (
    <span
      aria-hidden
      className={cx(
        "inline-grid shrink-0 select-none place-items-center overflow-hidden rounded-full font-semibold tracking-tight",
        "ring-1 ring-inset ring-border-strong",
        sizes[size],
        className,
      )}
      style={{
        background: `light-dark(oklch(0.9 0.05 ${hue}), oklch(0.36 0.07 ${hue}))`,
        color: `light-dark(oklch(0.34 0.09 ${hue}), oklch(0.93 0.04 ${hue}))`,
      }}
    >
      {src ? <img src={src} alt="" className="size-full object-cover" /> : initialsOf(name)}
    </span>
  );
}
