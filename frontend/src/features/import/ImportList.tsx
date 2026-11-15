import { memo, useEffect, useId } from "react";
import { FolderSearch, Wrench } from "lucide-react";
import type { EglInstall } from "../../ipc/contract.gen";
import { actions, pendingKey } from "../../state/actions";
import { driveOf, formatBytes, prettyVersion } from "../../state/format";
import { useGame } from "../../state/library";
import { useJobProgress } from "../../state/progress";
import { usePending, useUi } from "../../state/ui";
import { ArtThumb, Badge, Button, Checkbox, EmptyState, Skeleton, Spinner, cx } from "../../ui";
import { ErrorView } from "../common/ErrorView";
import { useImportBatch } from "./batch";
import { batchHeadline, batchNote, emptyBody, resultPhrase, unavailableLabel } from "./model";
import type { ImportRowModel, ImportSession } from "./useImportSession";

/**
 * The importable-installs list: the scanning placeholder, the empty result, the pick-what-to-import list, and the
 * per-game results of a running batch. Used by onboarding's "Find games" step and by Settings' "Import games" dialog.
 * Every row is one line; the parent owns the headline and the footer buttons.
 */

/** "2.1.2 · D: · 12.9 GB": whatever is known, nothing else. */
function meta(i: EglInstall): string {
  return [i.version ? prettyVersion(i.version) : null, driveOf(i.installPath) || i.drive.replace(/[\\/]+$/, ""), i.sizeBytes ? formatBytes(i.sizeBytes) : null].filter(Boolean).join(" · ");
}

const Meta = ({ text }: { text: string }) => <span className="shrink-0 text-xs tabular-nums text-fg-muted">{text}</span>;

/* ------------------------------------------------------------------ list rows */

const ReadyRow = memo(function ReadyRow({ install, checked, onToggle }: { install: EglInstall; checked: boolean; onToggle: (id: string) => void }) {
  const id = useId();
  return (
    <li>
      <label title={install.installPath} className="flex h-14 items-center gap-3 rounded-lg px-3 transition-colors duration-150 hover:bg-surface-hover has-[:focus-visible]:bg-surface-hover">
        <Checkbox checked={checked} onCheckedChange={() => onToggle(install.id)} aria-labelledby={id} />
        <ArtThumb src={install.artUrl} title={install.title} size="sm" />
        <span id={id} className="min-w-0 flex-1 truncate text-sm font-medium text-fg">
          {install.title}
        </span>
        <Meta text={meta(install)} />
      </label>
    </li>
  );
});

/** Imported or unavailable: not selectable, so no checkbox, one short label, the backend's reason as a tooltip. */
const StaticRow = memo(function StaticRow({ install }: { install: EglInstall }) {
  const imported = install.status === "imported";
  return (
    <li>
      <div title={install.reason ?? install.installPath} className={cx("flex h-14 items-center gap-3 rounded-lg px-3", !imported && "opacity-80")}>
        <span className="size-4 shrink-0" aria-hidden />
        <ArtThumb src={install.artUrl} title={install.title} size="sm" className={imported ? undefined : "opacity-60"} />
        <span className={cx("min-w-0 flex-1 truncate text-sm font-medium", imported ? "text-fg" : "text-fg-muted")}>{install.title}</span>
        <Badge tone={install.status === "busy" || install.status === "stale" ? "warning" : "neutral"}>{unavailableLabel(install.status)}</Badge>
      </div>
    </li>
  );
});

/* ------------------------------------------------------------------ batch rows */

/** "Needs repair · 12 files" with its one action: the check Core queued ended with damage left. The repair queues in the background; the row says so once it has. */
function RepairResult({ appName, title, phrase }: { appName: string; title: string; phrase: string }) {
  const game = useGame(appName);
  const starting = usePending(pendingKey("verify", appName));
  const underway = game?.activeJobId != null && game.activeJobKind !== "import";
  return (
    <span className="flex shrink-0 items-center gap-2">
      <span className="flex items-center gap-1.5 text-xs text-warning-text">
        <Wrench size={14} aria-hidden />
        {phrase}
      </span>
      {underway ? (
        <span className="text-xs text-fg-muted">Repairing…</span>
      ) : (
        <Button size="sm" variant="secondary" loading={starting} aria-label={`Repair ${title}`} onClick={() => void actions.verify(appName)}>
          Repair
        </Button>
      )}
    </span>
  );
}

function Result({ row, onRetry }: { row: ImportRowModel; onRetry: () => void }) {
  const { state } = row.result;
  const progress = useJobProgress(row.job?.id, row.job?.progress);
  const phrase = resultPhrase(row.result);
  switch (state) {
    case "running":
      return <span className="shrink-0 text-xs tabular-nums text-fg-muted">{Math.round(Math.max(0, Math.min(100, progress?.percent ?? 0)))}%</span>;
    case "checking":
      // Core is verifying what it adopted (and repairs on its own): working is accent, with the one spinner.
      return (
        <span className="flex shrink-0 items-center gap-1.5 text-xs text-accent-text">
          <Spinner size={12} />
          {phrase}
        </span>
      );
    case "imported":
      return <span className="shrink-0 text-xs text-fg-muted">{phrase}</span>;
    case "needsRepair":
      return <RepairResult appName={row.install.appName} title={row.install.title} phrase={phrase} />;
    case "failed":
      return (
        <span className="flex shrink-0 items-center gap-1">
          <span className="text-xs text-danger-text">{phrase}</span>
          <Button size="sm" variant="ghost" onClick={onRetry} aria-label={`Try ${row.install.title} again`}>
            Try again
          </Button>
        </span>
      );
    default:
      return <span className="shrink-0 text-xs text-fg-subtle">{phrase}</span>;
  }
}

const BatchRow = memo(function BatchRow({ row, onRetry }: { row: ImportRowModel; onRetry: (id: string) => void }) {
  const { install, result } = row;
  return (
    <li className="g-step-in flex h-14 items-center gap-3 px-3" title={result.message ?? install.installPath}>
      <ArtThumb src={install.artUrl} title={install.title} size="sm" />
      <span className="min-w-0 flex-1 truncate text-sm font-medium text-fg">{install.title}</span>
      <Result row={row} onRetry={() => onRetry(install.id)} />
    </li>
  );
});

/* ------------------------------------------------------------------ states */

function Scanning() {
  return (
    <div aria-busy="true">
      <p className="sr-only" role="status">
        Looking for installed games
      </p>
      <div className="flex flex-col">
        {[0, 1, 2, 3].map((i) => (
          <div key={i} className="flex h-14 items-center gap-3 px-3" style={{ opacity: 1 - i * 0.2 }}>
            <Skeleton className="size-4 rounded-xs" />
            <Skeleton className="h-11 w-8 rounded-[5px]" />
            <Skeleton className="h-3.5 w-44 rounded-sm" />
            <Skeleton className="ml-auto h-3 w-24 rounded-sm" />
          </div>
        ))}
      </div>
    </div>
  );
}

/** A one-line summary for surfaces without their own headline (the Settings dialog). */
export function BatchLine({ session }: { session: ImportSession }) {
  const s = session.summary;
  return (
    <p className="mb-2 flex items-center gap-2 px-3 text-sm font-medium text-fg" role="status">
      {!s.settled && <Spinner size={14} />}
      {batchHeadline(s)}
      {batchNote(s) && <span className="font-normal text-fg-muted">{batchNote(s)}</span>}
    </p>
  );
}

/* ------------------------------------------------------------------ the list */

export function ImportList({
  session,
  surface = "onboarding",
  launcherInstalled,
  toolbarClassName,
  withSummary,
  untitled,
}: {
  session: ImportSession;
  surface?: "onboarding" | "settings";
  launcherInstalled: boolean;
  toolbarClassName?: string;
  withSummary?: boolean;
  /** The page already carries the "No games found" heading and its sentence: show the action only. */
  untitled?: boolean;
}) {
  const error = useUi((s) => s.errors[surface]);
  useEffect(() => useImportBatch.getState().attach(), []);
  const { phase, groups } = session;
  const toggleLabel = session.allSelected === true ? "Deselect all" : "Select all";
  const total = groups.ready.length;
  const rest = [...groups.imported, ...groups.unavailable];

  return (
    <div>
      {error && <ErrorView className="mb-4" error={error.error} retry={error.retry} onDismiss={() => useUi.getState().setError(surface, undefined)} />}

      {phase === "scanning" && <Scanning />}
      {phase === "empty" && untitled && (
        <Button variant="secondary" loading={session.scanning} onClick={() => void session.scanDrives()}>
          Scan all drives
        </Button>
      )}
      {phase === "empty" && !untitled && (
        <EmptyState
          icon={<FolderSearch size={20} />}
          title="No games found on this PC"
          description={emptyBody(launcherInstalled)}
          action={
            <Button variant="secondary" loading={session.scanning} onClick={() => void session.scanDrives()}>
              Scan all drives
            </Button>
          }
        />
      )}

      {phase === "list" && (
        <>
          {total > 0 && (
            <div className={cx("flex h-10 items-center px-3", toolbarClassName)}>
              <label className="flex items-center gap-3 text-sm font-medium text-fg">
                <Checkbox checked={session.allSelected} onCheckedChange={(on) => (on ? session.selectAll() : session.selectNone())} aria-label={toggleLabel} />
                {toggleLabel}
              </label>
            </div>
          )}
          <section aria-label="Ready to import">
            <ul>
              {groups.ready.map((i) => (
                <ReadyRow key={i.id} install={i} checked={session.selectedIds.has(i.id)} onToggle={session.toggle} />
              ))}
            </ul>
          </section>
          {rest.length > 0 && (
            <section aria-label="Can't import" className={total > 0 ? "mt-4" : undefined}>
              <ul>
                {rest.map((i) => (
                  <StaticRow key={i.id} install={i} />
                ))}
              </ul>
            </section>
          )}
        </>
      )}

      {(phase === "importing" || phase === "done") && (
        <>
          {withSummary && <BatchLine session={session} />}
          <ul aria-label="Import results">
            {session.rows.map((r) => (
              <BatchRow key={r.install.id} row={r} onRetry={(id) => void session.retry(id)} />
            ))}
          </ul>
        </>
      )}
    </div>
  );
}
