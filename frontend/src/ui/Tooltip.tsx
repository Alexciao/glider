import type { ReactElement, ReactNode } from "react";
import { Tooltip as T } from "radix-ui";
import { Kbd } from "./Kbd";

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
  if (disabled || content == null || content === "") return children;
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
          side={side}
          align={align}
          sideOffset={8}
          collisionPadding={8}
          className="g-pop z-50 flex max-w-64 items-center gap-2 rounded-md border border-border-strong bg-popover px-2 py-1 text-xs text-fg shadow-md"
        >
          <span>{content}</span>
          {shortcut && <Kbd>{shortcut}</Kbd>}
        </T.Content>
      </T.Portal>
    </T.Root>
  );
}
