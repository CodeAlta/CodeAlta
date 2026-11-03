import { useEffect, useState } from "react";
import { Button, Callout, Dialog, DialogBody, DialogFooter, InputGroup, NonIdealState, PopoverNext, Tag } from "@blueprintjs/core";
import { fleetServer, type FleetDeviceView, type FleetServerView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { joinCommand, keyWords, orderedDevices, serverAddressOf, serverConnection, serverConnectionIntent, serverConnectionLabel, serverProblem } from "./fleetServer";

export type FleetServerApi = Pick<typeof fleetServer, "status" | "create" | "join" | "leave" | "createToken" | "devices" | "approve" | "revoke" | "watch">;

/** The address a server the person runs on this computer listens on, as `altaserver run` says it. */
export const localServerAddress = "http://127.0.0.1:2586";

/** How often the devices of a server are read again while the page shows them, so that a machine that enrolls appears. */
const devicesPollMilliseconds = 5000;

/**
 * Settings > Fleet > Server: the CodeAlta Server this computer is linked to and how it stands. Without one: create the server the
 * person runs (its address and the code `altaserver setup-code` prints: this computer becomes the owner's), or join one with an
 * enrollment code. With one: an enrollment code for a machine, the devices that wait for an approval, and leaving it.
 */
export function FleetServerSettings({ epoch, api = fleetServer }: { epoch: string | null; api?: FleetServerApi }) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload, mutate } = useSettingsEditor(options => api.status({ expectedEpoch: epoch ?? "" }, options), `${epoch}`);
  const [dialog, setDialog] = useState<"create" | "join" | null>(null);
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
      <Button icon={<AppIcon name="computer" size={15} />} disabled={!epoch} onClick={() => setDialog("join")}>{t("Join a server…")}</Button>
      <Button intent="primary" icon={<AppIcon name="plus" size={15} />} disabled={!epoch} onClick={() => setDialog("create")}>{t("Create my server…")}</Button>
    </> : undefined}>
    {!listing ? <SettingsUnavailable loading={loading} icon="server" title="Servers unavailable" />
      : servers.length === 0 ? <NonIdealState className="fleet-server-empty" icon={<AppIcon name="server" size={36} />} title={t("Not linked to a server.")} />
      : <div className="fleet-server-list">
        {servers.map(server => <ServerCard key={server.id} server={server} epoch={epoch ?? ""} api={api} busy={busy} revision={revision}
          onLeave={() => void mutate(() => api.leave({ expectedEpoch: epoch ?? "", linkId: server.id }, { timeoutMilliseconds: 30_000 }))} />)}
      </div>}
    {listing?.words && <p className="fleet-server-key muted-text">{t("The key of this computer:")} <code>{listing.words}</code></p>}
    {dialog && epoch && <ServerDialog kind={dialog} epoch={epoch} api={api} onClose={linked => { setDialog(null); if (linked) reload(); }} />}
  </SettingsPage>;
}

/** One server: its address, organization and connection, then what this computer may do there. */
function ServerCard({ server, epoch, api, busy, revision, onLeave }: {
  server: FleetServerView; epoch: string; api: FleetServerApi; busy: boolean; revision: number; onLeave: () => void;
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
      </div>
      {server.controller && <Button size="small" icon={<AppIcon name="plus" size={14} />} disabled={busy || connection === "revoked"} onClick={() => setToken(true)}>
        {t("Add a machine…")}</Button>}
      <PopoverNext placement="left" content={<div className="fleet-server-confirm">
        <p>{t("Leave {name}?", { name: server.organization ?? server.url })}</p>
        <p className="muted-text">{t("This computer stops publishing there; its spaces are unlinked.")}</p>
        <Button className="bp6-popover-dismiss" intent="danger" disabled={busy} onClick={onLeave}>{t("Leave")}</Button></div>}>
        <Button variant="minimal" size="small" intent="danger" icon={<AppIcon name="trash" size={15} />} disabled={busy} aria-label={`${t("Leave")} ${server.url}`} title={t("Leave")} />
      </PopoverNext>
    </div>
    {server.controller && connection !== "revoked" && <ServerDevices server={server} epoch={epoch} api={api} revision={revision} />}
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
      const result = await api[call]({ expectedEpoch: epoch, linkId: server.id, deviceId: device.id }, { timeoutMilliseconds: 30_000 });
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
            {device.status === "pending" ? <Tag minimal round intent="warning">{t("Waiting for approval")}</Tag>
              : device.status === "revoked" ? <Tag minimal round>{t("Revoked")}</Tag>
              : <Tag minimal round intent={device.online ? "success" : "none"}>{t(device.online ? "Online" : "Offline")}</Tag>}</div>
          {device.status === "pending" && <div className="fleet-device-words">{keyWords(device.words).map((word, index) => <Tag key={`${index}:${word}`} minimal>{word}</Tag>)}</div>}
        </div>
        {device.status === "pending" ? <span className="fleet-device-actions">
          <Button size="small" variant="minimal" disabled={!!acting} onClick={() => void act(device, "revoke")}>{t("Refuse")}</Button>
          <Button size="small" intent="primary" disabled={!!acting} icon={acting === device.id ? <ActivitySpinner size={12} /> : undefined}
            title={t("Approve when the machine shows the same words.")} onClick={() => void act(device, "approve")}>{t("Approve")}</Button>
        </span> : device.status === "approved" && device.kind !== "controller" ? <PopoverNext placement="left" content={<div className="fleet-server-confirm">
          <p>{t("Revoke {name}?", { name: device.name })}</p>
          <Button className="bp6-popover-dismiss" intent="danger" disabled={!!acting} onClick={() => void act(device, "revoke")}>{t("Revoke")}</Button></div>}>
          <Button variant="minimal" size="small" intent="danger" icon={<AppIcon name="trash" size={14} />} disabled={!!acting} aria-label={`${t("Revoke")} ${device.name}`} title={t("Revoke")} />
        </PopoverNext> : <span />}
      </li>)}
    </ul>}
  </div>;
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

/** Create my server (address, setup code, the owner's name) or Join a server (address, enrollment code). */
function ServerDialog({ kind, epoch, api, onClose }: { kind: "create" | "join"; epoch: string; api: Pick<FleetServerApi, "create" | "join">; onClose: (linked: boolean) => void }) {
  const { t } = useShellLanguage();
  const [url, setUrl] = useState(kind === "create" ? localServerAddress : "");
  const [code, setCode] = useState("");
  const [owner, setOwner] = useState("");
  const [organization, setOrganization] = useState("");
  const [working, setWorking] = useState(false);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const address = serverAddressOf(url);
  const ready = !!address && !!code.trim() && (kind === "join" || !!owner.trim());
  async function submit() {
    if (!ready) return;
    setWorking(true);
    setProblem(null);
    try {
      const result = kind === "create"
        ? await api.create({ expectedEpoch: epoch, url: address, code: code.trim(), ownerName: owner.trim(), organizationName: organization.trim() || null }, { timeoutMilliseconds: 60_000 })
        : await api.join({ expectedEpoch: epoch, url: address, token: code.trim() }, { timeoutMilliseconds: 60_000 });
      const failed = serverProblem(result.status);
      if (!failed) { onClose(true); return; }
      setProblem({ key: failed, detail: result.message });
    } catch {
      setProblem({ key: "The server does not answer." });
    } finally { setWorking(false); }
  }
  return <Dialog isOpen className="fleet-server-dialog" title={t(kind === "create" ? "Create my server" : "Join a server")} canOutsideClickClose={false}
    onClose={() => { if (!working) onClose(false); }}>
    <DialogBody>
      <form className="fleet-server-form" onSubmit={event => { event.preventDefault(); void submit(); }}>
        <label><span>{t("Address")}</span>
          <InputGroup autoFocus={kind === "join"} value={url} disabled={working} placeholder="https://fleet.example" spellCheck={false}
            intent={url.trim() && !address ? "danger" : undefined} onChange={event => setUrl(event.target.value)} /></label>
        <label><span>{t(kind === "create" ? "Setup code" : "Enrollment code")}</span>
          <InputGroup autoFocus={kind === "create"} value={code} disabled={working} spellCheck={false} autoComplete="off" onChange={event => setCode(event.target.value)} />
          <small className="muted-text">{t(kind === "create" ? "altaserver setup-code prints it on the computer the server runs on."
            : "An owner of the server makes it; this computer then waits for their approval.")}</small></label>
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
        text={t(kind === "create" ? "Create" : "Join")} onClick={() => void submit()} />
    </>} />
  </Dialog>;
}
