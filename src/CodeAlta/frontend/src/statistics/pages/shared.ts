import type { QueryState } from "../useQuery";
import type { CostAmount, SeriesLine, SeriesResult } from "../types";

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
