import { useMemo } from "react";
import { Check, ChevronDown, FolderSearch, HardDrive } from "lucide-react";
import { driveOf, formatBytes } from "../../../state/format";
import { useSnap } from "../../../state/store";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger, cx } from "../../../ui";
import { PathText } from "../../common/PathText";

export interface FolderOption {
  path: string;
  /** "Games (D:)" and the kind of drive; null while unknown. */
  caption: string | null;
  /** Free bytes on the drive; null until Core has answered for a folder it did not list. */
  freeBytes: number | null;
  isDefault: boolean;
  available: boolean;
}

const same = (a: string, b: string) => a.replace(/[\\/]+$/, "").toLowerCase() === b.replace(/[\\/]+$/, "").toLowerCase();

/**
 * Library folders with the room each one has, plus whatever folder the user picked by hand. `extra` is that folder
 * (with the free space Core reported for it, once known); `exclude` drops a folder (a Move cannot go where it is).
 */
export function useFolderOptions(extra: { path: string; freeBytes: number | null } | null, exclude?: string | null): FolderOption[] {
  const folders = useSnap((s) => s.storage.folders);
  const drives = useSnap((s) => s.storage.drives);
  return useMemo(() => {
    const caption = (path: string): string | null => {
      const drive = driveOf(path);
      const info = drive ? drives.find((d) => d.root.toUpperCase().startsWith(drive)) : undefined;
      if (!drive) return null;
      return [info?.label ? `${info.label} (${drive})` : drive, info ? (info.isSsd ? "SSD" : "Hard drive") : null].filter(Boolean).join(" · ");
    };
    const listed: FolderOption[] = folders
      .filter((f) => !exclude || !same(f.path, exclude))
      .map((f) => ({ path: f.path, caption: caption(f.path), freeBytes: f.freeBytes, isDefault: f.isDefault, available: f.exists }));
    if (extra && !listed.some((f) => same(f.path, extra.path)) && !(exclude && same(extra.path, exclude))) {
      listed.unshift({ path: extra.path, caption: caption(extra.path), freeBytes: extra.freeBytes, isDefault: false, available: true });
    }
    return listed;
  }, [folders, drives, extra, exclude]);
}

/**
 * Where it goes, as one row: the chosen folder and the room it has, opening a short menu of the other library folders
 * and "Choose another folder…". A folder that cannot hold `neededBytes` says so in the menu before it is chosen.
 */
export function FolderChooser({
  options,
  value,
  neededBytes,
  onChange,
  onChooseOther,
  disabled,
  label,
}: {
  options: readonly FolderOption[];
  value: string | null;
  neededBytes: number | null;
  onChange: (path: string) => void;
  onChooseOther: () => void;
  disabled?: boolean;
  label: string;
}) {
  const current = options.find((o) => !!value && same(o.path, value));
  const room = (o: FolderOption) =>
    !o.available ? "Not available" : o.freeBytes == null ? "" : neededBytes != null && o.freeBytes < neededBytes ? "Not enough space" : `${formatBytes(o.freeBytes)} free`;
  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          aria-label={label}
          disabled={disabled}
          className="group flex h-10 w-full items-center gap-3 rounded-lg border border-border-strong bg-surface-2 px-3 text-left text-sm transition-colors duration-150 hover:bg-surface-3 data-[state=open]:bg-surface-3 disabled:opacity-45"
        >
          <HardDrive size={16} className="shrink-0 text-fg-muted" aria-hidden />
          <span className="min-w-0 flex-1">{value ? <PathText path={current?.path ?? value} className="font-medium text-fg" tailSegments={1} /> : <span className="text-fg-subtle">Choose a folder</span>}</span>
          {current && <span className="shrink-0 text-xs text-fg-muted tabular-nums">{room(current)}</span>}
          <ChevronDown size={14} className="shrink-0 text-fg-subtle transition-transform duration-150 group-data-[state=open]:rotate-180" aria-hidden />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="w-[var(--radix-dropdown-menu-trigger-width)]">
        {options.map((o) => {
          const short = neededBytes != null && o.freeBytes != null && o.freeBytes < neededBytes;
          return (
            <DropdownMenuItem key={o.path} disabled={!o.available} onSelect={() => onChange(o.path)} className="h-auto min-h-8 py-1.5">
              <span className="flex w-full items-center gap-3">
                <span className="grid size-4 shrink-0 place-items-center text-accent-text">{!!value && same(o.path, value) && <Check size={14} strokeWidth={2.25} />}</span>
                <span className="min-w-0 flex-1">
                  <span className="block truncate font-medium">{o.path}</span>
                  {o.caption && <span className="block truncate text-xs text-fg-muted">{o.caption}</span>}
                </span>
                <span className={cx("shrink-0 text-xs tabular-nums", short ? "text-danger-text" : "text-fg-muted")}>{room(o)}</span>
              </span>
            </DropdownMenuItem>
          );
        })}
        {options.length > 0 && <DropdownMenuSeparator />}
        <DropdownMenuItem icon={<FolderSearch size={14} />} onSelect={onChooseOther}>
          Choose another folder…
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/**
 * What it takes against what the drive has. The verdict is written out (never colour alone); "doesn't fit" is the one
 * state that blocks the parent's primary button, and the parent reads it from the same `fits`.
 */
export function SpaceMeter({
  stats,
  neededBytes,
  freeBytes,
  fits,
  loading,
  verdict,
}: {
  stats: { label: string; value: string | null }[];
  neededBytes: number | null;
  freeBytes: number | null;
  fits: boolean;
  loading: boolean;
  verdict: string;
}) {
  const known = neededBytes != null && freeBytes != null;
  const fill = known && freeBytes > 0 ? Math.min(1, neededBytes / freeBytes) : 0;
  return (
    <div>
      <dl className="flex flex-wrap gap-x-10 gap-y-3">
        {stats.map((st) => (
          <div key={st.label} className="min-w-0">
            <dt className="text-xs text-fg-muted">{st.label}</dt>
            <dd className={cx("mt-0.5 text-md font-semibold tabular-nums transition-opacity duration-150", loading && "opacity-50")}>
              {st.value ?? <span className="g-skeleton mt-1 inline-block h-5 w-16 align-middle" aria-hidden />}
            </dd>
          </div>
        ))}
      </dl>
      <div
        role="img"
        aria-label={known ? `${formatBytes(neededBytes)} of ${formatBytes(freeBytes)} free` : "Checking free space"}
        className="mt-3 h-1.5 overflow-hidden rounded-full bg-surface-3"
      >
        <div
          className={cx("h-full w-full origin-left rounded-full transition-[transform,background-color] duration-300 ease-out", fits ? "bg-accent" : "bg-danger")}
          style={{ transform: `scaleX(${fill})` }}
        />
      </div>
      <p className={cx("mt-2.5 flex items-center gap-1.5 text-xs", fits ? "text-fg-muted" : "text-danger-text")} aria-live="polite">
        {verdict}
      </p>
    </div>
  );
}
