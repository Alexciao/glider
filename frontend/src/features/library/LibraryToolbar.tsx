import { memo, type Ref } from "react";
import { ChevronDown, Layers, LayoutGrid, List, Search } from "lucide-react";
import type { Collection } from "../../ipc/contract.gen";
import { LIBRARY_FILTERS, LIBRARY_SORTS, type LibraryCounts, type LibraryFilter, type LibrarySort } from "../../state/libraryIndex";
import type { LibraryView } from "../../state/libraryPrefs";
import { Button, DropdownMenu, DropdownMenuTrigger, SegmentedControl, Select, TextField, dropdownKit } from "../../ui";

export const FILTER_LABEL: Record<LibraryFilter, string> = {
  all: "All games",
  installed: "Installed",
  notInstalled: "Not installed",
  updates: "Updates available",
  cloudSaves: "Cloud saves",
  favorites: "Favorites",
  hidden: "Hidden",
};
export const SORT_LABEL: Record<LibrarySort, string> = {
  az: "A to Z",
  recent: "Recently played",
  added: "Recently added",
  size: "Size on disk",
};

export interface LibraryToolbarProps {
  search: string;
  onSearch: (v: string) => void;
  filter: LibraryFilter;
  onFilter: (f: LibraryFilter) => void;
  sort: LibrarySort;
  onSort: (s: LibrarySort) => void;
  view: LibraryView;
  onView: (v: LibraryView) => void;
  counts: LibraryCounts;
  /** Total games, for the search placeholder ("Search 300 games"). */
  total: number;
  collections: readonly Collection[];
  collection: Collection | undefined;
  onCollection: (id: string) => void;
  onNewCollection: () => void;
  onRenameCollection: (c: Collection) => void;
  onDeleteCollection: (c: Collection) => void;
  searchRef?: Ref<HTMLInputElement>;
}

/**
 * Search, filter, sort, view: one 32 px row. The collections menu appears only once a collection exists (the first one is
 * made from a game's menu), so most people never see a control they don't need.
 */
export function LibraryToolbar({ searchRef, search, onSearch, ...controls }: LibraryToolbarProps) {
  return (
    <div role="search" className="flex flex-wrap items-center gap-2">
      <TextField
        ref={searchRef}
        aria-label="Search your library"
        placeholder={`Search ${controls.total} ${controls.total === 1 ? "game" : "games"}`}
        prefix={<Search size={14} />}
        wrapperClassName="min-w-[200px] flex-1"
        value={search}
        onChange={(e) => onSearch(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Escape") {
            if (search) onSearch("");
            else e.currentTarget.blur();
          }
        }}
      />
      <ToolbarControls {...controls} />
    </div>
  );
}

/** Everything but the search box. Memoized: a keystroke re-renders the text field, not the selects and menus beside it. */
const ToolbarControls = memo(function ToolbarControls(p: Omit<LibraryToolbarProps, "search" | "onSearch" | "searchRef">) {
  const { Item, CheckItem, Separator } = dropdownKit;
  return (
    <>
      <Select<LibraryFilter>
        aria-label="Filter"
        minWidth={168}
        value={p.filter}
        onValueChange={p.onFilter}
        options={LIBRARY_FILTERS.map((f) => ({ value: f, label: `${FILTER_LABEL[f]} (${p.counts[f]})` }))}
      />
      {p.collections.length > 0 && (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="secondary" icon={<Layers size={14} className="text-fg-muted" />} iconRight={<ChevronDown size={14} className="text-fg-subtle" />} aria-label="Collections">
              <span className="max-w-32 truncate">{p.collection ? p.collection.name : "Collections"}</span>
            </Button>
          </DropdownMenuTrigger>
          <dropdownKit.Content align="end" className="w-60">
            <CheckItem checked={!p.collection} onCheckedChange={() => p.onCollection("all")}>
              All games
            </CheckItem>
            {p.collections.map((c) => (
              <CheckItem key={c.id} checked={p.collection?.id === c.id} onCheckedChange={() => p.onCollection(c.id)}>
                {c.name}
              </CheckItem>
            ))}
            <Separator />
            <Item onSelect={p.onNewCollection}>New collection…</Item>
            {p.collection && (
              <>
                <Item onSelect={() => p.onRenameCollection(p.collection!)}>Rename…</Item>
                <Item destructive onSelect={() => p.onDeleteCollection(p.collection!)}>
                  Delete…
                </Item>
              </>
            )}
          </dropdownKit.Content>
        </DropdownMenu>
      )}
      <Select<LibrarySort> aria-label="Sort" minWidth={160} value={p.sort} onValueChange={p.onSort} options={LIBRARY_SORTS.map((s) => ({ value: s, label: SORT_LABEL[s] }))} />
      <SegmentedControl<LibraryView>
        aria-label="View"
        value={p.view}
        onValueChange={p.onView}
        className="w-[72px]"
        options={[
          { value: "grid", ariaLabel: "Grid view", label: <LayoutGrid size={14} /> },
          { value: "list", ariaLabel: "List view", label: <List size={14} /> },
        ]}
      />
    </>
  );
});
