import { useCallback, useEffect, useMemo, useState, type CSSProperties } from "react";
import { Button, Tag } from "@blueprintjs/core";
import { fleetServer, machines, type FleetServerView, type MachineView } from "#neoastra";
import { AppIcon, type IconName } from "../../AppIcon";
import { SettingsPage, SettingsUnavailable } from "../../SettingsPage";
import { settingsFailure, type SettingsNotice } from "../../settingsEditing";
import { useShellLanguage } from "../../shellLanguage";
import type { MessageKey } from "../../localization";
import { settingsNavigation } from "../../settingsNavigation";
import { SpaceIcon } from "../../spaces/SpaceViews";
import { spacePageLink } from "../../spaces/spaceSummary";
import type { Space } from "../../spaces/spaces";
import { serverConnection, serverConnectionIntent, serverConnectionLabel } from "../fleetServer";
import { AddMachineDialog, type AddMachineApi } from "../../machines/AddMachineDialog";
import { PairMachineDialog } from "../../machines/MachinesSettings";
import { FleetHelp } from "./FleetHelp";
import { fleetAttention, fleetSteps, hasFleet, machineTotals, spacesFleet, type FleetAttention, type FleetStep } from "./overviewModel";

/** What the Overview reads: the machines, and the servers this computer is linked to. */
export type FleetOverviewApi = Readonly<{
  machines: Pick<typeof machines, "list" | "watch" | "previewPairing" | "pair"> & Partial<AddMachineApi>;
  servers: Pick<typeof fleetServer, "status" | "watch">;
}>;

const defaultApi: FleetOverviewApi = { machines, servers: fleetServer };

// The look of each part of the fleet: its icon and its color, the same wherever it is shown.
const steps: Readonly<Record<FleetStep["id"], Readonly<{ icon: IconName; color: string; title: MessageKey; text: MessageKey }>>> = {
  machines: { icon: "remote", color: "blue", title: "Add a machine", text: "Run sessions on another computer of yours." },
  server: { icon: "server", color: "violet", title: "Create or join a server", text: "One place for your machines, your people and their policies." },
  people: { icon: "people", color: "green", title: "Invite people", text: "Give your team access, each with a role." },
  policies: { icon: "shield", color: "orange", title: "Set policies", text: "Choose the providers, models and sandboxes allowed." },
  secrets: { icon: "secret", color: "teal", title: "Share secrets", text: "The keys your machines use, sealed for them." },
  budgets: { icon: "usage", color: "gold", title: "Set budgets", text: "Limits and alerts on what sessions spend." },
};

const attentionText: Readonly<Record<FleetAttention["kind"], MessageKey>> = {
  stopped: "The work of the fleet is stopped",
  stoppedPart: "Some of the work of the fleet is stopped",
  server: "Not connected to the server",
  unauthorized: "Refuses this computer",
  offline: "Does not answer",
};

/**
 * Settings > Fleet > Overview, the first page of Fleet: with nothing set up, the first steps as cards (a machine, a server,
 * then what a server adds); once there is a fleet, how it stands (machines online, sessions, the server), what needs the
 * person, the server each space publishes to, and the parts left to set up.
 */
export function FleetOverview({ epoch, spaces, api = defaultApi }: { epoch: string | null; spaces: readonly Space[]; api?: FleetOverviewApi }) {
  const { t } = useShellLanguage();
  const [machineList, setMachineList] = useState<readonly MachineView[] | null>(null);
  const [servers, setServers] = useState<readonly FleetServerView[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [notice, setNotice] = useState<SettingsNotice | null>(null);
  const [generation, setGeneration] = useState(0);
  const [adding, setAdding] = useState(false);
  const [pairing, setPairing] = useState(false);
  const reload = useCallback(() => setGeneration(value => value + 1), []);
  useEffect(() => {
    if (!epoch) { setLoading(false); return; }
    const abort = new AbortController();
    setLoading(true);
    const options = { signal: abort.signal, timeoutMilliseconds: 20_000 };
    void Promise.allSettled([api.machines.list({ expectedEpoch: epoch }, options), api.servers.status({ expectedEpoch: epoch }, options)]).then(([listed, status]) => {
      if (abort.signal.aborted) return;
      // What could not be read stays unknown (null), never empty: an empty fleet would show the first steps instead.
      const list = listed.status === "fulfilled" && listed.value.status === "ok" ? listed.value.machines : null;
      const read = status.status === "fulfilled" && status.value.status === "ok" ? status.value.servers : null;
      setMachineList(list);
      setServers(read);
      const failure = (result: PromiseSettledResult<{ status: string }>) =>
        result.status === "rejected" ? settingsFailure("read_failed") : settingsFailure(result.value.status) ?? settingsFailure("read_failed");
      setNotice(list === null ? failure(listed) : read === null ? failure(status) : null);
      setLoading(false);
    });
    return () => abort.abort();
  }, [api, epoch, generation]);
  // A machine that comes, goes or changes state changes the Overview, and so does a server that connects, approves or stops.
  useEffect(() => {
    if (!epoch) return;
    const abort = new AbortController();
    const follow = async (watch: () => Promise<AsyncIterable<unknown>>) => {
      try { for await (const _ of await watch()) if (!abort.signal.aborted) reload(); }
      catch { /* The bridge is gone: Reload still reads them. */ }
    };
    void follow(() => api.machines.watch({ expectedEpoch: epoch }, { signal: abort.signal }));
    void follow(() => api.servers.watch({ expectedEpoch: epoch }, { signal: abort.signal }));
    return () => abort.abort();
  }, [api, epoch, reload]);

  const list = machineList ?? [];
  const linked = servers ?? [];
  const started = hasFleet(list, linked);
  const open = (page: string) => settingsNavigation.open(page);
  return <SettingsPage className="fleet-overview" label={t("Fleet")} group="Fleet" title="Overview"
    description="Your machines, your server and what your organization shares, in one place."
    notice={notice} loading={loading} busy={false} onReload={reload}
    actions={<FleetHelp text="Fleet runs your sessions on your other machines, and shares a server with your organization: its people, policies, secrets and budgets." page="fleet/readme.md" />}>
    {machineList === null || (servers === null && list.length === 0)
      ? <SettingsUnavailable loading={loading || !epoch} icon="fleet" title="The fleet could not be read." />
      : !started ? <Onboarding onAdd={() => setAdding(true)} onOpen={open} epoch={epoch} />
      : <Running machines={list} servers={linked} spaces={spaces} onOpen={open} onAdd={() => setAdding(true)} />}
    {adding && epoch && <AddMachineDialog epoch={epoch} api={{ ...machines, ...api.machines } as AddMachineApi} onPairLink={() => { setAdding(false); setPairing(true); }}
      onClose={added => { setAdding(false); if (added) reload(); }} onOpen={machineId => { setAdding(false); open(`machines/${machineId}`); }} />}
    {pairing && epoch && <PairMachineDialog epoch={epoch} api={api.machines} guide onClose={paired => { setPairing(false); if (paired) reload(); }} />}
  </SettingsPage>;
}

/** The first steps: the two that start a fleet, large; what a server adds, after, waiting for it. */
function Onboarding({ onAdd, onOpen, epoch }: { onAdd: () => void; onOpen: (page: string) => void; epoch: string | null }) {
  const { t } = useShellLanguage();
  const list = fleetSteps([], []);
  return <div className="fleet-onboarding">
    <section className="fleet-onboarding-hero" aria-label={t("Start your fleet")}>
      <span className="fleet-onboarding-mark"><AppIcon name="fleet" size={30} /></span>
      <div>
        <h2>{t("Start your fleet")}</h2>
        <p className="muted-text">{t("Run sessions on your other computers, and share them with your team.")}</p>
      </div>
    </section>
    <div className="fleet-start-cards">
      {list.slice(0, 2).map(step => <StepCard key={step.id} step={step} large disabled={!epoch}
        action={step.id === "machines" ? t("Add a machine…") : t("Set up a server…")} onClick={step.id === "machines" ? onAdd : () => onOpen(step.page)} />)}
    </div>
    <h3 className="fleet-section-title">{t("With a server")}</h3>
    <div className="fleet-step-grid">{list.slice(2).map(step => <StepCard key={step.id} step={step} onClick={() => onOpen(step.page)} />)}</div>
  </div>;
}

/** A part of the fleet as a card: its colored icon, what it is for, and what opens it; done or waiting for a server says so. */
function StepCard({ step, large = false, disabled = false, action, onClick }: { step: FleetStep; large?: boolean; disabled?: boolean; action?: string; onClick: () => void }) {
  const { t } = useShellLanguage();
  const look = steps[step.id];
  const waiting = !step.ready;
  return <button type="button" className="fleet-step settings-own-button" data-large={large || undefined} data-done={step.done || undefined} data-waiting={waiting || undefined}
    style={{ "--fleet-accent": `var(--fleet-${look.color})` } as CSSProperties} disabled={disabled || waiting} onClick={onClick}>
    <span className="fleet-step-icon"><AppIcon name={look.icon} size={large ? 24 : 18} /></span>
    <span className="fleet-step-main">
      <strong>{t(look.title)}</strong>
      <span className="muted-text">{t(look.text)}</span>
      {large && action && <span className="fleet-step-action">{action}<AppIcon name="arrowRight" size={14} /></span>}
    </span>
    {step.done ? <Tag minimal round intent="success" icon={<AppIcon name="check" size={11} />}>{t("Done")}</Tag>
      : waiting ? <Tag minimal round icon={<AppIcon name={step.waitsFor === "administrator" ? "shield" : "server"} size={11} />}>
        {t(step.waitsFor === "administrator" ? "Administered from another computer" : "Needs a server")}</Tag>
      : !large && <AppIcon name="chevronRight" size={15} className="fleet-step-go" />}
  </button>;
}

/** A fleet that runs: how it stands, what needs the person, the spaces, and what is left to set up. */
function Running({ machines: list, servers, spaces, onOpen, onAdd }: {
  machines: readonly MachineView[]; servers: readonly FleetServerView[]; spaces: readonly Space[]; onOpen: (page: string) => void; onAdd: () => void;
}) {
  const { t } = useShellLanguage();
  const totals = useMemo(() => machineTotals(list), [list]);
  const attention = useMemo(() => fleetAttention(list, servers), [list, servers]);
  const plan = useMemo(() => fleetSteps(list, servers), [list, servers]);
  const bySpace = useMemo(() => spacesFleet(spaces, servers), [spaces, servers]);
  const server = servers[0] ?? null;
  const connection = server ? serverConnection(server) : null;
  const linkedSpaces = bySpace.filter(row => row.server).length;
  return <div className="fleet-running">
    <ul className="fleet-tiles" aria-label={t("Your fleet")}>
      <Tile icon="remote" color="blue" label={t("Machines")} onClick={() => onOpen("machines")}
        value={<>{totals.online}<small> / {totals.total}</small></>} detail={t("online")}
        meter={totals.total > 0 ? totals.online / totals.total : 0} />
      <Tile icon="chat" color="green" label={t("Sessions")} value={totals.sessions} detail={t("on your machines")} onClick={() => onOpen("machines")} />
      <Tile icon="server" color="violet" label={t(servers.length > 1 ? "Servers" : "Server")} onClick={() => onOpen("fleetServer")}
        value={server ? <span className="fleet-tile-name" title={servers.map(each => each.organization ?? each.url).join("\n")}>{server.organization ?? server.url}
          {servers.length > 1 && <small> {t("{count} more", { count: servers.length - 1 })}</small>}</span> : <span className="fleet-tile-name muted-text">{t("None")}</span>}
        detail={connection ? <Tag minimal round intent={serverConnectionIntent(connection)}>{t(serverConnectionLabel(connection))}</Tag> : t("Machines you paired only")} />
      <Tile icon="space" color="teal" label={t("Spaces")} value={<>{linkedSpaces}<small> / {bySpace.length}</small></>} detail={t("on a server")}
        onClick={() => onOpen("fleetServer")} />
    </ul>
    {attention.length > 0 && <section className="fleet-panel fleet-attention" aria-label={t("Needs your attention")}>
      <h3 className="fleet-section-title"><AppIcon name="warning" size={15} />{t("Needs your attention")}<Tag minimal round intent="warning">{attention.length}</Tag></h3>
      <ul>{attention.slice(0, 6).map((item, index) => <li key={`${item.kind}:${item.name}:${index}`} data-severity={item.severity}>
        <AppIcon name={item.kind === "offline" || item.kind === "unauthorized" ? "remote" : item.kind === "stopped" ? "stop" : "server"} size={15} />
        <span className="fleet-attention-main"><strong>{item.name}</strong><span>{t(attentionText[item.kind])}</span>
          {item.detail && <span className="muted-text fleet-attention-detail" title={item.detail}>{item.detail}</span>}</span>
        <Button size="small" variant="minimal" endIcon={<AppIcon name="chevronRight" size={13} />} text={t("Open")} onClick={() => onOpen(item.page)} />
      </li>)}</ul>
      {attention.length > 6 && <Button className="fleet-attention-more" size="small" variant="minimal" text={t("{count} more", { count: attention.length - 6 })}
        endIcon={<AppIcon name="chevronRight" size={13} />} onClick={() => onOpen("machines")} />}
    </section>}
    <div className="fleet-columns">
      <section className="fleet-panel" aria-label={t("Spaces")}>
        <h3 className="fleet-section-title">{t("Spaces")}
          <FleetHelp text="A space publishes its sessions to the server it is linked to; a space of none keeps them to this computer and the machines paired with it." page="fleet/server.md" /></h3>
        <table className="fleet-mini-table">
          <thead><tr><th scope="col">{t("Space")}</th><th scope="col">{t("Server")}</th></tr></thead>
          {/* A space opens its page; its server, the Server page, where the spaces that publish to it are chosen. */}
          <tbody>{bySpace.map(({ space, server: of }) => <tr key={space.id} data-space={space.id}>
            <td><button type="button" className="fleet-mini-link settings-own-button" onClick={() => onOpen(spacePageLink(space.id))}>
              <SpaceIcon space={space} size={14} /><span>{space.name}</span></button></td>
            <td><button type="button" className="fleet-mini-link settings-own-button" onClick={() => onOpen("fleetServer")}>
              {of ? <><AppIcon name="server" size={13} /><span>{of.organization ?? of.url}</span>
                <Tag minimal round intent={serverConnectionIntent(serverConnection(of))}>{t(serverConnectionLabel(serverConnection(of)))}</Tag></>
                : <span className="muted-text">{t("This computer and its machines")}</span>}</button></td>
          </tr>)}</tbody>
        </table>
      </section>
      <section className="fleet-panel" aria-label={t("Set up")}>
        <h3 className="fleet-section-title">{t("Set up")}</h3>
        <div className="fleet-step-list">{plan.map(step => <StepCard key={step.id} step={step} onClick={() => step.id === "machines" && step.done ? onOpen("machines") : step.id === "machines" ? onAdd() : onOpen(step.page)} />)}</div>
      </section>
    </div>
  </div>;
}

/** A figure of the fleet: what it counts, its value, a word about it, and the page it opens. */
function Tile({ icon, color, label, value, detail, meter, onClick }: {
  icon: IconName; color: string; label: string; value: React.ReactNode; detail: React.ReactNode; meter?: number; onClick: () => void;
}) {
  return <li><button type="button" className="fleet-tile settings-own-button" style={{ "--fleet-accent": `var(--fleet-${color})` } as CSSProperties} onClick={onClick}>
    <span className="fleet-tile-head"><span className="fleet-tile-icon"><AppIcon name={icon} size={16} /></span>{label}</span>
    <span className="fleet-tile-value">{value}</span>
    <span className="fleet-tile-detail muted-text">{detail}</span>
    {meter !== undefined && <span className="fleet-tile-meter" aria-hidden="true"><span style={{ width: `${Math.round(meter * 100)}%` }} /></span>}
  </button></li>;
}
