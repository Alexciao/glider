import type { Transition } from "motion/react";

/**
 * Motion language for Glider. One rule of thumb: critically damped springs for anything the user
 * touches (no overshoot, ~150–300 ms feel), a hair of bounce only for the rare celebratory moment.
 * Only transform/opacity are animated; reduced motion is handled globally (MotionConfig + CSS).
 */

/** Cubic-bezier curves matching --ease-* in tokens.css. */
export const ease = {
  out: [0.23, 1, 0.32, 1],
  inOut: [0.77, 0, 0.175, 1],
  drawer: [0.32, 0.72, 0, 1],
} as const;

/** Seconds, matching --duration-* in tokens.css. */
export const duration = {
  instant: 0.09,
  fast: 0.15,
  base: 0.2,
  slow: 0.28,
} as const;

export const spring = {
  /** Morphs and reflows the user triggers: critically damped, settles in ~280 ms. */
  snappy: { type: "spring", duration: 0.28, bounce: 0 },
  /** Larger surfaces (page-level content, panels). */
  smooth: { type: "spring", duration: 0.42, bounce: 0 },
  /** Rare, celebratory moments only. */
  lively: { type: "spring", duration: 0.5, bounce: 0.18 },
} as const satisfies Record<string, Transition>;

export const tween = {
  fast: { duration: duration.fast, ease: ease.out },
  base: { duration: duration.base, ease: ease.out },
  slow: { duration: duration.slow, ease: ease.out },
} as const satisfies Record<string, Transition>;

/** Label crossfade used by morphing controls: tiny vertical travel + blur bridges the two states. */
export const labelSwap = {
  initial: { opacity: 0, y: 6, filter: "blur(3px)" },
  animate: { opacity: 1, y: 0, filter: "blur(0px)" },
  exit: { opacity: 0, y: -6, filter: "blur(3px)" },
} as const;
