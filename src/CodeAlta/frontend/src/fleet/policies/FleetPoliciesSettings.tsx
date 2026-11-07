import { useCallback, useEffect, useState, type ReactNode } from "react";
import { Button, Callout, Card, Checkbox, HTMLSelect, InputGroup, NonIdealState, PopoverNext, Radio, RadioGroup, Switch, Tag } from "@blueprintjs/core";
import { fleetServer, type FleetPolicyOverview, type FleetPolicyRulesView, type FleetPolicyStopView, type FleetServerView } from "#neoastra";
import { AppIcon } from "../../AppIcon";
import { ActivitySpinner } from "../../ActivitySpinner";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../../SettingsPage";
import { FleetHelp } from "../console/FleetHelp";
import { useShellLanguage } from "../../shellLanguage";
import type { MessageKey } from "../../localization";
import { serverProblem } from "../fleetServer";
import { draftOf, minutesOf, ruleCount, rulesOf, sameDraft, stopReady, stopScopes, stopSentence, type RulesDraft } from "./policies";

export type FleetPoliciesApi = Pick<typeof fleetServer, "status" | "policy" | "preparePolicy" | "signPolicy">;
type Problem = { key: MessageKey; detail?: string | null };

const call = { timeoutMilliseconds: 60_000 };

/**
 * Settings > Fleet > Policies: the signed policy of each server this computer is linked to. The kill switch first, with what
 * it stops and since when, set and lifted by a signer in one action confirmed beside its button; then the rules of the
 * organization, which a signer changes and signs as this computer reads them; then the versions, the newest first.
 */
export function FleetPoliciesSettings({ epoch, api = fleetServer }: { epoch: string | null; api?: FleetPoliciesApi }) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload } = useSettingsEditor(options => api.status({ expectedEpoch: epoch ?? "" }, options), `${epoch}`);
  const servers = (listing?.servers ?? []).filter(server => server.controller && server.link === "approved");
  return <SettingsPage className="fleet-policies-settings" label={t("Policies")} group="Fleet" title="Policies"
    description="The rules of your organization, and its kill switch."
    notice={notice} loading={loading} busy={busy} onReload={reload}
    actions={<FleetHelp text="The rules of your organization for the work of the fleet, and the kill switch that stops it. Each machine verifies what an owner signed before it applies it." page="fleet/policies-and-audit.md" />}>
    {!listing ? <SettingsUnavailable loading={loading} icon="shield" title="Servers unavailable" />
      : servers.length === 0 ? <NonIdealState className="fleet-secrets-empty" icon={<AppIcon name="server" size={36} />} title={t("Not linked to a server.")} />
      : servers.map(server => <ServerPolicy key={server.id} server={server} epoch={epoch ?? ""} api={api} titled={servers.length > 1} onChanged={reload} />)}
  </SettingsPage>;
}

type Context = { overview: FleetPolicyOverview; api: FleetPoliciesApi; epoch: string; linkId: string; busy: boolean; sign: (rules: FleetPolicyRulesView | null, stop: FleetPolicyStopView | null, done: MessageKey) => Promise<boolean> };

/** The policy of one server. */
function ServerPolicy({ server, epoch, api, titled, onChanged }: { server: FleetServerView; epoch: string; api: FleetPoliciesApi; titled: boolean; onChanged: () => void }) {
  const { t } = useShellLanguage();
  const [overview, setOverview] = useState<FleetPolicyOverview | null>(null);
  const [problem, setProblem] = useState<Problem | null>(null);
  const [done, setDone] = useState<MessageKey | null>(null);
  const [busy, setBusy] = useState(false);
  const [generation, setGeneration] = useState(0);
  useEffect(() => {
    const abort = new AbortController();
    api.policy({ expectedEpoch: epoch, linkId: server.id }, { signal: abort.signal, ...call }).then(value => {
      if (abort.signal.aborted) return;
      if (value.status === "ok") { setOverview(value); setProblem(null); } else setProblem({ key: serverProblem(value.status) ?? "The operation did not complete.", detail: value.message });
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The server does not answer." }); });
    return () => abort.abort();
  }, [api, epoch, server.id, generation]);

  // The next version, prepared and signed by this computer for exactly the changes it reads.
  const sign = useCallback(async (rules: FleetPolicyRulesView | null, stop: FleetPolicyStopView | null, message: MessageKey) => {
    setBusy(true);
    setDone(null);
    try {
      const preview = await api.preparePolicy({ expectedEpoch: epoch, linkId: server.id, rules, stop }, call);
      if (preview.status !== "ok") { setProblem({ key: serverProblem(preview.status) ?? "The operation did not complete.", detail: preview.message }); return false; }
      if (preview.changes.length === 0) { setProblem(null); return true; }
      const signed = await api.signPolicy({ expectedEpoch: epoch, linkId: server.id, rules, stop, changes: preview.changes }, call);
      if (signed.status === "ok") { setProblem(null); setDone(message); onChanged(); return true; }
      setProblem({ key: signed.status === "changed" ? "The policy changed meanwhile: review it again." : serverProblem(signed.status) ?? "The operation did not complete.",
        detail: signed.message });
      return false;
    } catch {
      setProblem({ key: "The server does not answer." });
      return false;
    } finally {
      setBusy(false);
      setGeneration(value => value + 1);
    }
  }, [api, epoch, server.id, onChanged]);

  if (!overview) return <section className="fleet-secrets-server" aria-label={server.url}>
    {problem ? <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>
      : <ActivitySpinner size={16} />}
  </section>;
  const context: Context = { overview, api, epoch, linkId: server.id, busy, sign };
  return <section className="fleet-secrets-server fleet-policies-server settings-grid" aria-label={server.url}>
    {titled && <h2 className="fleet-secrets-server-title">{server.organization ?? server.url}</h2>}
    {overview.message && <Callout compact intent="danger" role="alert" title={t("The server answers a policy this computer does not verify.")}>
      {t("Nothing of it applies: machines keep the last version they verified.")}<div className="muted-text">{overview.message}</div>
    </Callout>}
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {done && <Callout compact intent="success" role="status">{t(done)}</Callout>}
    <KillSwitch context={context} />
    <Rules context={context} />
    <Versions context={context} />
  </section>;
}

/** The kill switch: what it stops now, and Stop or Let go on beside its confirmation. */
function KillSwitch({ context }: { context: Context }) {
  const { t, locale } = useShellLanguage();
  const { overview, linkId } = context;
  const stop = overview.stop;
  const since = overview.signedAt ? new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(new Date(overview.signedAt)) : "";
  return <Card className={`appearance-settings fleet-policy-switch${stop ? " is-stopped" : ""}`} aria-labelledby={`switch-${linkId}`}>
    <div className="fleet-secrets-head">
      <h2 id={`switch-${linkId}`}>{t("Kill switch")}</h2>
      {overview.canSign && (stop ? <Lift context={context} /> : <Stop context={context} />)}
    </div>
    {stop
      ? <Callout intent="danger" className="fleet-policy-stopped" role="status">
        <strong>{t(stopSentence(stop).key, stopSentence(stop).values)}</strong>
        {stop.reason && <div className="fleet-policy-reason">{stop.reason}</div>}
        <div className="muted-text">{t("Signed by {name}, {time}. Machines end the sessions and runs of the fleet and take no more; what people do at a machine goes on.",
          { name: overview.signedBy ?? t("an owner"), time: since })}</div>
      </Callout>
      : <p className="muted-text fleet-policy-running"><AppIcon name="check" size={14} /> {t("The work of the fleet goes on on every machine.")}</p>}
    {!overview.canSign && <p className="muted-text">{t("Owners and admins who sign set and lift the kill switch.")}</p>}
  </Card>;
}

/** Stop: what it reaches and why, confirmed in the popover beside the button, then signed in one action. */
function Stop({ context }: { context: Context }) {
  const { t } = useShellLanguage();
  const { overview, busy, sign } = context;
  const [open, setOpen] = useState(false);
  const [scope, setScope] = useState<"fleet" | "machines" | "teams">("fleet");
  const [machines, setMachines] = useState<string[]>([]);
  const [teams, setTeams] = useState<string[]>([]);
  const [reason, setReason] = useState("");
  const toggle = (list: string[], id: string) => list.includes(id) ? list.filter(item => item !== id) : [...list, id];
  const ready = stopReady(scope, machines, teams);
  const confirm = async () => {
    const stop: FleetPolicyStopView = { scope, machines: scope === "machines" ? machines : [], teams: scope === "teams" ? teams : [], reason: reason.trim() || null, names: [] };
    if (await sign(overview.rules, stop, "Stopped: each machine it reaches ends the work of the fleet as soon as it reads it.")) setOpen(false);
  };
  return <PopoverNext isOpen={open} placement="bottom-end" onInteraction={next => { if (!next && !busy) setOpen(false); }}
    content={<div className="fleet-server-confirm fleet-policy-stop-form" role="dialog" aria-label={t("Stop the work of the fleet")}>
      <strong>{t("Stop the work of the fleet")}</strong>
      <RadioGroup selectedValue={scope} onChange={event => setScope(event.currentTarget.value as typeof scope)}>
        {stopScopes.map(choice => <Radio key={choice.value} value={choice.value} label={t(choice.label)}
          disabled={choice.value === "teams" && overview.teams.length === 0 || choice.value === "machines" && overview.machines.length === 0} />)}
      </RadioGroup>
      {scope === "machines" && <div className="fleet-policy-choices" role="group" aria-label={t("Chosen machines")}>
        {overview.machines.map(machine => <Checkbox key={machine.id} checked={machines.includes(machine.id)} onChange={() => setMachines(value => toggle(value, machine.id))}
          labelElement={<span>{machine.name}{machine.team && <span className="muted-text"> · {machine.team}</span>}</span>} />)}
      </div>}
      {scope === "teams" && <div className="fleet-policy-choices" role="group" aria-label={t("The machines of chosen teams")}>
        {overview.teams.map(team => <Checkbox key={team.id} checked={teams.includes(team.id)} label={team.name} onChange={() => setTeams(value => toggle(value, team.id))} />)}
      </div>}
      <InputGroup size="small" placeholder={t("Why (the machines show it)")} aria-label={t("Why (the machines show it)")} value={reason} maxLength={500}
        onValueChange={value => setReason(value)} />
      <p className="muted-text">{t("This computer signs the stop with its key. Each machine it reaches ends the sessions and runs of the fleet and takes no more until the stop is lifted, also while the server is away.")}</p>
      <span className="fleet-policy-confirm-actions">
        <Button intent="danger" icon={<AppIcon name="hand" size={14} />} disabled={!ready || busy} loading={busy} text={t("Stop the work")} onClick={() => void confirm()} />
        <Button variant="minimal" disabled={busy} text={t("Cancel")} onClick={() => setOpen(false)} />
      </span>
    </div>}>
    <Button intent="danger" icon={<AppIcon name="hand" size={14} />} disabled={busy} text={t("Stop the fleet…")} onClick={() => setOpen(true)} />
  </PopoverNext>;
}

/** Let go on: the next version without the stop, confirmed beside its button. */
function Lift({ context }: { context: Context }) {
  const { t } = useShellLanguage();
  const { overview, busy, sign } = context;
  const [open, setOpen] = useState(false);
  return <PopoverNext isOpen={open} placement="bottom-end" onInteraction={next => { if (!next && !busy) setOpen(false); }}
    content={<div className="fleet-server-confirm fleet-policy-lift-form" role="dialog" aria-label={t("Let the work of the fleet go on")}>
      <p>{t("The machines take work again once they read this version.")}</p>
      <span className="fleet-policy-confirm-actions">
        <Button intent="primary" disabled={busy} loading={busy} text={t("Let the work go on")}
          onClick={() => void sign(overview.rules, null, "The work of the fleet goes on.").then(ok => { if (ok) setOpen(false); })} />
        <Button variant="minimal" disabled={busy} text={t("Cancel")} onClick={() => setOpen(false)} />
      </span>
    </div>}>
    <Button intent="primary" icon={<AppIcon name="play" size={14} />} disabled={busy} text={t("Let the work go on…")} onClick={() => setOpen(true)} />
  </PopoverNext>;
}

/** The rules: a form a signer changes, reviewed as this computer reads it, then signed. */
function Rules({ context }: { context: Context }) {
  const { t } = useShellLanguage();
  const { overview, api, epoch, linkId, busy, sign } = context;
  const saved = draftOf(overview.rules);
  const [draft, setDraft] = useState<RulesDraft>(saved);
  const [review, setReview] = useState<string[] | null>(null);
  const [reading, setReading] = useState(false);
  const savedKey = JSON.stringify(saved);
  useEffect(() => setDraft(JSON.parse(savedKey) as RulesDraft), [savedKey]);
  const set = (change: Partial<RulesDraft>) => setDraft(value => ({ ...value, ...change }));
  const rules = rulesOf(draft);
  const changed = !sameDraft(draft, saved);
  const readOnly = !overview.canSign;
  const open = async () => {
    if (!rules) return;
    setReading(true);
    try {
      const preview = await api.preparePolicy({ expectedEpoch: epoch, linkId, rules, stop: overview.stop }, call);
      setReview(preview.status === "ok" ? [...preview.changes] : []);
    } finally {
      setReading(false);
    }
  };
  const field = (label: MessageKey, hint: MessageKey, control: ReactNode) => <label className="fleet-policy-field">
    <span className="fleet-policy-label">{t(label)}</span>{control}<span className="fleet-policy-hint muted-text">{t(hint)}</span>
  </label>;
  return <Card className="appearance-settings fleet-policy-rules" aria-labelledby={`rules-${linkId}`}>
    <div className="fleet-secrets-head">
      <h2 id={`rules-${linkId}`}>{t("Rules")}</h2>
      <Tag minimal>{ruleCount(overview.rules) === 0 ? t("No rule") : t(ruleCount(overview.rules) === 1 ? "1 rule" : "{count} rules", { count: ruleCount(overview.rules) })}</Tag>
    </div>
    <p className="muted-text">{t("They apply to the work of the fleet in the spaces linked to this server: sessions started from another device, agents of other machines, automations. A rule only narrows what a machine allows.")}</p>
    <fieldset className="fleet-policy-form" disabled={readOnly || busy}>
      {field("Providers", "Kinds of providers, such as anthropic, openai. Empty: any.",
        <InputGroup size="small" value={draft.providers} placeholder={t("Any")} onValueChange={value => set({ providers: value })} />)}
      {field("Models", "Model ids; a trailing * names a prefix, such as claude-*. Empty: any.",
        <InputGroup size="small" value={draft.models} placeholder={t("Any")} onValueChange={value => set({ models: value })} />)}
      {field("Sandbox at least", "The profile the work runs in at least, such as workspace. Empty: no floor.",
        <InputGroup size="small" value={draft.sandbox} placeholder={t("No floor")} onValueChange={value => set({ sandbox: value })} />)}
      {field("Network at most", "The widest network of its sandbox.",
        <HTMLSelect value={draft.network} onChange={event => set({ network: event.currentTarget.value })}
          options={[{ value: "", label: t("No limit") }, { value: "off", label: t("None") }, { value: "hosts", label: t("Hosts of the profile") }, { value: "on", label: t("The internet") }]} />)}
      {field("Tools turned off", "Names of tools, or patterns such as web_*.",
        <InputGroup size="small" value={draft.toolsOff} placeholder={t("None")} onValueChange={value => set({ toolsOff: value })} />)}
      {field("Permission mode at most", "The widest mode the work runs in.",
        <HTMLSelect value={draft.permissionMode} onChange={event => set({ permissionMode: event.currentTarget.value })}
          options={[{ value: "", label: t("No limit") }, { value: "default", label: t("Ask") }, { value: "acceptEdits", label: t("Accept edits") },
            { value: "bypassPermissions", label: t("Bypass permissions") }]} />)}
      {field("People start sessions elsewhere", "Whether a person starts a session on another machine from their device.",
        <HTMLSelect value={draft.remoteStarts} onChange={event => set({ remoteStarts: event.currentTarget.value as RulesDraft["remoteStarts"] })}
          options={[{ value: "any", label: t("Allowed") }, { value: "no", label: t("Not allowed") }]} />)}
      {field("Agents start work elsewhere", "Whether agents start sessions or send prompts on other machines, where a machine allows it.",
        <HTMLSelect value={draft.agentStarts} onChange={event => set({ agentStarts: event.currentTarget.value as RulesDraft["agentStarts"] })}
          options={[{ value: "any", label: t("As each machine allows") }, { value: "no", label: t("Not allowed") }]} />)}
      {field("Longest session", "In minutes; a machine stops a session of the fleet after it. Empty: no limit.",
        <InputGroup size="small" value={draft.maxSessionMinutes} placeholder={t("No limit")} intent={minutesOf(draft.maxSessionMinutes) === undefined ? "danger" : "none"}
          onValueChange={value => set({ maxSessionMinutes: value })} />)}
      <div className="fleet-policy-switches">
        <Switch checked={draft.publishFacts} label={t("Team machines publish the facts of every session of the organization's spaces")}
          onChange={event => set({ publishFacts: event.currentTarget.checked })} />
        <Switch checked={draft.accountCredentials} label={t("Every automation runs with account credentials, never a personal plan")}
          onChange={event => set({ accountCredentials: event.currentTarget.checked })} />
      </div>
    </fieldset>
    {!readOnly && <div className="fleet-policy-actions">
      <PopoverNext isOpen={review !== null} placement="top-start" onInteraction={next => { if (!next && !busy) setReview(null); }}
        content={<div className="fleet-server-confirm fleet-policy-review" role="dialog" aria-label={t("Review and sign")}>
          {review && review.length === 0 ? <p>{t("Nothing changes.")}</p> : <>
            <p>{t("This computer signs these changes with its key:")}</p>
            <ul className="fleet-access-changes">{(review ?? []).map(line => <li key={line}>{line}</li>)}</ul>
            <Button intent="primary" disabled={busy || !review} text={t("Sign")}
              onClick={() => { setReview(null); void sign(rules, overview.stop, "Signed: machines apply these rules once they read them."); }} />
          </>}
        </div>}>
        <Button intent="primary" icon={<AppIcon name="secret" size={14} />} disabled={!changed || !rules || busy || reading} text={t("Review and sign…")} onClick={() => void open()} />
      </PopoverNext>
      {changed && <Button variant="minimal" disabled={busy} text={t("Undo the changes")} onClick={() => setDraft(saved)} />}
    </div>}
  </Card>;
}

/** The versions of the policy, the newest first, each with what it changed. */
function Versions({ context }: { context: Context }) {
  const { t, locale } = useShellLanguage();
  const { overview, linkId } = context;
  if (overview.versions.length === 0) return <Card className="appearance-settings" aria-labelledby={`versions-${linkId}`}>
    <h2 id={`versions-${linkId}`}>{t("Versions")}</h2>
    <p className="muted-text">{t("No version is signed yet: machines follow their own policy only.")}</p>
  </Card>;
  const format = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" });
  return <Card className="appearance-settings" aria-labelledby={`versions-${linkId}`}>
    <h2 id={`versions-${linkId}`}>{t("Versions")}</h2>
    <ol className="fleet-policy-versions">
      {overview.versions.map(version => <li key={version.number} className="fleet-policy-version" data-version={version.number}>
        <div className="fleet-policy-version-head">
          <strong>#{version.number}</strong>
          <span className="muted-text">{version.signedBy ?? t("an owner")} · {format.format(new Date(version.signedAt))}</span>
          {version.stops && <Tag minimal intent="danger">{t("Stop")}</Tag>}
        </div>
        <ul className="fleet-access-changes">{version.changes.map(line => <li key={line}>{line}</li>)}</ul>
      </li>)}
    </ol>
  </Card>;
}
