import { useCallback, useEffect, useMemo, useState } from "react";
import { Button, NonIdealState, Switch, Tag } from "@blueprintjs/core";
import type { FleetDirectoryResult, FleetMachineView, FleetSessionView } from "#neoastra";
import { AppIcon } from "../../AppIcon";
import { ActivitySpinner } from "../../ActivitySpinner";
import { useShellLanguage } from "../../shellLanguage";
import { watchIndex } from "../../search/canvas/canvasWatch";
import { fleetGroups, fleetSummary, serverConnection, serverConnectionIntent, serverConnectionLabel, sessionModel, sessionState, sessionStateLabel } from "../fleetServer";

/** What the Fleet canvas asks of the host. Each may throw when the host is gone; the canvas then keeps what it shows. */
export type FleetCanvasApi = Readonly<{
  directory(signal: AbortSignal): Promise<FleetDirectoryResult>;
  watch(signal: AbortSignal): Promise<AsyncIterable<unknown>>;
}>;

/** What the canvas does through the window. */
export type FleetCanvasActions = Readonly<{
  openSession(sessionId: string): void;
  /** Runs a command of the window by its name; false when it cannot run now. */
  runCommand(name: string): boolean;
}>;

/**
 * The Fleet canvas, the first of the console: the machines of the organization of the server, whether each is online, and the sessions
 * they publish (their facts: state, provider and model, times), live from the directory of the control connection. A session of this
 * computer or of a paired machine opens in a tab; the others are shown only.
 */
export function FleetCanvas({ api, visible, actions }: { api: FleetCanvasApi | null; visible: boolean; actions: FleetCanvasActions }) {
  const { t, locale } = useShellLanguage();
  const [directory, setDirectory] = useState<FleetDirectoryResult | null>(null);
  const [failed, setFailed] = useState(false);
  const [archived, setArchived] = useState(false);
  const [revision, setRevision] = useState(0);
  const reload = useCallback(() => setRevision(value => value + 1), []);
  useEffect(() => {
    if (!api || !visible) return;
    return watchIndex(signal => api.watch(signal), reload);
  }, [api, visible, reload]);
  useEffect(() => {
    if (!api || !visible) return;
    const abort = new AbortController();
    api.directory(abort.signal).then(value => { if (!abort.signal.aborted) { setDirectory(value); setFailed(false); } },
      () => { if (!abort.signal.aborted) setFailed(true); });
    return () => abort.abort();
  }, [api, visible, revision]);
  const groups = useMemo(() => directory ? fleetGroups(directory.machines, directory.sessions, archived) : [], [directory, archived]);
  const summary = useMemo(() => directory ? fleetSummary(directory.machines, directory.sessions) : null, [directory]);
  const time = useMemo(() => new Intl.DateTimeFormat(locale, { dateStyle: "short", timeStyle: "short" }), [locale]);
  if (!directory) {
    return <div className="fleet-canvas"><NonIdealState icon={failed ? <AppIcon name="server" size={36} /> : <ActivitySpinner size={28} />}
      title={t(failed ? "The server does not answer." : "Loading…")} /></div>;
  }

  if (directory.status !== "ok") {
    return <div className="fleet-canvas"><NonIdealState icon={<AppIcon name="server" size={36} />} title={t("Not linked to a server.")}
      description={t("Create your server or join one in Settings > Fleet > Server.")}
      action={<Button icon={<AppIcon name="settings" size={15} />} onClick={() => actions.runCommand("settings")}>{t("Settings")}</Button>} /></div>;
  }

  const connection = serverConnection({ connection: directory.connection ?? "offline" });
  return <div className="fleet-canvas">
    <header className="fleet-canvas-head">
      <div className="fleet-canvas-title">
        <h1>{directory.organization ?? t("Fleet")}</h1>
        <Tag minimal round intent={serverConnectionIntent(connection)}>{t(serverConnectionLabel(connection))}</Tag>
        <span className="muted-text">{directory.url}</span>
      </div>
      {summary && <div className="fleet-canvas-summary" role="status">
        <span><strong>{summary.online}</strong> / {summary.machines} {t("machines online")}</span>
        <span><strong>{summary.working}</strong> {t("at work")}</span>
        <span className={summary.waiting > 0 ? "fleet-canvas-waiting" : undefined}><strong>{summary.waiting}</strong> {t("wait for you")}</span>
      </div>}
      <Switch className="fleet-canvas-archived" checked={archived} label={t("Archived")} onChange={event => setArchived(event.currentTarget.checked)} />
    </header>
    {groups.length === 0 ? <NonIdealState icon={<AppIcon name="remote" size={36} />} title={t("No machine has published yet.")} />
      : <div className="fleet-canvas-machines">
        {groups.map(({ machine, sessions }) => <MachineCard key={machine.id} machine={machine} sessions={sessions} time={time} onOpen={actions.openSession} />)}
      </div>}
  </div>;
}

function MachineCard({ machine, sessions, time, onOpen }: { machine: FleetMachineView; sessions: readonly FleetSessionView[]; time: Intl.DateTimeFormat; onOpen: (id: string) => void }) {
  const { t } = useShellLanguage();
  const details = [machine.os, machine.version && `CodeAlta ${machine.version}`, machine.providers.join(", ")].filter(Boolean).join(" · ");
  return <section className="fleet-machine" data-online={machine.online} aria-label={machine.name}>
    <div className="fleet-machine-head">
      <span className="fleet-machine-icon"><AppIcon name={machine.isThisComputer ? "computer" : "remote"} size={18} /></span>
      <div className="fleet-machine-main">
        <div className="fleet-machine-title"><strong>{machine.name}</strong>
          <Tag minimal round intent={machine.online ? "success" : "none"}>{t(machine.online ? "Online" : "Offline")}</Tag>
          {machine.isThisComputer && <Tag minimal round>{t("This computer")}</Tag>}
          {machine.isPaired ? <Tag minimal round>{t("Paired")}</Tag>
            : machine.isReachable && <Tag minimal round title={t("Through the server, encrypted from end to end")}>{t("Relayed")}</Tag>}</div>
        <div className="muted-text fleet-machine-details">{details}</div>
      </div>
      <span className="muted-text fleet-machine-count">{t(sessions.length === 1 ? "{count} session" : "{count} sessions", { count: sessions.length })}</span>
    </div>
    {sessions.length > 0 && <table className="fleet-sessions">
      <tbody>
        {sessions.map(session => {
          const state = sessionState(session);
          return <tr key={session.id} data-state={state}>
            <td><Tag minimal round intent={state === "requires_action" ? "warning" : state === "running" ? "primary" : "none"}>{t(sessionStateLabel(state))}</Tag></td>
            <td className="fleet-session-model" title={sessionModel(session)}>{sessionModel(session)}</td>
            <td className="muted-text fleet-session-time" title={t("Created {time}", { time: time.format(new Date(session.createdAt)) })}>{time.format(new Date(session.updatedAt))}</td>
            <td className="fleet-session-open">{session.canOpen
              ? <Button size="small" variant="minimal" icon={<AppIcon name="open" size={14} />} onClick={() => onOpen(session.id)}>{t("Open")}</Button>
              : <span className="muted-text" title={t("Pair with this machine to open its sessions.")}><AppIcon name="eye" size={14} /></span>}</td>
          </tr>;
        })}
      </tbody>
    </table>}
  </section>;
}
