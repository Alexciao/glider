import { useLayoutEffect, useRef, useState, type CSSProperties, type ReactNode } from "react";
import type { GameArt } from "../ipc/contract.gen";
import { hueOf, initialsOf } from "./Avatar";
import { cx } from "./cx";

/** Initials for the no-art fallback. Plain ASCII titles (nearly all) skip the grapheme segmenter, which is slow per tile. */
function monogram(title: string): string {
  if (!/^[\x20-\x7e]*$/.test(title)) return initialsOf(title);
  const words = title.split(/[\s._-]+/).filter(Boolean);
  if (words.length === 0) return "?";
  const first = words[0]!;
  return (first[0]! + (words.length > 1 ? words[words.length - 1]![0]! : (first[1] ?? ""))).toUpperCase();
}

export type CoverShape = "tall" | "wide";

export interface GameCoverProps {
  art: GameArt;
  /** Used for the no-art fallback (a monogram) only; the image itself is decorative. */
  title: string;
  /** `tall` is the 3:4 box art, `wide` the 16:9 key art. */
  shape?: CoverShape;
  /** `thumb` skips the blur-up and uses the small image: for list rows and other tiny covers. */
  size?: "full" | "thumb";
  /** Eager-load (above the fold). Everything else is lazy. */
  priority?: boolean;
  className?: string;
  style?: CSSProperties;
  /** Overlays (badges, progress) rendered above the art. */
  children?: ReactNode;
}

interface Sources {
  src: string | null;
  /** Tiny image shown blurred until `src` has loaded. */
  blur: string | null;
}

export function coverSources(art: GameArt, shape: CoverShape, size: "full" | "thumb"): Sources {
  if (shape === "wide") return { src: art.wide ?? art.tall ?? art.tallThumb, blur: null };
  if (size === "thumb") return { src: art.tallThumb ?? art.tall, blur: null };
  const src = art.tall ?? art.tallThumb;
  return { src, blur: art.tallThumb && art.tallThumb !== src ? art.tallThumb : null };
}

/**
 * Cover art with no layout shift: the box has its aspect ratio (or the parent's explicit size) from the first frame,
 * a flat placeholder colour fills it, a tiny thumbnail shows blurred while the real image loads and then the image
 * fades over it. An image that is already decoded (a recycled virtual tile, a cached cover) appears instantly with
 * no fade. With no art at all (or a broken URL) the cover is a quiet gradient with the title's initials.
 */
export function GameCover({ art, title, shape = "tall", size = "full", priority, className, style, children }: GameCoverProps) {
  const { src, blur } = coverSources(art, shape, size);
  const [loadedSrc, setLoadedSrc] = useState<string | null>(null);
  const [failedSrc, setFailedSrc] = useState<string | null>(null);
  const [instant, setInstant] = useState(false);
  const [settled, setSettled] = useState(false);
  const img = useRef<HTMLImageElement>(null);

  const loaded = src !== null && loadedSrc === src;
  const failed = src === null || failedSrc === src;

  // A cached or already-decoded image never fires `load` after React attaches: catch it before paint so it doesn't fade.
  useLayoutEffect(() => {
    const el = img.current;
    if (el && el.complete && el.naturalWidth > 0) {
      setInstant(true);
      setSettled(true);
      setLoadedSrc(src);
    }
  }, [src]);

  const hue = hueOf(title);
  const base = art.placeholder ?? `oklch(0.3 0.045 ${hue})`;
  return (
    <div
      data-cover=""
      data-loaded={loaded || undefined}
      className={cx("relative isolate overflow-hidden bg-surface-3", shape === "tall" ? "aspect-[3/4]" : "aspect-video", className)}
      style={{ backgroundColor: base, ...style }}
    >
      {failed ? (
        <div
          aria-hidden
          className="absolute inset-0 grid place-items-center"
          style={{ background: `linear-gradient(160deg, oklch(0.42 0.08 ${hue}), oklch(0.24 0.05 ${(hue + 40) % 360}))` }}
        >
          <span className={cx("select-none font-semibold tracking-tight text-white/55", size === "thumb" ? "text-sm" : shape === "tall" ? "text-3xl" : "text-2xl")}>
            {monogram(title)}
          </span>
        </div>
      ) : (
        <>
          {blur && !settled && (
            <img aria-hidden alt="" src={blur} draggable={false} className="absolute inset-0 size-full scale-110 object-cover blur-[14px]" />
          )}
          <img
            ref={img}
            key={src}
            alt=""
            src={src}
            draggable={false}
            loading={priority ? "eager" : "lazy"}
            decoding="async"
            onLoad={() => setLoadedSrc(src)}
            onError={() => setFailedSrc(src)}
            onTransitionEnd={() => setSettled(true)}
            className={cx(
              "absolute inset-0 size-full object-cover",
              shape === "wide" && !art.wide && "object-top",
              instant ? "" : "transition-opacity duration-[240ms] ease-out",
              loaded ? "opacity-100" : "opacity-0",
            )}
          />
        </>
      )}
      {children}
    </div>
  );
}
