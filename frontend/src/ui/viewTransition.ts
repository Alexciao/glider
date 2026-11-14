import { flushSync } from "react-dom";

/** The `view-transition-name` shared by a game's cover and the hero art of its page. */
export const COVER_TRANSITION_NAME = "game-cover";

type StartViewTransition = (update: () => void | Promise<void>) => { finished: Promise<void> };

const reducedMotion = (): boolean => typeof matchMedia === "function" && matchMedia("(prefers-reduced-motion: reduce)").matches;

/**
 * Runs a navigation inside a View Transition so the clicked cover morphs into the page's hero art (the page marks its
 * art with `view-transition-name: game-cover`; `coverTransitionStyle` is the style to spread). Without browser support
 * the update just runs. With reduced motion nothing is named, so the transition degrades to a short crossfade.
 */
export function navigateWithCover(update: () => void, cover?: HTMLElement | null): void {
  const start = (document as Document & { startViewTransition?: StartViewTransition }).startViewTransition?.bind(document);
  if (!start) {
    update();
    return;
  }
  const named = cover && !reducedMotion() ? cover : null;
  if (named) named.style.viewTransitionName = COVER_TRANSITION_NAME;
  const t = start(() => flushSync(update));
  const clear = () => {
    if (named) named.style.viewTransitionName = "";
  };
  t.finished.then(clear, clear);
}

/** Spread onto a page's hero art so it is the target of the cover's shared-element transition. */
export const coverTransitionStyle = { viewTransitionName: COVER_TRANSITION_NAME } as const;
