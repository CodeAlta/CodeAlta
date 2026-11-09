import { useCallback, useEffect, useState, type CSSProperties } from "react";
import { Button, Callout, HTMLSelect, InputGroup, PopoverNext, Switch, Tag } from "@blueprintjs/core";
import { machines, type MachinePeopleView, type MachineAgentsView, type MachineSandboxView, type MachineServiceView, type MachineSessionDefaultsView, type MachineDeviceView, type MachineManagement, type MachineUpdateView, type MachineView, type MachineSeatsView, type SecretsList } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { SecretsBody, type SecretsActions } from "../secrets/SecretsSettings";
import { ActivitySpinner } from "../ActivitySpinner";
import { RemoveButton } from "../SettingsPage";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { machineIntent, machineState, machineStateLabel } from "./machines";
import { parseLabels } from "../fleet/fleetSecrets";
import { MachineProviders } from "./MachineProviders";
import { MachineModelServers } from "../modelServers/MachineModelServers";
import { MachineSignIns } from "./MachineSignIns";
import { formatSize } from "../search/SearchSettings";
import { loadParts, maxSessionLimit, parseSessionLimit } from "./seats";
import { agentSandboxChoices, agentsAllowed, confirmsBypass, confirmsLoss, devicePermissions, hasPermission, isMachineLabel, managementProblem, orderedDevices, permissionLabel, permissionsEditable, projectSource,
  updateSummary, type DevicePermission } from "./machineManagement";

/** The calls of the page of a machine. */
export type MachinePageApi = Pick<typeof machines, "management" | "setDevicePermission" | "revokeDevice" | "addProject" | "cloneProject" | "removeProject"
  | "readLog" | "updateMachine" | "secrets" | "setSecret" | "rotateSecret" | "deleteSecret" | "setVariable" | "deleteVariable"
  | "sessionDefaults" | "setSessionPermissionMode" | "agents" | "setAgentSpace" | "setAgentSandbox" | "setAgentBypass" | "people" | "setMachinePerson" | "labels" | "setLabels" | "limits" | "setLimits" | "seats" | "startSeat" | "cancelSeat" | "providers" | "setProvider" | "providerSignIn" | "providerSignInWait"
  | "signIns" | "signInStart" | "signInWait" | "signInCode" | "signInCancel" | "signOut">;

type Problem = { key: MessageKey; detail?: string | null } | null;

/**
 * The page of a paired machine, opened from its row in Settings > Fleet > Machines: its state and version, what each
 * of its devices may do, its projects, its ready providers, its updates and its log. A computer that manages the
 * machine changes them here; the others see what it is and how to be given the permission.
 */
export function MachinePage({ epoch, machine, api = machines, onBack }: { epoch: string; machine: MachineView; api?: MachinePageApi; onBack: () => void }) {
  const { t } = useShellLanguage();
  const [view, setView] = useState<MachineManagement | null>(null);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Problem>(null);
  const [generation, setGeneration] = useState(0);
  const reload = useCallback(() => setGeneration(value => value + 1), []);
  useEffect(() => {
    const abort = new AbortController();
    setLoading(true);
    api.management({ expectedEpoch: epoch, machineId: machine.id }, { signal: abort.signal, timeoutMilliseconds: 30_000 }).then(value => {
      if (!abort.signal.aborted) setView(value);
    }, () => { if (!abort.signal.aborted) setView(null); }).finally(() => { if (!abort.signal.aborted) setLoading(false); });
    return () => abort.abort();
  }, [api, epoch, machine.id, generation, machine.state]);

  /** Runs one change on the machine, then reads it again; says what went wrong. */
  const change = useCallback(async (call: () => Promise<{ status: string; message?: string | null }>, note?: (message: string | null | undefined) => void) => {
    setBusy(true);
    setProblem(null);
    try {
      const result = await call();
      const failed = managementProblem(result.status);
      if (failed) setProblem({ key: failed, detail: result.message });
      else note?.(result.message);
    } catch {
      setProblem({ key: "The machine does not answer: it is offline." });
    } finally {
      setBusy(false);
      reload();
    }
  }, [reload]);

  const state = machineState(machine);
  const details = [machine.os, view?.version ? `CodeAlta ${view.version}` : machine.version && `CodeAlta ${machine.version}`, machine.address].filter(Boolean).join(" · ");
  const failure = view ? managementProblem(view.status) : !loading ? "The machine does not answer: it is offline." as MessageKey : null;
  return <main className="configuration-page settings-editor machine-page" aria-label={machine.name}>
    <header className="page-heading settings-editor-heading">
      <div>
        <Button className="machine-page-back" variant="minimal" size="small" icon={<AppIcon name="chevronLeft" size={14} />} text={t("Machines")} onClick={onBack} />
        <h1 className="machine-page-title"><AppIcon name="remote" size={20} />{machine.name}
          <Tag minimal round intent={machineIntent(state)}>{t(machineStateLabel(state))}</Tag>
          {view?.status === "ok" && <Tag minimal round intent="primary" icon={<AppIcon name="shield" size={12} />}>{t("Managed from here")}</Tag>}</h1>
        <p>{details}</p>
      </div>
      <div className="settings-editor-actions">
        {(loading || busy) && <ActivitySpinner size={14} />}
        <Button icon={<AppIcon name="refresh" size={15} />} disabled={loading || busy} onClick={reload}>{t("Reload")}</Button>
      </div>
    </header>
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {failure && view?.status !== "not_manager" && <Callout compact intent="warning" role="alert" className="machine-page-failure">{t(failure)}
      {view?.message && <div className="muted-text">{view.message}</div>}
      <Button size="small" icon={<AppIcon name="refresh" size={14} />} text={t("Try again")} onClick={reload} /></Callout>}
    {view?.status === "not_manager" && <NotManaged machine={machine} device={view.thisDevice ?? null} />}
    {view?.status === "ok" && <>
      <DevicesSection view={view} busy={busy} machine={machine}
        onPermission={(device, permission, allowed) => void change(() => api.setDevicePermission({ expectedEpoch: epoch, machineId: machine.id, deviceId: device.id, permission, allowed }))}
        onRevoke={device => void change(() => api.revokeDevice({ expectedEpoch: epoch, machineId: machine.id, deviceId: device.id }))} />
      <PermissionsSection epoch={epoch} machine={machine} api={api} sandbox={view.sandbox} />
      <AgentsSection epoch={epoch} machine={machine} api={api} />
      <PeopleSection epoch={epoch} machine={machine} api={api} />
      <LabelsSection epoch={epoch} machine={machine} api={api} />
      <LimitsSection epoch={epoch} machine={machine} api={api} />
      <ProjectsSection view={view} busy={busy} machine={machine}
        onAdd={(kind, value, done) => void change(() => kind === "clone"
          ? api.cloneProject({ expectedEpoch: epoch, machineId: machine.id, value }, { timeoutMilliseconds: 15 * 60_000 })
          : api.addProject({ expectedEpoch: epoch, machineId: machine.id, value }), done)}
        onRemove={project => void change(() => api.removeProject({ expectedEpoch: epoch, machineId: machine.id, value: project }))} />
    </>}
    {/* A computer that manages the machine sees each provider, why it is not ready, and gives it a key or signs it in; the others see the ready ones. */}
    {view?.status === "ok" && <MachineProviders epoch={epoch} machine={machine} api={api} />}
    {view?.status === "ok" && <MachineSignIns epoch={epoch} machine={machine} api={api} />}
    {view?.status === "not_manager" && <ProvidersSection view={view} machine={machine} />}
    {/* Its llama.cpp servers, for a computer that may view the machine: Settings > Fleet > Model servers changes them. */}
    {view && <MachineModelServers epoch={epoch} machine={machine} />}
    {view?.status === "ok" && <>
      <SecretsSection epoch={epoch} machine={machine} api={api} />
      {view.service && <ServiceSection machine={machine} service={view.service} />}
      <UpdatesSection epoch={epoch} machine={machine} view={view} api={api} onChanged={reload} />
      <LogSection epoch={epoch} machine={machine} api={api} />
    </>}
  </main>;
}

/** What a computer that does not manage a machine is told: how the owner gives it the permission, on the machine, once. */
function NotManaged({ machine, device }: { machine: MachineView; device: string | null }) {
  const { t } = useShellLanguage();
  return <section className="machine-section" aria-label={t("Manage this machine")}>
    <h2>{t("Manage this machine")}</h2>
    <p className="muted-text">{t("This computer sees and drives the sessions of {name}, but does not manage it. The device that installed or first paired it manages it, and can give this computer the permission from its own page of {name}.", { name: machine.name })}</p>
    {/* A machine reached through the server only has no paired device to give the permission to: this computer pairs with it to manage it. */}
    {!machine.paired ? <><p className="muted-text">{t("This computer reaches {name} through your server and is not paired with it. To manage it from here, run on {name} itself:", { name: machine.name })}</p>
      <code className="machine-command">altanode pair --manage</code>
      <p className="muted-text">{t("Then paste the link it shows in Settings > Fleet > Machines > Pair a machine.")}</p></>
      : device && <><p className="muted-text">{t("On {name} itself, the owner gives it with:", { name: machine.name })}</p>
      <code className="machine-command">altanode permissions {device} --allow manage</code></>}
  </section>;
}

function DevicesSection({ view, busy, machine, onPermission, onRevoke }: {
  view: MachineManagement; busy: boolean; machine: MachineView;
  onPermission: (device: MachineDeviceView, permission: DevicePermission, allowed: boolean) => void; onRevoke: (device: MachineDeviceView) => void;
}) {
  const { t } = useShellLanguage();
  return <section className="machine-section" aria-label={t("Devices")}>
    <h2>{t("Devices")}</h2>
    <p className="muted-text">{t("The computers paired with {name}, and what each may do there.", { name: machine.name })}</p>
    <DeviceTable devices={view.devices} busy={busy} subject={machine.name} onPermission={onPermission} onRevoke={onRevoke} />
  </section>;
}

/**
 * The computers paired with a machine, or with this computer, and a switch per permission each has there; Revoke refuses
 * one from now on. `permissions` are the columns: those the machine serves.
 */
export function DeviceTable({ devices: unordered, permissions = devicePermissions, busy, subject, here = false, onPermission, onRevoke }: {
  devices: readonly MachineDeviceView[]; permissions?: readonly DevicePermission[]; busy: boolean; subject: string; here?: boolean;
  onPermission: (device: MachineDeviceView, permission: DevicePermission, allowed: boolean) => void; onRevoke: (device: MachineDeviceView) => void;
}) {
  const { t } = useShellLanguage();
  const devices = orderedDevices(unordered);
  return <div className="machine-devices" role="table" aria-label={t("Devices")} style={{ "--machine-permission-columns": permissions.length } as CSSProperties}>
      <div className="machine-device machine-device-head" role="row">
        <span role="columnheader">{t("Device")}</span>
        {permissions.map(permission => <span key={permission} role="columnheader" title={t(permissionLabel(permission).description)}>{t(permissionLabel(permission).name)}</span>)}
        <span role="columnheader" aria-label={t("Revoke")} />
      </div>
      {devices.map(device => <div key={device.id} className="machine-device" role="row" data-revoked={device.revokedAt ? "true" : undefined}>
        <span role="cell" className="machine-device-name">
          <strong>{device.name}</strong>
          {device.isThisComputer && <Tag minimal round>{t("This computer")}</Tag>}
          {device.revokedAt ? <Tag minimal round intent="danger">{t("Revoked")}</Tag> : device.channels > 0 && <Tag minimal round intent="success">{t("Connected")}</Tag>}
          <code className="muted-text" title={device.fingerprint}>{device.fingerprint.split(" ").slice(0, 2).join(" ")}</code>
        </span>
        {permissions.map(permission => <span key={permission} role="cell" className="machine-permission-cell">
          <small className="machine-permission-name" aria-hidden="true">{t(permissionLabel(permission).name)}</small>
          <PermissionSwitch device={device} permission={permission} disabled={busy || !permissionsEditable(device)} onChange={allowed => onPermission(device, permission, allowed)} />
        </span>)}
        <span role="cell">{!device.revokedAt && <PopoverNext placement="left" content={<div className="machine-confirm">
          <p>{t(device.isThisComputer ? "Revoke this computer on {name}?" : "Revoke {device}?", { device: device.name, name: subject })}</p>
          <p className="muted-text">{t(here ? "Its key is refused here from now on; pairing again trusts it again." : "Its key is refused there from now on; pairing again trusts it again.")}</p>
          <Button className="bp6-popover-dismiss" intent="danger" disabled={busy} onClick={() => onRevoke(device)}>{t("Revoke")}</Button></div>}>
          <Button variant="minimal" size="small" intent="danger" icon={<AppIcon name="close" size={15} />} disabled={busy}
            aria-label={`${t("Revoke")} ${device.name}`} title={t("Revoke")} />
        </PopoverNext>}</span>
      </div>)}
    </div>;
}

/** One permission of one device: a switch, and a small confirmation beside it when this computer would give up managing the machine. */
function PermissionSwitch({ device, permission, disabled, onChange }: { device: MachineDeviceView; permission: DevicePermission; disabled: boolean; onChange: (allowed: boolean) => void }) {
  const { t } = useShellLanguage();
  const checked = hasPermission(device, permission);
  const label = `${t(permissionLabel(permission).name)} — ${device.name}`;
  if (checked && confirmsLoss(device, permission)) {
    return <PopoverNext placement="top" content={<div className="machine-confirm">
      <p>{t("Stop managing this machine from here?")}</p>
      <p className="muted-text">{t("Only its owner, on the machine or from a computer that manages it, can give it back.")}</p>
      <Button className="bp6-popover-dismiss" intent="danger" disabled={disabled} onClick={() => onChange(false)}>{t("Stop managing")}</Button></div>}>
      <Switch className="machine-permission" checked aria-label={label} disabled={disabled} onChange={() => { }} />
    </PopoverNext>;
  }

  return <Switch className="machine-permission" checked={checked} aria-label={label} disabled={disabled} onChange={event => onChange(event.currentTarget.checked)} />;
}

/**
 * The people of the organization the machine lets in (its own list, which the server cannot change): each named person with
 * what they may do there, on their own sessions only; who asked to use it, with Let in and Refuse; and Add a person….
 */
function PeopleSection({ epoch, machine, api }: { epoch: string; machine: MachineView; api: MachinePageApi }) {
  const { t, locale } = useShellLanguage();
  const [view, setView] = useState<MachinePeopleView | null>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Problem>(null);
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    const abort = new AbortController();
    api.people({ expectedEpoch: epoch, machineId: machine.id }, { signal: abort.signal, timeoutMilliseconds: 30_000 }).then(value => {
      if (abort.signal.aborted) return;
      const failed = value.status === "team" ? null : managementProblem(value.status);
      if (failed) setProblem({ key: failed, detail: value.message }); else setView(value);
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The machine does not answer: it is offline." }); });
    return () => abort.abort();
  }, [api, epoch, machine.id, generation]);
  const set = async (personId: string, permissions: string[] | null, refuse = false) => {
    setBusy(true);
    setProblem(null);
    try {
      const result = await api.setMachinePerson({ expectedEpoch: epoch, machineId: machine.id, personId, permissions, refuse });
      const failed = managementProblem(result.status);
      if (failed) setProblem({ key: failed, detail: result.message });
    } catch {
      setProblem({ key: "The machine does not answer: it is offline." });
    } finally {
      setBusy(false);
      setGeneration(value => value + 1);
    }
  };
  const all = ["view", "drive", "start"];
  if (!view && !problem) return null;
  return <section className="machine-section machine-people" aria-label={t("People")}>
    <h2>{t("People")}</h2>
    {view?.status === "team"
      ? <p className="muted-text">{t("{name} belongs to {owner}: the members its access document names use it, each on their own sessions only.", { name: machine.name, owner: view.owner ?? "" })}</p>
      : <p className="muted-text">{t("People of your organization who may use {name}, each on their own sessions only.", { name: machine.name })}</p>}
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {view && view.status !== "team" && <>
      {view.asks.map(ask => <div key={ask.personId} className="machine-person machine-person-ask" data-ask={ask.name}>
        <span>{t("{name} asked to use this machine on {date}.", { name: ask.name, date: new Date(ask.askedAt).toLocaleString(locale, { dateStyle: "medium", timeStyle: "short" }) })}</span>
        <Button size="small" intent="primary" disabled={busy} text={t("Let in")} onClick={() => void set(ask.personId, all)} />
        <Button size="small" variant="minimal" disabled={busy} text={t("Refuse")} onClick={() => void set(ask.personId, null, true)} />
      </div>)}
      {view.people.map(person => <div key={person.personId} className="machine-person" data-person={person.name}>
        <span className="machine-person-name">{person.name}</span>
        {all.map(permission => <Switch key={permission} className="machine-permission" inline label={t(permissionLabel(permission as DevicePermission).name)}
          checked={person.permissions.includes(permission)} disabled={busy} onChange={event => {
            const next = event.currentTarget.checked ? [...person.permissions, permission] : person.permissions.filter(value => value !== permission);
            void set(person.personId, next.length > 0 ? next : null);
          }} />)}
        <RemoveButton name={person.name} disabled={busy} onRemove={() => void set(person.personId, null)} />
      </div>)}
      {view.others.length > 0 && <HTMLSelect value="" disabled={busy} aria-label={t("Add a person…")}
        options={[{ value: "", label: t("Add a person…") }, ...view.others.map(person => ({ value: person.personId, label: person.name }))]}
        onChange={event => { if (event.currentTarget.value) void set(event.currentTarget.value, all); }} />}
    </>}
  </section>;
}

/**
 * What a session of the machine without a mode of its own does before it acts, as `altanode permission-mode` sets it there:
 * bypass permissions, as a machine installed to run agents does by default, or ask first. A machine of an earlier version
 * has no such choice: its sessions ask first.
 */
function PermissionsSection({ epoch, machine, api, sandbox }: { epoch: string; machine: MachineView; api: MachinePageApi; sandbox: MachineSandboxView | null }) {
  const { t } = useShellLanguage();
  const [view, setView] = useState<MachineSessionDefaultsView | null>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Problem>(null);
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    const abort = new AbortController();
    api.sessionDefaults({ expectedEpoch: epoch, machineId: machine.id }, { signal: abort.signal, timeoutMilliseconds: 30_000 }).then(value => {
      if (abort.signal.aborted) return;
      const failed = value.status === "method_not_found" ? null : managementProblem(value.status);
      if (failed) setProblem({ key: failed, detail: value.message }); else setView(value);
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The machine does not answer: it is offline." }); });
    return () => abort.abort();
  }, [api, epoch, machine.id, generation]);
  const save = async (permissionMode: string) => {
    setBusy(true);
    setProblem(null);
    try {
      const result = await api.setSessionPermissionMode({ expectedEpoch: epoch, machineId: machine.id, permissionMode });
      const failed = managementProblem(result.status);
      if (failed) setProblem({ key: failed, detail: result.message });
    } catch {
      setProblem({ key: "The machine does not answer: it is offline." });
    } finally {
      setBusy(false);
      setGeneration(value => value + 1);
    }
  };
  return <section className="machine-section machine-session-permissions" aria-label={t("Permissions")}>
    <h2>{t("Permissions")}</h2>
    <p className="muted-text">{t("What a session of {name} without a mode of its own does before it runs a command or changes a file: one started from another computer, a run of an automation.", { name: machine.name })}</p>
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {view?.status === "method_not_found" && <p className="muted-text">{t("Update CodeAlta Node on {name} to choose it: until then, its sessions ask first.", { name: machine.name })}</p>}
    {view?.permissionMode && <label className="machine-agent-row">
      <span className="machine-agent-label">{t("Default mode")}</span>
      <HTMLSelect value={view.permissionMode} disabled={busy} aria-label={t("Default mode")}
        options={[{ value: "bypassPermissions", label: t("Bypass permissions") }, { value: "default", label: t("Ask first") }]}
        onChange={event => { const mode = event.currentTarget.value; void save(mode); }} />
    </label>}
    {sandbox && <SandboxState machine={machine} sandbox={sandbox} />}
  </section>;
}

/**
 * The sandbox the sessions of the machine run in by default: the profile of a node (the package registries and the git
 * hosts), the one its settings name, or none; when the sandbox cannot run there, why, and what to install for it to run.
 */
function SandboxState({ machine, sandbox }: { machine: MachineView; sandbox: MachineSandboxView }) {
  const { t } = useShellLanguage();
  if (!sandbox.available) {
    return <Callout compact intent="warning" className="machine-sandbox" role="status">
      {sandbox.profile ? t("The sandbox cannot run on {name}: its sessions sandboxed with {profile} run no command until it is set up.", { name: machine.name, profile: sandbox.profile })
        : t("The sandbox cannot run on {name}: its sessions run without one until it is set up.", { name: machine.name })}
      {sandbox.reason && <div className="muted-text">{sandbox.reason}</div>}
      {sandbox.setup && <div className="muted-text">{t("To set it up: {setup}", { setup: sandbox.setup })}</div>}
    </Callout>;
  }

  return <p className="muted-text machine-sandbox">{sandbox.profile === "node"
    ? t("Its sessions run in the sandbox of a machine: the package registries and the git hosts, never its home folder or its credentials.")
    : sandbox.profile ? t("Its sessions run in the sandbox {profile}.", { profile: sandbox.profile })
    : t("Its sessions run without a sandbox: its settings turn it off.")}</p>;
}

/**
 * Whether the node of the machine still runs after a restart or a logout, in the words of `altanode status` there, and
 * the command that makes it start at boot, run on the machine itself: it needs the administrator or `sudo` there.
 */
function ServiceSection({ machine, service }: { machine: MachineView; service: MachineServiceView }) {
  const { t } = useShellLanguage();
  return <section className="machine-section machine-service" aria-label={t("Start at boot")}>
    <h2>{t("Start at boot")}</h2>
    {service.startsAtBoot ? <p className="muted-text">{t("{name} starts CodeAlta Node when it starts, with nobody logged in.", { name: machine.name })}</p>
      : <Callout compact intent="warning" role="status">{service.installed
        ? t("After a restart, {name} waits for a login before CodeAlta Node runs again.", { name: machine.name })
        : t("No service runs CodeAlta Node on {name}: after a restart, nothing starts it again.", { name: machine.name })}</Callout>}
    <ul className="machine-service-lines muted-text">{service.lines.map(line => <li key={line}>{line}</li>)}</ul>
    {service.fix && <>
      <p className="muted-text">{service.fixWhere === "elevated" ? t("To start it at boot, run on {name}, in a terminal run as administrator:", { name: machine.name })
        : t("To start it at boot, run on {name}:", { name: machine.name })}</p>
      <code className="machine-command">{service.fix}</code></>}
  </section>;
}

/**
 * Whether agents of other machines may start sessions on the machine and send prompts to its sessions, per space; the
 * sandbox their sessions run in at least; and whether those may bypass permissions, as `altanode agents` sets it there.
 * Off until a space is turned on; turning on the bypass asks first, beside its switch.
 */
function AgentsSection({ epoch, machine, api }: { epoch: string; machine: MachineView; api: MachinePageApi }) {
  const { t } = useShellLanguage();
  const [view, setView] = useState<MachineAgentsView | null>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Problem>(null);
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    const abort = new AbortController();
    api.agents({ expectedEpoch: epoch, machineId: machine.id }, { signal: abort.signal, timeoutMilliseconds: 30_000 }).then(value => {
      if (abort.signal.aborted) return;
      const failed = managementProblem(value.status);
      if (failed) setProblem({ key: failed, detail: value.message }); else setView(value);
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The machine does not answer: it is offline." }); });
    return () => abort.abort();
  }, [api, epoch, machine.id, generation]);
  // One change of the policy on the machine, then the policy read again; what went wrong stays above it.
  const run = async (call: () => Promise<{ status: string; message?: string | null }>) => {
    setBusy(true);
    setProblem(null);
    try {
      const result = await call();
      const failed = managementProblem(result.status);
      if (failed) setProblem({ key: failed, detail: result.message });
    } catch {
      setProblem({ key: "The machine does not answer: it is offline." });
    } finally {
      setBusy(false);
      setGeneration(value => value + 1);
    }
  };
  const target = { expectedEpoch: epoch, machineId: machine.id };
  const allowed = view ? agentsAllowed(view) : false;
  const sandboxes = view ? agentSandboxChoices(view) : [];
  return <section className="machine-section machine-agents" aria-label={t("Agents of other machines")}>
    <div className="machine-section-head">
      <h2>{t("Agents of other machines")}</h2>
      {view && <Tag minimal round intent={allowed ? "success" : "none"}>{t(allowed ? "On" : "Off")}</Tag>}
    </div>
    <p className="muted-text">{t("Agents of other computers may start sessions on {name} and send prompts to its sessions, in the spaces turned on here.", { name: machine.name })}</p>
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {view && <div className="machine-agents-settings">
      <div className="machine-agent-row" role="group" aria-label={t("Spaces")}>
        <span className="machine-agent-label">{t("Spaces")}</span>
        <span className="machine-agent-spaces">{view.spaces.map(space => <Switch key={space.id} className="machine-permission" inline label={space.name}
          checked={space.allowed} disabled={busy} onChange={event => {
            const on = event.currentTarget.checked;
            void run(() => api.setAgentSpace({ ...target, space: space.id, allowed: on }));
          }} />)}</span>
      </div>
      <label className="machine-agent-row">
        <span className="machine-agent-label">{t("Sandbox")}</span>
        <HTMLSelect value={sandboxes.find(value => value.toLowerCase() === (view.sandbox ?? "").toLowerCase()) ?? ""} disabled={busy} aria-label={t("Sandbox")}
          options={sandboxes.map(value => ({ value, label: value || t("As their parent") }))}
          onChange={event => {
            const sandbox = event.currentTarget.value || null;
            void run(() => api.setAgentSandbox({ ...target, sandbox }));
          }} />
      </label>
      <div className="machine-agent-row">
        <span className="machine-agent-label">{t("Bypass permissions")}</span>
        <BypassSwitch allowed={view.allowBypass} disabled={busy} onChange={value => void run(() => api.setAgentBypass({ ...target, allowed: value }))} />
      </div>
    </div>}
  </section>;
}

/**
 * The tags of the machine, as `altanode tags` sets them there: it publishes them to its servers, an environment of the fleet
 * runs only on the machines with one of its tags, and an automation that runs on tags on the machines with all of them. Added
 * from the field, removed from their chip. The node API still names them labels, their name before 4.2.
 */
function LabelsSection({ epoch, machine, api }: { epoch: string; machine: MachineView; api: MachinePageApi }) {
  const { t } = useShellLanguage();
  const [labels, setLabels] = useState<readonly string[] | null>(null);
  const [typed, setTyped] = useState("");
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Problem>(null);
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    const abort = new AbortController();
    api.labels({ expectedEpoch: epoch, machineId: machine.id }, { signal: abort.signal, timeoutMilliseconds: 30_000 }).then(value => {
      if (abort.signal.aborted) return;
      const failed = managementProblem(value.status);
      if (failed) setProblem({ key: failed, detail: value.message }); else setLabels(value.labels);
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The machine does not answer: it is offline." }); });
    return () => abort.abort();
  }, [api, epoch, machine.id, generation]);
  const save = async (next: string[]) => {
    setBusy(true);
    setProblem(null);
    try {
      const result = await api.setLabels({ expectedEpoch: epoch, machineId: machine.id, labels: next });
      const failed = managementProblem(result.status);
      if (failed) setProblem({ key: failed, detail: result.message }); else setTyped("");
    } catch {
      setProblem({ key: "The machine does not answer: it is offline." });
    } finally {
      setBusy(false);
      setGeneration(value => value + 1);
    }
  };
  const added = parseLabels(typed);
  const valid = added.length > 0 && added.every(isMachineLabel);
  return <section className="machine-section machine-labels" aria-label={t("Machine tags")}>
    <h2>{t("Machine tags")}</h2>
    <p className="muted-text">{t("{name} publishes its tags to its servers. An environment runs only on the machines with one of its tags; an automation that runs on tags, on the machines with all of them.", { name: machine.name })}</p>
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {labels && <div className="machine-labels-row">
      {labels.length === 0 && <span className="muted-text">{t("No tag.")}</span>}
      {labels.map(label => <Tag key={label} minimal onRemove={busy ? undefined : () => void save(labels.filter(other => other !== label))}
        aria-label={label}>{label}</Tag>)}
      <form className="machine-labels-add" onSubmit={event => { event.preventDefault(); if (valid && !busy) void save([...labels, ...added]); }}>
        <InputGroup small value={typed} placeholder="gpu, prod" disabled={busy} intent={typed.trim() && !valid ? "danger" : "none"} aria-label={t("New tags")}
          onChange={event => setTyped(event.target.value)} />
        <Button type="submit" size="small" text={t("Add")} disabled={busy || !valid} />
      </form>
    </div>}
  </section>;
}

/**
 * The limits of the machine, as `altanode limits` sets them there: how many sessions work at once, saved when the field is left
 * or Enter is pressed; and the runs that wait for their turn, which a computer that manages the machine starts now or takes out.
 */
function LimitsSection({ epoch, machine, api }: { epoch: string; machine: MachineView; api: MachinePageApi }) {
  const { t } = useShellLanguage();
  const [typed, setTyped] = useState<string | null>(null);
  const [saved, setSaved] = useState<number | null | undefined>(undefined);
  const [seats, setSeats] = useState<MachineSeatsView | null>(null);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<Problem>(null);
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    const abort = new AbortController();
    const options = { signal: abort.signal, timeoutMilliseconds: 30_000 };
    api.limits({ expectedEpoch: epoch, machineId: machine.id }, options).then(value => {
      if (abort.signal.aborted) return;
      const failed = managementProblem(value.status);
      if (failed) setProblem({ key: failed, detail: value.message });
      else { setSaved(value.sessions); setTyped(value.sessions == null ? "" : String(value.sessions)); }
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The machine does not answer: it is offline." }); });
    api.seats({ expectedEpoch: epoch, machineId: machine.id }, options).then(value => {
      if (!abort.signal.aborted && value.status === "ok") setSeats(value);
    }, () => { });
    return () => abort.abort();
  }, [api, epoch, machine.id, generation, machine.queued, machine.working]);
  const run = async (call: () => Promise<{ status: string; message?: string | null }>) => {
    setBusy(true);
    setProblem(null);
    try {
      const result = await call();
      const failed = managementProblem(result.status);
      if (failed && result.status !== "not_found") setProblem({ key: failed, detail: result.message });
    } catch {
      setProblem({ key: "The machine does not answer: it is offline." });
    } finally {
      setBusy(false);
      setGeneration(value => value + 1);
    }
  };
  const parsed = typed == null ? undefined : parseSessionLimit(typed);
  const commit = () => {
    if (parsed === undefined || parsed === "invalid" || parsed === saved || busy) return;
    void run(() => api.setLimits({ expectedEpoch: epoch, machineId: machine.id, sessions: parsed }));
  };
  const load = seats ? loadParts({ working: seats.working, queued: seats.queue.length, sessionLimit: seats.limit }) : null;
  return <section className="machine-section machine-limits" aria-label={t("Limits")}>
    <h2>{t("Limits")}</h2>
    <p className="muted-text">{t("{name} runs at most this many sessions at once, a session and its sub-sessions counting once. The work of other machines beyond it waits for its turn; the work started on {name} itself never waits.", { name: machine.name })}</p>
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {typed != null && <label className="machine-limits-row">
      <span>{t("Sessions at work at once")}</span>
      <InputGroup small className="machine-limits-input" value={typed} placeholder={t("No limit")} disabled={busy} inputMode="numeric"
        intent={parsed === "invalid" ? "danger" : "none"} aria-label={t("Sessions at work at once")} title={t("A number from 1 to {max}, or empty for no limit.", { max: maxSessionLimit })}
        onChange={event => setTyped(event.target.value)} onBlur={commit} onKeyDown={event => { if (event.key === "Enter") commit(); }} />
      {load && <span className="muted-text machine-limits-load">{load.map(part => t(part.key, part.vars)).join(", ")}</span>}
    </label>}
    {seats && seats.queue.length > 0 && <div className="machine-seats" role="list" aria-label={t("Queue")}>
      {seats.queue.map(item => <div key={item.id} className="machine-seat" role="listitem">
        <span className="machine-seat-position">{item.position}</span>
        <span className="machine-seat-title">{item.title ?? t(item.kind === "automation" ? "An automation" : "A prompt")}</span>
        <Button size="small" variant="minimal" text={t("Start now")} disabled={busy}
          onClick={() => void run(() => api.startSeat({ expectedEpoch: epoch, machineId: machine.id, id: item.id }))} />
        <Button size="small" variant="minimal" text={t("Take out of the queue")} disabled={busy}
          onClick={() => void run(() => api.cancelSeat({ expectedEpoch: epoch, machineId: machine.id, id: item.id }))} />
      </div>)}
    </div>}
  </section>;
}

/** Whether the sessions of agents may bypass permissions: turned off at once, turned on from a small confirmation beside it. */
function BypassSwitch({ allowed, disabled, onChange }: { allowed: boolean; disabled: boolean; onChange: (allowed: boolean) => void }) {
  const { t } = useShellLanguage();
  const label = t("Bypass permissions");
  if (confirmsBypass(!allowed)) {
    return <PopoverNext placement="right" content={<div className="machine-confirm machine-bypass-confirm">
      <p>{t("Let their sessions bypass permissions when their parent does?")}</p>
      <Button className="bp6-popover-dismiss" intent="danger" disabled={disabled} onClick={() => onChange(true)}>{t("Allow")}</Button></div>}>
      <Switch className="machine-permission" checked={false} aria-label={label} disabled={disabled} onChange={() => { }} />
    </PopoverNext>;
  }

  return <Switch className="machine-permission" checked aria-label={label} disabled={disabled} onChange={() => onChange(false)} />;
}

function ProjectsSection({ view, busy, machine, onAdd, onRemove }: {
  view: MachineManagement; busy: boolean; machine: MachineView;
  onAdd: (kind: "clone" | "folder", value: string, done: (note: string | null | undefined) => void) => void; onRemove: (projectId: string) => void;
}) {
  const { t, locale } = useShellLanguage();
  const worktrees = view.worktrees;
  const [text, setText] = useState("");
  const [note, setNote] = useState<string | null>(null);
  const source = projectSource(text);
  const add = () => { if (source) onAdd(source.kind, source.value, value => { setText(""); setNote(value ?? null); }); };
  return <section className="machine-section" aria-label={t("Projects")}>
    <h2>{t("Projects")}</h2>
    <p className="muted-text">{t("The projects a session can start in on {name}.", { name: machine.name })}</p>
    {view.projects.length === 0 ? <p className="muted-text machine-empty">{t("No project yet: clone a repository or add a folder of the machine.")}</p>
      : <ul className="machine-projects">{view.projects.map(project => <li key={project.id}>
        <AppIcon name={project.repository ? "branch" : "folder"} size={15} />
        <span className="machine-project-main"><strong>{project.name}</strong><small className="muted-text" title={project.path}>{project.path}</small></span>
        <RemoveButton name={project.name} disabled={busy} onRemove={() => onRemove(project.id)} />
      </li>)}</ul>}
    <form className="machine-project-add" onSubmit={event => { event.preventDefault(); add(); }}>
      <InputGroup fill value={text} disabled={busy} aria-label={t("Repository URL or folder")} placeholder={t("https://github.com/owner/repo, or a folder of the machine")}
        onChange={event => { setText(event.currentTarget.value); setNote(null); }} />
      <Button type="submit" intent="primary" disabled={busy || !source} icon={<AppIcon name={source?.kind === "folder" ? "newFolder" : "download"} size={14} />}
        text={t(source?.kind === "folder" ? "Add the folder" : "Clone")} />
    </form>
    {text.trim() && !source && <small className="muted-text">{t("Give a repository URL, or the full path of a folder on the machine.")}</small>}
    {note && <small className="muted-text">{note}</small>}
    {view.problems.map(text => <Callout key={text} compact intent="warning">{text}</Callout>)}
    {/* Each session works in a worktree of its own there; the machine removes those its sessions no longer use. */}
    {worktrees && <p className="muted-text machine-worktrees">
      {t(worktrees.count === 1 ? "Its sessions work in 1 worktree, which takes {size} on its disk." : "Its sessions work in {count} worktrees, which take {size} on its disk.",
        { count: worktrees.count, size: formatSize(worktrees.kilobytes, locale) })}{" "}
      {t("A worktree goes once its session is archived, deleted or idle for {days} days, and nothing in it is left uncommitted.", { days: worktrees.idleDays })}
    </p>}
  </section>;
}

function ProvidersSection({ view, machine }: { view: MachineManagement; machine: MachineView }) {
  const { t } = useShellLanguage();
  return <section className="machine-section" aria-label={t("Providers")}>
    <h2>{t("Providers")}</h2>
    {view.providers.length === 0
      ? <Callout compact intent="warning">{t("No provider is ready on {name}: its sessions cannot start. Sign in to a provider on the machine, as in CodeAlta Desktop.", { name: machine.name })}</Callout>
      : <ul className="machine-providers">{view.providers.map(provider => <li key={provider.key}>
        <AppIcon name="checked" size={14} className="machine-ready" />
        <strong>{provider.name}</strong>
        {provider.isDefault && <Tag minimal round>{t("Default")}</Tag>}
        <span className="muted-text">{[provider.defaultModel, t(provider.models === 1 ? "{count} model" : "{count} models", { count: provider.models })].filter(Boolean).join(" · ")}</span>
      </li>)}</ul>}
  </section>;
}

/**
 * The secrets and variables of the machine, as Settings > Secrets shows those of this computer: secrets by name (Add,
 * Rotate, Delete), variables with their values. Each change goes to the machine, which checks that this computer manages it.
 */
function SecretsSection({ epoch, machine, api }: { epoch: string; machine: MachineView; api: MachinePageApi }) {
  const { t } = useShellLanguage();
  const [listing, setListing] = useState<SecretsList | null>(null);
  const [busy, setBusy] = useState(false);
  const [adding, setAdding] = useState(false);
  const [problem, setProblem] = useState<Problem>(null);
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    const abort = new AbortController();
    api.secrets({ expectedEpoch: epoch, machineId: machine.id }, { signal: abort.signal, timeoutMilliseconds: 30_000 }).then(value => {
      if (abort.signal.aborted) return;
      const failed = managementProblem(value.status);
      if (failed) setProblem({ key: failed }); else setListing(value);
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The machine does not answer: it is offline." }); });
    return () => abort.abort();
  }, [api, epoch, machine.id, generation]);
  // One change on the machine, then the list read again; what went wrong stays above the list.
  const run = async (call: () => Promise<{ status: string; message?: string | null }>) => {
    setBusy(true);
    setProblem(null);
    try {
      const result = await call();
      const failed = managementProblem(result.status);
      if (failed) { setProblem({ key: failed, detail: result.message }); return false; }
      return true;
    } catch {
      setProblem({ key: "The machine does not answer: it is offline." });
      return false;
    } finally {
      setBusy(false);
      setGeneration(value => value + 1);
    }
  };
  const target = { expectedEpoch: epoch, machineId: machine.id };
  const actions: SecretsActions = {
    setSecret: (name, value) => api.setSecret({ ...target, name, value }),
    rotateSecret: (name, value) => api.rotateSecret({ ...target, name, value }),
    deleteSecret: name => api.deleteSecret({ ...target, name }),
    setVariable: (name, value) => api.setVariable({ ...target, name, value }),
    deleteVariable: name => api.deleteVariable({ ...target, name }),
  };
  return <section className="machine-section machine-secrets" aria-label={t("Secrets")}>
    <div className="machine-section-head">
      <h2>{t("Secrets")}</h2>
      <Button size="small" icon={<AppIcon name="plus" size={14} />} disabled={!listing || busy || adding} onClick={() => setAdding(true)}>{t("Add a secret…")}</Button>
    </div>
    <p className="muted-text">{t("Named values of the machine. Its configuration refers to them as ${secret:NAME} and ${var:NAME}; the value of a secret never comes back here.")}</p>
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {listing && <SecretsBody flat listing={listing} busy={busy} adding={adding} onAdded={() => setAdding(false)} run={run} actions={actions} />}
  </section>;
}

function UpdatesSection({ epoch, machine, view, api, onChanged }: { epoch: string; machine: MachineView; view: MachineManagement; api: MachinePageApi; onChanged: () => void }) {
  const { t } = useShellLanguage();
  const [result, setResult] = useState<MachineUpdateView | null>(null);
  const [working, setWorking] = useState(false);
  const [failed, setFailed] = useState<Problem>(null);
  const run = async (check: boolean) => {
    setWorking(true);
    setFailed(null);
    try {
      const value = await api.updateMachine({ expectedEpoch: epoch, machineId: machine.id, check, now: false }, { timeoutMilliseconds: 11 * 60_000 });
      if (["up_to_date", "available", "staged", "unavailable"].includes(value.status)) setResult(value);
      else setFailed({ key: value.status === "failed" ? "The update did not go through." : managementProblem(value.status) ?? "The update did not go through.", detail: value.message });
      if (!check) onChanged();
    } catch {
      setFailed({ key: "The machine does not answer: it is offline." });
    } finally { setWorking(false); }
  };
  const pending = view.update?.pendingVersion;
  const last = view.update?.lastOutcome;
  return <section className="machine-section" aria-label={t("Updates")}>
    <h2>{t("Updates")}</h2>
    <p>{t("Runs CodeAlta {version}.", { version: view.version ?? machine.version ?? "?" })}
      {pending && <> {t("It switches to {version} once no session works.", { version: pending })}</>}
      {!pending && last === "rolled_back" && <> {t("Version {version} did not start and was undone.", { version: view.update?.lastVersion ?? "?" })}</>}</p>
    <div className="machine-actions">
      <Button icon={working ? <ActivitySpinner size={14} /> : <AppIcon name="refresh" size={14} />} disabled={working} text={t("Check for updates")} onClick={() => void run(true)} />
      {result?.status === "available" && result.target && <Button intent="primary" disabled={working} icon={<AppIcon name="download" size={14} />}
        text={t("Update to {version}", { version: result.target })} onClick={() => void run(false)} />}
    </div>
    {result && <small className="muted-text" role="status">{t(updateSummary(result), { version: result.target ?? "" })}{result.message && ` ${result.message}`}</small>}
    {failed && <Callout compact intent="warning" role="alert">{t(failed.key)}{failed.detail && <div className="muted-text">{failed.detail}</div>}</Callout>}
  </section>;
}

function LogSection({ epoch, machine, api }: { epoch: string; machine: MachineView; api: MachinePageApi }) {
  const { t } = useShellLanguage();
  const [log, setLog] = useState<{ text: string; path: string | null } | null>(null);
  const [reading, setReading] = useState(false);
  const [failed, setFailed] = useState<Problem>(null);
  const read = async () => {
    setReading(true);
    setFailed(null);
    try {
      const value = await api.readLog({ expectedEpoch: epoch, machineId: machine.id, lines: 300 }, { timeoutMilliseconds: 30_000 });
      if (value.status === "ok") setLog({ text: value.text ?? "", path: value.path ?? null });
      else setFailed({ key: managementProblem(value.status) ?? "The machine could not do it.", detail: value.message });
    } catch {
      setFailed({ key: "The machine does not answer: it is offline." });
    } finally { setReading(false); }
  };
  return <section className="machine-section" aria-label={t("Log")}>
    <h2>{t("Log")}</h2>
    <div className="machine-actions">
      <Button icon={reading ? <ActivitySpinner size={14} /> : <AppIcon name="logs" size={14} />} disabled={reading} text={t(log ? "Read again" : "Show the log")} onClick={() => void read()} />
      {log?.path && <small className="muted-text" title={log.path}>{log.path}</small>}
    </div>
    {failed && <Callout compact intent="warning" role="alert">{t(failed.key)}{failed.detail && <div className="muted-text">{failed.detail}</div>}</Callout>}
    {log && (log.text ? <pre className="machine-log" tabIndex={0} aria-label={t("Log")}>{log.text}</pre> : <p className="muted-text">{t("The machine wrote no log yet.")}</p>)}
  </section>;
}