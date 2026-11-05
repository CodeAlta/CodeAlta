import type { FleetStatisticsGroupView, FleetStatisticsMeasuresView, FleetStatisticsResult, FleetStatisticsView } from "#neoastra";
import type { Translate } from "./labels";

// The "My fleet" scope of the Statistics canvas (doc/fleet/statistics.md, in the console): the sums CodeAlta Server keeps of
// what the machines of the person published, read through the `fleetServer` service of the window. Logic without DOM.

/** A question about the statistics of the fleet. */
export type FleetQuery = Readonly<{ from: string; to: string; timeZone: string | null; groupBy: readonly FleetDimension[] }>;

/** What the fleet's numbers are grouped by. */
export type FleetDimension = "machine" | "project" | "provider" | "model" | "day" | "month";

/** The binding of the scope: whether this computer is linked to a server, and the questions. */
export type FleetStatisticsApi = Readonly<{
  /** Whether this computer is linked to a server whose statistics it can read: the scope is offered only then. */
  available(signal?: AbortSignal): Promise<boolean>;
  statistics(query: FleetQuery, signal?: AbortSignal): Promise<FleetStatisticsResult>;
}>;

/** The periods the scope offers. */
export type FleetPeriod = "7d" | "30d" | "90d" | "month";

/** The periods, in their order. */
export const fleetPeriods: readonly FleetPeriod[] = ["7d", "30d", "90d", "month"];

const pad = (value: number) => String(value).padStart(2, "0");
const isoDay = (date: Date) => `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;

/** The local days of a period that ends today, as the server takes them. */
export function fleetDays(period: FleetPeriod, today: Date): Readonly<{ from: string; to: string }> {
  const end = new Date(today.getFullYear(), today.getMonth(), today.getDate());
  if (period === "month") return { from: isoDay(new Date(end.getFullYear(), end.getMonth(), 1)), to: isoDay(end) };
  const days = period === "7d" ? 7 : period === "30d" ? 30 : 90;
  return { from: isoDay(new Date(end.getFullYear(), end.getMonth(), end.getDate() - (days - 1))), to: isoDay(end) };
}

/** The time zone of this computer, which the days of the scope are in; null when the page cannot tell. */
export function localTimeZone(): string | null {
  try { return Intl.DateTimeFormat().resolvedOptions().timeZone || null; }
  catch { return null; }
}

/** One row of a table of the scope. */
export type FleetRow = Readonly<{
  key: string;
  label: string;
  /** A second line: the provider of a model, the last publication of a machine. */
  detail: string | null;
  tokens: number;
  inputTokens: number;
  outputTokens: number;
  activeMs: number;
  runs: number;
  toolCalls: number;
  /** The cost in dollars, or 0. */
  usd: number;
}>;

/** The tokens of a group: what came in and what went out. */
export const tokensOf = (measures: FleetStatisticsMeasuresView) => measures.inputTokens + measures.outputTokens;

/** The cost of a group in a unit, 0 when none. */
export const costOf = (measures: FleetStatisticsMeasuresView, unit = "usd") => measures.costs.find(cost => cost.unit === unit)?.amount ?? 0;

/**
 * The name of a project of the fleet: the one this computer gives it (it has the same repository), else a short form of its key, which
 * the server cannot read either. A group without a project is the sessions outside any project.
 */
export function projectLabel(t: Translate, group: Pick<FleetStatisticsGroupView, "project" | "projectName">): string {
  if (group.projectName) return group.projectName;
  if (!group.project) return t("No project");
  return t("Project {key}", { key: group.project.slice(0, 6) });
}

/** The rows of a view grouped by one dimension, the largest first by tokens, then by active time. */
export function fleetRows(t: Translate, view: FleetStatisticsView, dimension: "machine" | "project" | "model"): FleetRow[] {
  const rows = view.groups.map((group): FleetRow => {
    const measures = group.measures;
    const base = { tokens: tokensOf(measures), inputTokens: measures.inputTokens, outputTokens: measures.outputTokens, activeMs: measures.activeMs, runs: measures.runs,
      toolCalls: measures.toolCalls, usd: costOf(measures) };
    switch (dimension) {
      case "machine": return { ...base, key: group.machine ?? "", label: group.machineName ?? group.machine ?? "", detail: null };
      case "project": return { ...base, key: group.project ?? "", label: projectLabel(t, group), detail: null };
      default: return { ...base, key: `${group.provider ?? ""}/${group.model ?? ""}`, label: group.model || t("No model"), detail: group.provider || null };
    }
  });
  return rows.sort((a, b) => b.tokens - a.tokens || b.activeMs - a.activeMs || a.label.localeCompare(b.label));
}

/** Why the scope could not be read, in words; null when it could. */
export function fleetProblem(t: Translate, status: string): string | null {
  switch (status) {
    case "ok": return null;
    case "no_server": return t("This computer is not linked to a server.");
    case "unreachable": return t("The server does not answer.");
    default: return t("The statistics of the fleet could not be read ({status}).", { status });
  }
}
