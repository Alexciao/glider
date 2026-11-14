import { useState } from "react";
import type { GameArt } from "../../ipc/contract.gen";
import { GliderMark } from "../../ui";

/**
 * Key art with a guaranteed-legible scrim. The scrim is dark in both themes on purpose: text over
 * arbitrary art only keeps AA contrast against a dark, art-independent layer. With no art (not yet
 * cached, or offline) a quiet graphite-and-glide-blue composition stands in.
 */
export function HeroArt({ art }: { art: GameArt | null }) {
  const [loaded, setLoaded] = useState(false);
  // Wide art is the hero; a tall box is a fine stand-in (cropped); nothing at all falls back to the composition below.
  const url = art?.wide ?? art?.tall ?? null;
  return (
    <div aria-hidden className="absolute inset-0 overflow-hidden bg-[oklch(0.16_0.012_265)]">
      <FallbackArt />
      {art?.placeholder && !loaded && <div className="absolute inset-0 opacity-70" style={{ backgroundColor: art.placeholder }} />}
      {url && (
        <img
          src={url}
          alt=""
          draggable={false}
          onLoad={() => setLoaded(true)}
          className="absolute inset-0 size-full object-cover object-[50%_35%] opacity-0 transition-opacity duration-700 ease-out data-[loaded]:opacity-100"
          data-loaded={loaded || undefined}
        />
      )}
      {/* Bottom-up scrim for the text block, left-in scrim for the title column. */}
      <div
        className="absolute inset-0"
        style={{
          background:
            "linear-gradient(180deg, oklch(0.14 0.01 265 / 0) 0%, oklch(0.14 0.01 265 / 0.35) 38%, oklch(0.14 0.01 265 / 0.82) 72%, oklch(0.14 0.01 265 / 0.96) 100%)",
        }}
      />
      <div
        className="absolute inset-0"
        style={{ background: "linear-gradient(90deg, oklch(0.14 0.01 265 / 0.55) 0%, oklch(0.14 0.01 265 / 0) 62%)" }}
      />
    </div>
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
      <GliderMark size={560} className="absolute -right-24 -top-16 opacity-[0.07] [filter:saturate(0)]" />
    </>
  );
}
