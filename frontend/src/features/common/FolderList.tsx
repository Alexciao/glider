import { useId } from "react";
import { RadioGroup } from "radix-ui";
import { FolderOpen, Trash2 } from "lucide-react";
import type { DriveInfoDto, LibraryFolder } from "../../ipc/contract.gen";
import { driveFor } from "../../state/folders";
import { formatBytes } from "../../state/format";
import { IconButton, UsageBar, cx, isLowOnSpace } from "../../ui";
import { PathText } from "./PathText";

export interface FolderListProps {
  folders: readonly LibraryFolder[];
  drives: readonly DriveInfoDto[];
  /** Path of the default folder. */
  defaultPath: string | null;
  onDefault: (path: string) => void;
  onRemove: (path: string) => void;
  /** Settings adds an "open in Explorer" button. */
  onOpen?: (path: string) => void;
  /** `cards`: each folder is a bordered card (onboarding). `rows`: plain rows on the page (Settings). */
  variant?: "cards" | "rows";
}

/**
 * One folder: the radio (selected = where new games go), the path, the free space, a thin drive meter. Remove and Open
 * appear on hover or focus, and Remove only exists for a folder with no games in it.
 */
function FolderRow({ folder, drive, isDefault, variant, onDefault, onRemove, onOpen }: { folder: LibraryFolder; drive: DriveInfoDto | undefined; isDefault: boolean; variant: "cards" | "rows"; onDefault: () => void; onRemove: () => void; onOpen?: () => void }) {
  const id = useId();
  const free = drive?.freeBytes ?? folder.freeBytes;
  const low = folder.exists && isLowOnSpace(free, drive?.totalBytes ?? 0);
  return (
    <div
      onClick={() => folder.exists && !isDefault && onDefault()}
      className={cx(
        "group/f flex items-center gap-3.5 transition-[background-color,border-color,box-shadow] duration-150 ease-out",
        variant === "cards"
          ? cx("rounded-xl border p-4", isDefault ? "border-accent bg-surface-2 shadow-[0_0_0_1px_var(--g-accent)]" : "border-border bg-surface-1 hover:border-border-strong hover:bg-surface-2")
          : "py-3",
      )}
    >
      <RadioGroup.Item
        value={folder.path}
        disabled={!folder.exists}
        aria-labelledby={`${id}-p`}
        aria-describedby={`${id}-f`}
        onClick={(e) => e.stopPropagation()}
        className={cx(
          "relative grid size-[18px] shrink-0 place-items-center rounded-full border transition-colors duration-150 disabled:opacity-45",
          isDefault ? "border-accent" : "border-fg-disabled hover:border-fg-subtle",
        )}
      >
        <RadioGroup.Indicator className="size-2 rounded-full bg-accent" />
      </RadioGroup.Item>

      <div className="min-w-0 flex-1">
        <div className="flex items-baseline gap-3">
          <span id={`${id}-p`} className="min-w-0 flex-1">
            <PathText path={folder.path} className="text-sm font-medium text-fg" tailSegments={2} />
          </span>
          <span id={`${id}-f`} className={cx("shrink-0 text-xs tabular-nums", !folder.exists ? "text-danger-text" : low ? "text-warning-text" : "text-fg-muted")}>
            {folder.exists ? `${formatBytes(free)} free` : "Folder missing"}
          </span>
        </div>
        {drive && folder.exists && <UsageBar className="mt-2" label={`${folder.drive || drive.root} drive usage`} usedBytes={drive.totalBytes - drive.freeBytes} totalBytes={drive.totalBytes} />}
      </div>

      <div className="flex shrink-0 items-center gap-0.5 opacity-0 transition-opacity duration-150 focus-within:opacity-100 group-hover/f:opacity-100" onClick={(e) => e.stopPropagation()}>
        {onOpen && folder.exists && (
          <IconButton label="Open in Explorer" size="sm" onClick={onOpen}>
            <FolderOpen size={14} />
          </IconButton>
        )}
        {folder.gameCount === 0 && (
          <IconButton label="Remove folder" size="sm" onClick={onRemove}>
            <Trash2 size={14} />
          </IconButton>
        )}
      </div>
    </div>
  );
}

/** The library folders as one radio group: the selected folder is where new installs go. */
export function FolderList({ folders, drives, defaultPath, onDefault, onRemove, onOpen, variant = "rows" }: FolderListProps) {
  return (
    <RadioGroup.Root value={defaultPath ?? ""} onValueChange={onDefault} aria-label="Default install folder" className={variant === "cards" ? "flex flex-col gap-2" : "flex flex-col"}>
      {folders.map((f, i) => (
        <FolderRow
          key={`${f.path}#${i}`}
          folder={f}
          drive={driveFor(f, drives)}
          isDefault={f.isDefault || (defaultPath !== null && f.path === defaultPath)}
          variant={variant}
          onDefault={() => onDefault(f.path)}
          onRemove={() => onRemove(f.path)}
          onOpen={onOpen ? () => onOpen(f.path) : undefined}
        />
      ))}
    </RadioGroup.Root>
  );
}
