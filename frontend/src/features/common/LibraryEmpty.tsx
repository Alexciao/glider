import { Gamepad2, LogIn } from "lucide-react";
import { actions } from "../../state/actions";
import { useSnap } from "../../state/store";
import { usePending } from "../../state/ui";
import { Button, EmptyState } from "../../ui";
import { useNeedsSignIn } from "./SignInBanner";

/**
 * "Nothing here yet" for Home and Library. The copy follows the account: signed out, an empty library means it was never
 * synced, so the one thing to do is sign in; only a signed-in account with no games is genuinely "empty".
 */
export function LibraryEmpty({ className, syncing }: { className?: string; syncing?: boolean }) {
  const needsSignIn = useNeedsSignIn();
  const offline = useSnap((s) => s.connectivity === "offline");
  const signingIn = usePending("signIn");
  if (needsSignIn) {
    return (
      <EmptyState
        className={className}
        icon={<LogIn size={20} />}
        title="Sign in to see your library"
        description="Your Epic games appear here once you're signed in."
        action={
          <Button variant="primary" loading={signingIn} disabled={offline} onClick={() => void actions.signIn("settings")}>
            Sign in
          </Button>
        }
      />
    );
  }
  return (
    <EmptyState
      className={className}
      icon={<Gamepad2 size={20} />}
      title="Your library is empty"
      action={
        <Button variant="primary" loading={syncing} onClick={() => void actions.refreshLibrary()}>
          Sync library
        </Button>
      }
    />
  );
}
