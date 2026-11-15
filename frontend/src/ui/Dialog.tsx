import type { ReactNode } from "react";
import { AlertDialog as AD, Dialog as D } from "radix-ui";
import { cx } from "./cx";

const sizes = { sm: "w-[400px]", md: "w-[480px]", lg: "w-[560px]" } as const;

const overlay = "g-overlay fixed inset-0 z-40 bg-overlay backdrop-blur-[2px]";
const content = (size: keyof typeof sizes, anchor: "center" | "top" = "center") =>
  cx(
    "g-dialog fixed z-50 m-auto flex h-fit max-w-[calc(100vw-32px)] flex-col overflow-hidden",
    // A dialog whose content arrives after it opens hangs from a fixed top edge (where the palette sits) instead of re-centring as it grows.
    anchor === "top" ? "inset-x-0 top-[12vh] max-h-[calc(88vh-24px)]" : "inset-0 max-h-[calc(100vh-48px)]",
    "rounded-xl border border-border-strong bg-popover shadow-lg outline-none",
    sizes[size],
  );

interface ModalProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: string;
  description?: ReactNode;
  size?: keyof typeof sizes;
  children?: ReactNode;
  /** Right-aligned action row. */
  footer?: ReactNode;
  /** Prevent Escape / outside-click dismissal. */
  modal?: boolean;
  /** `top` pins the top edge at 12 vh (dialogs that size themselves from data that arrives later); the default centres. */
  anchor?: "center" | "top";
}

function Header({ title, description, Title, Description }: { title: string; description?: ReactNode; Title: typeof D.Title; Description: typeof D.Description }) {
  return (
    <div className="px-5 pb-1 pt-5">
      <Title className="text-md font-semibold tracking-tight text-fg">{title}</Title>
      {description && <Description className="mt-1.5 text-sm leading-[1.45] text-fg-muted">{description}</Description>}
    </div>
  );
}

/** A dialog. No corner ×: every dialog carries its own Cancel or Close (as Windows content dialogs do), plus Escape and the scrim. */
export function Modal({ open, onOpenChange, title, description, size = "md", children, footer, anchor }: ModalProps) {
  return (
    <D.Root open={open} onOpenChange={onOpenChange}>
      <D.Portal>
        <D.Overlay className={overlay} />
        <D.Content
          className={content(size, anchor)}
          onOpenAutoFocus={(e) => {
            e.preventDefault();
            (e.currentTarget as HTMLElement).focus();
          }}
          {...(description ? {} : { "aria-describedby": undefined })}
        >
          <Header title={title} description={description} Title={D.Title} Description={D.Description} />
          {children && <div className="min-h-0 flex-1 overflow-y-auto px-5 py-3">{children}</div>}
          {footer && <div className="flex items-center justify-end gap-2 px-5 pb-5 pt-3">{footer}</div>}
        </D.Content>
      </D.Portal>
    </D.Root>
  );
}

/** Destructive / blocking confirmations. Like Modal it has no corner ×: its Cancel is the way out. Focus starts on the safe action, role is alertdialog. */
export function AlertModal({
  open,
  onOpenChange,
  title,
  description,
  size = "sm",
  children,
  cancel,
  action,
}: Omit<ModalProps, "footer"> & { cancel: ReactNode; action: ReactNode }) {
  return (
    <AD.Root open={open} onOpenChange={onOpenChange}>
      <AD.Portal>
        <AD.Overlay className={overlay} />
        <AD.Content className={content(size)}>
          <div className="px-5 pb-1 pt-5">
            <AD.Title className="text-md font-semibold tracking-tight text-fg">{title}</AD.Title>
            {description && <AD.Description className="mt-1.5 text-sm leading-[1.45] text-fg-muted">{description}</AD.Description>}
          </div>
          {children && <div className="min-h-0 flex-1 overflow-y-auto px-5 py-3">{children}</div>}
          <div className="flex items-center justify-end gap-2 px-5 pb-5 pt-3">
            <AD.Cancel asChild>{cancel}</AD.Cancel>
            <AD.Action asChild>{action}</AD.Action>
          </div>
        </AD.Content>
      </AD.Portal>
    </AD.Root>
  );
}
