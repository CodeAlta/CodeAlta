import { useCallback, useEffect, useState } from "react";
import { Button, Callout, Card, Checkbox, HTMLSelect, InputGroup, NonIdealState, PopoverNext, Tag } from "@blueprintjs/core";
import { fleetServer, type FleetApprovalView, type FleetEnvironmentView, type FleetSecretsOverview, type FleetSecretView, type FleetServerView } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { ActivitySpinner } from "../ActivitySpinner";
import { ConfirmPopover } from "../ConfirmPopover";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../SettingsPage";
import { settingsNavigation } from "../settingsNavigation";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { isSecretName, referenceOf, secretNameProblem } from "../secrets/secrets";
import { serverProblem } from "./fleetServer";
import { holderNames, isEnvironmentName, needsRotation, parseLabels, scopeLabel, scopeOptions, waitingApprovals } from "./fleetSecrets";

export type FleetSecretsApi = Pick<typeof fleetServer, "status" | "secrets" | "setSecret" | "rotateSecret" | "deleteSecret" | "grantSecret" | "withdrawSecret"
  | "setEnvironment" | "approveRun">;
type Result = Promise<{ status: string; message?: string | null }>;

/** How often the values of a server are read again while the page shows them, so that a run that waits for an approval appears. */
const pollMilliseconds = 10_000;

/**
 * Settings > Fleet > Secrets and environments: the secrets, variables and environments of the person at each server this
 * computer manages, as GitHub Actions shows those of a repository. A value is sealed here for the machines allowed to use
 * it and is never shown back; a variable is. Rotate, give a machine its copy or take it away, environments and their
 * rules, and the runs that wait for an approval.
 */
export function FleetSecretsSettings({ epoch, api = fleetServer }: { epoch: string | null; api?: FleetSecretsApi }) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload } = useSettingsEditor(options => api.status({ expectedEpoch: epoch ?? "" }, options), `${epoch}`);
  const servers = (listing?.servers ?? []).filter(server => server.controller && server.link === "approved");
  return <SettingsPage className="fleet-secrets-settings" label={t("Secrets and environments")} group="Fleet" title="Secrets and environments"
    description="Named values of your machines, sealed on this computer for the machines that use them." notice={notice} loading={loading} busy={busy} onReload={reload}>
    {!listing ? <SettingsUnavailable loading={loading} icon="secret" title="Servers unavailable" />
      : servers.length === 0 ? <NoServerSecrets />
      : servers.map(server => <ServerSecrets key={server.id} server={server} epoch={epoch ?? ""} api={api} titled={servers.length > 1} />)}
  </SettingsPage>;
}

/**
 * The page without a server. The values of this computer itself are on Settings > Secrets, which needs no fleet: this page,
 * whose title is close to it, says where they are.
 */
export function NoServerSecrets({ openSecrets = () => settingsNavigation.open("secrets") }: { openSecrets?: () => void }) {
  const { t } = useShellLanguage();
  return <NonIdealState className="fleet-secrets-empty" icon={<AppIcon name="server" size={36} />} title={t("Not linked to a server.")}
    description={t("The secrets and variables of this computer are in Settings > Secrets.")}
    action={<Button icon={<AppIcon name="secret" size={15} />} onClick={openSecrets}>{t("Secrets")}</Button>} />;
}

/** The values, environments and approvals of one server. */
function ServerSecrets({ server, epoch, api, titled }: { server: FleetServerView; epoch: string; api: FleetSecretsApi; titled: boolean }) {
  const { t } = useShellLanguage();
  const [overview, setOverview] = useState<FleetSecretsOverview | null>(null);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const [busy, setBusy] = useState(false);
  const [generation, setGeneration] = useState(0);
  const [adding, setAdding] = useState(false);
  useEffect(() => {
    const abort = new AbortController();
    api.secrets({ expectedEpoch: epoch, linkId: server.id }, { signal: abort.signal, timeoutMilliseconds: 30_000 }).then(value => {
      if (abort.signal.aborted) return;
      if (value.status === "ok") { setOverview(value); setProblem(null); } else setProblem({ key: serverProblem(value.status) ?? "The operation did not complete.", detail: value.message });
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The server does not answer." }); });
    return () => abort.abort();
  }, [api, epoch, server.id, generation]);
  useEffect(() => {
    const timer = setInterval(() => setGeneration(value => value + 1), pollMilliseconds);
    return () => clearInterval(timer);
  }, []);
  const run = useCallback(async (call: () => Result) => {
    setBusy(true);
    try {
      const result = await call();
      if (result.status === "ok") { setProblem(null); return true; }
      setProblem({ key: serverProblem(result.status) ?? "The operation did not complete.", detail: result.message });
      return false;
    } catch {
      setProblem({ key: "The server does not answer." });
      return false;
    } finally {
      setBusy(false);
      setGeneration(value => value + 1);
    }
  }, []);
  const linkId = server.id;
  if (!overview) return <section className="fleet-secrets-server" aria-label={server.url}>
    {problem ? <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>
      : <ActivitySpinner size={16} />}
  </section>;
  const waiting = waitingApprovals(overview);
  return <section className="fleet-secrets-server settings-grid" aria-label={server.url}>
    {titled && <h2 className="fleet-secrets-server-title">{server.organization ?? server.url}</h2>}
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {waiting.length > 0 && <Card className="appearance-settings fleet-secrets-approvals" aria-labelledby={`approvals-${linkId}`}>
      <h2 id={`approvals-${linkId}`}>{t("Runs that wait for you")}</h2>
      <ul className="secret-list" aria-label={t("Runs that wait for you")}>
        {waiting.map(approval => <ApprovalRow key={approval.id} approval={approval} busy={busy}
          onApprove={() => void run(() => api.approveRun({ expectedEpoch: epoch, linkId, approvalId: approval.id, review: approval.review }, { timeoutMilliseconds: 30_000 }))} />)}
      </ul>
    </Card>}
    <Card className="appearance-settings" aria-labelledby={`values-${linkId}`}>
      <div className="fleet-secrets-head">
        <h2 id={`values-${linkId}`}>{t("Secrets and variables")}</h2>
        <Button size="small" intent="primary" icon={<AppIcon name="plus" size={14} />} disabled={busy || adding} onClick={() => setAdding(true)}>{t("Add a value…")}</Button>
      </div>
      {adding && <NewValueForm overview={overview} busy={busy} onCancel={() => setAdding(false)}
        onSave={async request => { if (await run(() => api.setSecret({ expectedEpoch: epoch, linkId, ...request }, { timeoutMilliseconds: 30_000 }))) setAdding(false); }} />}
      {overview.secrets.length === 0 && !adding
        ? <NonIdealState className="secrets-empty" icon={<AppIcon name="secret" size={28} />} title={t("No value yet.")} />
        : <ul className="secret-list" aria-label={t("Secrets and variables")}>
          {overview.secrets.map(secret => <ValueRow key={secret.id} secret={secret} overview={overview} busy={busy}
            rotate={value => run(() => api.rotateSecret({ expectedEpoch: epoch, linkId, id: secret.id, value }, { timeoutMilliseconds: 30_000 }))}
            give={machineId => run(() => api.grantSecret({ expectedEpoch: epoch, linkId, id: secret.id, machineId }, { timeoutMilliseconds: 30_000 }))}
            withdraw={machineId => run(() => api.withdrawSecret({ expectedEpoch: epoch, linkId, id: secret.id, machineId }, { timeoutMilliseconds: 30_000 }))}
            remove={() => run(() => api.deleteSecret({ expectedEpoch: epoch, linkId, id: secret.id }, { timeoutMilliseconds: 30_000 }))} />)}
        </ul>}
    </Card>
    <Card className="appearance-settings" aria-labelledby={`environments-${linkId}`}>
      <h2 id={`environments-${linkId}`}>{t("Environments")}</h2>
      {overview.environments.length > 0 && <ul className="secret-list" aria-label={t("Environments")}>
        {overview.environments.map(environment => <EnvironmentRow key={environment.id} environment={environment} busy={busy}
          remove={() => run(() => api.setEnvironment({ expectedEpoch: epoch, linkId, name: environment.name, approvers: environment.approvers, labels: environment.labels,
            automations: environment.automations, removed: true }, { timeoutMilliseconds: 30_000 }))} />)}
      </ul>}
      <NewEnvironmentForm overview={overview} busy={busy}
        onSave={(name, approvers, labels) => run(() => api.setEnvironment({ expectedEpoch: epoch, linkId, name, approvers, labels, automations: [], removed: false },
          { timeoutMilliseconds: 30_000 }))} />
    </Card>
  </section>;
}

/** One value: its name and reference, its scope, its version, the machines that hold it; Rotate, Machines and Delete. */
function ValueRow({ secret, overview, busy, rotate, give, withdraw, remove }: {
  secret: FleetSecretView; overview: FleetSecretsOverview; busy: boolean; rotate: (value: string) => Promise<boolean>; give: (machineId: string) => Promise<boolean>;
  withdraw: (machineId: string) => Promise<boolean>; remove: () => Promise<boolean>;
}) {
  const { t, locale } = useShellLanguage();
  const [rotating, setRotating] = useState(false);
  const [value, setValue] = useState("");
  const [deleting, setDeleting] = useState(false);
  const variable = secret.kind === "variable";
  const scope = scopeLabel(secret);
  const holders = holderNames(secret);
  const held = new Set(secret.holders.map(holder => holder.id));
  const date = new Date(secret.setAt).toLocaleString(locale, { dateStyle: "medium", timeStyle: "short" });
  const submit = async () => {
    const typed = value;
    setValue("");
    if (await rotate(typed)) setRotating(false);
  };
  return <li className="secret-row fleet-secret-row" data-fleet-secret={secret.name} data-kind={secret.kind}>
    <span className="secret-name"><code title={secret.name}>{secret.name}</code>
      <span className="secret-reference" title={referenceOf(variable ? "var" : "secret", secret.name)}>{referenceOf(variable ? "var" : "secret", secret.name)}</span></span>
    <span className="fleet-secret-tags">
      <Tag minimal>{t(scope.key, { name: scope.name ?? "" })}</Tag>
      <Tag minimal>{t("Version {version}", { version: String(secret.version) })}</Tag>
      {variable && <Tag minimal intent="primary">{t("Variable")}</Tag>}
      {secret.hostOnly && <Tag minimal intent="warning" title={t("Given to MCP servers and plugin tools, never to the agent's shell.")}>{t("MCP servers and plugin tools only")}</Tag>}
      {needsRotation(secret) && <Tag minimal intent="danger" title={secret.flagged.map(holder => holder.name).join(", ")}>{t("Rotate: a revoked machine held it")}</Tag>}
    </span>
    <span className="secret-meta" title={holders.join(", ")}>
      {variable && secret.value !== null && <code className="fleet-secret-value">{secret.value}</code>}
      {holders.length > 0 ? t("On {machines}", { machines: holders.join(", ") }) : t("On no machine")} · {t("Set {date} by {who}", { date, who: secret.setBy })}
    </span>
    <span className="secret-actions">
      <PopoverNext isOpen={rotating} placement="bottom-end" onInteraction={next => { if (!next && !busy) { setRotating(false); setValue(""); } }}
        content={<form className="secret-rotate" onSubmit={event => { event.preventDefault(); if (value) void submit(); }}>
          <strong>{t("New value of {name}", { name: secret.name })}</strong>
          <InputGroup type={variable ? "text" : "password"} autoComplete="off" autoFocus small value={value} disabled={busy}
            aria-label={t("New value of {name}", { name: secret.name })} onChange={event => setValue(event.target.value)} />
          <div className="rename-actions">
            <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={() => { setRotating(false); setValue(""); }} />
            <Button type="submit" intent="primary" size="small" text={t("Rotate")} disabled={busy || !value} />
          </div>
        </form>}>
        <Button size="small" icon={<AppIcon name="refresh" size={14} />} text={t("Rotate")} disabled={busy} onClick={() => setRotating(true)} />
      </PopoverNext>
      <PopoverNext placement="bottom-end" content={<div className="fleet-secret-machines" role="group" aria-label={t("Machines that hold {name}", { name: secret.name })}>
        <strong>{t("Machines that hold {name}", { name: secret.name })}</strong>
        {overview.machines.length === 0 && <span className="muted-text">{t("No machine of yours.")}</span>}
        {overview.machines.map(machine => <Checkbox key={machine.id} label={machine.name} checked={held.has(machine.id)} disabled={busy}
          onChange={event => void ((event.target as HTMLInputElement).checked ? give(machine.id) : withdraw(machine.id))} />)}
      </div>}>
        <Button size="small" variant="minimal" icon={<AppIcon name="remote" size={14} />} text={t("Machines")} disabled={busy} />
      </PopoverNext>
      <span className="secret-delete">
        <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} aria-label={t("Delete {name}", { name: secret.name })}
          disabled={busy} onClick={() => setDeleting(true)} />
        {deleting && <ConfirmPopover always intent="danger" busy={busy} placement="bottom-end" title={t("Delete this value?")} subject={secret.name}
          detail={t("What refers to it stops working until it is set again.")} confirmLabel={t("Delete")}
          onConfirm={() => { setDeleting(false); void remove(); }} onCancel={() => setDeleting(false)} />}
      </span>
    </span>
  </li>;
}

/** The form that sets a value: its kind, name and value, its scope, whether only MCP servers and plugin tools get it, and its machines. */
function NewValueForm({ overview, busy, onSave, onCancel }: {
  overview: FleetSecretsOverview; busy: boolean; onCancel: () => void;
  onSave: (request: { kind: string; name: string; scopeKind: string; scopeId: string | null; value: string; hostOnly: boolean; machines: string[] }) => Promise<void>;
}) {
  const { t } = useShellLanguage();
  const scopes = scopeOptions(overview);
  const [kind, setKind] = useState<"secret" | "variable">("secret");
  const [name, setName] = useState("");
  const [value, setValue] = useState("");
  const [scope, setScope] = useState(scopes[0].value);
  const [hostOnly, setHostOnly] = useState(false);
  const [machines, setMachines] = useState<readonly string[]>(() => overview.machines.map(machine => machine.id));
  const chosen = scopes.find(option => option.value === scope) ?? scopes[0];
  const taken = overview.secrets.filter(secret => secret.kind === kind && secret.scopeKind === chosen.kind && (chosen.kind === "person" || secret.scopeLabel === chosen.name))
    .map(secret => secret.name);
  const problem = secretNameProblem(name, taken);
  const ready = isSecretName(name.trim()) && !problem && value.length > 0 && !busy;
  const save = async () => {
    if (!ready) return;
    const typed = value;
    setValue("");
    await onSave({ kind, name: name.trim(), scopeKind: chosen.kind, scopeId: chosen.id, value: typed, hostOnly: kind === "secret" && hostOnly, machines: [...machines] });
  };
  return <form className="fleet-secret-new" onSubmit={event => { event.preventDefault(); void save(); }}>
    <div className="fleet-secret-new-row">
      <HTMLSelect value={kind} disabled={busy} aria-label={t("Kind")} onChange={event => setKind(event.target.value as "secret" | "variable")}
        options={[{ value: "secret", label: t("Secret") }, { value: "variable", label: t("Variable") }]} />
      <InputGroup small autoFocus value={name} placeholder="GRAFANA_TOKEN" disabled={busy} intent={problem ? "danger" : "none"} aria-label={t("Name")}
        onChange={event => setName(event.target.value)} />
      <InputGroup small value={value} type={kind === "secret" ? "password" : "text"} autoComplete="off" disabled={busy} placeholder={t("Value")} aria-label={t("Value")}
        onChange={event => setValue(event.target.value)} />
    </div>
    <div className="fleet-secret-new-row">
      <HTMLSelect value={scope} disabled={busy} aria-label={t("Where it applies")} onChange={event => setScope(event.target.value)}
        options={scopes.map(option => ({ value: option.value, label: t(option.label, { name: option.name ?? "" }) }))} />
      {kind === "secret" && <Checkbox className="fleet-secret-host-only" label={t("Only for MCP servers and plugin tools, never the agent's shell")} checked={hostOnly} disabled={busy}
        onChange={event => setHostOnly((event.target as HTMLInputElement).checked)} />}
    </div>
    <div className="fleet-secret-new-row" role="group" aria-label={t("Machines")}>
      <span className="muted-text">{t("Machines")}</span>
      {overview.machines.length === 0 && <span className="muted-text">{t("No machine of yours.")}</span>}
      {overview.machines.map(machine => <Checkbox key={machine.id} inline label={machine.name} checked={machines.includes(machine.id)} disabled={busy}
        onChange={event => { const checked = (event.target as HTMLInputElement).checked; setMachines(current => checked ? [...current, machine.id] : current.filter(id => id !== machine.id)); }} />)}
    </div>
    <div className="fleet-secret-new-row fleet-secret-new-actions">
      {problem && <span className="secret-problem" role="alert">{t(problem)}</span>}
      <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={onCancel} />
      <Button type="submit" size="small" intent="primary" text={t("Add")} disabled={!ready} />
    </div>
  </form>;
}

/** One environment: its name and rules; Remove. */
function EnvironmentRow({ environment, busy, remove }: { environment: FleetEnvironmentView; busy: boolean; remove: () => Promise<boolean> }) {
  const { t } = useShellLanguage();
  const [removing, setRemoving] = useState(false);
  return <li className="secret-row fleet-environment-row" data-environment={environment.name}>
    <span className="secret-name"><code title={environment.name}>{environment.name}</code></span>
    <span className="fleet-secret-tags">
      {environment.approverNames.length > 0 ? <Tag minimal intent="warning">{t("Approved by {names}", { names: environment.approverNames.join(", ") })}</Tag>
        : <Tag minimal>{t("No approval")}</Tag>}
      {environment.labels.length > 0 && <Tag minimal>{t("On machines tagged {tags}", { tags: environment.labels.join(", ") })}</Tag>}
      {environment.automations.length > 0 && <Tag minimal>{t("{count} automations", { count: String(environment.automations.length) })}</Tag>}
      {!environment.verified && <Tag minimal intent="danger">{t("Its rules do not verify")}</Tag>}
    </span>
    <span className="secret-meta">{t("Version {version}", { version: String(environment.version) })}</span>
    <span className="secret-actions">
      <span className="secret-delete">
        <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} aria-label={t("Remove {name}", { name: environment.name })}
          disabled={busy} onClick={() => setRemoving(true)} />
        {removing && <ConfirmPopover always intent="danger" busy={busy} placement="bottom-end" title={t("Remove this environment?")} subject={environment.name}
          detail={t("Its runs no longer start, and its values are no longer given.")} confirmLabel={t("Remove")}
          onConfirm={() => { setRemoving(false); void remove(); }} onCancel={() => setRemoving(false)} />}
      </span>
    </span>
  </li>;
}

/** The form that adds an environment: its name, the people who approve its runs, the tags of its machines. */
function NewEnvironmentForm({ overview, busy, onSave }: { overview: FleetSecretsOverview; busy: boolean; onSave: (name: string, approvers: string[], labels: string[]) => Promise<boolean> }) {
  const { t } = useShellLanguage();
  const [name, setName] = useState("");
  const [approvers, setApprovers] = useState<readonly string[]>([]);
  const [labels, setLabels] = useState("");
  const trimmed = name.trim();
  const taken = overview.environments.some(environment => environment.name === trimmed);
  const ready = isEnvironmentName(trimmed) && !taken && !busy;
  return <form className="fleet-secret-new fleet-environment-new" onSubmit={async event => {
    event.preventDefault();
    if (ready && await onSave(trimmed, [...approvers], parseLabels(labels))) { setName(""); setApprovers([]); setLabels(""); }
  }}>
    <div className="fleet-secret-new-row">
      <InputGroup small value={name} placeholder="production" disabled={busy} intent={trimmed && !isEnvironmentName(trimmed) || taken ? "danger" : "none"}
        aria-label={t("Name of the environment")} onChange={event => setName(event.target.value)} />
      <InputGroup small value={labels} placeholder={t("Tags of its machines")} disabled={busy} aria-label={t("Tags of its machines")}
        onChange={event => setLabels(event.target.value)} />
      <Button type="submit" size="small" text={t("Add")} disabled={!ready} />
    </div>
    {overview.people.length > 0 && <div className="fleet-secret-new-row" role="group" aria-label={t("Approved by")}>
      <span className="muted-text">{t("Approved by")}</span>
      {overview.people.map(person => <Checkbox key={person.id} inline label={person.name} checked={approvers.includes(person.id)} disabled={busy}
        onChange={event => { const checked = (event.target as HTMLInputElement).checked; setApprovers(current => checked ? [...current, person.id] : current.filter(id => id !== person.id)); }} />)}
    </div>}
    {trimmed && !isEnvironmentName(trimmed) && <span className="secret-problem" role="alert">{t("Letters, digits, - and _.")}</span>}
  </form>;
}

/** One run that waits for this computer's person: its environment, its machine, what it is; Approve, confirmed beside it. */
function ApprovalRow({ approval, busy, onApprove }: { approval: FleetApprovalView; busy: boolean; onApprove: () => void }) {
  const { t, locale } = useShellLanguage();
  const asked = new Date(approval.requestedAt).toLocaleString(locale, { dateStyle: "medium", timeStyle: "short" });
  return <li className="secret-row fleet-approval-row" data-approval={approval.id}>
    <span className="secret-name"><code title={approval.environment}>{approval.environment}</code>
      <span className="secret-reference" title={approval.runId}>{approval.title ?? approval.runId}</span></span>
    <Tag minimal intent="warning">{t("Waiting")}</Tag>
    <span className="secret-meta">{t("{machine} asked {date}", { machine: approval.machine, date: asked })}</span>
    <span className="secret-actions">
      <PopoverNext placement="left" content={<div className="fleet-server-confirm">
        <p>{t("Approve this run of {environment} on {machine}?", { environment: approval.environment, machine: approval.machine })}</p>
        <Button className="bp6-popover-dismiss" intent="primary" disabled={busy} onClick={onApprove}>{t("Approve")}</Button></div>}>
        <Button size="small" intent="primary" disabled={busy}>{t("Approve…")}</Button>
      </PopoverNext>
    </span>
  </li>;
}
