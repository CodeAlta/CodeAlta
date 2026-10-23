import { useMemo } from "react";
import { Sparkline } from "../../charts";
import { Block, DataTable, RankedBars, type Column, type RankedItem } from "../blocks";
import { usePageColors } from "../colors";
import { filterValueLabel, skillSourceLabel } from "../labels";
import { SeriesChart, useDrill } from "../pageKit";
import { toolDurationOption, treemapOption } from "../options";
import { StatChart } from "../StatChart";
import { useDetails, useMcpTools, useSeries, useSkills, useToolDurations, useTools } from "../queries";
import { boxStatsOfSteps, mergeSteps } from "../steps";
import { useStatistics } from "../runtime";
import { useText } from "../text";
import type { SkillRow } from "../types";
import { mergeToolRows, type ToolGroup } from "./shared";

// Tools: what do agents do — which tools, how often, how long, how often do they fail?

// The local day of an instant, `yyyy-MM-dd`: a last use is told by its day, its quarter hour is in the tooltip.
function localDay(instant: string): string {
  const date = new Date(instant);
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

/** The Tools page. */
export function ToolsPage() {
  const { t } = useText();
  const { fmt, context } = useStatistics();
  const colors = usePageColors();
  const drill = useDrill();
  const byKind = useSeries("tool-calls", "kind", { main: true, extra: { limit: 8 } });
  const tools = useTools({ extra: { limit: 200, comparison: "none" } });
  // A tool the plugin has under two keys is one tool here: its rows are added up, and a question about it asks for each of its keys.
  const rows = useMemo(() => mergeToolRows(tools.data?.rows ?? []), [tools.data]);
  const top = useMemo(() => [...rows].sort((a, b) => b.calls - a.calls).slice(0, 6), [rows]);
  const topKeys = useMemo(() => top.flatMap(row => row.keys).slice(0, 12), [top]);
  const asked = useToolDurations(topKeys, { extra: { comparison: "none" } });
  // A period without a tool has no duration to ask for: the block then follows the table, and says that there is nothing.
  const durations = topKeys.length === 0 ? { ...asked, loading: tools.loading, error: tools.error, retry: tools.retry } : asked;
  const shell = useDetails("shell-program", { extra: { limit: 8, comparison: "none" } });
  const alta = useDetails("alta-command", { extra: { limit: 8, comparison: "none" } });
  // Every call of an MCP tool, those made through `alta mcp tool call` included, and every use of a skill.
  const mcpTools = useMcpTools({ extra: { limit: 8, comparison: "none" } });
  const skills = useSkills({ extra: { limit: 100, comparison: "none" } });

  const nameOf = (row: ToolGroup) => row.server ? `${row.name} (${row.server})` : row.name;
  const tree = useMemo(() => {
    const kinds = new Map<string, ToolGroup[]>();
    for (const row of rows) (kinds.get(row.kind) ?? kinds.set(row.kind, []).get(row.kind)!).push(row);
    const items = [...kinds].map(([kind, list]) => ({ name: filterValueLabel(t, "toolKind", kind), value: list.reduce((sum, row) => sum + row.timeMs, 0),
      children: list.filter(row => row.timeMs > 0).map(row => ({ name: nameOf(row), value: row.timeMs })) })).filter(item => item.value > 0).sort((a, b) => b.value - a.value);
    return treemapOption(items, t("Time"), fmt, "ms", (_, index) => colors.at(index));
  }, [rows, t, fmt, colors]);
  const boxes = useMemo(() => {
    const answers = durations.data ?? [];
    const items = top.flatMap(row => {
      const stats = boxStatsOfSteps(mergeSteps(answers.filter(result => result.subject !== undefined && row.keys.includes(result.subject)).map(result => result.steps)));
      return stats ? [{ name: nameOf(row), stats }] : [];
    });
    return items.length === 0 ? null : toolDurationOption(items, { fmt, name: t("Duration"), p90Name: t("90th percentile") });
  }, [durations.data, top, t, fmt]);
  const mcp = useMemo<RankedItem[]>(() => (mcpTools.data?.rows ?? [])
    .map(row => ({ key: `${row.server}/${row.tool}`, label: row.tool, detail: row.server, value: row.calls, text: fmt.number(row.calls), share: 0, spark: row.spark })), [mcpTools.data, fmt]);
  const toItems = (list: readonly { name: string; count: number }[] | undefined): RankedItem[] => (list ?? []).map(row => ({ key: row.name, label: row.name, value: row.count, text: fmt.number(row.count), share: 0 }));

  const columns: Column<ToolGroup>[] = [
    { id: "tool", header: t("Tool"), sort: row => row.name, wide: true, render: row =>
      <>{row.name}<small className="stats-cell-sub">{row.server ? `${filterValueLabel(t, "toolKind", row.kind)} · ${row.server}` : filterValueLabel(t, "toolKind", row.kind)}</small></> },
    { id: "calls", header: t("Calls"), sort: row => row.calls, align: "end", render: row => fmt.number(row.calls) },
    { id: "failures", header: t("Failures"), sort: row => row.failureRate, align: "end", render: row => row.failures ? `${fmt.number(row.failures)} · ${fmt.percent(row.failureRate)}` : "–" },
    { id: "time", header: t("Total time"), sort: row => row.timeMs, align: "end", render: row => fmt.duration(row.timeMs) },
    { id: "p50", header: t("Median"), sort: row => row.p50Ms ?? 0, align: "end", render: row => row.p50Ms === undefined ? "–" : fmt.duration(row.p50Ms) },
    { id: "p90", header: t("90th percentile"), sort: row => row.p90Ms ?? 0, align: "end", render: row => row.p90Ms === undefined ? "–" : fmt.duration(row.p90Ms) },
    { id: "in", header: t("Bytes in"), sort: row => row.bytesIn, align: "end", render: row => fmt.bytes(row.bytesIn) },
    { id: "out", header: t("Bytes out"), sort: row => row.bytesOut, align: "end", render: row => fmt.bytes(row.bytesOut) },
    { id: "trend", header: "", render: row => <Sparkline values={row.spark} width={64} height={20} ariaLabel={t("Calls of {name} over time", { name: nameOf(row) })} /> },
  ];

  const spaceName = (id: string) => context.spaces?.find(space => space.id === id)?.name ?? id;
  const skillColumns: Column<SkillRow>[] = [
    { id: "skill", header: t("Skill"), sort: row => row.name, wide: true, render: row => <>{row.name}<small className="stats-cell-sub">{skillSourceLabel(t, row.source)}</small></> },
    { id: "uses", header: t("Uses"), sort: row => row.uses, align: "end", render: row => fmt.number(row.uses) },
    { id: "activated", header: t("By agents"), sort: row => row.activated, align: "end", render: row => row.activated ? fmt.number(row.activated) : "–" },
    { id: "attached", header: t("Attached to prompts"), sort: row => row.attached, align: "end", render: row => row.attached ? fmt.number(row.attached) : "–" },
    { id: "read", header: t("SKILL.md read"), sort: row => row.read, align: "end", render: row => row.read ? fmt.number(row.read) : "–" },
    { id: "sessions", header: t("Sessions"), sort: row => row.sessions, align: "end", render: row => fmt.number(row.sessions) },
    { id: "where", header: t("Used in"), sort: row => row.projects[0] ?? "", render: row => <>{row.projects.join(", ")}
      {row.spaces.length > 1 || (row.spaces.length === 1 && row.spaces[0] !== "default") ? <small className="stats-cell-sub">{row.spaces.map(spaceName).join(", ")}</small> : null}</> },
    { id: "last", header: t("Last used"), sort: row => row.lastUsed ?? "", align: "end",
      render: row => row.lastUsed ? <time dateTime={row.lastUsed} title={fmt.dateTime(row.lastUsed)}>{fmt.dayLong(localDay(row.lastUsed))}</time> : "–" },
    { id: "trend", header: "", render: row => <Sparkline values={row.spark} width={64} height={20} ariaLabel={t("Uses of {name} over time", { name: row.name })} /> },
  ];
  const skillKey = (row: SkillRow) => `${row.source ?? ""}/${row.name}`;

  return <div className="stats-grid">
    <Block title={t("Tool calls by kind")} span={12} minHeight={280} query={byKind} empty={byKind.data !== undefined && byKind.data.series.length === 0}>
      {byKind.data && <SeriesChart result={byKind.data} height={260} ariaLabel={t("Tool calls by kind")} name={line => filterValueLabel(t, "toolKind", line.key)}
        onLine={line => { if (line.key !== "other") drill.filterBy("toolKind", line.key, filterValueLabel(t, "toolKind", line.key)); }} />}
    </Block>
    <Block title={t("The tools")} span={12} minHeight={200} query={tools} empty={tools.data !== undefined && rows.length === 0}>
      {tools.data && <DataTable label={t("Tools")} columns={columns} rows={rows} rowKey={row => row.tool} initialSort={{ column: "calls", descending: true }} />}
    </Block>
    <Block title={t("Where time goes")} span={6} minHeight={280} query={tools} empty={tools.data !== undefined && rows.every(row => row.timeMs === 0)}>
      {tools.data && <StatChart option={tree.option} table={tree.table} ariaLabel={t("Tool time by kind, then by tool")} height={260} group="tools-tree" />}
    </Block>
    <Block title={t("Duration of one tool")} span={6} minHeight={280} query={durations} empty={!durations.loading && boxes === null}>
      {boxes && <StatChart option={boxes} ariaLabel={t("Duration of the most called tools, on a logarithmic scale")} height={260} group="tools-box" />}
    </Block>
    <Block title={t("Shell")} span={4} minHeight={170} query={shell} empty={shell.data !== undefined && shell.data.rows.length === 0}>
      {shell.data && <RankedBars label={t("Programs run in the shell")} items={toItems(shell.data.rows)} color={colors.at(3)} />}
    </Block>
    <Block title={t("alta commands")} span={4} minHeight={170} query={alta} empty={alta.data !== undefined && alta.data.rows.length === 0}>
      {alta.data && <RankedBars label={t("Commands of the alta tool")} items={toItems(alta.data.rows)} color={colors.at(4)} />}
    </Block>
    <Block title={t("MCP servers")} span={4} minHeight={170} query={mcpTools} empty={mcpTools.data !== undefined && mcp.length === 0}>
      <RankedBars label={t("Calls by MCP server and tool")} items={mcp} color={colors.at(5)} showSpark />
    </Block>
    <Block title={t("Skills")} span={12} minHeight={170} query={skills} empty={skills.data !== undefined && skills.data.rows.length === 0}>
      {skills.data && <DataTable label={t("Skills")} columns={skillColumns} rows={skills.data.rows} rowKey={skillKey} initialSort={{ column: "uses", descending: true }} />}
    </Block>
  </div>;
}
