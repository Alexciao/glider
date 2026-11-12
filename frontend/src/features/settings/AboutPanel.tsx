import { useEffect, useState } from "react";
import { ExternalLink } from "lucide-react";
import { rpc } from "../../ipc/rpc";
import type { LicenseInfo } from "../../ipc/contract.gen";
import { actions, toGliderError } from "../../state/actions";
import { useSnap } from "../../state/store";
import { Badge, Button, ErrorCallout, GliderMark, Section, Skeleton, Surface } from "../../ui";

const LEGENDARY_SOURCE = "https://github.com/legendary-gl/legendary/tree/0.21.1";

export function AboutPanel() {
  const app = useSnap((s) => s.app);
  const [items, setItems] = useState<LicenseInfo[] | null>(null);
  const [error, setError] = useState<string | null>(null);

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
    <div className="flex flex-col gap-7">
      <Surface tone="raised" className="flex items-center gap-5 p-5">
        <div className="grid size-16 shrink-0 place-items-center rounded-2xl bg-surface-2 ring-1 ring-inset ring-border-strong">
          <GliderMark size={44} />
        </div>
        <div className="min-w-0">
          <h2 className="text-lg font-semibold tracking-tight text-fg">Glider</h2>
          <p className="mt-0.5 text-sm text-fg-muted tabular-nums">
            Version {app.version} · powered by Legendary {app.legendaryVersion}
          </p>
          <p className="mt-2 max-w-prose text-xs leading-[1.55] text-fg-subtle">
            Glider is an independent app and isn't affiliated with or endorsed by Epic Games. Fortnite is a trademark of Epic Games, Inc.
          </p>
        </div>
      </Surface>

      <Section title="Legendary" description="Glider talks to Epic's servers through Legendary, a free and open-source launcher engine.">
        <div className="flex items-start gap-4 px-4 py-3.5">
          <div className="min-w-0 flex-1">
            <div className="flex items-center gap-2.5">
              <p className="text-sm font-medium text-fg">Legendary {legendary?.version ?? app.legendaryVersion}</p>
              <Badge tone="accent">{legendary?.license ?? "GPL-3.0"}</Badge>
            </div>
            <p className="mt-1.5 max-w-prose text-xs leading-[1.55] text-fg-muted">
              Legendary is licensed under the GNU General Public License v3. Glider ships it unmodified as a separate program, and you're free to
              read, change and redistribute its source under the same license.
            </p>
            {legendary?.text && <p className="selectable mt-2 max-w-prose text-xs leading-[1.55] text-fg-subtle">{legendary.text}</p>}
          </div>
          <Button size="sm" variant="secondary" iconRight={<ExternalLink size={13} />} onClick={() => void actions.openExternal(LEGENDARY_SOURCE)}>
            Source code
          </Button>
        </div>
      </Section>

      <Section title="Open-source software" description="Glider is built on these projects.">
        {error ? (
          <div className="p-3">
            <ErrorCallout title="Couldn't load licenses" message={error} />
          </div>
        ) : items === null ? (
          [0, 1, 2, 3].map((i) => (
            <div key={i} className="flex items-center gap-4 px-4 py-3">
              <Skeleton className="h-4 w-40" />
              <Skeleton className="ml-auto h-5 w-16 rounded-full" />
            </div>
          ))
        ) : (
          others.map((l) => (
            <div key={`${l.name}-${l.version}`} className="flex items-center gap-4 px-4 py-2.5">
              <div className="min-w-0 flex-1">
                <p className="truncate text-sm text-fg">
                  {l.name} <span className="text-fg-subtle tabular-nums">{l.version}</span>
                </p>
              </div>
              <Badge>{l.license}</Badge>
              <Button size="sm" variant="ghost" aria-label={`Open ${l.name} website`} onClick={() => void actions.openExternal(l.url)}>
                <ExternalLink size={13} />
              </Button>
            </div>
          ))
        )}
      </Section>
    </div>
  );
}
