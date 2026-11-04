import { useCallback, useEffect, useState } from "react";
import { Button, Callout, Card, Checkbox, HTMLSelect, InputGroup, NonIdealState, PopoverNext, Switch, Tag } from "@blueprintjs/core";
import { fleetServer, type FleetAccessOverview, type FleetAccessOwnerView, type FleetAccessPersonView, type FleetAccessTeamView, type FleetServerView } from "#neoastra";
import { AppIcon } from "../../AppIcon";
import { ActivitySpinner } from "../../ActivitySpinner";
import { ConfirmPopover } from "../../ConfirmPopover";
import { SettingsPage, SettingsUnavailable, useSettingsEditor } from "../../SettingsPage";
import { useShellLanguage } from "../../shellLanguage";
import type { MessageKey } from "../../localization";
import { serverProblem } from "../fleetServer";
import { onCallSummary, shiftChoices } from "./access";
import { assignableRoles, attention, attentionCount, canManagePerson, grantCapabilities, isAdvanced, isContentCapability, managesAccess, nameOf, ownerTargets, parseTarget,
  proposalDetail, roleHints, roleLabels } from "./access";

export type FleetAccessApi = Pick<typeof fleetServer, "status" | "access" | "invitePerson" | "setPersonRole" | "deprovisionPerson" | "createTeam" | "updateTeam" | "dissolveTeam"
  | "setTeamMember" | "createGrant" | "revokeGrant" | "confirmGrantKeys" | "offerTransfer" | "decideTransfer" | "resumeResource" | "createServiceIdentity" | "enableServiceIdentity"
  | "decideProposal" | "prepareAccessDocument" | "signAccessDocument" | "accessReview" | "setOnCall">;
type Result = Promise<{ status: string; message?: string | null }>;
type Run = (call: () => Result, done?: MessageKey) => Promise<boolean>;
type Context = { overview: FleetAccessOverview; api: FleetAccessApi; epoch: string; linkId: string; busy: boolean; run: Run };

/** How often people and access are read again while the page shows them. */
const pollMilliseconds = 15_000;
const call = { timeoutMilliseconds: 30_000 };

/**
 * Settings > Fleet > People and access: who is in the organization of each server this computer is linked to, what each may
 * do, who owns its machines and automations, and what waits for this person (changes machines follow once a signer signs
 * them, transfers, the proposals of agents, resources whose owner left). At home it shows people and the owners of machines;
 * teams, grants and service identities come with an organization that uses them, or under Advanced.
 */
export function FleetAccessSettings({ epoch, api = fleetServer }: { epoch: string | null; api?: FleetAccessApi }) {
  const { t } = useShellLanguage();
  const { listing, loading, busy, notice, reload } = useSettingsEditor(options => api.status({ expectedEpoch: epoch ?? "" }, options), `${epoch}`);
  const servers = (listing?.servers ?? []).filter(server => server.controller && server.link === "approved");
  return <SettingsPage className="fleet-access-settings" label={t("People and access")} group="Fleet" title="People and access"
    description="Who is in your organization, what each person may do, and who owns your machines and automations." notice={notice} loading={loading} busy={busy}
    onReload={reload}>
    {!listing ? <SettingsUnavailable loading={loading} icon="user" title="Servers unavailable" />
      : servers.length === 0 ? <NonIdealState className="fleet-secrets-empty" icon={<AppIcon name="server" size={36} />} title={t("Not linked to a server.")} />
      : servers.map(server => <ServerAccess key={server.id} server={server} epoch={epoch ?? ""} api={api} titled={servers.length > 1} />)}
  </SettingsPage>;
}

/** People and access at one server. */
function ServerAccess({ server, epoch, api, titled }: { server: FleetServerView; epoch: string; api: FleetAccessApi; titled: boolean }) {
  const { t } = useShellLanguage();
  const [overview, setOverview] = useState<FleetAccessOverview | null>(null);
  const [problem, setProblem] = useState<{ key: MessageKey; detail?: string | null } | null>(null);
  const [done, setDone] = useState<MessageKey | null>(null);
  const [busy, setBusy] = useState(false);
  const [generation, setGeneration] = useState(0);
  const [advanced, setAdvanced] = useState(false);
  useEffect(() => {
    const abort = new AbortController();
    api.access({ expectedEpoch: epoch, linkId: server.id }, { signal: abort.signal, ...call }).then(value => {
      if (abort.signal.aborted) return;
      if (value.status === "ok") { setOverview(value); setProblem(null); } else setProblem({ key: serverProblem(value.status) ?? "The operation did not complete.", detail: value.message });
    }, () => { if (!abort.signal.aborted) setProblem({ key: "The server does not answer." }); });
    return () => abort.abort();
  }, [api, epoch, server.id, generation]);
  useEffect(() => {
    const timer = setInterval(() => setGeneration(value => value + 1), pollMilliseconds);
    return () => clearInterval(timer);
  }, []);
  const run = useCallback<Run>(async (action, message) => {
    setBusy(true);
    setDone(null);
    try {
      const result = await action();
      if (result.status === "ok") { setProblem(null); if (message) setDone(message); return true; }
      setProblem({ key: result.status === "changed" ? "Access changed meanwhile: review the changes again." : serverProblem(result.status) ?? "The operation did not complete.",
        detail: result.message });
      return false;
    } catch {
      setProblem({ key: "The server does not answer." });
      return false;
    } finally {
      setBusy(false);
      setGeneration(value => value + 1);
    }
  }, []);
  if (!overview) return <section className="fleet-secrets-server" aria-label={server.url}>
    {problem ? <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>
      : <ActivitySpinner size={16} />}
  </section>;
  const context: Context = { overview, api, epoch, linkId: server.id, busy, run };
  const showAdvanced = isAdvanced(overview) || advanced;
  const waiting = attention(overview);
  return <section className="fleet-secrets-server fleet-access-server settings-grid" aria-label={server.url}>
    {titled && <h2 className="fleet-secrets-server-title">{server.organization ?? server.url}</h2>}
    {problem && <Callout compact intent="warning" role="alert">{t(problem.key)}{problem.detail && <div className="muted-text">{problem.detail}</div>}</Callout>}
    {done && <Callout compact intent="success" role="status">{t(done)}</Callout>}
    {attentionCount(waiting) > 0 && <Waiting context={context} />}
    <People context={context} />
    {showAdvanced && <Teams context={context} />}
    {overview.owners.length > 0 && <Owners context={context} />}
    {showAdvanced && <Grants context={context} />}
    {showAdvanced && <Identities context={context} />}
    <div className="fleet-access-footer">
      {!isAdvanced(overview) && managesAccess(overview) && <Switch checked={advanced} label={t("Show teams, grants and service identities")}
        onChange={event => setAdvanced(event.currentTarget.checked)} />}
      {overview.capabilities.includes("access.review") && <Review context={context} onCopied={() => setDone("Who can do what was copied as CSV.")} />}
    </div>
  </section>;
}

/** What waits for this person: the changes to sign, transfers to take, proposals of agents, resources whose owner left or that wait. */
function Waiting({ context }: { context: Context }) {
  const { t } = useShellLanguage();
  const { overview, api, epoch, linkId, busy, run } = context;
  const waiting = attention(overview);
  return <Card className="appearance-settings fleet-access-waiting" aria-labelledby={`waiting-${linkId}`}>
    <h2 id={`waiting-${linkId}`}>{t("Waiting for you")}</h2>
    {waiting.sign.length > 0 && <Sign context={context} pending={waiting.sign} />}
    <ul className="secret-list" aria-label={t("Waiting for you")}>
      {waiting.transfers.map(transfer => <li key={transfer.id} className="secret-row fleet-access-row" data-transfer={transfer.id}>
        <span className="secret-name"><code>{transfer.resourceName}</code>
          <span className="secret-reference">{t("{from} gives it to {to}", { from: transfer.fromName, to: transfer.toName })}</span></span>
        <Tag minimal>{t(transfer.resourceKind === "machine" ? "Machine" : "Automation")}</Tag>
        <span className="secret-actions">
          <Button size="small" intent="primary" disabled={busy} text={t("Accept")}
            onClick={() => void run(() => api.decideTransfer({ expectedEpoch: epoch, linkId, id: transfer.id, verb: "accept" }, call), "It is yours now.")} />
          <Button size="small" variant="minimal" disabled={busy} text={t("Decline")}
            onClick={() => void run(() => api.decideTransfer({ expectedEpoch: epoch, linkId, id: transfer.id, verb: "decline" }, call))} />
        </span>
      </li>)}
      {waiting.proposals.map(proposal => <li key={proposal.id} className="secret-row fleet-access-row" data-proposal={proposal.id}>
        <span className="secret-name"><span className="fleet-access-summary">{proposal.summary}</span>
          <span className="secret-reference">{t("Proposed by an agent of {name}", { name: proposal.personName })} · {proposalDetail(proposal.payload, id => nameOf(overview, id))}</span></span>
        <span className="secret-actions">
          <Button size="small" intent="primary" disabled={busy} text={t("Apply")}
            onClick={() => void run(() => api.decideProposal({ expectedEpoch: epoch, linkId, id: proposal.id, apply: true }, call), "The change was applied.")} />
          <Button size="small" variant="minimal" disabled={busy} text={t("Dismiss")}
            onClick={() => void run(() => api.decideProposal({ expectedEpoch: epoch, linkId, id: proposal.id, apply: false }, call))} />
        </span>
      </li>)}
      {waiting.orphans.map(owner => <OwnerRow key={`${owner.kind}:${owner.id}`} context={context} owner={owner} note={t("Its owner left: give it to someone")} />)}
      {waiting.paused.map(owner => <OwnerRow key={`${owner.kind}:${owner.id}`} context={context} owner={owner} note={t("It waits for you to resume it")} />)}
    </ul>
  </Card>;
}

/** The changes machines do not follow yet: reviewed as this computer reads them, then signed with its key. */
function Sign({ context, pending }: { context: Context; pending: readonly string[] }) {
  const { t } = useShellLanguage();
  const { api, epoch, linkId, busy, run } = context;
  const [review, setReview] = useState<string[] | null>(null);
  const [reading, setReading] = useState(false);
  const open = async () => {
    setReading(true);
    try {
      const preview = await api.prepareAccessDocument({ expectedEpoch: epoch, linkId }, call);
      setReview(preview.status === "ok" ? [...preview.changes] : []);
    } finally {
      setReading(false);
    }
  };
  return <div className="fleet-access-sign" aria-label={t("Changes machines do not follow yet")}>
    <p className="muted-text">{t("Machines follow teams, team machines and suspensions only once an owner or an admin signs them.")}</p>
    <ul className="fleet-access-changes">{pending.map(line => <li key={line}>{line}</li>)}</ul>
    <PopoverNext isOpen={review !== null} placement="bottom-start" onInteraction={next => { if (!next && !busy) setReview(null); }}
      content={<div className="fleet-server-confirm fleet-access-review">
        {review && review.length === 0 ? <p>{t("Nothing waits to be signed.")}</p> : <>
          <p>{t("This computer signs these changes with its key:")}</p>
          <ul className="fleet-access-changes">{(review ?? []).map(line => <li key={line}>{line}</li>)}</ul>
          <Button intent="primary" disabled={busy || !review} text={t("Sign")}
            onClick={() => { const changes = review ?? []; setReview(null); void run(() => api.signAccessDocument({ expectedEpoch: epoch, linkId, changes }, call), "Signed: machines follow these changes."); }} />
        </>}
      </div>}>
      <Button size="small" intent="primary" icon={<AppIcon name="secret" size={14} />} disabled={busy || reading} text={t("Review and sign…")} onClick={() => void open()} />
    </PopoverNext>
  </div>;
}

/** The people of the organization, invitations, roles, and a person who leaves. */
function People({ context }: { context: Context }) {
  const { t } = useShellLanguage();
  const { overview, linkId, busy } = context;
  const [inviting, setInviting] = useState(false);
  return <Card className="appearance-settings" aria-labelledby={`people-${linkId}`}>
    <div className="fleet-secrets-head">
      <h2 id={`people-${linkId}`}>{t("People")}</h2>
      {managesAccess(overview) && <Button size="small" intent="primary" icon={<AppIcon name="plus" size={14} />} disabled={busy || inviting} text={t("Invite a person…")}
        onClick={() => setInviting(true)} />}
    </div>
    {inviting && <Invite context={context} onClose={() => setInviting(false)} />}
    <ul className="secret-list" aria-label={t("People")}>
      {overview.people.map(person => <PersonRow key={person.id} context={context} person={person} />)}
    </ul>
  </Card>;
}

/** The form of an invitation, then the code and the address the person joins with. */
function Invite({ context, onClose }: { context: Context; onClose: () => void }) {
  const { t, locale } = useShellLanguage();
  const { overview, api, epoch, linkId, busy } = context;
  const [name, setName] = useState("");
  const [role, setRole] = useState("member");
  const [invitation, setInvitation] = useState<{ token: string; url: string | null; expiresAt: string | null } | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const submit = async () => {
    const result = await api.invitePerson({ expectedEpoch: epoch, linkId, name: name.trim(), role }, call);
    if (result.status === "ok" && result.token) setInvitation({ token: result.token, url: result.serverUrl, expiresAt: result.expiresAt });
    else setProblem(result.message ?? result.status);
  };
  if (invitation) return <div className="fleet-secret-new fleet-access-invitation" role="status">
    <p>{t("Give {name} this code and the address of the server. On their computer: Settings > Fleet > Server > Join an organization…", { name: name.trim() })}</p>
    <div className="fleet-secret-new-row"><InputGroup small readOnly value={invitation.url ?? overview.serverUrl ?? ""} aria-label={t("Address")} />
      <Button size="small" text={t("Copy")} onClick={() => void navigator.clipboard?.writeText(invitation.url ?? overview.serverUrl ?? "")} /></div>
    <div className="fleet-secret-new-row"><InputGroup small readOnly value={invitation.token} aria-label={t("Code")} />
      <Button size="small" text={t("Copy")} onClick={() => void navigator.clipboard?.writeText(invitation.token)} /></div>
    <p className="muted-text">{t("The code works once, until {date}. You approve their computer when it shows its six words.",
      { date: invitation.expiresAt ? new Date(invitation.expiresAt).toLocaleString(locale, { dateStyle: "medium", timeStyle: "short" }) : "" })}</p>
    <div className="fleet-secret-new-row fleet-secret-new-actions"><Button size="small" intent="primary" text={t("Done")} onClick={onClose} /></div>
  </div>;
  const ready = name.trim().length > 0 && name.trim().length <= 64 && !busy;
  return <form className="fleet-secret-new fleet-access-invite" aria-label={t("Invite a person…")} onSubmit={event => { event.preventDefault(); if (ready) void submit(); }}>
    <div className="fleet-secret-new-row">
      <InputGroup small autoFocus value={name} placeholder={t("Name")} aria-label={t("Name")} disabled={busy} onChange={event => setName(event.target.value)} />
      <HTMLSelect value={role} aria-label={t("Role")} disabled={busy} onChange={event => setRole(event.target.value)}
        options={assignableRoles(overview).map(value => ({ value, label: t(roleLabels[value]) }))} />
    </div>
    <p className="muted-text">{t(roleHints[role] ?? "Member")}</p>
    {problem && <Callout compact intent="warning">{problem}</Callout>}
    <div className="fleet-secret-new-row fleet-secret-new-actions">
      <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={onClose} />
      <Button type="submit" size="small" intent="primary" text={t("Invite")} disabled={!ready} />
    </div>
  </form>;
}

/** One person: their name, role, status and teams; their role changed, or their removal from the organization. */
function PersonRow({ context, person }: { context: Context; person: FleetAccessPersonView }) {
  const { t } = useShellLanguage();
  const { overview, api, epoch, linkId, busy, run } = context;
  const [removing, setRemoving] = useState(false);
  const [successor, setSuccessor] = useState("organization:");
  const manage = canManagePerson(overview, person);
  const roles = assignableRoles(overview);
  return <li className="secret-row fleet-access-row fleet-access-person" data-person={person.name} data-status={person.status}>
    <span className="secret-name"><code title={person.name}>{person.name}</code>
      <span className="secret-reference">{person.teams.map(team => `${team.teamName}${team.role === "maintainer" ? ` (${t("maintainer")})` : ""}`).join(", ")}</span></span>
    <span className="fleet-secret-tags">
      {person.isMe && <Tag minimal intent="primary">{t("You")}</Tag>}
      {!person.joined && person.status === "active" && <Tag minimal intent="warning">{t("Invited")}</Tag>}
      {person.status === "suspended" && <Tag minimal intent="danger">{t("Suspended")}</Tag>}
      {manage && roles.includes(person.role)
        ? <HTMLSelect minimal value={person.role} disabled={busy} aria-label={t("Role of {name}", { name: person.name })}
          onChange={event => { const role = event.target.value; void run(() => api.setPersonRole({ expectedEpoch: epoch, linkId, personId: person.id, role }, call)); }}
          options={roles.map(value => ({ value, label: t(roleLabels[value]) }))} />
        : <Tag minimal>{t(roleLabels[person.role] ?? "Member")}</Tag>}
    </span>
    <span className="secret-actions">
      {manage && <span className="secret-delete">
        <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} aria-label={t("Remove {name} from the organization", { name: person.name })}
          disabled={busy} onClick={() => setRemoving(true)} />
        {removing && <PopoverNext isOpen placement="bottom-end" onInteraction={next => { if (!next && !busy) setRemoving(false); }} content={<div className="fleet-server-confirm fleet-access-remove">
          <p><strong>{t("Remove {name} from the organization?", { name: person.name })}</strong></p>
          <p className="muted-text">{t("Their devices are signed out at once. What they owned goes to the one you choose, paused until they resume it; their work on team machines stops once you sign.")}</p>
          <HTMLSelect value={successor} aria-label={t("What they owned goes to")} onChange={event => setSuccessor(event.target.value)}
            options={ownerTargets(overview).filter(target => target.id !== person.id).map(target => ({ value: target.value,
              label: target.kind === "organization" ? t("The organization") : target.kind === "team" ? t("Team {name}", { name: target.name }) : target.name }))} />
          <Button intent="danger" disabled={busy} text={t("Remove from the organization")} onClick={() => {
            const target = parseTarget(successor);
            setRemoving(false);
            void run(() => api.deprovisionPerson({ expectedEpoch: epoch, linkId, personId: person.id, successorKind: target.kind === "organization" ? null : target.kind,
              successorId: target.id }, call), "They left: review and sign so that machines stop their work.");
          }} />
        </div>}><span /></PopoverNext>}
      </span>}
    </span>
  </li>;
}

/** The teams: their members and roles, their machines, whether they run the automations of their members. */
function Teams({ context }: { context: Context }) {
  const { t } = useShellLanguage();
  const { overview, api, epoch, linkId, busy, run } = context;
  const [name, setName] = useState<string | null>(null);
  return <Card className="appearance-settings" aria-labelledby={`teams-${linkId}`}>
    <div className="fleet-secrets-head">
      <h2 id={`teams-${linkId}`}>{t("Teams")}</h2>
      {managesAccess(overview) && <Button size="small" icon={<AppIcon name="plus" size={14} />} disabled={busy || name !== null} text={t("New team…")} onClick={() => setName("")} />}
    </div>
    {name !== null && <form className="fleet-secret-new" aria-label={t("New team…")} onSubmit={event => {
      event.preventDefault();
      const value = name.trim();
      if (value) void run(() => api.createTeam({ expectedEpoch: epoch, linkId, name: value }, call)).then(ok => { if (ok) setName(null); });
    }}>
      <div className="fleet-secret-new-row">
        <InputGroup small autoFocus value={name} placeholder={t("Name")} aria-label={t("Name")} disabled={busy} onChange={event => setName(event.target.value)} />
        <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={() => setName(null)} />
        <Button type="submit" size="small" intent="primary" text={t("Add")} disabled={busy || !name.trim()} />
      </div>
    </form>}
    {overview.teams.length === 0 && name === null
      ? <NonIdealState className="secrets-empty" icon={<AppIcon name="user" size={28} />} title={t("No team yet.")}
        description={t("A team shares machines and automations; its maintainers run them.")} />
      : <ul className="secret-list" aria-label={t("Teams")}>{overview.teams.map(team => <TeamRow key={team.id} context={context} team={team} />)}</ul>}
  </Card>;
}

function TeamRow({ context, team }: { context: Context; team: FleetAccessTeamView }) {
  const { t } = useShellLanguage();
  const { overview, api, epoch, linkId, busy, run } = context;
  const [dissolving, setDissolving] = useState(false);
  const outside = overview.people.filter(person => person.status === "active" && person.joined && !team.members.some(member => member.personId === person.id));
  const member = (personId: string, role: string | null) => run(() => api.setTeamMember({ expectedEpoch: epoch, linkId, teamId: team.id, personId, role }, call));
  return <li className="secret-row fleet-access-team" data-team={team.name}>
    <div className="fleet-access-team-head">
      <code>{team.name}</code>
      <span className="secret-reference">{team.machines.length === 0 ? t("No team machine yet") : t("Machines: {names}", { names: team.machines.map(machine => machine.name).join(", ") })}</span>
      {managesAccess(overview) && <span className="secret-delete">
        <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} aria-label={t("Dissolve {name}", { name: team.name })} disabled={busy}
          onClick={() => setDissolving(true)} />
        {dissolving && <ConfirmPopover always intent="danger" busy={busy} placement="bottom-end" title={t("Dissolve this team?")} subject={team.name}
          detail={t("What it owned goes to the organization.")} confirmLabel={t("Dissolve")}
          onConfirm={() => { setDissolving(false); void run(() => api.dissolveTeam({ expectedEpoch: epoch, linkId, id: team.id }, call)); }} onCancel={() => setDissolving(false)} />}
      </span>}
    </div>
    <ul className="fleet-access-members" aria-label={t("Members of {name}", { name: team.name })}>
      {team.members.map(entry => <li key={entry.personId} data-member={entry.name}>
        <span>{entry.name}</span>
        {team.canManage
          ? <HTMLSelect minimal value={entry.role} disabled={busy} aria-label={t("Role of {name}", { name: entry.name })} onChange={event => void member(entry.personId, event.target.value)}
            options={[{ value: "maintainer", label: t("maintainer") }, { value: "member", label: t("member") }]} />
          : <Tag minimal>{t(entry.role === "maintainer" ? "maintainer" : "member")}</Tag>}
        {team.canManage && <Button size="small" variant="minimal" icon={<AppIcon name="close" size={12} />} aria-label={t("Take {name} out of the team", { name: entry.name })}
          disabled={busy} onClick={() => void member(entry.personId, null)} />}
      </li>)}
    </ul>
    {team.canManage && <div className="fleet-access-team-actions">
      {outside.length > 0 && <HTMLSelect value="" disabled={busy} aria-label={t("Add a member to {name}", { name: team.name })}
        onChange={event => { if (event.target.value) void member(event.target.value, "member"); }}
        options={[{ value: "", label: t("Add a member…") }, ...outside.map(person => ({ value: person.id, label: person.name }))]} />}
      <Switch checked={team.runsMemberAutomations} disabled={busy} label={t("Its machines run the automations of its members")}
        onChange={event => { const value = event.currentTarget.checked; void run(() => api.updateTeam({ expectedEpoch: epoch, linkId, teamId: team.id, name: null, runsMemberAutomations: value }, call)); }} />
    </div>}
    <OnCall context={context} team={team} />
  </li>;
}

/**
 * Who of a team is on call: its people in turn, in the order of the team, each for a turn of hours. The person on call
 * answers the approvals of its automations that ask the person on call; without a rotation, the team answers them.
 */
function OnCall({ context, team }: { context: Context; team: FleetAccessTeamView }) {
  const { t, locale } = useShellLanguage();
  const { api, epoch, linkId, busy, run } = context;
  const people = team.members.map(member => member.personId);
  const hours = team.onCall?.shiftHours ?? 12;
  const set = (rotation: string[] | null, shiftHours: number) => run(() => api.setOnCall({ expectedEpoch: epoch, linkId, teamId: team.id, people: rotation, shiftHours }, call));
  if (!team.onCall && !team.canManage) return null;
  return <div className="fleet-access-oncall">
    <span className={team.onCall ? undefined : "muted-text"}>{team.onCall ? onCallSummary(team.onCall, t, locale)
      : t("Without a rotation, the automations that ask the person on call ask the whole team.")}</span>
    {team.canManage && <span className="fleet-access-oncall-actions">
      <Switch checked={!!team.onCall} disabled={busy || people.length === 0} label={t("Take turns on call")} onChange={event => void set(event.currentTarget.checked ? people : null, hours)} />
      {team.onCall && <HTMLSelect minimal value={String(hours)} disabled={busy} aria-label={t("Length of a turn on call")}
        onChange={event => void set(people, Number(event.target.value))}
        options={shiftChoices.map(choice => ({ value: String(choice), label: choice === 168 ? t("a week") : t("{hours} hours", { hours: choice }) }))} />}
    </span>}
  </div>;
}

/** The machines and automations, their owners, and what was offered from here. */
function Owners({ context }: { context: Context }) {
  const { t } = useShellLanguage();
  const { overview, api, epoch, linkId, busy, run } = context;
  const offered = overview.transfers.filter(transfer => transfer.outgoing);
  return <Card className="appearance-settings" aria-labelledby={`owners-${linkId}`}>
    <h2 id={`owners-${linkId}`}>{t("Machines and automations")}</h2>
    <ul className="secret-list" aria-label={t("Machines and automations")}>
      {overview.owners.map(owner => <OwnerRow key={`${owner.kind}:${owner.id}`} context={context} owner={owner} />)}
    </ul>
    {offered.length > 0 && <ul className="secret-list" aria-label={t("Offered")}>
      {offered.map(transfer => <li key={transfer.id} className="secret-row fleet-access-row" data-offer={transfer.id}>
        <span className="secret-name"><code>{transfer.resourceName}</code><span className="secret-reference">{t("Offered to {name}", { name: transfer.toName })}</span></span>
        <span className="secret-actions"><Button size="small" variant="minimal" disabled={busy} text={t("Cancel")}
          onClick={() => void run(() => api.decideTransfer({ expectedEpoch: epoch, linkId, id: transfer.id, verb: "cancel" }, call))} /></span>
      </li>)}
    </ul>}
  </Card>;
}

/** A machine or an automation: its owner, whether it waits; Give to… and Resume for who answers for it. */
function OwnerRow({ context, owner, note }: { context: Context; owner: FleetAccessOwnerView; note?: string }) {
  const { t } = useShellLanguage();
  const { overview, api, epoch, linkId, busy, run } = context;
  const [giving, setGiving] = useState(false);
  const targets = ownerTargets(overview, owner);
  const [target, setTarget] = useState(targets[0]?.value ?? "organization:");
  const ownerName = owner.ownerKind === "organization" ? t("The organization") : owner.ownerKind === "team" ? t("Team {name}", { name: owner.ownerName }) : owner.ownerName;
  return <li className="secret-row fleet-access-row fleet-access-owner" data-resource={owner.name} data-paused={owner.paused || undefined}>
    <span className="secret-name"><code title={owner.id}>{owner.name}</code><span className="secret-reference">{note ?? ownerName}</span></span>
    <span className="fleet-secret-tags">
      <Tag minimal>{t(owner.kind === "machine" ? "Machine" : "Automation")}</Tag>
      {owner.paused && <Tag minimal intent="warning">{t("Paused")}</Tag>}
      {owner.ownerSuspended && <Tag minimal intent="danger">{t("Owner left")}</Tag>}
    </span>
    <span className="secret-actions">
      {owner.paused && owner.canAct && !owner.ownerSuspended && <Button size="small" disabled={busy} text={t("Resume")}
        onClick={() => void run(() => api.resumeResource({ expectedEpoch: epoch, linkId, kind: owner.kind, id: owner.id }, call))} />}
      {owner.canAct && targets.length > 0 && <PopoverNext isOpen={giving} placement="bottom-end" onInteraction={next => { if (!next && !busy) setGiving(false); }}
        content={<div className="fleet-server-confirm fleet-access-give">
          <p>{t("Give {name} to:", { name: owner.name })}</p>
          <HTMLSelect value={target} aria-label={t("New owner")} onChange={event => setTarget(event.target.value)} options={targets.map(item => ({ value: item.value,
            label: item.kind === "organization" ? t("The organization") : item.kind === "team" ? t("Team {name}", { name: item.name }) : item.name }))} />
          <p className="muted-text">{t(owner.kind === "machine" ? "A machine given to a team becomes a team machine once the change is signed." : "The new owner accepts it; it waits for them to resume it.")}</p>
          <Button intent="primary" disabled={busy} text={t("Give")} onClick={() => {
            const chosen = parseTarget(target);
            setGiving(false);
            void run(() => api.offerTransfer({ expectedEpoch: epoch, linkId, resourceKind: owner.kind, resourceId: owner.id, toKind: chosen.kind, toId: chosen.id }, call), "Offered: the new owner accepts it.");
          }} />
        </div>}>
        <Button size="small" variant="minimal" icon={<AppIcon name="move" size={14} />} disabled={busy} text={t("Give to…")} onClick={() => setGiving(true)} />
      </PopoverNext>}
    </span>
  </li>;
}

/** The grants: who may do what where; content is granted on one resource, and sealed content waits for its keys. */
function Grants({ context }: { context: Context }) {
  const { t } = useShellLanguage();
  const { overview, api, epoch, linkId, busy, run } = context;
  const [adding, setAdding] = useState(false);
  const capability = (value: string) => grantCapabilities.find(item => item.value === value)?.label;
  return <Card className="appearance-settings" aria-labelledby={`grants-${linkId}`}>
    <div className="fleet-secrets-head">
      <h2 id={`grants-${linkId}`}>{t("Grants")}</h2>
      <Button size="small" icon={<AppIcon name="plus" size={14} />} disabled={busy || adding} text={t("Add a grant…")} onClick={() => setAdding(true)} />
    </div>
    {adding && <GrantForm context={context} onClose={() => setAdding(false)} />}
    {overview.grants.length === 0 && !adding
      ? <NonIdealState className="secrets-empty" icon={<AppIcon name="secret" size={28} />} title={t("No grant yet.")} description={t("Roles and teams give most of what people need.")} />
      : <ul className="secret-list" aria-label={t("Grants")}>
        {overview.grants.map(grant => <li key={grant.id} className="secret-row fleet-access-row" data-grant={grant.capability}>
          <span className="secret-name"><code>{grant.subjectName}</code>
            <span className="secret-reference">{capability(grant.capability) ? t(capability(grant.capability)!) : grant.capability} · {grant.scopeKind === "organization" ? t("The organization") : grant.scopeName}</span></span>
          <span className="fleet-secret-tags">{grant.status === "pending_keys" && <Tag minimal intent="warning">{t("Waiting for keys")}</Tag>}</span>
          <span className="secret-actions">
            {grant.status === "pending_keys" && <Button size="small" disabled={busy} text={t("Confirm keys")}
              onClick={() => void run(() => api.confirmGrantKeys({ expectedEpoch: epoch, linkId, id: grant.id }, call))} />}
            <Button size="small" variant="minimal" intent="danger" icon={<AppIcon name="trash" size={14} />} aria-label={t("Remove the grant of {name}", { name: grant.subjectName })}
              disabled={busy} onClick={() => void run(() => api.revokeGrant({ expectedEpoch: epoch, linkId, id: grant.id }, call))} />
          </span>
        </li>)}
      </ul>}
  </Card>;
}

function GrantForm({ context, onClose }: { context: Context; onClose: () => void }) {
  const { t } = useShellLanguage();
  const { overview, api, epoch, linkId, busy, run } = context;
  const subjects = [...overview.people.filter(person => person.status === "active").map(person => ({ value: `person:${person.id}`, label: person.name })),
    ...overview.teams.map(team => ({ value: `team:${team.id}`, label: t("Team {name}", { name: team.name }) }))];
  const machines = overview.owners.filter(owner => owner.kind === "machine");
  const [subject, setSubject] = useState(subjects[0]?.value ?? "");
  const [capability, setCapability] = useState(grantCapabilities[0].value);
  const content = isContentCapability(capability);
  const scopes = [...(content ? [] : [{ value: "organization:", label: t("The organization") }, ...overview.teams.map(team => ({ value: `team:${team.id}`, label: t("Team {name}", { name: team.name }) }))]),
    ...machines.map(machine => ({ value: `resource:machine:${machine.id}`, label: t("Machine: {name}", { name: machine.name }) }))];
  const [scope, setScope] = useState("");
  const [sealed, setSealed] = useState(false);
  const chosenScope = scopes.some(item => item.value === scope) ? scope : scopes[0]?.value ?? "";
  const submit = () => {
    const who = parseTarget(subject);
    const where = parseTarget(chosenScope);
    void run(() => api.createGrant({ expectedEpoch: epoch, linkId, subjectKind: who.kind, subjectId: who.id ?? "", capability, scopeKind: where.kind, scopeId: where.id,
      sealed: content && sealed }, call)).then(ok => { if (ok) onClose(); });
  };
  return <form className="fleet-secret-new fleet-access-grant" aria-label={t("Add a grant…")} onSubmit={event => { event.preventDefault(); if (subject && chosenScope) submit(); }}>
    <div className="fleet-secret-new-row">
      <HTMLSelect value={subject} aria-label={t("Who")} disabled={busy} onChange={event => setSubject(event.target.value)} options={subjects} />
      <HTMLSelect value={capability} aria-label={t("What")} disabled={busy} onChange={event => setCapability(event.target.value)}
        options={grantCapabilities.map(item => ({ value: item.value, label: t(item.label) }))} />
      <HTMLSelect value={chosenScope} aria-label={t("Where")} disabled={busy || scopes.length === 0} onChange={event => setScope(event.target.value)} options={scopes} />
    </div>
    {content && <Checkbox checked={sealed} disabled={busy} label={t("The content is sealed from the server (a Private space): a device of its owner gives the keys")}
      onChange={event => setSealed(event.currentTarget.checked)} />}
    {content && scopes.length === 0 && <p className="muted-text">{t("Content is granted on one machine or session, never on a whole scope.")}</p>}
    <div className="fleet-secret-new-row fleet-secret-new-actions">
      <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={onClose} />
      <Button type="submit" size="small" intent="primary" text={t("Add")} disabled={busy || !subject || !chosenScope} />
    </div>
  </form>;
}

/** The identities a team or the organization runs unattended work as: disabled until a person enables them. */
function Identities({ context }: { context: Context }) {
  const { t } = useShellLanguage();
  const { overview, api, epoch, linkId, busy, run } = context;
  const [name, setName] = useState<string | null>(null);
  const [owner, setOwner] = useState("organization:");
  const owners = [...(managesAccess(overview) ? [{ value: "organization:", label: t("The organization") }] : []),
    ...overview.teams.filter(team => team.canManage).map(team => ({ value: `team:${team.id}`, label: t("Team {name}", { name: team.name }) }))];
  return <Card className="appearance-settings" aria-labelledby={`identities-${linkId}`}>
    <div className="fleet-secrets-head">
      <h2 id={`identities-${linkId}`}>{t("Service identities")}</h2>
      {owners.length > 0 && <Button size="small" icon={<AppIcon name="plus" size={14} />} disabled={busy || name !== null} text={t("New identity…")} onClick={() => setName("")} />}
    </div>
    {name !== null && <form className="fleet-secret-new" aria-label={t("New identity…")} onSubmit={event => {
      event.preventDefault();
      const value = name.trim();
      const chosen = parseTarget(owners.some(item => item.value === owner) ? owner : owners[0].value);
      if (value) void run(() => api.createServiceIdentity({ expectedEpoch: epoch, linkId, name: value, ownerKind: chosen.kind, ownerId: chosen.id }, call)).then(ok => { if (ok) setName(null); });
    }}>
      <div className="fleet-secret-new-row">
        <InputGroup small autoFocus value={name} placeholder={t("Name")} aria-label={t("Name")} disabled={busy} onChange={event => setName(event.target.value)} />
        <HTMLSelect value={owner} aria-label={t("Belongs to")} disabled={busy} onChange={event => setOwner(event.target.value)} options={owners} />
        <Button variant="minimal" size="small" text={t("Cancel")} disabled={busy} onClick={() => setName(null)} />
        <Button type="submit" size="small" intent="primary" text={t("Add")} disabled={busy || !name.trim()} />
      </div>
    </form>}
    {overview.identities.length === 0 && name === null
      ? <NonIdealState className="secrets-empty" icon={<AppIcon name="automation" size={28} />} title={t("No service identity yet.")}
        description={t("Team and organization automations run as one, with account credentials only.")} />
      : <ul className="secret-list" aria-label={t("Service identities")}>
        {overview.identities.map(identity => <li key={identity.id} className="secret-row fleet-access-row" data-identity={identity.name}>
          <span className="secret-name"><code>{identity.name}</code>
            <span className="secret-reference">{identity.ownerKind === "organization" ? t("The organization") : t("Team {name}", { name: identity.ownerName })}</span></span>
          <span className="secret-actions"><Switch checked={identity.enabled} disabled={busy} label={t("Enabled")}
            onChange={event => { const enabled = event.currentTarget.checked; void run(() => api.enableServiceIdentity({ expectedEpoch: epoch, linkId, id: identity.id, enabled }, call)); }} /></span>
        </li>)}
      </ul>}
  </Card>;
}

/** Who can do what, copied as CSV for an access review. */
function Review({ context, onCopied }: { context: Context; onCopied: () => void }) {
  const { t } = useShellLanguage();
  const { api, epoch, linkId, busy } = context;
  return <Button size="small" variant="minimal" icon={<AppIcon name="copy" size={14} />} disabled={busy} text={t("Copy who can do what (CSV)")} onClick={async () => {
    const review = await api.accessReview({ expectedEpoch: epoch, linkId }, call);
    if (review.status === "ok" && review.csv) { await navigator.clipboard?.writeText(review.csv); onCopied(); }
  }} />;
}
