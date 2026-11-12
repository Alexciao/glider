import type { ReactNode } from "react";
import { LazyMotion, MotionConfig } from "motion/react";

/** Features (layout projection, gestures) load after first paint so they stay out of the critical bundle. */
const loadFeatures = () => import("./motionFeatures").then((m) => m.default);

export function MotionProvider({ children }: { children: ReactNode }) {
  return (
    <MotionConfig reducedMotion="user">
      <LazyMotion features={loadFeatures} strict>
        {children}
      </LazyMotion>
    </MotionConfig>
  );
}
