import { useState } from "react";
import { Gamepad2, RefreshCw, Search } from "lucide-react";
import { actions } from "../../state/actions";
import { useLibrary } from "../../state/library";
import { LIBRARY_FILTERS, LIBRARY_SORTS, useLibraryCounts, useLibraryQuery, type LibraryFilter, type LibrarySort } from "../../state/libraryIndex";
import { useSnap } from "../../state/store";
import { usePending } from "../../state/ui";
import { Button, EmptyState, Select, TextField } from "../../ui";
import { PageHeader, PageScroll } from "../common/Page";
import { GameRow } from "./GameRow";

const FILTER_LABEL: Record<LibraryFilter, string> = {
  all: "All games",
  installed: "Installed",
  notInstalled: "Not installed",
  updates: "Updates available",
  cloudSaves: "Cloud saves",
  favorites: "Favorites",
  hidden: "Hidden",
};
const SORT_LABEL: Record<LibrarySort, string> = {
  az: "A to Z",
  recent: "Recently played",
  added: "Recently added",
  size: "Size on disk",
};

/** Rows rendered at once; "Show more" adds another page. (Temporary list: the grid will virtualize.) */
const PAGE = 100;

/** A simple, temporary list of the library: search, filter, sort and collection, all through the in-memory index. */
export function LibraryPage() {
  const [search, setSearch] = useState("");
  const [filter, setFilter] = useState<LibraryFilter>("all");
  const [sort, setSort] = useState<LibrarySort>("az");
  const [collectionId, setCollectionId] = useState("all");
  const [shown, setShown] = useState(PAGE);
  const collections = useSnap((s) => s.collections);
  const syncing = useSnap((s) => s.library.syncing);
  const lastSyncedAt = useSnap((s) => s.library.lastSyncedAt);
  const syncError = useSnap((s) => s.library.syncError);
  const loaded = useLibrary((s) => s.loaded);
  const counts = useLibraryCounts();
  const refreshing = usePending("refreshLibrary");

  const collection = collections.find((c) => c.id === collectionId);
  const results = useLibraryQuery({ search, filter, sort, collection: collection?.appNames ?? null });
  const rows = results.slice(0, shown);

  return (
    <PageScroll>
      <PageHeader
        title="Library"
        description={`${counts.all} ${counts.all === 1 ? "game" : "games"}${counts.installed ? `  ·  ${counts.installed} installed` : ""}${counts.updates ? `  ·  ${counts.updates} with updates` : ""}`}
        actions={
          <Button size="sm" variant="secondary" icon={<RefreshCw size={13} />} loading={refreshing || syncing} onClick={() => void actions.refreshLibrary()}>
            {syncing ? "Syncing…" : "Sync"}
          </Button>
        }
      />
      {syncError && <p className="mb-4 rounded-lg bg-warning-soft px-3.5 py-2.5 text-sm text-warning-text">{syncError.message} Showing your cached library{lastSyncedAt ? "" : " (nothing synced yet)"}.</p>}

      <div className="mb-4 flex flex-wrap items-center gap-2">
        <TextField
          aria-label="Search your library"
          placeholder="Search your library"
          prefix={<Search size={14} />}
          wrapperClassName="min-w-[220px] flex-1"
          value={search}
          onChange={(e) => {
            setSearch(e.target.value);
            setShown(PAGE);
          }}
        />
        <Select<LibraryFilter>
          aria-label="Filter"
          minWidth={170}
          value={filter}
          onValueChange={(v) => {
            setFilter(v);
            setShown(PAGE);
          }}
          options={LIBRARY_FILTERS.map((f) => ({ value: f, label: `${FILTER_LABEL[f]}${f === "all" ? "" : ` (${counts[f]})`}` }))}
        />
        <Select<LibrarySort> aria-label="Sort" minWidth={160} value={sort} onValueChange={setSort} options={LIBRARY_SORTS.map((s) => ({ value: s, label: SORT_LABEL[s] }))} />
        {collections.length > 0 && (
          <Select
            aria-label="Collection"
            minWidth={150}
            value={collectionId}
            onValueChange={(v) => {
              setCollectionId(v);
              setShown(PAGE);
            }}
            options={[{ value: "all", label: "All collections" }, ...collections.map((c) => ({ value: c.id, label: `${c.name} (${c.appNames.length})` }))]}
          />
        )}
      </div>

      {rows.length === 0 ? (
        <EmptyState
          icon={<Gamepad2 size={20} />}
          title={!loaded || (syncing && counts.all === 0) ? "Syncing your library…" : counts.all === 0 && !search ? "No games yet" : "Nothing matches"}
          description={counts.all === 0 && !search ? "Games you own on Epic show up here after a sync." : "Try a different search or filter."}
        />
      ) : (
        <>
          <ul aria-label="Games" className="flex flex-col">
            {rows.map((g) => (
              <GameRow key={g.appName} game={g} />
            ))}
          </ul>
          {results.length > rows.length && (
            <div className="mt-3 flex justify-center">
              <Button size="sm" variant="ghost" onClick={() => setShown((n) => n + PAGE)}>
                Show {Math.min(PAGE, results.length - rows.length)} more
              </Button>
            </div>
          )}
        </>
      )}
    </PageScroll>
  );
}
