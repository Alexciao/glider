import { Fragment, useEffect, useRef, useState, type ReactNode } from "react";
import { Search, X } from "lucide-react";
import type { Settings as SettingsT } from "../../ipc/contract.gen";
import { useSnap } from "../../state/store";
import { consumeSettingsFocus, useRoute } from "../../state/router";
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
            <X size={14} />
          </IconButton>
        ) : null
      }
      wrapperClassName="w-full"
      className="[&::-webkit-search-cancel-button]:hidden"
    />
  );
}

export function Settings() {
  const storedSection = useUi((s) => s.settingsSection);
  const go = useUi((s) => s.go);
  const settings = useSnap((s) => s.settings);
  const err = useUi((s) => s.errors.settings);
  const [query, setQuery] = useState("");
  const [matches, setMatches] = useState<number | null>(null);
  // Sections that still have a row after the filter; the rest of the nav steps back.
  const [hits, setHits] = useState<ReadonlySet<string> | null>(null);
  const route = useRoute();
  const focus = route.page === "settings" ? route.focus : undefined;
  const body = useRef<HTMLDivElement>(null);
  // A link to a row shows its section at once (and no search), before the state catches up below.
  const stored = focus ? "downloads" : storedSection;
  const q = focus ? "" : query.trim();
  const current = sectionById(stored);
  // The body rises in when the section changes, not on the way into Settings: the page wrapper (Shell) already plays the same
  // entrance there, and both together moved the body twice as far as the header and faded it in late.
  const bodyKey = q ? "search" : current.id;
  const [shownKey, setShownKey] = useState(bodyKey);
  const [switched, setSwitched] = useState(false);
  if (bodyKey !== shownKey) {
    setShownKey(bodyKey);
    setSwitched(true);
  }

  // Count the rows that survived the filter once the DOM has settled; zero shows the empty state.
  useEffect(() => {
    if (!q) return;
    const id = requestAnimationFrame(() => {
      setMatches(body.current?.querySelectorAll("[data-setting]").length ?? 0);
      const found = new Set<string>();
      body.current?.querySelectorAll<HTMLElement>("[data-block]").forEach((b) => {
        if (b.querySelector("[data-setting]")) found.add(b.id.replace(/^settings-/, ""));
      });
      setHits(found);
    });
    return () => cancelAnimationFrame(id);
  }, [q, settings]);

  // `settings?focus=downloadMode`: open that row's section, bring the row to the middle, focus its control, and flash it once.
  useEffect(() => {
    if (!focus) return;
    // No cleanup on purpose: consuming the request below changes `focus`, and the flash must outlive that re-render.
    const show = () => {
      const row = body.current?.querySelector<HTMLElement>(`[data-setting-id="${focus}"]`);
      if (!row) return false;
      row.scrollIntoView({ block: "center", behavior: "instant" });
      // The selected segment of a segmented control (Space on the first one would change the setting), else the first control.
      (row.querySelector<HTMLElement>('[role="radio"][aria-checked="true"]') ?? row.querySelector<HTMLElement>('button, [role="combobox"], input'))?.focus({ preventScroll: true });
      row.setAttribute("data-flash", "");
      window.setTimeout(() => row.removeAttribute("data-flash"), 1900);
      return true;
    };
    // The section mounts on the next frame (it is keyed by section); try twice before giving up.
    window.setTimeout(() => {
      setQuery("");
      useUi.setState({ settingsSection: "downloads" });
      if (!show()) window.setTimeout(show, 120);
      consumeSettingsFocus();
    }, 60);
  }, [focus]);

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
                  // While searching, a section with no matching row steps back instead of competing with the ones that matched.
                  const dim = !!q && hits !== null && !hits.has(s.id);
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
                        "flex h-9 items-center gap-2.5 rounded-lg px-3 text-left text-sm font-medium transition-[background-color,color,transform,scale] duration-150 ease-out press:scale-[0.985]",
                        active ? "bg-surface-2 text-fg" : dim ? "text-fg-subtle hover:bg-surface-hover" : "text-fg-muted hover:bg-surface-hover hover:text-fg",
                      )}
                    >
                      <span className={cx("transition-colors duration-150", active ? "text-accent-text" : dim ? "text-fg-disabled" : "text-fg-subtle")}>{s.icon}</span>
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
          key={bodyKey}
          data-searching={q ? "" : undefined}
          className={cx("w-[640px] min-w-0 max-w-full", !q && switched && "g-page-in")}
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
