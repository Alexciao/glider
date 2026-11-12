import type { CSSProperties } from "react";
import { cx } from "./cx";

export function Skeleton({ className, style }: { className?: string; style?: CSSProperties }) {
  return <div aria-hidden className={cx("g-skeleton rounded-md", className)} style={style} />;
}
