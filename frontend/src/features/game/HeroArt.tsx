import { useEffect, useRef, useState } from "react";
import { m } from "motion/react";
import type { GameArt } from "../../ipc/contract.gen";
import { GliderMark, spring } from "../../ui";
import { morphOrigin } from "../library/SharedCover";

/** Corner radius of the hero card (radius-2xl). Passed as a style so a shared-element transition can correct it. */
export const HERO_RADIUS = 20;

/** When the wide art starts replacing the cover if no morph reports its end: the hero lands at about 330 ms, and the 150 ms crossfade must end with the motion, not after it. */
const LAND_AFTER_MS = 320;

/** What the scrim fades the art towards: the base colour under the masked art. */
const SCRIM = "oklch(0.14 0.01 265)";

/**
 * How much each scrim takes away from the art (0 = none, 1 = only the scrim colour) at each stop. The title block sits
 * bottom-left, so the bottom and left edges are the darkest; the upper right stays the art's showcase. Tuned with
 * `e2e/hero-legibility.spec.ts`: every hero text keeps WCAG AA on flat white, busy and bright art.
 */
const BOTTOM: [number, number][] = [
  [0, 0],
  [35, 0.3],
  [62, 0.68],
  [100, 0.86],
];
const LEFT: [number, number][] = [
  [0, 0.66],
  [45, 0.42],
  [80, 0],
];

/** The two scrims as one alpha mask on the art; `--scrim` (0 to 1) fades them in. */
const stops = (pts: [number, number][]) => pts.map(([at, a]) => `rgb(0 0 0 / calc(1 - var(--scrim) * ${a})) ${at}%`).join(", ");
const SCRIM_MASK = `linear-gradient(180deg, ${stops(BOTTOM)}), linear-gradient(90deg, ${stops(LEFT)})`;

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
 * contrast against a dark, art-independent layer. The scrim is a mask on the art over a base of the scrim colour, not
 * a dark layer on top: two layers each clipped to the rounded corners let the art bleed through their antialiased
 * edges as a bright fringe; one masked layer has a single edge.
 *
 * It carries `layoutId="cover-<appName>"`: the cover you clicked morphs into this layer. A shared element has to look
 * like the thing you clicked, so the layer starts as that cover's own (already decoded) picture with no scrim: a Library
 * tile's tall cover, then the wide key art crossfades in once it has landed; a Home card's wide art, which needs no swap.
 * The scrims fade in over the last stretch of the morph.
 */
export function HeroArt({ appName, art }: { appName: string; art: GameArt | null }) {
  const wide = art?.wide ?? null;
  // Read once: the cover that was armed when this page opened. Only a tall cover needs a tall stand-in.
  const [from] = useState(morphOrigin);
  // The morph's stand-in is the picture the Library tile already decoded (its 300 px cover); with no wide art the cover IS the hero, so it gets the big one.
  const tall = (wide ? (from === "tall" ? (art?.tallThumb ?? art?.tall) : null) : (art?.tall ?? art?.tallThumb)) ?? null;
  // Wide art is the hero; a tall box is a fine stand-in (cropped); nothing at all falls back to the composition below.
  const [tallLoaded, tallInstant, tallRef, onTall] = useLoaded();
  const [wideLoaded, wideInstant, wideRef, onWide] = useLoaded();
  // The morph has settled (or there was none): only now does the wide art replace the cover.
  const [landed, setLanded] = useState(false);
  const timer = useRef<number>(0);
  useEffect(() => {
    timer.current = window.setTimeout(() => setLanded(true), LAND_AFTER_MS);
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
      className="absolute inset-0 overflow-hidden"
    >
      <div className="absolute inset-0" style={{ backgroundColor: SCRIM }} />
      {/* Everything that is art sits under one mask (the scrims); they arrive late in the morph, so the cover reads as the cover until it lands. */}
      <m.div
        className="absolute inset-0"
        style={{
          maskImage: SCRIM_MASK,
          WebkitMaskImage: SCRIM_MASK,
          maskComposite: "intersect",
          WebkitMaskComposite: "source-in",
        }}
        initial={{ "--scrim": 0 }}
        animate={{ "--scrim": 1 }}
        transition={{ duration: 0.2, delay: 0.14, ease: [0.22, 1, 0.36, 1] }}
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
