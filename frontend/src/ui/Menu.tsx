import type { ComponentProps, ReactNode } from "react";
import { ContextMenu as ContextMenuPrimitive, DropdownMenu as DropdownMenuPrimitive } from "radix-ui";
import { Check, ChevronRight } from "lucide-react";
import { cx } from "./cx";
import { Kbd } from "./Kbd";

/**
 * One menu look for both a right-click menu and a "…" button menu. Radix's ContextMenu and DropdownMenu expose the
 * same parts, so the styled parts are built once per primitive and the same item list renders into either
 * (`GameMenuItems` takes a kit). Everything inherits the `g-pop` motion and hover language of `DropdownMenu`.
 */
type Primitive = typeof DropdownMenuPrimitive;

const itemBase =
  "flex h-8 cursor-default select-none items-center gap-2.5 rounded-md px-2.5 text-sm outline-none transition-colors duration-100 data-[disabled]:opacity-45";

export interface MenuItemProps extends Omit<ComponentProps<Primitive["Item"]>, "children"> {
  icon?: ReactNode;
  shortcut?: string;
  destructive?: boolean;
  children: ReactNode;
}

function createKit(P: Primitive) {
  function Content({ className, ...rest }: ComponentProps<Primitive["Content"]>) {
    return (
      <P.Portal>
        <P.Content
          collisionPadding={12}
          className={cx("g-pop z-50 min-w-52 rounded-lg border border-border-strong bg-popover p-1 text-sm shadow-lg outline-none", className)}
          {...rest}
        />
      </P.Portal>
    );
  }

  function Item({ icon, shortcut, destructive, className, children, ...rest }: MenuItemProps) {
    return (
      <P.Item
        className={cx(itemBase, destructive ? "text-danger-text data-[highlighted]:bg-danger-soft" : "text-fg data-[highlighted]:bg-surface-hover", className)}
        {...rest}
      >
        {icon && <span className={cx("grid size-4 shrink-0 place-items-center", destructive ? "text-danger-text" : "text-fg-muted")}>{icon}</span>}
        <span className="min-w-0 flex-1 truncate">{children}</span>
        {shortcut && <Kbd>{shortcut}</Kbd>}
      </P.Item>
    );
  }

  /** A toggle row: the check sits where an icon would, so toggles and actions line up. */
  function CheckItem({ checked, onCheckedChange, children, className, ...rest }: Omit<ComponentProps<Primitive["CheckboxItem"]>, "children"> & { children: ReactNode }) {
    return (
      <P.CheckboxItem
        checked={checked}
        onCheckedChange={onCheckedChange}
        className={cx(itemBase, "text-fg data-[highlighted]:bg-surface-hover", className)}
        {...rest}
      >
        <span className="grid size-4 shrink-0 place-items-center text-accent-text">
          <P.ItemIndicator>
            <Check size={14} strokeWidth={2.25} />
          </P.ItemIndicator>
        </span>
        <span className="min-w-0 flex-1 truncate">{children}</span>
      </P.CheckboxItem>
    );
  }

  function Separator() {
    return <P.Separator className="-mx-1 my-1 h-px bg-border" />;
  }

  function Label({ children }: { children: ReactNode }) {
    return <P.Label className="px-2.5 py-1.5 text-xs text-fg-subtle">{children}</P.Label>;
  }

  function Sub({ label, icon, children, disabled }: { label: string; icon?: ReactNode; children: ReactNode; disabled?: boolean }) {
    return (
      <P.Sub>
        <P.SubTrigger disabled={disabled} className={cx(itemBase, "text-fg data-[highlighted]:bg-surface-hover data-[state=open]:bg-surface-hover")}>
          {icon && <span className="grid size-4 shrink-0 place-items-center text-fg-muted">{icon}</span>}
          <span className="min-w-0 flex-1 truncate">{label}</span>
          <ChevronRight size={14} className="shrink-0 text-fg-subtle" />
        </P.SubTrigger>
        <P.Portal>
          <P.SubContent
            sideOffset={6}
            alignOffset={-5}
            collisionPadding={12}
            className="g-pop z-50 min-w-48 max-w-72 rounded-lg border border-border-strong bg-popover p-1 text-sm shadow-lg outline-none"
          >
            {children}
          </P.SubContent>
        </P.Portal>
      </P.Sub>
    );
  }

  return { Content, Item, CheckItem, Separator, Label, Sub };
}

export type MenuKit = ReturnType<typeof createKit>;

/** Parts for a menu opened from a button. */
export const dropdownKit: MenuKit = createKit(DropdownMenuPrimitive);
/** Parts for a menu opened by right-click or the context-menu key. */
export const contextKit: MenuKit = createKit(ContextMenuPrimitive as unknown as Primitive);

export const ContextMenu = ContextMenuPrimitive.Root;
export const ContextMenuTrigger = ContextMenuPrimitive.Trigger;
