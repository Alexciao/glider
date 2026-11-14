import { m } from "motion/react";
import { spring } from "../../ui";

/**
 * The shared-element handle of a cover. The game page's hero art carries `layoutId="cover-<appName>"`; rendering this
 * invisible box over the cover (only while it is hovered or focused, so there is at most one at a time) lets the hero
 * grow out of the cover's exact rectangle when the page opens. Keeping it off the 600 covers themselves is what keeps
 * mounting a row of tiles cheap. `radius` is the cover's corner radius, so the hero can interpolate it.
 */
export function SharedCover({ id, radius }: { id: string; radius: number }) {
  return <m.span aria-hidden layoutId={`cover-${id}`} transition={spring.smooth} style={{ borderRadius: radius }} className="pointer-events-none absolute inset-0" />;
}
