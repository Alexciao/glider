import type { FixAction, GliderError } from "../../ipc/contract.gen";
import { actions, runFix } from "../../state/actions";
import { FIX_LABELS, usePending } from "../../state/ui";
import { Button, ErrorCallout } from "../../ui";

const PENDING_KEY: Partial<Record<FixAction, string>> = { signIn: "signIn", repair: "verify" };

/** A GliderError rendered with its one-click fix wired up. */
export function ErrorView({
  error,
  retry,
  path,
  onDismiss,
  className,
}: {
  error: GliderError;
  retry?: () => void;
  path?: string | null;
  onDismiss?: () => void;
  className?: string;
}) {
  const fix = error.fix;
  const loading = usePending(fix ? (PENDING_KEY[fix] ?? "_") : "_");
  const waiting = error.code === "eglBusy";
  // A closed sign-in window is not silent: Epic may have shown its own error that the user never reported.
  const cancelled = error.code === "authCancelled";
  return (
    <ErrorCallout
      className={className}
      tone={waiting || cancelled ? "warning" : "error"}
      title={error.title}
      message={error.message}
      detail={error.detail}
      fixLabel={fix ? FIX_LABELS[fix] : undefined}
      fixLoading={loading}
      onFix={fix ? () => runFix(fix, { retry, path }) : undefined}
      onDismiss={onDismiss}
      secondary={
        cancelled ? (
          <Button size="sm" variant="ghost" onClick={() => void actions.copyLogs()}>
            Copy diagnostic logs
          </Button>
        ) : undefined
      }
    />
  );
}
