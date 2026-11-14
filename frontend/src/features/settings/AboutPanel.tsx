import { useEffect, useState } from "react";
import { rpc } from "../../ipc/rpc";
import type { LicenseInfo } from "../../ipc/contract.gen";
import { actions, toGliderError } from "../../state/actions";
import { useSnap } from "../../state/store";
import { Button, Disclosure, ErrorCallout, Skeleton } from "../../ui";
import { Block, Group, Line, Setting } from "./parts";
import { sectionById } from "./sections";

const LEGENDARY_SOURCE = "https://github.com/legendary-gl/legendary/tree/0.21.1";

export function AboutPanel() {
  const app = useSnap((s) => s.app);
  const [items, setItems] = useState<LicenseInfo[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const def = sectionById("about");

  useEffect(() => {
    let alive = true;
    rpc()
      .call("app.getLicenses")
      .then((r) => alive && setItems(r.items))
      .catch((e) => alive && setError(toGliderError(e).message));
    return () => {
      alive = false;
    };
  }, []);

  const legendary = items?.find((i) => /legendary/i.test(i.name));
  const others = items?.filter((i) => i !== legendary) ?? [];

  return (
    <Block id="about" title={def.label}>
      <Group>
        <Setting label="Glider">
          <Line label="Glider" description={`Version ${app.version}`} />
        </Setting>
        <Setting label="Legendary">
          <Line
            label="Legendary"
            description={`Version ${legendary?.version ?? app.legendaryVersion} · ${legendary?.license ?? "GPL-3.0"}`}
            control={
              <Button size="sm" variant="secondary" onClick={() => void actions.openExternal(LEGENDARY_SOURCE)}>
                Source code
              </Button>
            }
          />
        </Setting>
      </Group>
      <Disclosure title="Open-source licenses">
        {error ? (
          <ErrorCallout title="Couldn't load licenses" message={error} />
        ) : items === null ? (
          <div className="flex flex-col gap-3 py-2">
            {[0, 1, 2, 3].map((i) => (
              <Skeleton key={i} className="h-4 w-48" />
            ))}
          </div>
        ) : (
          <ul>
            {others.map((l) => (
              <li key={`${l.name}-${l.version}`} className="flex items-center gap-4 py-2 text-sm">
                <button type="button" className="min-w-0 flex-1 truncate text-left text-fg hover:underline" onClick={() => void actions.openExternal(l.url)}>
                  {l.name} <span className="tabular-nums text-fg-subtle">{l.version}</span>
                </button>
                <span className="text-xs text-fg-muted">{l.license}</span>
              </li>
            ))}
          </ul>
        )}
      </Disclosure>
      <p className="text-xs text-fg-subtle">Glider isn't affiliated with or endorsed by Epic Games.</p>
    </Block>
  );
}
