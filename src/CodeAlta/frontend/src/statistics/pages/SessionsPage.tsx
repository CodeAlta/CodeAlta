import { Block, DataTable, type Column } from "../blocks";
import { useDrill } from "../pageKit";
import { useSessions } from "../queries";
import { useStatistics } from "../runtime";
import { useText } from "../text";
import type { SessionEntry } from "../types";
import { costIn, costUnits } from "./shared";

// Sessions: the sessions of the period as a table that sorts on any number, each opening its session.

/** The Sessions page. */
export function SessionsPage() {
  const { t } = useText();
  const { fmt, providerName } = useStatistics();
  const drill = useDrill();
  const sessions = useSessions("recent", { main: true, extra: { limit: 500, comparison: "none" } });
  const rows = sessions.data?.rows ?? [];
  const units = costUnits(rows);
  const open = drill.openSession;
  const columns: Column<SessionEntry>[] = [
    { id: "title", header: t("Session"), sort: row => row.title ?? row.sessionId, wide: true, render: row => row.deleted
      ? <span className="stats-deleted" title={t("This session was deleted; its numbers are kept.")}>{row.title ?? row.sessionId.slice(0, 8)}</span>
      : <button type="button" className="stats-cell-link" onClick={() => open(row.sessionId)} title={t("Open the session")}>{row.title ?? row.sessionId.slice(0, 8)}</button> },
    { id: "project", header: t("Project"), sort: row => row.projectName ?? "", render: row => row.projectName ?? "–" },
    { id: "model", header: t("Model"), sort: row => row.model ?? "", render: row => row.model ? <>{row.model}<small className="stats-cell-sub">{row.provider ? providerName(row.provider) : ""}</small></> : "–" },
    { id: "runs", header: t("Runs"), sort: row => row.runs, align: "end", render: row => fmt.number(row.runs) },
    { id: "time", header: t("Active time"), sort: row => row.activeMs, align: "end", render: row => fmt.duration(row.activeMs) },
    { id: "tokens", header: t("Tokens"), sort: row => row.tokens, align: "end", render: row => fmt.compact(row.tokens) },
    { id: "calls", header: t("Tool calls"), sort: row => row.toolCalls, align: "end", render: row => fmt.number(row.toolCalls) },
    ...units.map((unit): Column<SessionEntry> => ({ id: `cost-${unit}`, header: unit === "usd" ? t("Cost (USD)") : t("Cost (credits)"), sort: row => costIn(row.costs, unit), align: "end",
      render: row => costIn(row.costs, unit) ? fmt.cost(unit, costIn(row.costs, unit)) : "–" })),
    { id: "agents", header: t("Sub-agents"), sort: row => row.subAgents, align: "end", render: row => row.subAgents ? fmt.number(row.subAgents) : "–" },
    { id: "last", header: t("Last activity"), sort: row => row.lastActivity ?? "", align: "end", render: row => row.lastActivity ? fmt.dayLong(row.lastActivity.slice(0, 10)) : "–" },
  ];
  return <div className="stats-grid">
    <Block title={t("The sessions of the period")} span={12} minHeight={260} query={sessions} empty={sessions.data !== undefined && rows.length === 0}
      caption={sessions.data?.truncated ? t("The {count} most recent of {total}", { count: rows.length, total: sessions.data.totalRows }) : undefined}>
      {sessions.data && <DataTable label={t("Sessions")} columns={columns} rows={rows} rowKey={row => row.sessionId} dimmed={row => row.deleted} initialSort={{ column: "last", descending: true }} limit={30} />}
    </Block>
  </div>;
}
