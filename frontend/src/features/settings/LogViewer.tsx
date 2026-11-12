import { useCallback, useEffect, useRef, useState } from "react";
import { Copy, FolderOpen, RefreshCw } from "lucide-react";
import { rpc } from "../../ipc/rpc";
import type { LogsResult } from "../../ipc/contract.gen";
import { actions, toGliderError } from "../../state/actions";
import { useSnap } from "../../state/store";
import { useUi } from "../../state/ui";
import { Button, ErrorCallout, IconButton, Skeleton, Surface, cx } from "../../ui";

const MAX_LINES = 400;

function lineTone(line: string): string {
  if (/\b(ERR|ERROR|FATAL)\b/.test(line)) return "text-danger-text";
  if (/\b(WARN|WARNING)\b/.test(line)) return "text-warning-text";
  if (/\b(DBG|DEBUG)\b/.test(line)) return "text-fg-subtle";
  return "text-fg-muted";
}

/** Read-only log tail with copy-to-clipboard for bug reports. Scrolls to the newest line. */
export function LogViewer() {
  const [logs, setLogs] = useState<LogsResult | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const logDir = useSnap((s) => s.app.logDirectory);
  const level = useSnap((s) => s.settings.logLevel);
  const focus = useUi((s) => s.logsFocus);
  const root = useRef<HTMLDivElement>(null);
  const scroller = useRef<HTMLPreElement>(null);

  // Bumping `nonce` (refresh, or a log-level change) requests a fresh tail.
  const [nonce, setNonce] = useState(0);
  const load = useCallback(() => {
    setLoading(true);
    setError(null);
    setNonce((n) => n + 1);
  }, []);

  useEffect(() => {
    let alive = true;
    rpc()
      .call("app.getLogs", { maxLines: MAX_LINES })
      .then((r) => alive && setLogs(r))
      .catch((e) => alive && setError(toGliderError(e).message))
      .finally(() => alive && setLoading(false));
    return () => {
      alive = false;
    };
  }, [nonce, level]);

  useEffect(() => {
    if (scroller.current) scroller.current.scrollTop = scroller.current.scrollHeight;
  }, [logs]);

  useEffect(() => {
    if (focus > 0) root.current?.scrollIntoView({ block: "center" });
  }, [focus]);

  const lines = logs?.text ? logs.text.split("\n") : [];
  return (
    <div ref={root} className="flex flex-col gap-2">
      <div className="flex items-end justify-between gap-4 px-1">
        <div>
          <h2 className="text-sm font-semibold text-fg">Logs</h2>
          <p className="mt-0.5 text-xs text-fg-muted">Copy these into a bug report. They never include your password.</p>
        </div>
        <div className="flex items-center gap-1">
          <IconButton label="Refresh logs" size="sm" onClick={() => load()}>
            <RefreshCw size={14} className={cx(loading && "g-spinner")} />
          </IconButton>
          <IconButton label="Open log folder" size="sm" onClick={() => void actions.openPath(logDir)}>
            <FolderOpen size={14} />
          </IconButton>
          <Button size="sm" variant="secondary" icon={<Copy size={13} />} onClick={() => void actions.copyLogs()}>
            Copy logs
          </Button>
        </div>
      </div>
      {error ? (
        <ErrorCallout title="Couldn't read the logs" message={error} fixLabel="Try again" onFix={() => load()} />
      ) : (
        <Surface className="overflow-hidden">
          {loading && !logs ? (
            <div className="flex flex-col gap-2.5 p-4" aria-label="Loading logs">
              {[92, 64, 80, 48, 72, 58].map((w, i) => (
                <Skeleton key={i} className="h-3" style={{ width: `${w}%` }} />
              ))}
            </div>
          ) : (
            <pre
              ref={scroller}
              tabIndex={0}
              aria-label="Log output"
              className="selectable h-72 overflow-auto whitespace-pre px-4 py-3 font-mono text-2xs leading-[1.65] focus-visible:outline-offset-[-2px]"
            >
              {lines.length === 0 ? <span className="text-fg-subtle">No log lines yet.</span> : lines.map((l, i) => <div key={i} className={lineTone(l)}>{l || " "}</div>)}
            </pre>
          )}
          {logs && (
            <div className="border-t border-border px-4 py-2 text-2xs text-fg-subtle">
              <span className="selectable">{logs.logFile}</span>
            </div>
          )}
        </Surface>
      )}
    </div>
  );
}
