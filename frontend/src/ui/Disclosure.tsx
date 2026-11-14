import type { ReactNode } from "react";
import { Collapsible } from "radix-ui";
import { ChevronRight } from "lucide-react";
import { cx } from "./cx";

export interface DisclosureProps {
  title: string;
  description?: ReactNode;
  open?: boolean;
  defaultOpen?: boolean;
  onOpenChange?: (open: boolean) => void;
  children: ReactNode;
  className?: string;
}

/** "Advanced" and other optional depth: a quiet header row that expands in place. */
export function Disclosure({ title, description, open, defaultOpen, onOpenChange, children, className }: DisclosureProps) {
  return (
    <Collapsible.Root open={open} defaultOpen={defaultOpen} onOpenChange={onOpenChange} className={cx("group/d", className)}>
      <Collapsible.Trigger
        className={cx(
          "flex w-full items-center gap-2 rounded-lg px-1 py-1.5 text-left text-sm font-medium text-fg-muted",
          "transition-colors duration-150 hover:text-fg",
        )}
      >
        <ChevronRight size={14} className="shrink-0 transition-transform duration-200 ease-out group-data-[state=open]/d:rotate-90" />
        <span>{title}</span>
        {description && <span className="ml-1 truncate text-xs font-normal text-fg-subtle">{description}</span>}
      </Collapsible.Trigger>
      <Collapsible.Content className="g-collapse overflow-hidden">
        <div className="pt-2">{children}</div>
      </Collapsible.Content>
    </Collapsible.Root>
  );
}
