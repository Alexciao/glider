import type { FixAction, GliderError } from "../../ipc/contract.gen";
import { actions, runFix } from "../../state/actions";
import { FIX_LABELS, usePending, useUi } from "../../state/ui";
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
  // Epic rejected the sign-in (often its captcha in the embedded window): offer the browser route, quietly.
  const cancelled = error.code === "authCancelled";
  const browserRoute = cancelled || (error.code === "authFailed" && fix === "signIn");
  const callout = (
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
  if (!browserRoute) return callout;
  return (
    <div className="flex flex-col items-start gap-1.5">
      {callout}
      <button
        type="button"
        className="rounded text-xs text-fg-muted underline-offset-2 hover:text-fg hover:underline focus-visible:outline-2 focus-visible:outline-accent"
        onClick={() => useUi.getState().openDialog({ kind: "browserSignIn" })}
      >
        Sign in with your browser instead
      </button>
    </div>
  );
}
