// What Settings > Fleet > Policies edits and shows of the signed policy of a server, without the DOM: the rules as a form holds
// them, the kill switch as people read it, and the stop the window shows while it holds.
import type { FleetPolicyRulesView, FleetPolicyStopView, FleetServerView } from "#neoastra";
import type { MessageKey } from "../../localization";

/** The rules as the form holds them: texts and choices, an empty one saying nothing. */
export type RulesDraft = {
  providers: string; models: string; sandbox: string; toolsOff: string; network: string; remoteStarts: "any" | "no"; agentStarts: "any" | "no";
  maxSessionMinutes: string; permissionMode: string; publishFacts: boolean; accountCredentials: boolean;
};

/** The form of rules. */
export function draftOf(rules: FleetPolicyRulesView | null | undefined): RulesDraft {
  return {
    providers: (rules?.providers ?? []).join(", "), models: (rules?.models ?? []).join(", "), sandbox: rules?.sandbox ?? "", toolsOff: (rules?.toolsOff ?? []).join(", "),
    network: rules?.network ?? "", remoteStarts: rules?.remoteStarts === false ? "no" : "any", agentStarts: rules?.agentStarts === false ? "no" : "any",
    maxSessionMinutes: rules?.maxSessionMinutes ? String(rules.maxSessionMinutes) : "", permissionMode: rules?.permissionMode ?? "",
    publishFacts: rules?.publishFacts === true, accountCredentials: rules?.accountCredentials === true,
  };
}

/** The items of a list typed with commas or new lines; null for none. */
export function splitList(text: string): string[] | null {
  const items = [...new Set(text.split(/[,\n]/).map(item => item.trim()).filter(item => item.length > 0))];
  return items.length > 0 ? items : null;
}

/** The longest time a form names, in minutes: null for none, undefined when it does not read (1 minute to 7 days). */
export function minutesOf(text: string): number | null | undefined {
  const trimmed = text.trim();
  if (trimmed === "") return null;
  if (!/^\d+$/.test(trimmed)) return undefined;
  const value = Number(trimmed);
  return value >= 1 && value <= 7 * 24 * 60 ? value : undefined;
}

/** The rules a form says; null when one of its fields does not read. */
export function rulesOf(draft: RulesDraft): FleetPolicyRulesView | null {
  const minutes = minutesOf(draft.maxSessionMinutes);
  if (minutes === undefined) return null;
  return {
    providers: splitList(draft.providers), models: splitList(draft.models), sandbox: draft.sandbox.trim() || null, toolsOff: splitList(draft.toolsOff),
    network: draft.network || null, remoteStarts: draft.remoteStarts === "no" ? false : null, agentStarts: draft.agentStarts === "no" ? false : null,
    maxSessionMinutes: minutes, permissionMode: draft.permissionMode || null, publishFacts: draft.publishFacts ? true : null,
    accountCredentials: draft.accountCredentials ? true : null,
  };
}

/** Whether two forms say the same. */
export function sameDraft(left: RulesDraft, right: RulesDraft): boolean {
  return JSON.stringify(rulesOf(left)) === JSON.stringify(rulesOf(right));
}

/** How many rules a policy sets. */
export function ruleCount(rules: FleetPolicyRulesView | null | undefined): number {
  if (!rules) return 0;
  return Object.values(rules).filter(value => value !== null && value !== undefined).length;
}

/** Where a stop reaches, as a sentence of the page. */
export function stopSentence(stop: Pick<FleetPolicyStopView, "scope" | "names">): { key: MessageKey; values: Record<string, string> } {
  if (stop.scope === "fleet") return { key: "The work of the fleet is stopped on every machine.", values: {} };
  const names = stop.names.join(", ");
  return stop.scope === "teams"
    ? { key: "The work of the fleet is stopped on the machines of {names}.", values: { names } }
    : { key: "The work of the fleet is stopped on {names}.", values: { names } };
}

/** The stop the window shows: the first server whose policy holds one, with what it reaches. */
export function bannerOf(servers: readonly FleetServerView[]): { server: FleetServerView; label: MessageKey; values: Record<string, string | number> } | null {
  const server = servers.find(candidate => candidate.stop);
  if (!server?.stop) return null;
  const stop = server.stop;
  if (stop.scope === "fleet") return { server, label: "Fleet stopped: every machine", values: {} };
  if (stop.scope === "teams") return { server, label: stop.teams === 1 ? "Fleet stopped: the machines of 1 team" : "Fleet stopped: the machines of {count} teams", values: { count: stop.teams } };
  return { server, label: stop.machines === 1 ? "Fleet stopped: 1 machine" : "Fleet stopped: {count} machines", values: { count: stop.machines } };
}

/** The choices of the scope of a stop. */
export const stopScopes: readonly { value: "fleet" | "machines" | "teams"; label: MessageKey }[] = Object.freeze([
  { value: "fleet", label: "Every machine" },
  { value: "machines", label: "Chosen machines" },
  { value: "teams", label: "The machines of chosen teams" },
]);

/** Whether a stop names what it stops. */
export function stopReady(scope: string, machines: readonly string[], teams: readonly string[]): boolean {
  return scope === "fleet" || scope === "machines" && machines.length > 0 || scope === "teams" && teams.length > 0;
}
