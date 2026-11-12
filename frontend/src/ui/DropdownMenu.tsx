import type { ComponentProps, ReactNode } from "react";
import { DropdownMenu as M } from "radix-ui";
import { cx } from "./cx";
import { Kbd } from "./Kbd";

export const DropdownMenu = M.Root;
export const DropdownMenuTrigger = M.Trigger;

export function DropdownMenuContent({ className, align = "end", sideOffset = 8, ...rest }: ComponentProps<typeof M.Content>) {
  return (
    <M.Portal>
      <M.Content
        align={align}
        sideOffset={sideOffset}
        collisionPadding={12}
        className={cx(
          "g-pop z-50 min-w-52 rounded-lg border border-border-strong bg-popover p-1 text-sm shadow-lg outline-none",
          className,
        )}
        {...rest}
      />
    </M.Portal>
  );
}

interface ItemProps extends Omit<ComponentProps<typeof M.Item>, "children"> {
  icon?: ReactNode;
  shortcut?: string;
  destructive?: boolean;
  children: ReactNode;
}

export function DropdownMenuItem({ icon, shortcut, destructive, className, children, ...rest }: ItemProps) {
  return (
    <M.Item
      className={cx(
        "flex h-8 cursor-default select-none items-center gap-2.5 rounded-md px-2.5 outline-none",
        "transition-colors duration-100 data-[disabled]:opacity-45",
        destructive
          ? "text-danger-text data-[highlighted]:bg-danger-soft"
          : "text-fg data-[highlighted]:bg-surface-hover",
        className,
      )}
      {...rest}
    >
      {icon && <span className={cx("grid size-4 shrink-0 place-items-center", destructive ? "text-danger-text" : "text-fg-muted")}>{icon}</span>}
      <span className="min-w-0 flex-1 truncate">{children}</span>
      {shortcut && <Kbd>{shortcut}</Kbd>}
    </M.Item>
  );
}

export function DropdownMenuSeparator() {
  return <M.Separator className="-mx-1 my-1 h-px bg-border" />;
}

export function DropdownMenuLabel({ children }: { children: ReactNode }) {
  return <M.Label className="px-2.5 py-1.5 text-xs text-fg-subtle">{children}</M.Label>;
}
