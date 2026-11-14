import { useState } from "react";
import { m } from "motion/react";
import type { GameArt } from "../../ipc/contract.gen";
import { GliderMark, spring } from "../../ui";

/** Corner radius of the hero card (radius-2xl). Passed as a style so a shared-element transition can correct it. */
export const HERO_RADIUS = 20;

/**
 * The hero's key art with a scrim that is dark in both themes on purpose: text over arbitrary art only keeps AA
 * contrast against a dark, art-independent layer. The scrim is built for the layout, not the picture: the title block
 * sits bottom-left, so the bottom and left edges are the darkest; the upper right stays the art's showcase.
 *
 * It carries `layoutId="cover-<appName>"`: the Library grid cover of the same game morphs into this layer.
 */
export function HeroArt({ appName, art }: { appName: string; art: GameArt | null }) {
  const [loaded, setLoaded] = useState(false);
  // Wide art is the hero; a tall box is a fine stand-in (cropped); nothing at all falls back to the composition below.
  const url = art?.wide ?? art?.tall ?? null;
  return (
    <m.div
      aria-hidden
      layoutId={`cover-${appName}`}
      transition={spring.smooth}
      style={{ borderRadius: HERO_RADIUS }}
      className="absolute inset-0 overflow-hidden bg-[oklch(0.16_0.012_265)]"
    >
      <FallbackArt />
      {art?.placeholder && !loaded && <div className="absolute inset-0 opacity-70" style={{ backgroundColor: art.placeholder }} />}
      {url && (
        <img
          src={url}
          alt=""
          draggable={false}
          onLoad={() => setLoaded(true)}
          className="absolute inset-0 size-full object-cover object-[50%_30%] opacity-0 transition-opacity duration-700 ease-out data-[loaded]:opacity-100"
          data-loaded={loaded || undefined}
        />
      )}
      {/* Bottom-up and left-in scrims, opaque enough that flat white art still gives white text more than 4.5:1. */}
      <div
        className="absolute inset-0"
        style={{
          background:
            "linear-gradient(180deg, oklch(0.14 0.01 265 / 0.12) 0%, oklch(0.14 0.01 265 / 0.42) 34%, oklch(0.14 0.01 265 / 0.86) 66%, oklch(0.14 0.01 265 / 0.96) 100%)",
        }}
      />
      <div
        className="absolute inset-0"
        style={{ background: "linear-gradient(90deg, oklch(0.14 0.01 265 / 0.84) 0%, oklch(0.14 0.01 265 / 0.64) 46%, oklch(0.14 0.01 265 / 0) 86%)" }}
      />
      <div aria-hidden className="absolute inset-0 rounded-[inherit] shadow-[inset_0_0_0_1px_oklch(1_0_0/0.09)]" />
    </m.div>
  );
}

function FallbackArt() {
  return (
    <>
      <div
        className="absolute inset-0"
        style={{
          background: [
            "radial-gradient(70% 90% at 78% 12%, oklch(0.42 0.13 262 / 0.62), transparent 70%)",
            "radial-gradient(55% 70% at 10% 95%, oklch(0.36 0.1 300 / 0.4), transparent 70%)",
            "linear-gradient(180deg, oklch(0.24 0.045 268), oklch(0.15 0.015 265))",
          ].join(","),
        }}
      />
      <GliderMark size={420} className="absolute -right-16 -top-12 opacity-[0.07] [filter:saturate(0)]" />
    </>
  );
}
