// What Settings > Fleet > Audit shows of the audit log of a server, without the DOM: the kinds of actions a person filters by,
// the periods, the days the timeline is grouped by, the words of the actions, and what a result means.
import type { FleetAuditFilters, FleetAuditItem } from "#neoastra";
import type { MessageKey } from "../../localization";

/** A kind of action the page filters by: the prefixes of the actions it holds. */
export type AuditCategory = Readonly<{ id: string; label: MessageKey; actions: readonly string[] }>;

/** The kinds of actions, in the order of the filter: none chosen is the whole log, several add up. */
export const auditCategories: readonly AuditCategory[] = Object.freeze([
  { id: "sign-in", label: "Sign-in", actions: ["sign_in.", "passkey.", "server.setup"] },
  { id: "devices", label: "Devices and machines", actions: ["device.", "enrollment_token.", "recovery_key.", "metadata_key.", "relay.", "relay"] },
  { id: "access", label: "People and access", actions: ["person.", "team.", "grant.", "service_identity.", "owner.", "transfer.", "access.", "access_document."] },
  { id: "policies", label: "Policies and the kill switch", actions: ["policy.", "telemetry.", "update.policy."] },
  { id: "automations", label: "Automations", actions: ["automation.", "hook.", "environment."] },
  { id: "secrets", label: "Providers and secrets", actions: ["secret.", "provider."] },
  { id: "budgets", label: "Budgets", actions: ["budget."] },
  { id: "updates", label: "Updates", actions: ["update.campaign."] },
  { id: "sessions", label: "Sessions and facts", actions: ["session.", "statistics.", "facts."] },
  { id: "audit", label: "Exports of the log", actions: ["audit."] },
]);

/** The periods of the filter. */
export type AuditPeriod = "any" | "day" | "week" | "month" | "custom";

/** The results of the filter. */
export type AuditOutcome = "any" | "success" | "failure";

/** What the page filters by, as the person sets it. */
export type AuditFilterState = Readonly<{ categories: readonly string[]; person: string; outcome: AuditOutcome; period: AuditPeriod; from: string; to: string; text: string }>;

/** No filter: the whole log. */
export const noFilter: AuditFilterState = Object.freeze({ categories: Object.freeze([]) as readonly string[], person: "", outcome: "any", period: "any", from: "", to: "", text: "" });

/** Whether a filter narrows the log. */
export function isFiltered(state: AuditFilterState): boolean {
  return state.categories.length > 0 || state.person !== "" || state.outcome !== "any" || state.period !== "any" || state.text.trim() !== "";
}

const day = 24 * 60 * 60 * 1000;

/**
 * The filters a request carries for what the person set, at a time: a period counts back from now; a custom one runs from
 * the start of its first day to the end of its last, in the time zone of this computer. Null when a custom day does not read.
 */
export function toFilters(state: AuditFilterState, now: Date): FleetAuditFilters | null {
  const actions = auditCategories.filter(item => state.categories.includes(item.id)).flatMap(item => item.actions);
  let from: string | null = null;
  let to: string | null = null;
  if (state.period === "day" || state.period === "week" || state.period === "month") {
    const back = state.period === "day" ? day : state.period === "week" ? 7 * day : 30 * day;
    from = new Date(now.getTime() - back).toISOString();
  } else if (state.period === "custom") {
    const start = state.from ? localDay(state.from) : null;
    const end = state.to ? localDay(state.to) : null;
    if (state.from && !start || state.to && !end) return null;
    if (start && end && end.getTime() < start.getTime()) return null;
    from = start ? start.toISOString() : null;
    to = end ? new Date(end.getTime() + day).toISOString() : null;
  }

  const text = state.text.trim();
  return {
    actions: actions.length > 0 ? actions : null,
    personId: state.person || null, deviceId: null, result: state.outcome === "any" ? null : state.outcome, target: null, address: null, from, to,
    text: text.length > 0 ? text.slice(0, 256) : null,
  };
}

// A day typed as yyyy-mm-dd, at midnight here; null when it is not a day.
function localDay(text: string): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
  if (!match) return null;
  const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  return date.getFullYear() === Number(match[1]) && date.getMonth() === Number(match[2]) - 1 && date.getDate() === Number(match[3]) ? date : null;
}

/** A day of the timeline and its records, newest first. */
export type AuditDay = Readonly<{ key: string; date: Date; items: readonly FleetAuditItem[] }>;

/** The day a record was recorded here: a key that is the same for the records of a day, and its midnight. */
export function dayOf(recordedAt: string): Readonly<{ key: string; date: Date }> {
  const time = new Date(recordedAt);
  return { key: `${time.getFullYear()}-${time.getMonth() + 1}-${time.getDate()}`, date: new Date(time.getFullYear(), time.getMonth(), time.getDate()) };
}

/** Groups records, newest first, by the day they were recorded here. */
export function groupByDay(items: readonly FleetAuditItem[]): AuditDay[] {
  const days: { key: string; date: Date; items: FleetAuditItem[] }[] = [];
  for (const item of items) {
    const { key, date } = dayOf(item.recordedAt);
    const last = days[days.length - 1];
    if (last && last.key === key) last.items.push(item);
    else days.push({ key, date, items: [item] });
  }

  return days;
}

/** How a day is named: today, yesterday, or its date. */
export function dayName(date: Date, now: Date): "today" | "yesterday" | "date" {
  const today = new Date(now.getFullYear(), now.getMonth(), now.getDate()).getTime();
  const time = date.getTime();
  return time === today ? "today" : time === today - day ? "yesterday" : "date";
}

/** The names of what records reach, by id: the people and teams of the organization and its devices. */
export function targetNames(...lists: readonly (readonly Readonly<{ id: string; name: string }>[])[]): ReadonlyMap<string, string> {
  const names = new Map<string, string>();
  for (const list of lists) for (const item of list) if (!names.has(item.id)) names.set(item.id, item.name);
  return names;
}

/**
 * What a record reached, as a person reads it: the name of a person, a team or a device, or the number of a version of the
 * policy; null when the page knows no name for it, which then shows the target as the server wrote it.
 */
export function targetOf(item: Pick<FleetAuditItem, "action" | "target">, names: ReadonlyMap<string, string>): { name: string } | { version: string } | null {
  if (!item.target) return null;
  const name = names.get(item.target);
  if (name) return { name };
  return item.action.startsWith("policy.") && /^\d+$/.test(item.target) ? { version: item.target } : null;
}

/** Whether a record tells of an action that succeeded. */
export function succeeded(item: Pick<FleetAuditItem, "result">): boolean {
  return item.result === "success";
}

/** The words of the actions people meet most; another action shows its code. */
export const actionLabels: Readonly<Record<string, MessageKey>> = Object.freeze({
  "server.setup": "Set the server up",
  "sign_in.start": "Started a sign-in",
  "sign_in.complete": "Signed in",
  "sign_in.link": "Linked a sign-in method",
  "sign_in.settings.set": "Changed the sign-in methods",
  "sign_in.login.remove": "Removed a sign-in method",
  "sign_in.approval.request": "Asked to approve a sign-in",
  "sign_in.approval.approve": "Approved a sign-in",
  "passkey.add": "Added a passkey",
  "device.enroll": "Enrolled a device",
  "device.approve": "Approved a device",
  "device.refuse": "Refused a device",
  "device.revoke": "Revoked a device",
  "device.leave": "Left the organization",
  "device.recover": "Recovered with the recovery key",
  "enrollment_token.create": "Made an enrollment code",
  "recovery_key.create": "Made a recovery key",
  "metadata_key.create": "Made a key of a space",
  "metadata_key.wrap": "Gave a key of a space to devices",
  "relay.open": "Opened a channel to a machine",
  "relay": "Closed a channel to a machine",
  "person.invite": "Invited a person",
  "person.role": "Changed the role of a person",
  "person.deprovision": "Deprovisioned a person",
  "team.create": "Created a team",
  "team.update": "Changed a team",
  "team.dissolve": "Dissolved a team",
  "team.member": "Changed a member of a team",
  "team.member.remove": "Took a person out of a team",
  "team.oncall": "Changed who is on call",
  "team.oncall.clear": "Cleared who is on call",
  "grant.create": "Granted access",
  "grant.revoke": "Took a grant back",
  "grant.keys": "Gave the keys of a grant",
  "transfer.offer": "Offered a transfer",
  "transfer.accept": "Accepted a transfer",
  "transfer.decline": "Declined a transfer",
  "transfer.cancel": "Cancelled a transfer",
  "owner.resume": "Resumed what they own",
  "access.propose": "An agent proposed a change of access",
  "access.proposal.decide": "Decided on a proposal of an agent",
  "access_document.sign": "Signed the access document",
  "policy.sign": "Signed a policy",
  "policy.stop": "Stopped the work of the fleet",
  "policy.lift": "Let the work of the fleet go on",
  "telemetry.set": "Changed the telemetry of the fleet",
  "update.policy.set": "Changed the update policy",
  "update.campaign.create": "Started an update of the fleet",
  "update.campaign.cancel": "Cancelled an update of the fleet",
  "automation.put": "Saved an automation",
  "automation.delete": "Deleted an automation",
  "automation.run": "Ran an automation",
  "automation.takeover": "Took a run over",
  "hook.create": "Created a hook",
  "hook.delete": "Deleted a hook",
  "environment.set": "Changed an environment",
  "environment.approve": "Approved a run in an environment",
  "provider.put": "Saved a provider of the organization",
  "provider.delete": "Deleted a provider of the organization",
  "secret.set": "Set a secret",
  "secret.delete": "Deleted a secret",
  "secret.grant": "Gave a secret to machines",
  "budget.set": "Set a budget",
  "budget.continue": "Let a run go on past a budget",
  "budget.stop": "Stopped a run at a budget",
  "session.withdraw": "Withdrew a session from the server",
  "audit.export": "Exported the audit log",
});

/** The first and the last characters of a hash, enough to compare it by eye. */
export function shortHash(hash: string): string {
  return hash.length <= 16 ? hash : `${hash.slice(0, 8)}…${hash.slice(-6)}`;
}

/** The details of a record, indented when they are JSON, else as they are. */
export function formatDetails(details: string | null | undefined): string | null {
  if (!details) return null;
  try {
    return JSON.stringify(JSON.parse(details), null, 2);
  } catch {
    return details;
  }
}
