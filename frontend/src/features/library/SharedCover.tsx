import { useEffect } from "react";
import { m } from "motion/react";
import { spring, type CoverShape } from "../../ui";

/** The shape of the cover that is armed for a morph right now (null: none). The hero reads it once, when it mounts. */
let armedShape: CoverShape | null = null;

/** Which picture the game page's hero should start its morph from: the one the clicked cover shows. */
export const morphOrigin = (): CoverShape | null => armedShape;

/**
 * The shared-element handle of a cover. The game page's hero art carries `layoutId="cover-<appName>"`; rendering this
 * invisible box over the cover (only while it is hovered or focused, so there is at most one at a time) lets the hero
 * grow out of the cover's exact rectangle when the page opens. Keeping it off the 600 covers themselves is what keeps
 * mounting a row of tiles cheap. `radius` is the cover's corner radius, so the hero can interpolate it; `shape` is the
 * picture the cover shows (a Library tile: the tall box art; a Home card: the wide key art), so the hero starts as it.
 */
export function SharedCover({ id, radius, shape }: { id: string; radius: number; shape: CoverShape }) {
  useEffect(() => {
    armedShape = shape;
    return () => {
      armedShape = null;
    };
  }, [shape]);
  return <m.span aria-hidden layoutId={`cover-${id}`} transition={spring.smooth} style={{ borderRadius: radius }} className="pointer-events-none absolute inset-0" />;
}
