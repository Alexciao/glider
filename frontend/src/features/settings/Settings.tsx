import { Fragment, useEffect, useRef, useState, type ReactNode } from "react";
import { Search, X } from "lucide-react";
import type { Settings as SettingsT } from "../../ipc/contract.gen";
import { useSnap } from "../../state/store";
import { useUi } from "../../state/ui";
import { EmptyState, IconButton, TextField, cx } from "../../ui";
import { ErrorView } from "../common/ErrorView";
import { PageHeader, PageScroll } from "../common/Page";
import { AboutPanel } from "./AboutPanel";
import { AccountSection, AdvancedSection, AppearanceSection, LinksSection, OverlaySection, StartupSection } from "./AppSections";
import { DownloadsSection } from "./DownloadsSection";
import { EglSection } from "./EglSection";
import { CloudSavesSection, LibraryFoldersSection, UpdatesSection } from "./LibrarySections";
import { SearchProvider } from "./parts";
import { SECTION_GROUPS, SECTIONS, resolveSection, sectionById, type SectionId } from "./sections";

/**
 * Settings: twelve sections in a left nav, one shown at a time. Typing in the search box instead shows every matching
 * row from every section, grouped under its section title (rows hide themselves when they don't match; see parts.tsx).
 */

function renderSection(id: SectionId, s: SettingsT): ReactNode {
  switch (id) {
    case "downloads":
      return <DownloadsSection s={s} />;
    case "library":
      return <LibraryFoldersSection s={s} />;
    case "updates":
      return <UpdatesSection s={s} />;
    case "saves":
      return <CloudSavesSection s={s} />;
    case "egl":
      return <EglSection s={s} />;
    case "overlay":
      return <OverlaySection />;
    case "links":
      return <LinksSection s={s} />;
    case "appearance":
      return <AppearanceSection s={s} />;
    case "startup":
      return <StartupSection s={s} />;
    case "account":
      return <AccountSection />;
    case "advanced":
      return <AdvancedSection s={s} />;
    case "about":
      return <AboutPanel />;
  }
}

function SearchField({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const input = useRef<HTMLInputElement>(null);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && !e.shiftKey && !e.altKey && e.key.toLowerCase() === "f") {
        e.preventDefault();
        input.current?.focus();
        input.current?.select();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);
  return (
    <TextField
      ref={input}
      type="search"
      role="searchbox"
      aria-label="Search settings"
      placeholder="Search settings"
      value={value}
      onChange={(e) => onChange(e.target.value)}
      onKeyDown={(e) => {
        if (e.key === "Escape" && value) {
          e.stopPropagation();
          onChange("");
        }
      }}
      prefix={<Search size={14} />}
      suffix={
        value ? (
          <IconButton label="Clear search" size="sm" className="-mr-1.5 size-6" onClick={() => onChange("")}>
            <X size={13} />
          </IconButton>
        ) : null
      }
      wrapperClassName="w-full"
      className="[&::-webkit-search-cancel-button]:hidden"
    />
  );
}

export function Settings() {
  const stored = useUi((s) => s.settingsSection);
  const go = useUi((s) => s.go);
  const settings = useSnap((s) => s.settings);
  const err = useUi((s) => s.errors.settings);
  const [query, setQuery] = useState("");
  const [matches, setMatches] = useState<number | null>(null);
  const body = useRef<HTMLDivElement>(null);
  const q = query.trim();
  const current = sectionById(stored);

  // Count the rows that survived the filter once the DOM has settled; zero shows the empty state.
  useEffect(() => {
    if (!q) return;
    const id = requestAnimationFrame(() => setMatches(body.current?.querySelectorAll("[data-setting]").length ?? 0));
    return () => cancelAnimationFrame(id);
  }, [q, settings]);

  return (
    <PageScroll wide>
      <PageHeader title="Settings" />
      <div className="grid grid-cols-[200px_minmax(0,1fr)] gap-10 max-[1100px]:grid-cols-[188px_minmax(0,1fr)] max-[1100px]:gap-6">
        <div className="sticky top-0 flex h-fit flex-col gap-3">
        <SearchField value={query} onChange={setQuery} />
        <nav aria-label="Settings sections" className="flex flex-col">
          {SECTION_GROUPS.map((group, gi) => (
            <Fragment key={gi}>
              {gi > 0 && <div aria-hidden className="mx-3 my-2 h-px bg-border" />}
              <div className="flex flex-col gap-0.5">
                {group.map((s) => {
                  const active = !q && s.id === resolveSection(stored);
                  return (
                    <button
                      key={s.id}
                      type="button"
                      aria-current={active ? "page" : undefined}
                      onClick={() => {
                        setQuery("");
                        go("settings", s.id);
                      }}
                      className={cx(
                        "flex h-9 items-center gap-2.5 rounded-lg px-3 text-left text-sm font-medium transition-[background-color,color,transform] duration-150 ease-out active:scale-[0.985]",
                        active ? "bg-surface-2 text-fg" : "text-fg-muted hover:bg-surface-hover hover:text-fg",
                      )}
                    >
                      <span className={cx("transition-colors duration-150", active ? "text-accent-text" : "text-fg-subtle")}>{s.icon}</span>
                      <span className="truncate">{s.label}</span>
                    </button>
                  );
                })}
              </div>
            </Fragment>
          ))}
        </nav>
        </div>

        <div
          ref={body}
          key={q ? "search" : current.id}
          data-searching={q ? "" : undefined}
          className={cx("w-[640px] min-w-0 max-w-full", !q && "g-page-in")}
          role="region"
          aria-label={q ? "Search results" : current.label}
        >
          {err && <ErrorView className="mb-5" error={err.error} retry={err.retry} onDismiss={() => useUi.getState().setError("settings", undefined)} />}
          <SearchProvider value={q}>
            {q ? (
              <div className="flex flex-col gap-10">
                {SECTIONS.filter((s) => s.id !== "about").map((s) => (
                  <Fragment key={s.id}>{renderSection(s.id, settings)}</Fragment>
                ))}
              </div>
            ) : (
              renderSection(current.id, settings)
            )}
          </SearchProvider>
          {q && matches === 0 && (
            <EmptyState icon={<Search size={20} />} title={`No settings match “${q}”`} />
          )}
          <p className="sr-only" role="status">
            {q && matches !== null ? (matches === 0 ? "No settings found" : `${matches} ${matches === 1 ? "setting" : "settings"} found`) : ""}
          </p>
        </div>
      </div>
    </PageScroll>
  );
}
