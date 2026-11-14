import { createContext, useContext, useId, useState, type ReactNode } from "react";
import { fuzzyMatch } from "../../state/fuzzy";
import { NumberField, SegmentedControl, Slider, Switch, cx } from "../../ui";
import { PathText } from "../common/PathText";

/**
 * The building blocks every Settings section is made of: flat rows (label, an optional one-line description, a control)
 * on the page itself, no boxes or dividers; grouping is whitespace. Rows are searchable: each carries the words it
 * should be found by, and while a search is active a row that doesn't match renders nothing (CSS then hides any group
 * or section left with no rows, via the `data-setting` / `data-group` / `data-block` attributes).
 */

const SearchContext = createContext("");
export const SearchProvider = SearchContext.Provider;
export const useSettingsQuery = (): string => useContext(SearchContext);

function useFound(...parts: (string | undefined)[]): boolean {
  const q = useSettingsQuery();
  return !q || fuzzyMatch(q, parts.filter(Boolean).join(" ")) !== null;
}

/** A row the search can find. `find` is every word it should match besides its label. */
export function Setting({ label, find, children }: { label: string; find?: string; children: ReactNode }) {
  const found = useFound(label, find);
  return found ? <div data-setting>{children}</div> : null;
}

/** A run of rows. With a title it is a small subsection (Epic section); without, just spacing. */
export function Group({ title, action, children }: { title?: string; action?: ReactNode; children: ReactNode }) {
  return (
    <section data-group className="flex flex-col">
      {(title || action) && (
        <div className="mb-1 flex items-center justify-between gap-4">
          {title && <h3 className="text-sm font-semibold text-fg">{title}</h3>}
          {action}
        </div>
      )}
      {children}
    </section>
  );
}

/** A section: its title (h2) and groups. No intro text. */
export function Block({ id, title, children }: { id: string; title: string; children: ReactNode }) {
  const headingId = useId();
  return (
    <section data-block id={`settings-${id}`} aria-labelledby={headingId} className="flex scroll-mt-4 flex-col gap-3">
      <h2 id={headingId} className="text-lg font-semibold tracking-tight text-fg">
        {title}
      </h2>
      {children}
    </section>
  );
}

/** Label on the left, control on the right, one short description line under the label when it earns it. */
export function Line({ label, description, htmlFor, control, children, className }: { label: ReactNode; description?: ReactNode; htmlFor?: string; control?: ReactNode; children?: ReactNode; className?: string }) {
  return (
    <div className={cx("py-3", className)}>
      <div className="flex min-h-8 items-center justify-between gap-6">
        <div className="min-w-0 flex-1">
          <label htmlFor={htmlFor} className="block text-sm font-medium text-fg">
            {label}
          </label>
          {description && <p className="mt-0.5 text-xs leading-[1.4] text-fg-muted">{description}</p>}
        </div>
        {control && <div className="flex shrink-0 items-center gap-2">{control}</div>}
      </div>
      {children}
    </div>
  );
}

export function SwitchRow({
  label,
  description,
  find,
  checked,
  onChange,
  disabled,
}: {
  label: string;
  description?: ReactNode;
  find?: string;
  checked: boolean;
  onChange: (v: boolean) => void;
  disabled?: boolean;
}) {
  const id = useId();
  return (
    <Setting label={label} find={find}>
      <Line label={label} description={description} htmlFor={id} control={<Switch id={id} checked={checked} onCheckedChange={onChange} disabled={disabled} />} />
    </Setting>
  );
}

/** "Auto · value" or a custom number (slider or stepper). The number mode keeps its own draft so dragging never fights the store. */
export function AutoNumberRow({
  label,
  description,
  find,
  value,
  auto,
  min,
  max,
  step = 1,
  suffix,
  slider,
  onChange,
}: {
  label: string;
  description?: ReactNode;
  find?: string;
  value: number | null;
  auto: number;
  min: number;
  max: number;
  step?: number;
  suffix?: string;
  slider?: boolean;
  onChange: (v: number | null) => void;
}) {
  const custom = value !== null;
  const [draft, setDraft] = useState(value ?? auto);
  const [seen, setSeen] = useState(value ?? auto);
  if (seen !== (value ?? auto)) {
    setSeen(value ?? auto);
    setDraft(value ?? auto);
  }
  return (
    <Setting label={label} find={find}>
      <Line
        label={label}
        description={description}
        control={
          <SegmentedControl
            aria-label={`${label} mode`}
            className="w-[300px]"
            value={custom ? "custom" : "auto"}
            onValueChange={(v) => onChange(v === "auto" ? null : (value ?? auto))}
            options={[
              { value: "auto", label: `Auto · ${auto}${suffix ? ` ${suffix}` : ""}` },
              { value: "custom", label: "Custom" },
            ]}
          />
        }
      >
        {custom && (
          <div className="mt-2 flex items-center gap-4">
            {slider ? (
              <>
                <span className="w-5 text-xs tabular-nums text-fg-muted">{min}</span>
                <Slider aria-label={label} min={min} max={max} step={step} value={draft} onValueChange={setDraft} onValueCommit={(v) => onChange(v)} className="w-full flex-1" />
                <span className="w-5 text-right text-xs tabular-nums text-fg-muted">{max}</span>
                <span className="w-8 text-right text-sm font-medium tabular-nums text-fg">{draft}</span>
              </>
            ) : (
              <NumberField aria-label={label} value={value} min={min} max={max} step={step} suffix={suffix} onCommit={(v) => onChange(v)} />
            )}
          </div>
        )}
      </Line>
    </Setting>
  );
}

/** A location: the label, the path as its one description line, and the action(s). */
export function PathRow({ label, find, path, actions, empty }: { label: string; find?: string; path: string | null; actions?: ReactNode; empty?: string }) {
  return (
    <Setting label={label} find={find}>
      <Line
        label={label}
        description={path ? <PathText path={path} tailSegments={3} /> : (empty ?? "Not set")}
        control={actions}
      />
    </Setting>
  );
}
