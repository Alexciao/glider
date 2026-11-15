import { useCallback, useDeferredValue, useRef, useState } from "react";
import { RefreshCw, SearchX, TriangleAlert } from "lucide-react";
import { actions } from "../../state/actions";
import { collectionActions } from "../../state/collections";
import { useHotkey } from "../../state/hooks";
import { useLibrary } from "../../state/library";
import { useLibraryCounts, useLibraryQuery, type LibraryFilter, type LibrarySort } from "../../state/libraryIndex";
import { useLibraryPrefs, type LibraryView } from "../../state/libraryPrefs";
import { useSnap } from "../../state/store";
import { usePending } from "../../state/ui";
import type { Collection } from "../../ipc/contract.gen";
import { AlertModal, Button, EmptyState, IconButton, Spinner, cx } from "../../ui";
import { LibraryEmpty } from "../common/LibraryEmpty";
import { SignInBanner, useNeedsSignIn } from "../common/SignInBanner";
import { CollectionDialog, useCollectionDialog } from "./collectionDialog";
import { LibraryGrid } from "./LibraryGrid";
import { LibrarySkeleton } from "./LibrarySkeleton";
import { LibraryToolbar } from "./LibraryToolbar";

/** What an empty filter says: a title, and a hint only where the next step isn't obvious. */
const FILTER_EMPTY: Partial<Record<LibraryFilter, { title: string; hint?: string }>> = {
  installed: { title: "Nothing installed yet" },
  notInstalled: { title: "Everything is installed" },
  updates: { title: "Everything is up to date" },
  cloudSaves: { title: "No games with cloud saves" },
  favorites: { title: "No favorites yet", hint: "Add one from a game's menu." },
  hidden: { title: "No hidden games" },
};

const isSlash = (e: KeyboardEvent): boolean => e.key === "/" && !e.ctrlKey && !e.metaKey && !e.altKey;
const clip = (s: string, n: number): string => (s.length > n ? `${s.slice(0, n - 1)}…` : s);

/** The Library: every game you own, as covers or as a list, searchable and filterable in memory. */
export function LibraryPage() {
  const [prefs, setPrefs] = useLibraryPrefs();
  const [search, setSearch] = useState("");
  const deferredSearch = useDeferredValue(search);
  const [scrolled, setScrolled] = useState(false);
  const [deleting, setDeleting] = useState<Collection | null>(null);
  const searchRef = useRef<HTMLInputElement>(null);

  const collections = useSnap((s) => s.collections);
  const syncing = useSnap((s) => s.library.syncing);
  const syncError = useSnap((s) => s.library.syncError);
  const offline = useSnap((s) => s.connectivity === "offline");
  const loaded = useLibrary((s) => s.loaded);
  const counts = useLibraryCounts();
  const refreshing = usePending("refreshLibrary");
  const needsSignIn = useNeedsSignIn();
  const openCreate = useCollectionDialog((s) => s.openCreate);
  const openRename = useCollectionDialog((s) => s.openRename);

  const collection = collections.find((c) => c.id === prefs.collectionId);
  const results = useLibraryQuery({ search: deferredSearch, filter: prefs.filter, sort: prefs.sort, collection: collection?.appNames ?? null });

  const total = counts.all + counts.hidden;
  const query = search.trim();
  const filtered = query !== "" || prefs.filter !== "all" || collection !== undefined;
  const busySyncing = syncing || refreshing;
  const showSkeleton = !loaded || (busySyncing && total === 0);

  // "/" jumps to search from anywhere on the page.
  useHotkey(isSlash, () => searchRef.current?.focus());

  const clearFilters = () => {
    setSearch("");
    setPrefs({ filter: "all", collectionId: "all" });
  };
  const onScrolled = useCallback((v: boolean) => setScrolled(v), []);
  // Stable handlers: the toolbar's controls are memoized, so a keystroke in the search box doesn't re-render them.
  const onFilter = useCallback((filter: LibraryFilter) => setPrefs({ filter }), [setPrefs]);
  const onSort = useCallback((sort: LibrarySort) => setPrefs({ sort }), [setPrefs]);
  const onView = useCallback((view: LibraryView) => setPrefs({ view }), [setPrefs]);
  const onCollection = useCallback((collectionId: string) => setPrefs({ collectionId }), [setPrefs]);
  const onNewCollection = useCallback(() => openCreate(), [openCreate]);
  const onRenameCollection = useCallback((c: Collection) => openRename(c.id), [openRename]);

  let body;
  if (showSkeleton) {
    body = <LibrarySkeleton view={prefs.view} />;
  } else if (total === 0) {
    body = <LibraryEmpty className="h-full justify-center" syncing={busySyncing} />;
  } else if (results.length === 0) {
    const named = query === "" && !collection ? FILTER_EMPTY[prefs.filter] : undefined;
    const emptyCollection = collection !== undefined && query === "" && prefs.filter === "all";
    body = (
      <EmptyState
        className="h-full justify-center"
        icon={<SearchX size={20} />}
        title={emptyCollection ? "This collection is empty" : (named?.title ?? (query ? `No games match "${clip(query, 32)}"` : "No games match"))}
        description={emptyCollection ? "Add games from their menu." : named?.hint}
        action={
          filtered && !emptyCollection ? (
            <Button variant="secondary" onClick={clearFilters}>
              {named ? "Show all games" : "Clear search and filters"}
            </Button>
          ) : undefined
        }
      />
    );
  } else {
    body = (
      <LibraryGrid
        games={results}
        view={prefs.view}
        resetKey={`${deferredSearch}|${prefs.filter}|${prefs.sort}|${prefs.collectionId}|${prefs.view}`}
        onScrolled={onScrolled}
      />
    );
  }

  return (
    <div className="flex h-full flex-col">
      <header className={cx("shrink-0 overflow-y-hidden border-b px-10 pb-4 pt-8 [scrollbar-gutter:stable] transition-[border-color] duration-150 max-[1100px]:px-7 [@media(max-height:700px)]:pt-5", scrolled ? "border-border" : "border-transparent")}>
        <div className="mb-4 flex h-7 items-center justify-between gap-6 [@media(max-height:700px)]:mb-3">
          <div className="flex min-w-0 items-baseline gap-3">
            <h1 className="text-xl font-semibold tracking-tight text-fg">Library</h1>
            {/* One quiet status, only when there is something to say. */}
            {syncError && !offline ? (
              <button
                type="button"
                title={syncError.message}
                onClick={() => void actions.refreshLibrary()}
                className="inline-flex items-center gap-1.5 rounded-sm text-sm text-warning-text outline-none transition-opacity duration-150 hover:opacity-80 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-focus"
              >
                <TriangleAlert size={14} aria-hidden />
                Couldn&apos;t sync. Try again
              </button>
            ) : filtered && total > 0 && !showSkeleton ? (
              <span className="text-sm tabular-nums text-fg-subtle">
                {results.length} of {prefs.filter === "hidden" ? counts.hidden : counts.all}
              </span>
            ) : null}
          </div>
          {total > 0 && (
            <IconButton label={busySyncing ? "Syncing library…" : "Sync library"} onClick={() => void actions.refreshLibrary()} disabled={busySyncing || needsSignIn !== null} className={cx("-mr-2", busySyncing ? "disabled:opacity-100" : undefined)}>
              {busySyncing ? <Spinner size={14} className="text-fg-muted" /> : <RefreshCw size={14} />}
            </IconButton>
          )}
        </div>
        {total > 0 && (
          <LibraryToolbar
            search={search}
            onSearch={setSearch}
            searchRef={searchRef}
            filter={prefs.filter}
            onFilter={onFilter}
            sort={prefs.sort}
            onSort={onSort}
            view={prefs.view}
            onView={onView}
            counts={counts}
            total={counts.all || total}
            collections={collections}
            collection={collection}
            onCollection={onCollection}
            onNewCollection={onNewCollection}
            onRenameCollection={onRenameCollection}
            onDeleteCollection={setDeleting}
          />
        )}
        {total > 0 && <SignInBanner className="mt-3" />}
      </header>
      <div className="min-h-0 flex-1">{body}</div>

      <CollectionDialog />
      <AlertModal
        open={deleting !== null}
        onOpenChange={(o) => !o && setDeleting(null)}
        title={`Delete ${deleting?.name ?? "collection"}?`}
        description="The games stay in your library."
        cancel={<Button variant="ghost">Cancel</Button>}
        action={
          <Button
            variant="danger"
            onClick={() => {
              if (deleting) {
                if (prefs.collectionId === deleting.id) setPrefs({ collectionId: "all" });
                void collectionActions.remove(deleting.id);
              }
            }}
          >
            Delete
          </Button>
        }
      />
    </div>
  );
}
