// The logic of Settings > Fleet > People and access, without DOM: what waits for the person, which roles they may give, what a
// grant may name, and how the page stays simple at home (doc/fleet/access-control.md, "At home").
import type { FleetAccessOnCallView, FleetAccessOverview, FleetAccessOwnerView, FleetAccessPersonView, FleetAccessTransferView } from "#neoastra";
import type { MessageKey } from "../../localization";

/** The lengths of a turn on call a team chooses from, in hours. */
export const shiftChoices: readonly number[] = [8, 12, 24, 168];

/** What the on-call rotation of a team says on its row: who is on call now and until when, then the turns. */
export function onCallSummary(onCall: FleetAccessOnCallView, say: (key: MessageKey, values?: Readonly<Record<string, string | number>>) => string, locale: string): string {
  const until = onCall.until ? new Date(onCall.until).toLocaleString(locale, { weekday: "short", hour: "2-digit", minute: "2-digit" }) : null;
  const now = onCall.onCallNow ? until ? say("On call now: {name}, until {time}", { name: onCall.onCallNow, time: until }) : say("On call now: {name}", { name: onCall.onCallNow })
    : say("No one of its rotation is in the team now");
  return `${now} · ${say("turns of {hours} hours: {names}", { hours: onCall.shiftHours, names: onCall.people.join(", ") })}`;
}

/** The roles of the organization, from the widest, with their names on the page. */
export const roleLabels: Readonly<Record<string, MessageKey>> = Object.freeze({
  owner: "Owner", admin: "Admin", member: "Member", auditor: "Auditor", guest: "Guest",
});

/** What each role may do, in one line, as the role picker says it. */
export const roleHints: Readonly<Record<string, MessageKey>> = Object.freeze({
  owner: "Everything, except reading what others did not share",
  admin: "People, teams, machines and budgets; never what others did not share",
  member: "Their own machines, sessions and automations, and their teams'",
  auditor: "Reads the audit log and statistics; changes nothing",
  guest: "Only what is shared with them",
});

/** The capabilities a grant gives most often, with their names; the others are rarely granted by hand. */
export const grantCapabilities: readonly { value: string; label: MessageKey }[] = Object.freeze([
  { value: "machine.start", label: "Start sessions on machines" },
  { value: "machine.manage", label: "Manage machines" },
  { value: "automation.run", label: "Run automations by hand" },
  { value: "automation.edit", label: "Edit automations" },
  { value: "automation.approve", label: "Answer the approvals of automations" },
  { value: "run.view-facts", label: "See runs" },
  { value: "budget.set", label: "Set budgets" },
  { value: "statistics.view-team", label: "See the statistics of a team" },
  { value: "statistics.view-per-person", label: "See statistics per person" },
  { value: "audit.read", label: "Read the audit log" },
  { value: "machine.logs", label: "Read the log of a machine" },
  { value: "session.view-content", label: "Read a session" },
]);

/** Gets whether a capability opens content: it is granted on one resource only, never on a scope. */
export function isContentCapability(capability: string): boolean {
  return ["session.view-content", "session.interact", "session.approve", "run.view-content", "machine.logs", "statistics.view-per-person"].includes(capability);
}

/** Gets whether the person may manage people and teams of the whole organization. */
export function managesAccess(overview: FleetAccessOverview): boolean {
  return overview.capabilities.includes("access.manage");
}

/** The roles the person may give: an owner gives every role, an admin all but owner and admin. */
export function assignableRoles(overview: FleetAccessOverview): string[] {
  if (!managesAccess(overview)) return [];
  const all = Object.keys(roleLabels);
  return overview.me?.role === "owner" ? all : all.filter(role => role !== "owner" && role !== "admin");
}

/** Gets whether the person may change the role of another, or deprovision them. */
export function canManagePerson(overview: FleetAccessOverview, person: FleetAccessPersonView): boolean {
  if (person.isMe || person.status !== "active" || !managesAccess(overview)) return false;
  return overview.me?.role === "owner" || (person.role !== "owner" && person.role !== "admin");
}

/**
 * Gets whether the page shows teams, grants and identities first: in an organization that uses them. A household (an owner
 * and members) sees people and the owners of its machines; the rest stays under Advanced until someone needs it.
 */
export function isAdvanced(overview: FleetAccessOverview): boolean {
  return overview.advanced || overview.teams.length > 0 || overview.grants.length > 0 || overview.identities.length > 0;
}

/** What waits for the person: changes to sign, transfers to take, proposals of agents, resources whose owner left. */
export type AccessAttention = {
  sign: readonly string[];
  transfers: readonly FleetAccessTransferView[];
  proposals: FleetAccessOverview["proposals"];
  orphans: readonly FleetAccessOwnerView[];
  paused: readonly FleetAccessOwnerView[];
};

/** Reads what waits for the person on the page. */
export function attention(overview: FleetAccessOverview): AccessAttention {
  return {
    sign: overview.signer ? overview.document?.pending ?? [] : [],
    transfers: overview.transfers.filter(transfer => transfer.incoming),
    proposals: managesAccess(overview) || overview.proposals.some(proposal => proposal.personName === overview.me?.name)
      ? overview.proposals.filter(proposal => proposal.state === "open") : [],
    orphans: overview.owners.filter(owner => owner.ownerSuspended && owner.canAct),
    paused: overview.owners.filter(owner => owner.paused && !owner.ownerSuspended && owner.canAct),
  };
}

/** Counts what waits for the person. */
export function attentionCount(value: AccessAttention): number {
  return (value.sign.length > 0 ? 1 : 0) + value.transfers.length + value.proposals.length + value.orphans.length + value.paused.length;
}

/** Who a resource can go to: the organization, its teams, its active people other than its owner. */
export function ownerTargets(overview: FleetAccessOverview, owner?: { ownerKind: string; ownerId: string }): { value: string; kind: string; id: string | null; name: string }[] {
  const targets = [
    { value: "organization:", kind: "organization", id: null as string | null, name: "" },
    ...overview.teams.map(team => ({ value: `team:${team.id}`, kind: "team", id: team.id as string | null, name: team.name })),
    ...overview.people.filter(person => person.status === "active" && person.joined).map(person => ({ value: `person:${person.id}`, kind: "person", id: person.id as string | null,
      name: person.name })),
  ];
  return targets.filter(target => !owner || !(target.kind === owner.ownerKind && (target.id === owner.ownerId || target.kind === "organization")));
}

/** The types of provider a team or the organization runs with: those that take the key of an account. */
export const providerTypes: readonly { value: string; label: string }[] = [
  { value: "anthropic", label: "Anthropic" },
  { value: "openai-responses", label: "OpenAI (Responses)" },
  { value: "openai-chat", label: "OpenAI (Chat Completions)" },
  { value: "azure-openai", label: "Azure OpenAI" },
  { value: "google-genai", label: "Google Gemini" },
  { value: "mistral", label: "Mistral" },
  { value: "xai", label: "xAI" },
];

/** The id of a provider from its name: lowercase letters, digits and dashes, which automations and machines name it by. */
export function providerId(name: string): string {
  return name.normalize("NFKD").replace(/[̀-ͯ]/g, "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 64).replace(/-+$/, "");
}

/** Splits a value of a target select: <kind>:<id>. */
export function parseTarget(value: string): { kind: string; id: string | null } {
  const colon = value.indexOf(":");
  const kind = colon < 0 ? value : value.slice(0, colon);
  const id = colon < 0 ? "" : value.slice(colon + 1);
  return { kind, id: id.length > 0 ? id : null };
}

/** What a proposal of an agent does, for people: its summary, and the change it names in short. */
export function proposalDetail(payload: string, names: (id: string) => string): string {
  try {
    const value = JSON.parse(payload) as Record<string, unknown>;
    return Object.entries(value).filter(([, item]) => typeof item === "string").map(([key, item]) => `${key}: ${key.endsWith("Id") ? names(item as string) : item}`).join(" · ");
  } catch {
    return "";
  }
}

/** The name of a person, team or machine by its id, as the overview knows it. */
export function nameOf(overview: FleetAccessOverview, id: string): string {
  return overview.people.find(person => person.id === id)?.name ?? overview.teams.find(team => team.id === id)?.name
    ?? overview.owners.find(owner => owner.id === id)?.name ?? id;
}
