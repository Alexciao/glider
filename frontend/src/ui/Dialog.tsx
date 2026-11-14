import type { ReactNode } from "react";
import { AlertDialog as AD, Dialog as D } from "radix-ui";
import { X } from "lucide-react";
import { cx } from "./cx";
import { IconButton } from "./Button";

const sizes = { sm: "w-[400px]", md: "w-[480px]", lg: "w-[560px]" } as const;

const overlay = "g-overlay fixed inset-0 z-40 bg-overlay backdrop-blur-[2px]";
const content = (size: keyof typeof sizes) =>
  cx(
    "g-dialog fixed inset-0 z-50 m-auto flex h-fit max-h-[calc(100vh-48px)] max-w-[calc(100vw-32px)] flex-col overflow-hidden",
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
  /** Hide the corner close button (e.g. while a request is in flight). */
  hideClose?: boolean;
  /** Prevent Escape / outside-click dismissal. */
  modal?: boolean;
}

function Header({ title, description, Title, Description }: { title: string; description?: ReactNode; Title: typeof D.Title; Description: typeof D.Description }) {
  return (
    <div className="px-5 pb-1 pt-5 pr-12">
      <Title className="text-md font-semibold tracking-tight text-fg">{title}</Title>
      {description && <Description className="mt-1.5 text-sm leading-[1.45] text-fg-muted">{description}</Description>}
    </div>
  );
}

export function Modal({ open, onOpenChange, title, description, size = "md", children, footer, hideClose }: ModalProps) {
  return (
    <D.Root open={open} onOpenChange={onOpenChange}>
      <D.Portal>
        <D.Overlay className={overlay} />
        <D.Content
          className={content(size)}
          onOpenAutoFocus={(e) => {
            e.preventDefault();
            (e.currentTarget as HTMLElement).focus();
          }}
          {...(description ? {} : { "aria-describedby": undefined })}
        >
          <Header title={title} description={description} Title={D.Title} Description={D.Description} />
          {!hideClose && (
            <D.Close asChild>
              <IconButton label="Close" size="sm" className="absolute right-3 top-3">
                <X size={16} />
              </IconButton>
            </D.Close>
          )}
          {children && <div className="min-h-0 flex-1 overflow-y-auto px-5 py-3">{children}</div>}
          {footer && <div className="flex items-center justify-end gap-2 px-5 pb-5 pt-3">{footer}</div>}
        </D.Content>
      </D.Portal>
    </D.Root>
  );
}

/** Destructive / blocking confirmations. Focus starts on the safe action, role is alertdialog. */
export function AlertModal({
  open,
  onOpenChange,
  title,
  description,
  size = "sm",
  children,
  cancel,
  action,
}: Omit<ModalProps, "footer" | "hideClose"> & { cancel: ReactNode; action: ReactNode }) {
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
