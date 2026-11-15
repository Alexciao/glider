import { useEffect, useRef, useState } from "react";
import { m } from "motion/react";
import type { GameArt } from "../../ipc/contract.gen";
import { GliderMark, spring } from "../../ui";

/** Corner radius of the hero card (radius-2xl). Passed as a style so a shared-element transition can correct it. */
export const HERO_RADIUS = 20;

/** A cached or already-decoded image is there at frame 1: only a real network load earns a fade. */
function useLoaded(): [boolean, boolean, (el: HTMLImageElement | null) => void, () => void] {
  const [loaded, setLoaded] = useState(false);
  const [instant, setInstant] = useState(false);
  const ref = (el: HTMLImageElement | null) => {
    if (el && el.complete && el.naturalWidth > 0 && !loaded) {
      setInstant(true);
      setLoaded(true);
    }
  };
  return [loaded, instant, ref, () => setLoaded(true)];
}

/**
 * The hero's key art with a scrim that is dark in both themes on purpose: text over arbitrary art only keeps AA
 * contrast against a dark, art-independent layer. The scrim is built for the layout, not the picture: the title block
 * sits bottom-left, so the bottom and left edges are the darkest; the upper right stays the art's showcase.
 *
 * It carries `layoutId="cover-<appName>"`: the Library grid cover of the same game morphs into this layer. A shared
 * element has to look like the thing you clicked, so the layer starts as the tile's own (already decoded) tall cover with
 * no scrim, the scrims fade in over the last stretch of the morph, and the wide key art crossfades in once it has landed.
 */
export function HeroArt({ appName, art }: { appName: string; art: GameArt | null }) {
  const tall = art?.tall ?? art?.tallThumb ?? null;
  const wide = art?.wide ?? null;
  // Wide art is the hero; a tall box is a fine stand-in (cropped); nothing at all falls back to the composition below.
  const [tallLoaded, tallInstant, tallRef, onTall] = useLoaded();
  const [wideLoaded, wideInstant, wideRef, onWide] = useLoaded();
  // The morph has settled (or there was none): only now does the wide art replace the cover.
  const [landed, setLanded] = useState(false);
  const timer = useRef<number>(0);
  useEffect(() => {
    timer.current = window.setTimeout(() => setLanded(true), 520);
    return () => window.clearTimeout(timer.current);
  }, []);
  // With no tall cover to start from, the wide art needs no waiting; without wide art, the tall one is the hero.
  const showWide = wideLoaded && (landed || !tall);

  return (
    <m.div
      aria-hidden
      layoutId={`cover-${appName}`}
      transition={spring.smooth}
      onLayoutAnimationComplete={() => setLanded(true)}
      style={{ borderRadius: HERO_RADIUS }}
      className="absolute inset-0 overflow-hidden bg-[oklch(0.16_0.012_265)]"
    >
      <FallbackArt />
      {art?.placeholder && !tallLoaded && !wideLoaded && <div className="absolute inset-0 opacity-70" style={{ backgroundColor: art.placeholder }} />}
      {tall && (
        <img
          ref={tallRef}
          src={tall}
          alt=""
          draggable={false}
          onLoad={onTall}
          className={`absolute inset-0 size-full object-cover object-[50%_30%] ${tallInstant ? "" : "transition-opacity duration-200 ease-out"} ${tallLoaded ? "opacity-100" : "opacity-0"}`}
        />
      )}
      {wide && (
        <img
          ref={wideRef}
          src={wide}
          alt=""
          draggable={false}
          onLoad={onWide}
          className={`absolute inset-0 size-full object-cover object-[50%_30%] ${wideInstant && !tall ? "" : "transition-opacity duration-150 ease-out"} ${showWide ? "opacity-100" : "opacity-0"}`}
        />
      )}
      {/* Bottom-up and left-in scrims, opaque enough that flat white art still gives white text more than 4.5:1. They arrive late in the morph, so the cover reads as the cover until it lands. */}
      <m.div aria-hidden className="absolute inset-0" initial={{ opacity: 0 }} animate={{ opacity: 1 }} transition={{ duration: 0.2, delay: 0.14, ease: [0.22, 1, 0.36, 1] }}>
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
      </m.div>
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
