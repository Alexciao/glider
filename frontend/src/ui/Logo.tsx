import { cx } from "./cx";
import wordmarkDark from "../assets/glider-wordmark.svg";
import wordmarkLight from "../assets/glider-wordmark-onlight.svg";

/**
 * The Glider mark (assets/brand). Wing = paper on dark / graphite on light, flap = glide blue,
 * both via theme tokens, so a single inline SVG serves both themes. Never stroke, glow or recolour.
 */
export function GliderMark({ size = 24, className }: { size?: number; className?: string }) {
  return (
    <svg width={size} height={size} viewBox="0 0 64 64" fill="none" aria-hidden className={cx("shrink-0", className)}>
      <path
        d="M7.772 40.407L55.342 15.527Q58 14.136 55.454 16.292Q39.807 29.844 31.285 44.91Q30.398 46.519 28.442 46.104L7.956 41.749Q6 41.334 7.772 40.407Z"
        fill="var(--g-brand-wing)"
      />
      <path
        d="M35.653 45.793Q41.333 36.316 49.869 27.445Q51.061 26.224 50.44 27.54Q46.592 36.105 46.179 47.599Q46.131 49.864 44.175 49.448L36.651 47.849Q34.695 47.433 35.653 45.793Z"
        fill="var(--g-brand-flap)"
      />
    </svg>
  );
}

/** Mark + "Glider" lettering, outlined by the brand generator (never set the name in another face). */
export function GliderWordmark({ height = 28, className }: { height?: number; className?: string }) {
  const width = Math.round((height * 201) / 81);
  return (
    <span className={cx("inline-flex shrink-0", className)} style={{ width, height }} role="img" aria-label="Glider">
      <img src={wordmarkDark} alt="" width={width} height={height} className="hidden [[data-theme=dark]_&]:block" draggable={false} />
      <img src={wordmarkLight} alt="" width={width} height={height} className="hidden [[data-theme=light]_&]:block" draggable={false} />
    </span>
  );
}
