import { cx } from "./cx";

export interface StatRowData {
  key: string;
  label: string;
  value: string;
  /** Full text when `value` is shortened (a host name, a path). */
  title?: string;
  /** Muted: no live value right now (paused, waiting). */
  dim?: boolean;
  tone?: "warning" | "danger";
}

export interface StatGroupData {
  id: string;
  title: string;
  rows: readonly StatRowData[];
}

const toneText = { warning: "text-warning-text", danger: "text-danger-text" } as const;

/**
 * Labelled figures in titled groups: each group is a definition list of label → value rows, so a screen reader reads
 * "Speed, Now, 48.2 MB/s" and sighted users scan four short columns. Values use tabular numerals and never wrap.
 */
export function StatGroups({ groups, columns, className }: { groups: readonly StatGroupData[]; columns?: readonly (readonly string[])[]; className?: string }) {
  // With `columns` the groups stack into the given columns (by id, in that order) so uneven groups balance; groups not named are dropped.
  if (columns) {
    const byId = new Map(groups.map((g) => [g.id, g]));
    return (
      <div className={cx("grid grid-cols-2 gap-x-8 [&_section]:min-w-0", className)}>
        {columns.map((ids, i) => (
          <div key={i} className="flex min-w-0 flex-col gap-5">
            {ids.map((id) => byId.get(id)).filter((g): g is StatGroupData => !!g).map((g) => <StatGroup key={g.id} group={g} />)}
          </div>
        ))}
      </div>
    );
  }
  return (
    <div className={cx("flex flex-wrap gap-x-14 gap-y-6", className)}>
      {groups.map((g) => (
        <StatGroup key={g.id} group={g} />
      ))}
    </div>
  );
}

function StatGroup({ group: g }: { group: StatGroupData }) {
  return (
        <section aria-label={g.title} className="min-w-44 max-w-full">
          <h3 className="mb-1.5 text-xs font-medium text-fg-subtle" aria-hidden>
            {g.title}
          </h3>
          <dl>
            {g.rows.map((r) => (
              <div key={r.key} className="flex h-6 items-baseline justify-between gap-3">
                <dt className="shrink-0 text-xs text-fg-muted">{r.label}</dt>
                <dd className={cx("min-w-0 truncate text-right text-sm tabular-nums transition-colors duration-200", r.dim ? "text-fg-subtle" : (r.tone && toneText[r.tone]) || "text-fg")} title={r.title ?? r.value}>
                  {r.value}
                </dd>
              </div>
            ))}
          </dl>
        </section>
  );
}
