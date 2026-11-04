import { useEffect, useState } from "react";
import { Button, Callout, Checkbox, Dialog, DialogBody, DialogFooter, HTMLSelect, InputGroup, NonIdealState, PopoverNext, Switch, Tag, TextArea } from "@blueprintjs/core";
import { fleetServer, type FleetDeviceView, type FleetServerView, type FleetTelemetryView, type FleetUpdateCampaignView, type FleetUpdatesView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { CreateServerDialog } from "./CreateServerDialog";
import { JoinOrganizationDialog } from "./JoinOrganizationDialog";
import { ServerSignIn } from "./FleetSignInSettings";
import { campaignProgress, campaignStateIntent, campaignStateLabel, canCancelCampaign, emptyUpdateDraft, joinCommand, keyWords, localTimeZone, machineStateIntent,
  machineStateLabel, orderedDevices, policyDraft, policyDraftProblem, policySummary, recoveryCheckPasses, recoveryCheckPositions, recoveryWordCount, serverAddressOf, serverConnection,
  serverConnectionIntent, serverConnectionLabel, serverProblem, telemetryDraft, telemetryHeaderEdits, telemetryProblem, telemetrySignals, telemetrySummary, updateDraftProblem,
  updateProblem, updateStart, type PolicyDraft, type TelemetryDraft, type TelemetrySignal, type UpdateDraft } from "./fleetServer";

export type FleetServerApi = Pick<typeof fleetServer, "status" | "create" | "join" | "leave" | "createToken" | "devices" | "approve" | "revoke" | "watch" | "telemetry"
  | "setTelemetry" | "updates" | "startUpdate" | "cancelUpdate" | "setUpdatePolicy" | "createRecoveryKey" | "recover" | "signInMethods" | "joinWithSignIn" | "signInSettings"
  | "setSignInSettings" | "linkSignIn">;

/** The address a server the person runs on this computer listens on, as `altaserver run` says it. */
export const localServerAddress = "http://127.0.0.1:2586";

/** How often the devices of a server are read again while the page shows them, so that a machine that enrolls appears. */
const devicesPollMilliseconds = 5000;

/**
 * Settings > Fleet > Server: the CodeAlta Server this computer is linked to and how it stands. Without one: create the server the
 * person runs (its address and the code `altaserver setup-code` prints: this computer becomes the owner's), or join one with an
 * enrollment code, or recover with the recovery key when every computer of the person is lost. With one: an enrollment code for
 * a machine, the devices that wait for an approval, and leaving it.
 */
export function FleetServerSettings({ epoch, api = fleetServer }: { epoch: string | null; api?: FleetServerApi }) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload, mutate } = useSettingsEditor(options => api.status({ expectedEpoch: epoch ?? "" }, options), `${epoch}`);
  const [dialog, setDialog] = useState<ServerDialogKind | null>(null);
  // Create my server…: on this computer in one go, or a server that runs already.
  const [creating, setCreating] = useState(false);
  // Join an organization…: its server, signing in, the space, the approval.
  const [joining, setJoining] = useState(false);
  // The words of a recovery key just made: shown once, until the person showed they kept them.
  const [recoveryWords, setRecoveryWords] = useState<string | null>(null);
  const [revision, setRevision] = useState(0);
  // The host says when a link or its connection changes: the page reads them again, and the devices of a server with them.
  useEffect(() => {
    if (!epoch) return;
    const abort = new AbortController();
    void (async () => {
      try { for await (const _ of await api.watch({ expectedEpoch: epoch }, { signal: abort.signal })) if (!abort.signal.aborted) { reload(); setRevision(value => value + 1); } }
      catch { /* The bridge is gone: Reload still reads the page. */ }
    })();
    return () => abort.abort();
  }, [api, epoch, reload]);
  const servers = listing?.servers ?? [];
  return <SettingsPage className="fleet-server-settings" label={t("Server")} group="Fleet" title="CodeAlta Server"
    description="A server shows the machines and sessions of all your computers in one place. It never reads their content."
    notice={notice} loading={loading} busy={busy} onReload={reload}
    actions={servers.length === 0 && listing ? <>
      <Button icon={<AppIcon name="computer" size={15} />} disabled={!epoch} onClick={() => setJoining(true)}>{t("Join an organization…")}</Button>
      <Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!epoch} onClick={() => setCreating(true)}>{t("Create my server…")}</Button>
    </> : undefined}>
    {!listing ? <SettingsUnavailable loading={loading} icon="server" title="Servers unavailable" />
      : servers.length === 0 ? <NonIdealState className="fleet-server-empty" icon={<AppIcon name="server" size={36} />} title={t("Not linked to a server.")}
        description={t("Create a server for your machines, or join the server of your organization.")} action={<Button variant="minimal" size="small" icon={<AppIcon name="shield" size={14} />} disabled={!epoch} onClick={() => setDialog("recover")}>
          {t("Recover with the recovery key…")}</Button>} />
      : <div className="fleet-server-list">
        {servers.map(server => <ServerCard key={server.id} server={server} epoch={epoch ?? ""} api={api} busy={busy} revision={revision} words={listing?.words ?? null}
          onRecoveryKey={words => { setRecoveryWords(words); reload(); }}
          onLeave={() => void mutate(() => api.leave({ expectedEpoch: epoch ?? "", linkId: server.id }, { timeoutMilliseconds: 30_000 }), undefined,
            status => status === "left_unreachable" ? { key: "The server did not answer: what this computer published there stays on it.", intent: "warning" } : undefined)} />)}
      </div>}
    {listing?.words && <p className="fleet-server-key muted-text">{t("The key of this computer:")} <code>{listing.words}</code></p>}
    {dialog && epoch && <ServerDialog kind={dialog} epoch={epoch} api={api} onClose={(linked, words) => { setDialog(null); if (words) setRecoveryWords(words); if (linked) reload(); }} />}
    {joining && epoch && <JoinOrganizationDialog epoch={epoch} onRecover={() => { setJoining(false); setDialog("recover"); }}
      onClose={joined => { setJoining(false); if (joined) reload(); }} />}
    {/* A server created here comes with the owner's recovery key: its words are shown next, once. */}
    {creating && epoch && <CreateServerDialog epoch={epoch} onExisting={() => { setCreating(false); setDialog("create"); }}
      onClose={(created, words) => { setCreating(false); if (words) setRecoveryWords(words); if (created) reload(); }} />}
    {recoveryWords && <RecoveryWordsDialog words={recoveryWords} onClose={() => setRecoveryWords(null)} />}
  </SettingsPage>;
}

/** One server: its address, organization and connection, then what this computer may do there. */
function ServerCard({ server, epoch, api, busy, revision, words, onRecoveryKey, onLeave }: {
  server: FleetServerView; epoch: string; api: FleetServerApi; busy: boolean; revision: number; words: string | null; onRecoveryKey: (words: string) => void; onLeave: () => void;
}) {
  const { t } = useShellLanguage();
  const connection = serverConnection(server);
  const [token, setToken] = useState(false);
  return <section className="fleet-server-card" data-connection={connection} aria-label={server.url}>
    <div className="fleet-server-head">
      <span className="fleet-server-icon"><AppIcon name="server" size={18} /></span>
      <div className="fleet-server-main">
        <div className="fleet-server-title"><strong>{server.organization ?? server.url}</strong>
          <Tag minimal round intent={serverConnectionIntent(connection)}>{t(serverConnectionLabel(connection))}</Tag></div>
        <div className="muted-text fleet-server-details">{[server.organization ? server.url : null,
          t(server.controller ? "This computer is the owner's" : "This computer is a member")].filter(Boolean).join(" · ")}</div>
        {server.problem && connection !== "online" && <div className="muted-text fleet-server-problem" title={server.problem}>{server.problem}</div>}
        {connection === "pending" && <div className="muted-text">{t("An owner of the server approves this computer from theirs.")}</div>}
        {connection === "pending" && keyWords(words).length > 0 && <div className="fleet-server-pending-words">
          <span className="muted-text">{t("Your other computer shows these words before it approves this one:")}</span>
          <div className="fleet-device-words">{keyWords(words).map((word, index) => <Tag key={`${index}:${word}`} minimal>{word}</Tag>)}</div>
        </div>}
      </div>
      {server.controller && <Button size="small" icon={<AppIcon name="plus" size={14} />} disabled={busy || connection === "revoked"} onClick={() => setToken(true)}>
        {t("Add a machine…")}</Button>}
      <PopoverNext placement="left" content={<div className="fleet-server-confirm">
        <p>{t("Leave {name}?", { name: server.organization ?? server.url })}</p>
        <p className="muted-text">{t("The server removes what this computer published there; its spaces are unlinked.")}</p>
        <Button className="bp6-popover-dismiss" intent="danger" disabled={busy} onClick={onLeave}>{t("Leave")}</Button></div>}>
        <Button variant="minimal" size="small" intent="danger" icon={<AppIcon name="trash" size={15} />} disabled={busy} aria-label={`${t("Leave")} ${server.url}`} title={t("Leave")} />
      </PopoverNext>
    </div>
    {server.controller && connection !== "revoked" && server.recoveryKey !== null && <ServerRecovery server={server} epoch={epoch} api={api} onMade={onRecoveryKey} />}
    {server.controller && connection !== "revoked" && <ServerDevices server={server} epoch={epoch} api={api} revision={revision} />}
    {server.controller && server.link === "approved" && connection !== "revoked" && <ServerSignIn server={server} epoch={epoch} api={api} revision={revision} />}
    {server.controller && connection !== "revoked" && <ServerTelemetry server={server} epoch={epoch} api={api} revision={revision} />}
    {server.controller && connection !== "revoked" && <ServerUpdates server={server} epoch={epoch} api={api} revision={revision} />}
    {token && <TokenDialog server={server} epoch={epoch} api={api} onClose={() => setToken(false)} />}
  </section>;
}

/** The devices of the organization: those that wait first, with the words of their key, Approve and Refuse; then the others. */
function ServerDevices({ server, epoch, api, revision }: { server: FleetServerView; epoch: string; api: FleetServerApi; revision: number }) {
  const { t } = useShellLanguage();
  const [devices, setDevices] = useState<readonly FleetDeviceView[] | null>(null);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const [acting, setActing] = useState<string | null>(null);
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    const abort = new AbortController();
    api.devices({ expectedEpoch: epoch, linkId: server.id }, { signal: abort.signal, timeoutMilliseconds: 20_000 }).then(value => {
      if (abort.signal.aborted) return;
      if (value.status === "ok") { setDevices(value.devices); setProblem(null); } else setProblem({ key: serverProblem(value.status) ?? "The operation did not complete.", detail: value.message });
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The server does not answer." }); });
    return () => abort.abort();
    // The server announces no enrollment: the list is read again with each read of the page (Reload, a change) and now and then.
  }, [api, epoch, server, revision, generation]);
  useEffect(() => {
    const timer = setInterval(() => setGeneration(value => value + 1), devicesPollMilliseconds);
    return () => clearInterval(timer);
  }, []);
  async function act(device: FleetDeviceView, call: "approve" | "revoke") {
    setActing(device.id);
    try {
      // An approval names the words the person compared: the host signs the key of those words only.
      const approving = call === "approve";
      const result = await api[call]({ expectedEpoch: epoch, linkId: server.id, deviceId: device.id, words: approving ? device.words : null, kind: approving ? device.kind : null },
        { timeoutMilliseconds: 30_000 });
      setProblem(result.status === "ok" ? null : { key: serverProblem(result.status) ?? "The operation did not complete.", detail: result.message });
    } catch { setProblem({ key: "The server does not answer." }); }
    finally { setActing(null); setGeneration(value => value + 1); }
  }
  if (!devices && !problem) return <div className="fleet-server-devices"><ActivitySpinner size={14} /></div>;
  const ordered = orderedDevices(devices ?? []);
  return <div className="fleet-server-devices">
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {ordered.length > 0 && <ul className="fleet-device-list" aria-label={t("Devices")}>
      {ordered.map(device => <li key={device.id} className="fleet-device-row" data-status={device.status}>
        <AppIcon name={device.kind === "controller" ? "computer" : "remote"} size={15} />
        <div className="fleet-device-main">
          <div className="fleet-device-title"><strong>{device.name}</strong>
            {device.status === "pending" ? <Tag minimal round intent="warning">{t("New device")}</Tag>
              : device.status === "revoked" ? <Tag minimal round>{t("Revoked")}</Tag>
              : <Tag minimal round intent={device.online ? "success" : "none"}>{t(device.online ? "Online" : "Offline")}</Tag>}</div>
        </div>
        {device.status === "pending" ? <span className="fleet-device-actions">
          <Button size="small" variant="minimal" disabled={!!acting} onClick={() => void act(device, "revoke")}>{t("Refuse")}</Button>
          {/* It gets a key only once the person saw the same words on both screens. */}
          <PopoverNext placement="left" content={<div className="fleet-server-confirm fleet-device-confirm">
            <p>{t("Does {name} show these words?", { name: device.name })}</p>
            <div className="fleet-device-words">{keyWords(device.words).map((word, index) => <Tag key={`${index}:${word}`} minimal>{word}</Tag>)}</div>
            <Button className="bp6-popover-dismiss" intent="primary" disabled={!!acting || keyWords(device.words).length === 0}
              onClick={() => void act(device, "approve")}>{t("It shows the same words")}</Button></div>}>
            <Button size="small" intent="primary" disabled={!!acting} icon={acting === device.id ? <ActivitySpinner size={12} /> : undefined}
              title={t("Approve when the machine shows the same words.")}>{t("Approve…")}</Button>
          </PopoverNext>
        </span> : device.status === "approved" && device.kind !== "controller" ? <PopoverNext placement="left" content={<div className="fleet-server-confirm">
          <p>{t("Revoke {name}?", { name: device.name })}</p>
          <Button className="bp6-popover-dismiss" intent="danger" disabled={!!acting} onClick={() => void act(device, "revoke")}>{t("Revoke")}</Button></div>}>
          <Button variant="minimal" size="small" intent="danger" icon={<AppIcon name="trash" size={14} />} disabled={!!acting} aria-label={`${t("Revoke")} ${device.name}`} title={t("Revoke")} />
        </PopoverNext> : <span />}
      </li>)}
    </ul>}
  </div>;
}

/**
 * The recovery key of this computer's person at a server, for an owner's computer: whether there is one, and Make one (or a new
 * one, which the words kept before stop working for, confirmed beside the row).
 */
function ServerRecovery({ server, epoch, api, onMade }: { server: FleetServerView; epoch: string; api: FleetServerApi; onMade: (words: string) => void }) {
  const { t } = useShellLanguage();
  const [working, setWorking] = useState(false);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  async function make() {
    setWorking(true);
    setProblem(null);
    try {
      const result = await api.createRecoveryKey({ expectedEpoch: epoch, linkId: server.id }, { timeoutMilliseconds: 30_000 });
      if (result.status === "ok" && result.words) onMade(result.words);
      else setProblem({ key: serverProblem(result.status) ?? "The operation did not complete.", detail: result.message });
    } catch { setProblem({ key: "The server does not answer." }); }
    finally { setWorking(false); }
  }
  const kept = server.recoveryKey === true;
  return <div className="fleet-server-recovery" data-kept={kept}>
    <div className="fleet-server-recovery-row">
      <AppIcon name="shield" size={14} />
      <span>{t("Recovery key")}</span>
      <Tag minimal round intent={kept ? "success" : "warning"}>{t(kept ? "Kept" : "None")}</Tag>
      <span className="fleet-server-recovery-spacer" />
      {kept ? <PopoverNext placement="left" content={<div className="fleet-server-confirm">
        <p>{t("Make a new recovery key?")}</p>
        <p className="muted-text">{t("The words kept now stop working.")}</p>
        <Button className="bp6-popover-dismiss" intent="danger" disabled={working} onClick={() => void make()}>{t("Make a new one")}</Button></div>}>
        <Button size="small" variant="minimal" disabled={working} icon={working ? <ActivitySpinner size={12} /> : undefined}>{t("New recovery key…")}</Button>
      </PopoverNext>
        : <Button size="small" intent="primary" disabled={working} icon={working ? <ActivitySpinner size={12} /> : undefined} onClick={() => void make()}>
          {t("Make a recovery key…")}</Button>}
    </div>
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
  </div>;
}

/**
 * The words of a recovery key, shown once: Copy, then two of them typed back to show the person kept them. The dialog closes only
 * then, so that the words are not lost by a click outside it.
 */
function RecoveryWordsDialog({ words, onClose }: { words: string; onClose: () => void }) {
  const { t } = useShellLanguage();
  const list = keyWords(words);
  const [positions] = useState(() => recoveryCheckPositions(list.length));
  const [checking, setChecking] = useState(false);
  const [answers, setAnswers] = useState(["", ""]);
  const [copied, setCopied] = useState(false);
  const passes = recoveryCheckPasses(list, positions, answers);
  return <Dialog isOpen className="fleet-recovery-dialog" title={t("Recovery key")} canOutsideClickClose={false} canEscapeKeyClose={false} isCloseButtonShown={false}>
    <DialogBody>
      {!checking ? <>
        <p>{t("Write these words down: they are shown once.")}</p>
        <ol className="fleet-recovery-words" aria-label={t("Recovery key")}>{list.map((word, index) => <li key={`${index}:${word}`}><Tag minimal>{word}</Tag></li>)}</ol>
        <Button size="small" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />} text={t(copied ? "Copied" : "Copy")}
          onClick={() => void navigator.clipboard?.writeText(words).then(() => setCopied(true), () => setCopied(false))} />
      </> : <form className="fleet-server-form fleet-recovery-check" onSubmit={event => { event.preventDefault(); if (passes) onClose(); }}>
        {positions.map((position, index) => <label key={position}><span>{t("Word {n}", { n: position })}</span>
          <InputGroup autoFocus={index === 0} value={answers[index]} spellCheck={false} autoComplete="off"
            intent={answers[index].trim() && !recoveryCheckPasses(list, [position], [answers[index]]) ? "danger" : undefined}
            onChange={event => { const value = event.target.value; setAnswers(current => current.map((answer, at) => at === index ? value : answer)); }} /></label>)}
        <button type="submit" hidden />
      </form>}
    </DialogBody>
    <DialogFooter actions={!checking ? <Button intent="primary" text={t("I wrote them down")} onClick={() => setChecking(true)} /> : <>
      <Button text={t("Back")} onClick={() => { setChecking(false); setAnswers(["", ""]); }} />
      <Button intent="primary" disabled={!passes} text={t("Done")} onClick={onClose} />
    </>} />
  </Dialog>;
}

const signalLabels: Record<TelemetrySignal, MessageKey> = { traces: "Traces", metrics: "Metrics", logs: "Logs" };

/**
 * The telemetry of the fleet, for an owner's computer: off, or what the machines send and where. A computer that may not read it
 * (a member's) shows nothing.
 */
function ServerTelemetry({ server, epoch, api, revision }: { server: FleetServerView; epoch: string; api: FleetServerApi; revision: number }) {
  const { t } = useShellLanguage();
  const [view, setView] = useState<FleetTelemetryView | null>(null);
  const [editing, setEditing] = useState(false);
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    const abort = new AbortController();
    api.telemetry({ expectedEpoch: epoch, linkId: server.id }, { signal: abort.signal, timeoutMilliseconds: 20_000 }).then(value => {
      if (!abort.signal.aborted) setView(value.status === "ok" ? value.telemetry : null);
    }, () => { if (!abort.signal.aborted) setView(null); });
    return () => abort.abort();
  }, [api, epoch, server.id, revision, generation]);
  if (!view) return null;
  const summary = telemetrySummary(view);
  return <div className="fleet-server-telemetry">
    <AppIcon name="usage" size={15} />
    <div className="fleet-telemetry-main">
      <strong>{t("Telemetry")}</strong>
      <span className="muted-text fleet-telemetry-summary" title={summary.detail ?? undefined}>{t(summary.key)}{summary.detail ? ` · ${summary.detail}` : ""}</span>
    </div>
    <Button size="small" variant="minimal" onClick={() => setEditing(true)}>{t("Change…")}</Button>
    {editing && <TelemetryDialog server={server} epoch={epoch} api={api} view={view} onClose={changed => { setEditing(false); if (changed) setGeneration(value => value + 1); }} />}
  </div>;
}

/** The form of the telemetry of the fleet: the collector, how the machines reach it, the signals, its headers (masked when held) and the environment. */
function TelemetryDialog({ server, epoch, api, view, onClose }: {
  server: FleetServerView; epoch: string; api: Pick<FleetServerApi, "setTelemetry">; view: FleetTelemetryView; onClose: (changed: boolean) => void;
}) {
  const { t } = useShellLanguage();
  const [draft, setDraft] = useState<TelemetryDraft>(() => telemetryDraft(view));
  const [working, setWorking] = useState(false);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const invalid = telemetryProblem(draft);
  const update = (change: Partial<TelemetryDraft>) => setDraft(value => ({ ...value, ...change }));
  const updateHeader = (index: number, change: Partial<TelemetryDraft["headers"][number]>) =>
    setDraft(value => ({ ...value, headers: value.headers.map((header, at) => at === index ? { ...header, ...change } : header) }));
  async function save() {
    if (invalid) return;
    setWorking(true);
    setProblem(null);
    try {
      const result = await api.setTelemetry({ expectedEpoch: epoch, linkId: server.id, enabled: draft.enabled, endpoint: serverAddressOf(draft.endpoint) || null,
        protocol: draft.protocol, route: draft.route, signals: [...draft.signals], headers: telemetryHeaderEdits(draft), environment: draft.environment.trim() || null },
        { timeoutMilliseconds: 60_000 });
      if (result.status === "ok") { onClose(true); return; }
      setProblem({ key: serverProblem(result.status) ?? "The operation did not complete.", detail: result.message });
    } catch {
      setProblem({ key: "The server does not answer." });
    } finally { setWorking(false); }
  }
  return <Dialog isOpen className="fleet-telemetry-dialog" title={t("Telemetry of the fleet")} canOutsideClickClose={false} onClose={() => { if (!working) onClose(false); }}>
    <DialogBody>
      <form className="fleet-server-form" onSubmit={event => { event.preventDefault(); void save(); }}>
        <Switch checked={draft.enabled} disabled={working} label={t("Send to a collector")} onChange={event => update({ enabled: event.currentTarget.checked })} />
        <label><span>{t("Collector")}</span>
          <InputGroup value={draft.endpoint} disabled={working} placeholder="https://otlp.example:4318" spellCheck={false}
            intent={draft.endpoint.trim() && !serverAddressOf(draft.endpoint) ? "danger" : undefined} onChange={event => update({ endpoint: event.target.value })} /></label>
        <div className="fleet-telemetry-choices">
          <label><span>{t("Protocol")}</span>
            <HTMLSelect value={draft.protocol} disabled={working} options={[{ value: "http/protobuf", label: "OTLP/HTTP" }, { value: "grpc", label: "OTLP/gRPC" }]}
              onChange={event => update({ protocol: event.currentTarget.value === "grpc" ? "grpc" : "http/protobuf" })} /></label>
          <label><span>{t("Route")}</span>
            <HTMLSelect value={draft.route} disabled={working} options={[{ value: "direct", label: t("Each machine sends to the collector") },
              { value: "server", label: t("Machines send through this server") }]}
              onChange={event => update({ route: event.currentTarget.value === "server" ? "server" : "direct" })} /></label>
        </div>
        <div className="fleet-telemetry-signals" role="group" aria-label={t("Signals")}><span>{t("Signals")}</span>
          {telemetrySignals.map(signal => <Checkbox key={signal} inline disabled={working} label={t(signalLabels[signal])} checked={draft.signals.includes(signal)}
            onChange={event => update({ signals: telemetrySignals.filter(item => item === signal ? event.currentTarget.checked : draft.signals.includes(item)) })} />)}
        </div>
        <div className="fleet-telemetry-headers" role="group" aria-label={t("Headers")}><span>{t("Headers")}</span>
          {draft.headers.map((header, index) => <div className="fleet-telemetry-header" key={index}>
            <InputGroup aria-label={t("Name")} placeholder={t("Name")} value={header.name} disabled={working} spellCheck={false}
              onChange={event => updateHeader(index, { name: event.target.value })} />
            <InputGroup aria-label={t("Value")} type="password" autoComplete="off" placeholder={header.masked ?? t("Value")} value={header.value} disabled={working}
              onChange={event => updateHeader(index, { value: event.target.value })} />
            <Button variant="minimal" icon={<AppIcon name="trash" size={14} />} aria-label={t("Remove")} title={t("Remove")} disabled={working}
              onClick={() => update({ headers: draft.headers.filter((_, at) => at !== index) })} />
          </div>)}
          <Button size="small" variant="minimal" icon={<AppIcon name="plus" size={14} />} disabled={working || draft.headers.length >= 16}
            onClick={() => update({ headers: [...draft.headers, { name: "", value: "", masked: null }] })}>{t("Add a header")}</Button>
        </div>
        <label><span>{t("Environment")}</span>
          <InputGroup value={draft.environment} disabled={working} placeholder="production" spellCheck={false} onChange={event => update({ environment: event.target.value })} /></label>
        <button type="submit" hidden />
      </form>
      {(problem ?? (invalid ? { key: invalid } : null)) && <Callout compact intent="warning" role="alert">{t((problem ?? { key: invalid! }).key)}
        {problem?.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={working} text={t("Cancel")} onClick={() => onClose(false)} />
      <Button intent="primary" disabled={!!invalid || working} icon={working ? <ActivitySpinner size={14} /> : undefined} text={t("Save")} onClick={() => void save()} />
    </>} />
  </Dialog>;
}

/**
 * The updates of the fleet, for an owner's computer: the version of the server and of each machine, the policy, Update the fleet…
 * and the last campaign with each machine's state. Nodes update first, then the server; the host says when it moves on.
 */
function ServerUpdates({ server, epoch, api, revision }: { server: FleetServerView; epoch: string; api: FleetServerApi; revision: number }) {
  const { t } = useShellLanguage();
  const [view, setView] = useState<FleetUpdatesView | null>(null);
  const [dialog, setDialog] = useState<"update" | "policy" | null>(null);
  const [generation, setGeneration] = useState(0);
  const [cancelling, setCancelling] = useState(false);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  useEffect(() => {
    const abort = new AbortController();
    api.updates({ expectedEpoch: epoch, linkId: server.id }, { signal: abort.signal, timeoutMilliseconds: 20_000 }).then(value => {
      if (!abort.signal.aborted) setView(value.status === "ok" ? value.updates : null);
    }, () => { if (!abort.signal.aborted) setView(null); });
    return () => abort.abort();
  }, [api, epoch, server.id, revision, generation]);
  if (!view) return null;
  const campaign = view.campaigns[0] ?? null;
  const policy = policySummary(view.policy);
  async function cancel(target: FleetUpdateCampaignView) {
    setCancelling(true);
    setProblem(null);
    try {
      const result = await api.cancelUpdate({ expectedEpoch: epoch, linkId: server.id, campaignId: target.id }, { timeoutMilliseconds: 30_000 });
      if (result.status !== "ok") setProblem({ key: updateProblem(result.status) ?? "The operation did not complete.", detail: result.message });
      setGeneration(value => value + 1);
    } catch {
      setProblem({ key: "The server does not answer." });
    } finally { setCancelling(false); }
  }
  return <div className="fleet-server-updates">
    <div className="fleet-updates-head">
      <AppIcon name="download" size={15} />
      <div className="fleet-telemetry-main">
        <strong>{t("Updates")}</strong>
        <span className="muted-text fleet-updates-summary">{t("Server {version}", { version: view.serverVersion })}
          {view.serverPending ? ` → ${view.serverPending}` : ""} · {t(policy.key)}{policy.detail ? ` · ${policy.detail}` : ""}</span>
      </div>
      <Button size="small" variant="minimal" onClick={() => setDialog("policy")}>{t("Policy…")}</Button>
      <Button size="small" icon={<AppIcon name="download" size={14} />} disabled={!!campaign && canCancelCampaign(campaign)} onClick={() => setDialog("update")}>
        {t("Update the fleet…")}</Button>
    </div>
    {campaign && <CampaignProgress campaign={campaign} cancelling={cancelling} onCancel={() => void cancel(campaign)} />}
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {dialog === "update" && <UpdateDialog server={server} epoch={epoch} api={api} view={view}
      onClose={changed => { setDialog(null); if (changed) setGeneration(value => value + 1); }} />}
    {dialog === "policy" && <PolicyDialog server={server} epoch={epoch} api={api} view={view}
      onClose={changed => { setDialog(null); if (changed) setGeneration(value => value + 1); }} />}
  </div>;
}

/** The last campaign: its version, its state, when it starts, and each machine with its state; Cancel before the server's step. */
function CampaignProgress({ campaign, cancelling, onCancel }: { campaign: FleetUpdateCampaignView; cancelling: boolean; onCancel: () => void }) {
  const { t } = useShellLanguage();
  const progress = campaignProgress(campaign);
  const start = campaign.notBefore ? new Date(campaign.notBefore) : null;
  return <div className="fleet-campaign" data-state={campaign.state}>
    <div className="fleet-campaign-head">
      <span>{t("CodeAlta {version}", { version: campaign.targetVersion })}</span>
      <Tag minimal round intent={campaignStateIntent(campaign.state)}>{t(campaignStateLabel(campaign.state))}</Tag>
      <span className="muted-text">{t("{updated} of {total} machines", { updated: String(progress.updated), total: String(progress.total) })}</span>
      {start && campaign.state === "scheduled" && <span className="muted-text fleet-campaign-start">
        {t("At {time}", { time: start.toLocaleString() })}{campaign.timeZone ? ` (${campaign.timeZone})` : ""}</span>}
      {canCancelCampaign(campaign) && <Button size="small" variant="minimal" intent="danger" disabled={cancelling} onClick={onCancel}>{t("Cancel")}</Button>}
    </div>
    <ul className="fleet-campaign-machines">
      <li key="server"><span>{t("This server")}</span><Tag minimal intent={machineStateIntent(campaign.serverState)}>{t(machineStateLabel(campaign.serverState))}</Tag>
        {campaign.serverMessage && <span className="muted-text" title={campaign.serverMessage}>{campaign.serverMessage}</span>}</li>
      {campaign.machines.map(machine => <li key={machine.id} data-machine={machine.id}>
        <span>{machine.name}</span>
        <Tag minimal intent={machineStateIntent(machine.state)}>{t(machineStateLabel(machine.state))}</Tag>
        {!machine.online && machine.state !== "updated" && <span className="muted-text">{t("Offline")}</span>}
        {machine.running && <span className="muted-text">{machine.running}</span>}
        {machine.message && <span className="muted-text fleet-campaign-message" title={machine.message}>{machine.message}</span>}
      </li>)}
    </ul>
  </div>;
}

/** Update the fleet: a version (the latest by default), its feed, now or at a time of this computer, and the machines (all by default). */
function UpdateDialog({ server, epoch, api, view, onClose }: {
  server: FleetServerView; epoch: string; api: Pick<FleetServerApi, "startUpdate">; view: FleetUpdatesView; onClose: (changed: boolean) => void;
}) {
  const { t } = useShellLanguage();
  const [draft, setDraft] = useState<UpdateDraft>(() => ({ ...emptyUpdateDraft, feed: view.policy.feed ?? "" }));
  const [working, setWorking] = useState(false);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const invalid = updateDraftProblem(draft);
  const update = (change: Partial<UpdateDraft>) => setDraft(value => ({ ...value, ...change }));
  const zone = localTimeZone();
  const chosen = (id: string) => draft.machines === null || draft.machines.includes(id);
  async function start() {
    if (invalid) return;
    setWorking(true);
    setProblem(null);
    try {
      const result = await api.startUpdate({ expectedEpoch: epoch, linkId: server.id, version: draft.version.trim() || null, feed: draft.feed.trim() || null,
        notBefore: updateStart(draft), timeZone: draft.when === "at" ? zone : null, machines: draft.machines ? [...draft.machines] : null, rollback: false },
        { timeoutMilliseconds: 180_000 });
      if (result.status === "ok") { onClose(true); return; }
      setProblem({ key: updateProblem(result.status) ?? "The operation did not complete.", detail: result.message });
    } catch {
      setProblem({ key: "The server does not answer." });
    } finally { setWorking(false); }
  }
  return <Dialog isOpen className="fleet-update-dialog" title={t("Update the fleet")} canOutsideClickClose={false} onClose={() => { if (!working) onClose(false); }}>
    <DialogBody>
      <p className="muted-text">{t("The machines update first, a few at a time, then the server. This computer signs what they install.")}</p>
      <form className="fleet-server-form" onSubmit={event => { event.preventDefault(); void start(); }}>
        <label><span>{t("Version")}</span>
          <InputGroup value={draft.version} disabled={working} placeholder={t("The latest")} spellCheck={false} onChange={event => update({ version: event.target.value })} /></label>
        <label><span>{t("Feed")}</span>
          <InputGroup value={draft.feed} disabled={working} placeholder="nuget.org" spellCheck={false} onChange={event => update({ feed: event.target.value })} /></label>
        <div className="fleet-telemetry-choices">
          <label><span>{t("When")}</span>
            <HTMLSelect value={draft.when} disabled={working} options={[{ value: "now", label: t("Now") }, { value: "at", label: t("At a time") }]}
              onChange={event => update({ when: event.currentTarget.value === "at" ? "at" : "now" })} /></label>
          {draft.when === "at" && <label><span>{t("Time ({zone})", { zone })}</span>
            <input className="bp6-input" type="datetime-local" value={draft.at} disabled={working} onChange={event => update({ at: event.target.value })} /></label>}
        </div>
        <div className="fleet-update-machines" role="group" aria-label={t("Machines")}><span>{t("Machines")}</span>
          {view.machines.map(machine => <Checkbox key={machine.id} disabled={working} checked={chosen(machine.id)}
            labelElement={<span>{machine.name} <span className="muted-text">{machine.version ?? "?"}{machine.online ? "" : ` · ${t("Offline")}`}</span></span>}
            onChange={event => {
              const next = view.machines.filter(item => item.id === machine.id ? event.currentTarget.checked : chosen(item.id)).map(item => item.id);
              update({ machines: next.length === view.machines.length ? null : next });
            }} />)}
          {view.machines.length === 0 && <span className="muted-text">{t("No machine yet: the server updates itself.")}</span>}
        </div>
        <button type="submit" hidden />
      </form>
      {(problem ?? (invalid ? { key: invalid } : null)) && <Callout compact intent="warning" role="alert">{t((problem ?? { key: invalid! }).key)}
        {problem?.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={working} text={t("Cancel")} onClick={() => onClose(false)} />
      <Button intent="primary" disabled={!!invalid || working} icon={working ? <ActivitySpinner size={14} /> : undefined} text={t("Update")} onClick={() => void start()} />
    </>} />
  </Dialog>;
}

/** The update policy of the fleet: manual (campaigns only), pinned to a version, or automatic at a daily maintenance window. */
function PolicyDialog({ server, epoch, api, view, onClose }: {
  server: FleetServerView; epoch: string; api: Pick<FleetServerApi, "setUpdatePolicy">; view: FleetUpdatesView; onClose: (changed: boolean) => void;
}) {
  const { t } = useShellLanguage();
  const [draft, setDraft] = useState<PolicyDraft>(() => policyDraft(view.policy));
  const [working, setWorking] = useState(false);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const invalid = policyDraftProblem(draft);
  const update = (change: Partial<PolicyDraft>) => setDraft(value => ({ ...value, ...change }));
  async function save() {
    if (invalid) return;
    setWorking(true);
    setProblem(null);
    try {
      const result = await api.setUpdatePolicy({ expectedEpoch: epoch, linkId: server.id, mode: draft.mode, pinnedVersion: draft.pinnedVersion.trim() || null,
        feed: draft.feed.trim() || null, window: draft.window || null, timeZone: draft.timeZone || null }, { timeoutMilliseconds: 60_000 });
      if (result.status === "ok") { onClose(true); return; }
      setProblem({ key: updateProblem(result.status) ?? "The operation did not complete.", detail: result.message });
    } catch {
      setProblem({ key: "The server does not answer." });
    } finally { setWorking(false); }
  }
  return <Dialog isOpen className="fleet-policy-dialog" title={t("Update policy")} canOutsideClickClose={false} onClose={() => { if (!working) onClose(false); }}>
    <DialogBody>
      <form className="fleet-server-form" onSubmit={event => { event.preventDefault(); void save(); }}>
        <label><span>{t("Policy")}</span>
          <HTMLSelect value={draft.mode} disabled={working} options={[{ value: "manual", label: t("Manual: when I update the fleet") },
            { value: "pinned", label: t("Pinned: stay on one version") }, { value: "automatic", label: t("Automatic: the latest stable version") }]}
            onChange={event => update({ mode: event.currentTarget.value === "pinned" ? "pinned" : event.currentTarget.value === "automatic" ? "automatic" : "manual" })} /></label>
        {draft.mode === "pinned" && <label><span>{t("Version")}</span>
          <InputGroup value={draft.pinnedVersion} disabled={working} placeholder={view.serverVersion} spellCheck={false}
            onChange={event => update({ pinnedVersion: event.target.value })} /></label>}
        {draft.mode === "automatic" && <>
          <label><span>{t("Feed")}</span>
            <InputGroup value={draft.feed} disabled={working} placeholder="nuget.org" spellCheck={false} onChange={event => update({ feed: event.target.value })} /></label>
          <label><span>{t("Maintenance window ({zone})", { zone: draft.timeZone })}</span>
            <input className="bp6-input" type="time" value={draft.window} disabled={working} onChange={event => update({ window: event.target.value })} /></label>
          <p className="muted-text">{t("The server reads the feed each day and updates the fleet at this time. Machines check the feed's hash of what they install.")}</p>
        </>}
        <button type="submit" hidden />
      </form>
      {(problem ?? (invalid ? { key: invalid } : null)) && <Callout compact intent="warning" role="alert">{t((problem ?? { key: invalid! }).key)}
        {problem?.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={working} text={t("Cancel")} onClick={() => onClose(false)} />
      <Button intent="primary" disabled={!!invalid || working} icon={working ? <ActivitySpinner size={14} /> : undefined} text={t("Save")} onClick={() => void save()} />
    </>} />
  </Dialog>;
}

/** An enrollment code for a machine, made when asked and shown once, with the command that joins with it. */
function TokenDialog({ server, epoch, api, onClose }: { server: FleetServerView; epoch: string; api: FleetServerApi; onClose: () => void }) {
  const { t } = useShellLanguage();
  const [made, setMade] = useState<{ token: string; expiresAt: string | null } | null>(null);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const [copied, setCopied] = useState(false);
  useEffect(() => {
    const abort = new AbortController();
    api.createToken({ expectedEpoch: epoch, linkId: server.id, hours: 1, label: null }, { signal: abort.signal, timeoutMilliseconds: 30_000 }).then(value => {
      if (abort.signal.aborted) return;
      if (value.status === "ok" && value.token) setMade({ token: value.token, expiresAt: value.expiresAt });
      else setProblem({ key: serverProblem(value.status) ?? "The operation did not complete.", detail: value.message });
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The server does not answer." }); });
    return () => abort.abort();
  }, [api, epoch, server.id]);
  const command = made ? joinCommand(server.url, made.token) : "";
  return <Dialog isOpen className="fleet-token-dialog" title={t("Add a machine")} onClose={onClose}>
    <DialogBody>
      {!made && !problem && <ActivitySpinner size={16} />}
      {made && <>
        <p>{t("Run this on the machine; it then waits for your approval here:")}</p>
        <InputGroup readOnly fill value={command} aria-label={t("Command")} onFocus={event => event.currentTarget.select()}
          rightElement={<Button variant="minimal" icon={<AppIcon name={copied ? "check" : "copy"} size={14} />} aria-label={t("Copy")} title={t("Copy")}
            onClick={() => void navigator.clipboard?.writeText(command).then(() => setCopied(true), () => setCopied(false))} />} />
        <p className="muted-text">{t("The code works once, for an hour, and is not shown again.")}</p>
      </>}
      {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<Button intent="primary" text={t("Done")} onClick={onClose} />} />
  </Dialog>;
}

/** What the server dialog does: create a server that runs already, join one with an enrollment code, or recover with the recovery key. */
type ServerDialogKind = "create" | "join" | "recover";

/**
 * Create my server (address, setup code, the owner's name), Join a server (address, enrollment code), or Recover with the
 * recovery key (address, its 32 words): this computer becomes one of the person's, with their keys, when every other is lost.
 */
function ServerDialog({ kind, epoch, api, onClose }: {
  kind: ServerDialogKind; epoch: string; api: Pick<FleetServerApi, "create" | "join" | "recover">; onClose: (linked: boolean, recoveryWords?: string | null) => void;
}) {
  const { t } = useShellLanguage();
  const [url, setUrl] = useState(kind === "create" ? localServerAddress : "");
  const [code, setCode] = useState("");
  const [owner, setOwner] = useState("");
  const [organization, setOrganization] = useState("");
  const [working, setWorking] = useState(false);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const address = serverAddressOf(url);
  const typedWords = keyWords(code).length;
  const ready = !!address && !!code.trim() && (kind === "join" || kind === "create" && !!owner.trim() || kind === "recover" && typedWords === recoveryWordCount);
  async function submit() {
    if (!ready) return;
    setWorking(true);
    setProblem(null);
    try {
      const result = kind === "create"
        ? await api.create({ expectedEpoch: epoch, url: address, code: code.trim(), ownerName: owner.trim(), organizationName: organization.trim() || null }, { timeoutMilliseconds: 60_000 })
        : kind === "recover" ? await api.recover({ expectedEpoch: epoch, url: address, words: keyWords(code).join(" ") }, { timeoutMilliseconds: 60_000 })
        : await api.join({ expectedEpoch: epoch, url: address, token: code.trim() }, { timeoutMilliseconds: 60_000 });
      const failed = serverProblem(result.status);
      // A new server comes with the owner's recovery key, whose words the page shows next, once.
      if (!failed) { onClose(true, result.recoveryWords); return; }
      setProblem({ key: failed, detail: result.message });
    } catch {
      setProblem({ key: "The server does not answer." });
    } finally { setWorking(false); }
  }
  return <Dialog isOpen className="fleet-server-dialog" data-kind={kind}
    title={t(kind === "create" ? "Create my server" : kind === "recover" ? "Recover with the recovery key" : "Join a server")} canOutsideClickClose={false}
    onClose={() => { if (!working) onClose(false); }}>
    <DialogBody>
      {kind === "recover" && <p className="muted-text">{t("When every computer that held your keys is lost, the 32 words of your recovery key make this computer one of yours again, with your keys.")}</p>}
      <form className="fleet-server-form" onSubmit={event => { event.preventDefault(); void submit(); }}>
        <label><span>{t("Address")}</span>
          <InputGroup autoFocus={kind !== "create"} value={url} disabled={working} placeholder="https://fleet.example" spellCheck={false}
            intent={url.trim() && !address ? "danger" : undefined} onChange={event => setUrl(event.target.value)} /></label>
        {kind === "recover" ? <label><span>{t("Recovery key")}</span>
          <TextArea fill value={code} disabled={working} rows={4} spellCheck={false} autoComplete="off" aria-label={t("Recovery key")}
            intent={typedWords > recoveryWordCount ? "danger" : undefined} onChange={event => setCode(event.target.value)} />
          <small className="muted-text">{t("{count} of {total} words", { count: typedWords, total: recoveryWordCount })}</small></label>
        : <label><span>{t(kind === "create" ? "Setup code" : "Enrollment code")}</span>
          <InputGroup autoFocus={kind === "create"} value={code} disabled={working} spellCheck={false} autoComplete="off" onChange={event => setCode(event.target.value)} />
          <small className="muted-text">{t(kind === "create" ? "altaserver setup-code prints it on the computer the server runs on."
            : "An owner of the server makes it; this computer then waits for their approval.")}</small></label>}
        {kind === "create" && <>
          <label><span>{t("Your name")}</span>
            <InputGroup value={owner} disabled={working} onChange={event => setOwner(event.target.value)} /></label>
          <label><span>{t("Organization")}</span>
            <InputGroup value={organization} disabled={working} placeholder={owner.trim() || undefined} onChange={event => setOrganization(event.target.value)} /></label>
        </>}
        <button type="submit" hidden />
      </form>
      {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    </DialogBody>
    <DialogFooter actions={<>
      <Button disabled={working} text={t("Cancel")} onClick={() => onClose(false)} />
      <Button intent="primary" disabled={!ready || working} icon={working ? <ActivitySpinner size={14} /> : undefined}
        text={t(kind === "create" ? "Create" : kind === "recover" ? "Recover" : "Join")} onClick={() => void submit()} />
    </>} />
  </Dialog>;
}
