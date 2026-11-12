import type { FixAction, GliderError } from "../../ipc/contract.gen";
import { runFix } from "../../state/actions";
import { FIX_LABELS, usePending } from "../../state/ui";
import { ErrorCallout } from "../../ui";

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
  return (
    <ErrorCallout
      className={className}
      tone={waiting ? "warning" : "error"}
      title={error.title}
      message={error.message}
      detail={error.detail}
      fixLabel={fix ? FIX_LABELS[fix] : undefined}
      fixLoading={loading}
      onFix={fix ? () => runFix(fix, { retry, path }) : undefined}
      onDismiss={onDismiss}
    />
  );
}
