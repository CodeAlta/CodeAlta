import { useMemo } from "react";
import { Sparkline } from "../../charts";
import { Block, DataTable, type Column } from "../blocks";
import { usePageColors } from "../colors";
import { useDrill } from "../pageKit";
import { treemapOption } from "../options";
import { StatChart } from "../StatChart";
import { useProjects } from "../queries";
import { useStatistics } from "../runtime";
import { useText } from "../text";
import type { ProjectRow } from "../types";
import { costIn, costUnits } from "./shared";

// Projects: which projects and spaces take the work?

type SpaceRow = Readonly<{ id: string; name: string; projects: number; sessions: number; runs: number; activeMs: number; tokens: number }>;

/** The Projects page. */
export function ProjectsPage() {
  const { t } = useText();
  const { fmt, context } = useStatistics();
  const colors = usePageColors();
  const drill = useDrill();
  const projects = useProjects({ main: true, extra: { limit: 200 } });
  const rows = projects.data?.rows ?? [];
  const units = costUnits(rows);
  const spaces = useMemo<SpaceRow[]>(() => (context.spaces ?? []).flatMap(space => {
    if (!space.projectIds) return [];
    const mine = rows.filter(row => space.projectIds!.includes(row.project));
    return mine.length === 0 ? [] : [{ id: space.id, name: space.name, projects: mine.length, sessions: mine.reduce((sum, row) => sum + row.sessions, 0), runs: mine.reduce((sum, row) => sum + row.runs, 0),
      activeMs: mine.reduce((sum, row) => sum + row.activeMs, 0), tokens: mine.reduce((sum, row) => sum + row.tokens, 0) }];
  }), [context.spaces, rows]);
  const tree = useMemo(() => {
    const groups = (context.spaces ?? []).filter(space => space.projectIds).map(space => ({ name: space.name, children: rows.filter(row => space.projectIds!.includes(row.project)) }));
    const placed = new Set(groups.flatMap(group => group.children.map(row => row.project)));
    const loose = rows.filter(row => !placed.has(row.project));
    const items = [...groups.filter(group => group.children.length > 0).map(group => ({ name: group.name, value: group.children.reduce((sum, row) => sum + row.activeMs, 0),
      children: group.children.map(row => ({ name: row.name, value: row.activeMs })) })), ...loose.map(row => ({ name: row.name, value: row.activeMs }))].filter(item => item.value > 0);
    return treemapOption(items, t("Active time"), fmt, "ms", (_, index) => colors.at(index));
  }, [context.spaces, rows, t, fmt, colors]);
  const columns: Column<ProjectRow>[] = [
    { id: "name", header: t("Project"), sort: row => row.name, wide: true, render: row => <button type="button" className="stats-cell-link" onClick={() => drill.filterBy("project", row.project, row.name)}
      title={t("Filter on {name}", { name: row.name })}>{row.name}</button> },
    { id: "sessions", header: t("Sessions"), sort: row => row.sessions, align: "end", render: row => fmt.number(row.sessions) },
    { id: "runs", header: t("Runs"), sort: row => row.runs, align: "end", render: row => fmt.number(row.runs) },
    { id: "time", header: t("Active time"), sort: row => row.activeMs, align: "end", render: row => fmt.duration(row.activeMs) },
    { id: "tokens", header: t("Tokens"), sort: row => row.tokens, align: "end", render: row => fmt.compact(row.tokens) },
    { id: "calls", header: t("Tool calls"), sort: row => row.toolCalls, align: "end", render: row => fmt.number(row.toolCalls) },
    ...units.map((unit): Column<ProjectRow> => ({ id: `cost-${unit}`, header: unit === "usd" ? t("Cost (USD)") : t("Cost (credits)"), sort: row => costIn(row.costs, unit), align: "end",
      render: row => costIn(row.costs, unit) ? fmt.cost(unit, costIn(row.costs, unit)) : "–" })),
    { id: "trend", header: "", render: row => <Sparkline values={row.spark} width={64} height={20} ariaLabel={t("Active time of {name} over time", { name: row.name })} /> },
  ];
  const spaceColumns: Column<SpaceRow>[] = [
    { id: "name", header: t("Space"), sort: row => row.name, wide: true, render: row => <button type="button" className="stats-cell-link" onClick={() => drill.filterBy("space", row.id, row.name)}>{row.name}</button> },
    { id: "projects", header: t("Projects"), sort: row => row.projects, align: "end", render: row => fmt.number(row.projects) },
    { id: "sessions", header: t("Sessions"), sort: row => row.sessions, align: "end", render: row => fmt.number(row.sessions) },
    { id: "runs", header: t("Runs"), sort: row => row.runs, align: "end", render: row => fmt.number(row.runs) },
    { id: "time", header: t("Active time"), sort: row => row.activeMs, align: "end", render: row => fmt.duration(row.activeMs) },
    { id: "tokens", header: t("Tokens"), sort: row => row.tokens, align: "end", render: row => fmt.compact(row.tokens) },
  ];
  return <div className="stats-grid">
    <Block title={t("The projects")} span={12} minHeight={200} query={projects} empty={projects.data !== undefined && rows.length === 0}>
      {projects.data && <DataTable label={t("Projects")} columns={columns} rows={rows} rowKey={row => row.project} initialSort={{ column: "time", descending: true }} />}
    </Block>
    {spaces.length > 0 && <Block title={t("The spaces")} span={6} minHeight={140} query={projects}>
      <DataTable label={t("Spaces")} columns={spaceColumns} rows={spaces} rowKey={row => row.id} initialSort={{ column: "time", descending: true }} />
    </Block>}
    <Block title={t("Where the time goes")} span={spaces.length > 0 ? 6 : 12} minHeight={260} query={projects} empty={projects.data !== undefined && rows.every(row => row.activeMs === 0)}>
      {projects.data && <StatChart option={tree.option} table={tree.table} ariaLabel={t("Active time by space, then by project")} height={240} group="projects-tree" />}
    </Block>
  </div>;
}
