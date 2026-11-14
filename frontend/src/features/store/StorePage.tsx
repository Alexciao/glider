import { Store } from "lucide-react";
import { actions } from "../../state/actions";
import { Button, EmptyState } from "../../ui";
import { PageHeader, PageScroll } from "../common/Page";

/**
 * Placeholder. The Store is a native WebView2 surface the host shows over a placeholder rectangle
 * (store.show / store.setBounds / store.hide, contract v2); that page is built in the Store phase.
 */
export function StorePage() {
  return (
    <PageScroll>
      <PageHeader title="Store" />
      <EmptyState
        icon={<Store size={20} />}
        title="The Epic Games Store is coming to Glider"
        description="Browse and claim games here without leaving the app."
        action={
          <Button variant="secondary" onClick={() => void actions.openExternal("https://store.epicgames.com")}>
            Open in your browser
          </Button>
        }
      />
    </PageScroll>
  );
}
