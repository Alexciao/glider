import type { ReactElement, ReactNode } from "react";
import { Tooltip as T } from "radix-ui";
import { Kbd } from "./Kbd";
import { useNativeViewOnScreen } from "./nativeView";

/** Mount once. The first tooltip waits; neighbours then open instantly (skipDelayDuration). */
export function TooltipProvider({ children }: { children: ReactNode }) {
  return (
    <T.Provider delayDuration={450} skipDelayDuration={300}>
      {children}
    </T.Provider>
  );
}

export interface TooltipProps {
  content: ReactNode;
  /** Keyboard hint rendered as a Kbd after the label, e.g. "Ctrl+1". */
  shortcut?: string;
  side?: "top" | "right" | "bottom" | "left";
  align?: "start" | "center" | "end";
  disabled?: boolean;
  children: ReactElement;
}

export function Tooltip({ content, shortcut, side = "top", align = "center", disabled, children }: TooltipProps) {
  // While the Store's native view is up, a tooltip reaching into it would be drawn under it: sideways ones (the rail's) open
  // below their trigger instead, staying in its column, and none flips across into the view to dodge the window edge.
  const nativeView = useNativeViewOnScreen();
  if (disabled || content == null || content === "") return children;
  const placedSide = nativeView && (side === "right" || side === "left") ? "bottom" : side;
  return (
    <T.Root>
      {/* Opens on keyboard focus only. Radix opens on any focus not preceded by a pointerdown, and WebView2 re-focuses the last
          focused control whenever Glider gets focus back (Alt+Tab, the native Store view): a clicked rail item then showed its
          tooltip with nothing hovered, and on the Store tab that tooltip covered the page. */}
      <T.Trigger asChild onFocus={(e) => !e.currentTarget.matches(":focus-visible") && e.preventDefault()}>
        {children}
      </T.Trigger>
      <T.Portal>
        <T.Content
          side={placedSide}
          align={nativeView && placedSide !== side ? "center" : align}
          sideOffset={nativeView && placedSide !== side ? 4 : 8}
          collisionPadding={nativeView && placedSide !== side ? 2 : 8} /* the rail is barely wider than its tooltips */
          avoidCollisions={!nativeView || placedSide !== side}
          className="g-pop z-50 flex max-w-64 items-center gap-2 rounded-md border border-border-strong bg-popover px-2 py-1 text-xs text-fg shadow-md"
        >
          <span>{content}</span>
          {shortcut && <Kbd>{shortcut}</Kbd>}
        </T.Content>
      </T.Portal>
    </T.Root>
  );
}
