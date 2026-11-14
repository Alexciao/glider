import { useEffect, useId, useMemo, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { Dialog } from "radix-ui";
import { CornerDownLeft, Search } from "lucide-react";
import { useGame } from "../state/library";
import { useLibraryIndex } from "../state/libraryIndex";
import { getRecents, pushRecent } from "../state/recents";
import { useRoute } from "../state/router";
import { useApp } from "../state/store";
import { useUi } from "../state/ui";
import { ArtThumb, Kbd, cx } from "../ui";
import { GROUP_LABEL, buildCommands, buildSections, gameItem, type ItemAction, type PaletteContext, type PaletteItem, type Ranked } from "./palette/model";

/**
 * Ctrl+K from anywhere. Search every game and every command, run the top result with Enter, or a game's other
 * actions with a chord. Instant by design: it opens many times a day, so there is no enter or exit animation.
 */

/** The label with the matched letters emphasised and the rest quiet. */
function Highlight({ text, ranges }: { text: string; ranges: [number, number][] }) {
  if (ranges.length === 0) return <>{text}</>;
  const out: ReactNode[] = [];
  let at = 0;
  ranges.forEach(([a, b], i) => {
    if (a > at) out.push(<span key={`m${i}`} className="text-fg-muted">{text.slice(at, a)}</span>);
    out.push(<span key={`h${i}`} className="font-semibold text-fg">{text.slice(a, b)}</span>);
    at = b;
  });
  if (at < text.length) out.push(<span key="tail" className="text-fg-muted">{text.slice(at)}</span>);
  return <>{out}</>;
}

/** The one action hint on a row. On the active row it is the button Enter presses. */
function Chip({ action, active, onRun }: { action: ItemAction; active: boolean; onRun: (a: ItemAction) => void }) {
  return (
    <span
      aria-hidden
      onClick={(e) => {
        e.stopPropagation();
        onRun(action);
      }}
      className={cx(
        "inline-flex h-6 shrink-0 items-center gap-1.5 rounded-md px-2 text-xs font-medium transition-colors duration-100",
        active ? "bg-primary text-primary-fg" : "text-fg-subtle",
      )}
    >
      {action.label}
      {active && <CornerDownLeft size={12} className="opacity-70" />}
    </span>
  );
}

function Row({ r, index, active, id, onRun, onHover }: { r: Ranked; index: number; active: boolean; id: string; onRun: (item: PaletteItem, a?: ItemAction) => void; onHover: (i: number) => void }) {
  const { item, match } = r;
  const primary = item.actions[0];
  const disabled = !!item.disabled;
  return (
    <div
      id={id}
      role="option"
      data-index={index}
      aria-selected={active}
      aria-disabled={disabled || undefined}
      aria-label={`${item.label}${item.hint ? `, ${item.hint}` : ""}${disabled ? `. ${item.disabled}` : item.art && primary ? `. Enter: ${primary.label}` : ""}`}
      onPointerMove={() => onHover(index)}
      onClick={() => onRun(item)}
      className={cx("flex h-10 items-center gap-3 rounded-lg px-3 text-sm", active && "bg-surface-hover", disabled && "opacity-50")}
    >
      {item.art ? (
        <ArtThumb src={item.art.src} title={item.art.title} placeholder={item.art.placeholder} size="xs" />
      ) : (
        <span aria-hidden className="grid size-6 shrink-0 place-items-center text-fg-muted">
          {item.icon}
        </span>
      )}
      <span className="min-w-0 flex-1 truncate text-fg">
        <Highlight text={item.label} ranges={match.ranges} />
      </span>
      {disabled ? (
        <span className="shrink-0 text-xs text-fg-subtle">{item.disabled}</span>
      ) : item.art && primary ? (
        <Chip action={primary} active={active} onRun={(a) => onRun(item, a)} />
      ) : item.hint ? (
        <span className="shrink-0 text-xs text-fg-subtle">{item.hint}</span>
      ) : item.shortcut ? (
        <Kbd>{item.shortcut}</Kbd>
      ) : null}
    </div>
  );
}

function usePaletteContext(): PaletteContext | null {
  const snap = useApp((s) => s.snapshot);
  const route = useRoute();
  const currentGame = useGame(route.page === "game" ? route.appName : undefined);
  const go = useUi((s) => s.go);
  const openDialog = useUi((s) => s.openDialog);
  const themePref = snap?.settings.theme;
  const resolved: "dark" | "light" = themePref === "light" ? "light" : themePref === "dark" ? "dark" : typeof document !== "undefined" && document.documentElement.dataset.theme === "light" ? "light" : "dark";
  return useMemo(() => (snap ? { snap, currentGame, theme: resolved, go, openDialog } : null), [snap, currentGame, resolved, go, openDialog]);
}

function PaletteBody({ close }: { close: () => void }) {
  const ctx = usePaletteContext();
  const index = useLibraryIndex();
  const [query, setQuery] = useState("");
  const [cursor, setCursor] = useState(0);
  const [recentIds] = useState(getRecents);
  const list = useRef<HTMLDivElement>(null);
  const uid = useId();

  const commands = useMemo(() => (ctx ? buildCommands(ctx) : []), [ctx]);
  const games = useMemo<PaletteItem[]>(() => {
    if (!ctx || !query.trim()) return [];
    // The index does the word/prefix search instantly; the fuzzy pass in `buildSections` then orders and highlights.
    return index.query({ search: query }).slice(0, 24).map((g) => gameItem(g, ctx));
  }, [ctx, index, query]);

  const recents = useMemo<PaletteItem[]>(() => {
    if (!ctx) return [];
    const byId = new Map(commands.map((c) => [c.id, c] as const));
    const out: PaletteItem[] = [];
    for (const id of recentIds) {
      if (id.startsWith("game:")) {
        const g = index.get(id.slice(5));
        if (g) out.push(gameItem(g, ctx, "recent"));
      } else {
        const c = byId.get(id);
        if (c) out.push({ ...c, group: "recent" });
      }
    }
    return out.slice(0, 5);
  }, [ctx, commands, index, recentIds]);

  const suggested = useMemo<PaletteItem[]>(() => {
    if (!ctx) return [];
    const out: PaletteItem[] = [];
    const take = (id: string) => commands.find((c) => c.id === id && !c.disabled);
    const primary = take("cmd:game-primary");
    if (primary) out.push({ ...primary, group: "suggested" });
    if (ctx.snap.queue.paused) {
      const r = take("cmd:resume-all");
      if (r) out.push({ ...r, group: "suggested" });
    }
    if (ctx.snap.library.updatesAvailable > 0) {
      const u = take("cmd:update-all");
      if (u) out.push({ ...u, group: "suggested" });
    }
    for (const g of index.recentlyPlayed(3)) if (out.length < 5) out.push(gameItem(g, ctx, "suggested"));
    return out.slice(0, 5);
  }, [ctx, commands, index]);

  const sections = useMemo(() => buildSections(query, games, commands, recents, suggested), [query, games, commands, recents, suggested]);
  const flat = useMemo(() => sections.flatMap((s) => s.rows), [sections]);
  const active = flat[Math.min(cursor, flat.length - 1)];

  useEffect(() => {
    const el = list.current;
    if (!el) return;
    if (cursor === 0) el.scrollTop = 0;
    else el.querySelector<HTMLElement>(`[data-index="${cursor}"]`)?.scrollIntoView({ block: "nearest" });
  }, [cursor]);

  const run = (item: PaletteItem | undefined, action?: ItemAction) => {
    if (!item || item.disabled) return;
    const a = action ?? item.actions[0];
    if (!a) return;
    pushRecent(item.id);
    close();
    a.run();
  };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    const last = flat.length - 1;
    switch (e.key) {
      case "ArrowDown":
        e.preventDefault();
        setCursor((i) => (i >= last ? 0 : i + 1));
        break;
      case "ArrowUp":
        e.preventDefault();
        setCursor((i) => (i <= 0 ? last : i - 1));
        break;
      case "PageDown":
        e.preventDefault();
        setCursor((i) => Math.min(last, i + 6));
        break;
      case "PageUp":
        e.preventDefault();
        setCursor((i) => Math.max(0, i - 6));
        break;
      case "Home":
        if (e.ctrlKey) {
          e.preventDefault();
          setCursor(0);
        }
        break;
      case "End":
        if (e.ctrlKey) {
          e.preventDefault();
          setCursor(last);
        }
        break;
      case "Enter": {
        e.preventDefault();
        if (!active) return;
        const keys = e.ctrlKey || e.metaKey ? "Ctrl+Enter" : e.shiftKey ? "Shift+Enter" : "Enter";
        run(active.item, active.item.actions.find((a) => a.keys === keys));
        break;
      }
    }
  };

  const optionId = (item: PaletteItem) => `${uid}-${item.id}`;
  let n = -1;

  return (
    <>
      <div className="flex items-center gap-3 border-b border-border px-4">
        <Search size={16} aria-hidden className="shrink-0 text-fg-subtle" />
        <input
          autoFocus
          role="combobox"
          aria-expanded={flat.length > 0}
          aria-controls={`${uid}-list`}
          aria-autocomplete="list"
          aria-label="Search games and commands"
          aria-activedescendant={active ? optionId(active.item) : undefined}
          value={query}
          onChange={(e) => {
            setQuery(e.target.value);
            setCursor(0);
          }}
          onKeyDown={onKeyDown}
          placeholder="Search games, settings and actions…"
          spellCheck={false}
          autoComplete="off"
          className="h-12 min-w-0 flex-1 bg-transparent text-base text-fg outline-none placeholder:text-fg-subtle"
        />
        <Kbd>Esc</Kbd>
      </div>

      {flat.length === 0 && <p className="px-3 py-8 text-center text-sm text-fg-muted">No results for “{query.trim()}”</p>}
      <div ref={list} id={`${uid}-list`} role="listbox" aria-label="Results" hidden={flat.length === 0} className="max-h-[min(420px,calc(100vh-220px))] overflow-y-auto p-1.5">
        {sections.map((s) => {
          const gid = `${uid}-g-${s.group}`;
          return (
            <div key={s.group} role="group" aria-labelledby={gid} className="mt-1 first:mt-0">
              <p id={gid} className="px-3 pb-1 pt-2 text-xs font-medium text-fg-subtle">
                {GROUP_LABEL[s.group]}
              </p>
              {s.rows.map((r) => {
                n++;
                return <Row key={r.item.id + s.group} r={r} index={n} active={n === cursor} id={optionId(r.item)} onRun={run} onHover={(i) => i !== cursor && setCursor(i)} />;
              })}
            </div>
          );
        })}
      </div>

      <p className="sr-only" role="status" aria-live="polite">
        {query.trim() ? `${flat.length} ${flat.length === 1 ? "result" : "results"}` : ""}
      </p>
    </>
  );
}

export function CommandPalette() {
  const open = useUi((s) => s.palette);
  const setOpen = useUi((s) => s.setPalette);
  return (
    <Dialog.Root open={open} onOpenChange={setOpen}>
      <Dialog.Portal>
        {/* No enter/exit animation: this opens hundreds of times a day, speed is the feature. */}
        <Dialog.Overlay className="fixed inset-0 z-40 bg-overlay" />
        <Dialog.Content
          aria-label="Command palette"
          aria-describedby={undefined}
          className="fixed left-1/2 top-[12vh] z-50 w-[600px] max-w-[calc(100vw-32px)] -translate-x-1/2 overflow-hidden rounded-xl border border-border-strong bg-popover shadow-lg outline-none"
        >
          <Dialog.Title className="sr-only">Command palette</Dialog.Title>
          <PaletteBody close={() => setOpen(false)} />
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
