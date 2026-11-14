import type { ReactNode } from "react";
import { Download, Gamepad2, House, Settings as SettingsIcon, Store } from "lucide-react";
import { useLibraryCounts } from "../state/libraryIndex";
import { usePage } from "../state/router";
import { currentJob, useSnap } from "../state/store";
import { useUi } from "../state/ui";
import { Tooltip, cx } from "../ui";

function RailItem({
  page,
  label,
  shortcut,
  icon,
  badge,
}: {
  page: "home" | "library" | "downloads" | "store" | "settings";
  label: string;
  shortcut: string;
  icon: ReactNode;
  badge?: ReactNode;
}) {
  const current = usePage();
  // A game's page belongs to the Library.
  const active = current === page || (page === "library" && current === "game");
  const go = useUi((s) => s.go);
  return (
    <Tooltip content={label} shortcut={shortcut} side="right">
      <button
        type="button"
        aria-label={label}
        aria-current={active ? "page" : undefined}
        onClick={() => go(page)}
        className={cx(
          "group relative flex h-[54px] w-[60px] flex-col items-center justify-center gap-1 rounded-lg",
          "transition-[background-color,color,transform] duration-150 ease-out active:scale-95",
          active ? "bg-surface-2 text-fg" : "text-fg-subtle hover:bg-surface-hover hover:text-fg",
        )}
      >
        {/* Selection bar: scales in on the rail edge. */}
        <span
          aria-hidden
          className={cx(
            "absolute -left-[7px] top-1/2 h-4 w-[3px] -translate-y-1/2 rounded-full bg-accent transition-[transform,opacity] duration-200 ease-out",
            active ? "scale-y-100 opacity-100" : "scale-y-50 opacity-0",
          )}
        />
        <span className="relative">
          {icon}
          {badge}
        </span>
        <span className="text-2xs font-medium leading-none">{label}</span>
      </button>
    </Tooltip>
  );
}

function DownloadsBadge() {
  const status = useSnap((s) => currentJob(s.queue.jobs)?.status);
  if (!status) return null;
  return (
    <span
      aria-hidden
      className={cx(
        "absolute -right-1 -top-1 size-2 rounded-full ring-2 ring-[var(--g-bg-elevated)]",
        status === "running" ? "animate-[g-breathe_1.8s_ease-in-out_infinite] bg-accent" : "bg-warning",
      )}
    />
  );
}

/** A quiet dot on Library when installed games have updates waiting. */
function UpdatesBadge() {
  const n = useLibraryCounts().updates;
  if (n === 0) return null;
  return <span aria-hidden className="absolute -right-1 -top-1 size-2 rounded-full bg-accent ring-2 ring-[var(--g-bg-elevated)]" />;
}

export function Rail() {
  return (
    <nav aria-label="Main" className="flex w-[var(--sidebar-width)] shrink-0 flex-col items-center pb-3 pt-2">
      <div className="flex flex-col gap-1">
        <RailItem page="home" label="Home" shortcut="Ctrl+1" icon={<House size={20} strokeWidth={1.75} />} />
        <RailItem page="library" label="Library" shortcut="Ctrl+2" icon={<Gamepad2 size={20} strokeWidth={1.75} />} badge={<UpdatesBadge />} />
        <RailItem
          page="downloads"
          label="Downloads"
          shortcut="Ctrl+3"
          icon={<Download size={20} strokeWidth={1.75} />}
          badge={<DownloadsBadge />}
        />
        <RailItem page="store" label="Store" shortcut="Ctrl+4" icon={<Store size={20} strokeWidth={1.75} />} />
      </div>
      <div className="mt-auto">
        <RailItem page="settings" label="Settings" shortcut="Ctrl+5" icon={<SettingsIcon size={20} strokeWidth={1.75} />} />
      </div>
    </nav>
  );
}
