import { LogIn } from "lucide-react";
import { actions } from "../../state/actions";
import { useSnap } from "../../state/store";
import { usePending } from "../../state/ui";
import { Button, cx } from "../../ui";

/** True while the account can't talk to Epic (signed out, or the session ended) and no sign-in is under way. */
export function useNeedsSignIn(): "signedOut" | "expired" | null {
  const status = useSnap((s) => s.auth.status);
  return status === "signedOut" || status === "expired" ? status : null;
}

/**
 * Home and Library while signed out: one quiet line and the one button. Core keeps the library, installs and settings
 * when the session ends, so the page below is real; what is gone is everything that talks to Epic.
 */
export function SignInBanner({ className }: { className?: string }) {
  const state = useNeedsSignIn();
  const signingIn = usePending("signIn");
  const offline = useSnap((s) => s.connectivity === "offline");
  if (!state) return null;
  return (
    <div role="status" className={cx("flex items-center gap-3 rounded-lg border border-border bg-surface-1 py-2 pl-3.5 pr-2", className)}>
      <p className="min-w-0 flex-1 text-sm text-fg-muted">
        <span className="font-medium text-fg">{state === "expired" ? "Your Epic session expired." : "You're signed out."}</span> Installed games that support offline play still launch.
      </p>
      <Button size="sm" variant="primary" icon={<LogIn size={14} />} loading={signingIn} disabled={offline} onClick={() => void actions.signIn("settings")}>
        {state === "expired" ? "Sign in again" : "Sign in"}
      </Button>
    </div>
  );
}
