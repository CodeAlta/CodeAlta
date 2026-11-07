import { useEffect, useState } from "react";
import { Button, Tag } from "@blueprintjs/core";
import { fleetServer, machines } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { settingsNavigation } from "../settingsNavigation";
import { machineState } from "../machines/machines";
import { serverConnection, serverConnectionIntent, serverConnectionLabel, type ServerConnection } from "../fleet/fleetServer";

type FleetGlance = Readonly<{ machines: number; online: number; server: string | null; connection: ServerConnection | null }>;

/** What the card reads; the window's services unless a test brings its own. */
export type LandingFleetApi = Readonly<{ machines: Pick<typeof machines, "list">; servers: Pick<typeof fleetServer, "status"> }>;

// One object for the life of the window: a new one at each render would read the fleet again at each render.
const windowApi: LandingFleetApi = { machines, servers: fleetServer };

/**
 * Fleet on the welcome page: how the machines and the server stand at a glance, or what Fleet is for while there is none,
 * and the way to its Overview (Settings > Fleet).
 */
export function LandingFleetCard({ epoch, visible, api = windowApi }: { epoch: string | null; visible: boolean; api?: LandingFleetApi }) {
  const { t } = useShellLanguage();
  const [glance, setGlance] = useState<FleetGlance | null>(null);
  useEffect(() => {
    if (!epoch || !visible) return;
    const abort = new AbortController();
    const options = { signal: abort.signal, timeoutMilliseconds: 15_000 };
    void Promise.allSettled([api.machines.list({ expectedEpoch: epoch }, options), api.servers.status({ expectedEpoch: epoch }, options)]).then(([listed, status]) => {
      if (abort.signal.aborted) return;
      const list = listed.status === "fulfilled" ? listed.value.machines : [];
      const server = status.status === "fulfilled" && status.value.status === "ok" ? status.value.servers[0] ?? null : null;
      setGlance({ machines: list.length, online: list.filter(machine => machineState(machine) === "online").length,
        server: server ? server.organization ?? server.url : null, connection: server ? serverConnection(server) : null });
    });
    return () => abort.abort();
  }, [api, epoch, visible]);
  const started = !!glance && (glance.machines > 0 || glance.server !== null);
  return <section className="landing-card landing-fleet" data-list="fleet" aria-label={t("Fleet")}>
    <header className="landing-card-header"><AppIcon name="fleet" size={16} className="landing-card-icon landing-fleet-icon" aria-hidden="true" /><h2>{t("Fleet")}</h2></header>
    {started ? <div className="landing-fleet-glance">
      <div><strong>{glance!.online}</strong><span className="muted-text"> / {glance!.machines} </span><span className="muted-text">{t("machines online")}</span></div>
      {glance!.server && <div className="landing-fleet-server"><AppIcon name="server" size={14} /><span>{glance!.server}</span>
        {glance!.connection && <Tag minimal round intent={serverConnectionIntent(glance!.connection)}>{t(serverConnectionLabel(glance!.connection))}</Tag>}</div>}
    </div> : <p className="landing-empty">{t("Run sessions on your other computers, and share them with your team.")}</p>}
    <Button className="landing-fleet-open" intent={started ? "none" : "primary"} icon={<AppIcon name="fleet" size={15} />} onClick={() => settingsNavigation.open("fleet")}>
      {t(started ? "Open Fleet" : "Start your fleet")}</Button>
  </section>;
}
