import { cx } from "./cx";

export interface UsageBarProps {
  /** Bytes in use on the drive (total − free). */
  usedBytes: number;
  totalBytes: number;
  /** Accessible name, e.g. "D: drive usage". */
  label: string;
  /** Turns the bar amber when free space drops below this many bytes (default 5 % of the drive, at least 20 GB). */
  lowBelowBytes?: number;
  className?: string;
}

const GIB = 1024 ** 3;

export const isLowOnSpace = (freeBytes: number, totalBytes: number, lowBelowBytes?: number): boolean =>
  freeBytes < (lowBelowBytes ?? Math.max(20 * GIB, totalBytes * 0.05));

/** A thin drive meter. The fill is a scaleX transform (compositor only); amber when the drive is nearly full. */
export function UsageBar({ usedBytes, totalBytes, label, lowBelowBytes, className }: UsageBarProps) {
  const fraction = totalBytes > 0 ? Math.min(1, Math.max(0, usedBytes / totalBytes)) : 0;
  const low = totalBytes > 0 && isLowOnSpace(totalBytes - usedBytes, totalBytes, lowBelowBytes);
  return (
    <div
      role="meter"
      aria-label={label}
      aria-valuemin={0}
      aria-valuemax={100}
      aria-valuenow={Math.round(fraction * 100)}
      aria-valuetext={`${Math.round(fraction * 100)}% used`}
      className={cx("h-1.5 overflow-hidden rounded-full bg-surface-3", className)}
    >
      <div
        className={cx("h-full origin-left rounded-full transition-transform duration-300 ease-out", low ? "bg-warning" : "bg-fg-subtle")}
        style={{ transform: `scaleX(${fraction})`, width: "100%" }}
      />
    </div>
  );
}
