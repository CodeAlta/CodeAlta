import type { Translate } from "../labels";
import type { QueryState } from "../useQuery";
import type { CostAmount, RankedRow, SeriesLine, SeriesResult, ToolRow } from "../types";

// Small pure helpers the pages share.

/** A result whose lines are the first line of each of the results given, renamed: several metrics drawn in one chart. */
export function combineSeries(results: readonly Readonly<{ result: SeriesResult; key: string; label: string; negate?: boolean }>[], metric: string): SeriesResult | null {
  if (results.length === 0) return null;
  const base = results[0].result;
  const series: SeriesLine[] = results.map(({ result, key, label, negate }) => {
    const line = result.series[0];
    const sign = negate ? -1 : 1;
    const values = (line?.values ?? base.buckets.map(() => 0)).map(value => value * sign);
    const previous = line?.previous?.map(value => value * sign);
    return { key, label, values, ...(previous ? { previous } : {}), total: (line?.total ?? 0) * sign, ...(line?.previousTotal !== undefined ? { previousTotal: line.previousTotal * sign } : {}) };
  });
  return { ...base, metric, unit: base.unit, group: undefined, series };
}

/** The words for a depth of the tree of sub-agents: 1 is a sub-agent of a session of yours, 2 a sub-agent of a sub-agent. */
export function depthLabel(t: Translate, depth: number): string {
  return depth <= 1 ? t("Sub-agents of your sessions") : depth === 2 ? t("Sub-agents of sub-agents") : t("{count} levels down", { count: depth });
}

/** The first result among the states that has data, for the header of a block made of several questions. */
export function firstQuery(...states: readonly Pick<QueryState<unknown>, "loading" | "error" | "retry" | "refreshing" | "placeholder">[]): Pick<QueryState<unknown>, "loading" | "error" | "retry" | "refreshing" | "placeholder"> {
  const failed = states.find(state => state.error);
  return {
    loading: states.some(state => state.loading), error: failed?.error ?? null, retry: () => states.forEach(state => state.error && state.retry()),
    refreshing: states.some(state => state.refreshing), placeholder: states.some(state => state.placeholder),
  };
}

/** The amount of a cost in a unit; zero when the row has none. */
export const costIn = (costs: readonly CostAmount[], unit: string): number => costs.find(cost => cost.unit === unit)?.total ?? 0;

/** The units that appear in the costs of some rows, in a stable order. */
export const costUnits = (rows: readonly Readonly<{ costs: readonly CostAmount[] }>[]): string[] => [...new Set(rows.flatMap(row => row.costs.map(cost => cost.unit)))].sort();

/** The sum of numbers. */
export const total = (values: readonly number[]): number => values.reduce((sum, value) => sum + value, 0);

/** Splits `mcp__server__tool` into its server and tool; other names have no server. */
export function mcpParts(name: string): Readonly<{ server: string; tool: string }> | null {
  const match = /^mcp__(.+?)__(.+)$/.exec(name);
  return match ? { server: match[1], tool: match[2] } : null;
}

// The plugin keys a tool by the kind of activity it was recorded as and its name (`ToolCall:read_file`, `Skill:alta`), and every shell tool by `shell`.
const activityKind = /^(?:ToolCall|CommandExecution|FileChange|McpToolCall|DynamicToolCall|CollabAgentToolCall|WebSearch|ImageGeneration|Skill):(?=.)/;

/** The name of a tool as people know it, and the MCP server it belongs to when it has one, from the key the plugin gives it. */
export function toolParts(tool: string): Readonly<{ name: string; server: string | null }> {
  const name = tool.replace(activityKind, "");
  const mcp = mcpParts(name);
  return mcp ? { name: mcp.tool, server: mcp.server } : { name, server: null };
}

/** The name of a tool in one text: `read_file`, and `issue_read (github)` for a tool of an MCP server. */
export function toolName(tool: string): string {
  const parts = toolParts(tool);
  return parts.server ? `${parts.name} (${parts.server})` : parts.name;
}

/** A tool as a page shows it: the rows of the plugin that read the same (one name, one server, one kind) added up, with the keys they came from. */
export type ToolGroup = ToolRow & Readonly<{ name: string; server: string | null; keys: readonly string[] }>;

/**
 * Adds up the rows of the table of tools that a page would write the same way: the plugin keys a tool by the kind of activity it was recorded
 * as, so one tool can come under two keys (`ToolCall:mcp__github__issue_read` and `McpToolCall:mcp__github__issue_read`). The counts, the times
 * and the sizes are sums and the longest call is the longest of all; the median and the 90th percentile, which do not add up, are those of the
 * rows weighted by their calls. `tool` is the key with the most calls, and `keys` has them all: a question about the tool asks for each.
 */
export function mergeToolRows(rows: readonly ToolRow[]): ToolGroup[] {
  const groups = new Map<string, ToolRow[]>();
  for (const row of rows) {
    const parts = toolParts(row.tool);
    const id = `${row.kind}\u001f${parts.server ?? ""}\u001f${parts.name}`;
    (groups.get(id) ?? groups.set(id, []).get(id)!).push(row);
  }
  return [...groups.values()].map(members => {
    const first = [...members].sort((a, b) => b.calls - a.calls)[0];
    const parts = toolParts(first.tool);
    if (members.length === 1) return { ...first, name: parts.name, server: parts.server, keys: [first.tool] };
    const sum = (pick: (row: ToolRow) => number) => members.reduce((total, row) => total + pick(row), 0);
    const weighted = (pick: (row: ToolRow) => number | undefined) => {
      const known = members.filter(row => pick(row) !== undefined && row.calls > 0);
      const calls = known.reduce((total, row) => total + row.calls, 0);
      return calls === 0 ? undefined : known.reduce((total, row) => total + pick(row)! * row.calls, 0) / calls;
    };
    const calls = sum(row => row.calls), failures = sum(row => row.failures);
    const p50Ms = weighted(row => row.p50Ms), p90Ms = weighted(row => row.p90Ms);
    const length = Math.max(...members.map(row => row.spark.length));
    return {
      kind: first.kind, tool: first.tool, calls, failures, failureRate: calls === 0 ? 0 : failures / calls, timeMs: sum(row => row.timeMs),
      ...(p50Ms === undefined ? {} : { p50Ms }), ...(p90Ms === undefined ? {} : { p90Ms }), maxMs: Math.max(...members.map(row => row.maxMs)),
      bytesIn: sum(row => row.bytesIn), bytesOut: sum(row => row.bytesOut), spark: Array.from({ length }, (_, at) => sum(row => row.spark[at] ?? 0)),
      name: parts.name, server: parts.server, keys: members.map(row => row.tool),
    };
  });
}

/** The rows of a ranking of tools that read the same (one name, one kind) added up; the key is the one with the largest value. */
export function mergeRankedTools(rows: readonly RankedRow[]): RankedRow[] {
  const groups = new Map<string, RankedRow[]>();
  for (const row of rows) {
    const id = `${row.detail ?? ""}\u001f${toolName(row.label)}`;
    (groups.get(id) ?? groups.set(id, []).get(id)!).push(row);
  }
  return [...groups.values()].map(members => {
    if (members.length === 1) return members[0];
    const first = [...members].sort((a, b) => b.value - a.value)[0];
    const sum = (pick: (row: RankedRow) => number) => members.reduce((total, row) => total + pick(row), 0);
    const length = Math.max(...members.map(row => row.spark.length));
    return { ...first, value: sum(row => row.value), share: sum(row => row.share), tokens: sum(row => row.tokens), timeMs: sum(row => row.timeMs), calls: sum(row => row.calls),
      requests: sum(row => row.requests), ...(members.some(row => row.failures !== undefined) ? { failures: sum(row => row.failures ?? 0) } : {}),
      spark: Array.from({ length }, (_, at) => sum(row => row.spark[at] ?? 0)) };
  }).sort((a, b) => b.value - a.value);
}

/**
 * The name of each model of a list, by `provider/model`: the model alone, and with the name of its provider when another row has the same model
 * under another provider, so that no two rows of a chart read the same.
 */
export function modelNames(rows: readonly Readonly<{ provider: string; model: string }>[], providerName: (key: string) => string): Map<string, string> {
  const providers = new Map<string, Set<string>>();
  for (const row of rows) (providers.get(row.model) ?? providers.set(row.model, new Set()).get(row.model)!).add(row.provider);
  return new Map(rows.map(row => [`${row.provider}/${row.model}`, providers.get(row.model)!.size > 1 ? `${row.model} (${providerName(row.provider)})` : row.model]));
}
