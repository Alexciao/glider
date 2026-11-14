import type { ComponentProps, ReactNode } from "react";
import { DropdownMenu as M } from "radix-ui";
import { Check, ChevronRight } from "lucide-react";
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

/* ---- Sub-menus and checkable items (collections, shortcuts) ---- */

const contentClass = "g-pop z-50 min-w-52 rounded-lg border border-border-strong bg-popover p-1 text-sm shadow-lg outline-none";

export const DropdownMenuSub = M.Sub;

export function DropdownMenuSubTrigger({ icon, className, children, ...rest }: Omit<ComponentProps<typeof M.SubTrigger>, "children"> & { icon?: ReactNode; children: ReactNode }) {
  return (
    <M.SubTrigger
      className={cx(
        "flex h-8 cursor-default select-none items-center gap-2.5 rounded-md px-2.5 text-fg outline-none transition-colors duration-100",
        "data-[highlighted]:bg-surface-hover data-[state=open]:bg-surface-hover data-[disabled]:opacity-45",
        className,
      )}
      {...rest}
    >
      {icon && <span className="grid size-4 shrink-0 place-items-center text-fg-muted">{icon}</span>}
      <span className="min-w-0 flex-1 truncate">{children}</span>
      <ChevronRight size={14} className="shrink-0 text-fg-subtle" aria-hidden />
    </M.SubTrigger>
  );
}

export function DropdownMenuSubContent({ className, ...rest }: ComponentProps<typeof M.SubContent>) {
  return (
    <M.Portal>
      <M.SubContent sideOffset={6} alignOffset={-4} collisionPadding={12} className={cx(contentClass, className)} {...rest} />
    </M.Portal>
  );
}

/** A menu item with a check: membership (collections) and toggles (shortcuts). Stays open on select so several can be set. */
export function DropdownMenuCheckboxItem({ className, children, ...rest }: ComponentProps<typeof M.CheckboxItem>) {
  return (
    <M.CheckboxItem
      onSelect={(e) => e.preventDefault()}
      className={cx(
        "relative flex h-8 cursor-default select-none items-center gap-2.5 rounded-md pl-8 pr-2.5 text-fg outline-none",
        "transition-colors duration-100 data-[highlighted]:bg-surface-hover data-[disabled]:opacity-45",
        className,
      )}
      {...rest}
    >
      <M.ItemIndicator className="absolute left-2.5 text-accent-text">
        <Check size={14} strokeWidth={2.25} />
      </M.ItemIndicator>
      <span className="min-w-0 flex-1 truncate">{children}</span>
    </M.CheckboxItem>
  );
}
