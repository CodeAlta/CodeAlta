import { useMemo } from "react";
import { CalendarHeatmap, StatTile } from "../../charts";
import { Block, Choice, RankedBars, type RankedItem } from "../blocks";
import { usePageColors } from "../colors";
import { addDays } from "../frame";
import { comparisonPhrase, filterValueLabel, recordLabel, tileLabel, unitLabel } from "../labels";
import { SeriesChart, useDrill } from "../pageKit";
import { useCalendar, useRecords, useSeries, useSummary, useTop } from "../queries";
import { useStatistics } from "../runtime";
import { useText } from "../text";
import type { RankedRow, RecordEntry, SummaryTile } from "../types";
import type { StackBy, UsageUnit } from "../frame";

// The Overview: how much did I use CodeAlta, and on what?

const stackMetric: Record<UsageUnit, string> = { tokens: "tokens", requests: "requests", time: "active-time" };

/** The tiles at the top of a page: a number, its change against the compared period, and its line. */
export function SummaryTiles({ ids }: Readonly<{ ids: readonly string[] }>) {
  const { t } = useText();
  const { fmt, frame } = useStatistics();
  const summary = useSummary({ main: true });
  const costs = summary.data?.costs ?? [];
  const tiles = summary.data ? ids.flatMap(id => summary.data!.tiles.filter(tile => tile.id === id)) : [];
  const phrase = comparisonPhrase(t, frame.comparison);
  const show = (tile: SummaryTile, label: string, value: string, key = tile.id) => <StatTile key={key} label={label} value={value} current={tile.value} compared={tile.previous ?? null}
    comparedLabel={phrase} goodWhen={tile.id === "errors" || tile.id === "tool-failures" ? "down" : "neutral"} trend={tile.spark} />;
  if (summary.error || summary.loading) return <div className="stats-tiles" aria-busy={summary.loading || undefined}>
    {summary.error ? <Block title={t("Summary")} span={12} minHeight={60} query={summary} /> : ids.map(id => <div key={id} className="stat-tile stats-tile-skeleton bp6-skeleton" aria-hidden="true" />)}
  </div>;
  return <div className="stats-tiles" data-refreshing={summary.refreshing || summary.placeholder || undefined}>
    {tiles.map(tile => show(tile, tileLabel(t, tile.id), fmt.value(tile.unit, tile.value)))}
    {ids.includes("cost") && (costs.length === 0
      ? <StatTile key="cost" label={t("Cost")} value="–" />
      : costs.map(cost => show(cost, `${t("Cost")} · ${unitLabel(t, cost.unit)}`, fmt.costShort(cost.unit, cost.value), `cost-${cost.unit}`)))}
  </div>;
}

function rankedOf(rows: readonly RankedRow[], kind: "projects" | "models" | "tools", textOf: (row: RankedRow) => string): RankedItem[] {
  return rows.map(row => ({ key: row.key, label: row.label, detail: kind === "tools" ? row.detail : kind === "models" ? row.detail : undefined, value: row.value, text: textOf(row), share: row.share, spark: row.spark }));
}

/** The records as small cards. */
export function RecordCards({ records }: Readonly<{ records: readonly RecordEntry[] }>) {
  const { t } = useText();
  const { fmt } = useStatistics();
  const drill = useDrill();
  const text = (record: RecordEntry): string => {
    switch (record.measure) {
      case "longestStreak": return record.value === 1 ? t("1 day") : t("{count} days", { count: record.value });
      case "largestPrompt": return t("{count} characters", { count: fmt.number(record.value) });
      case "mostToolCallsInRun": return fmt.number(record.value);
      case "highestContextFill": return fmt.percent(record.value, 0);
      case "largestRequestInput": return t("{count} tokens", { count: fmt.compact(record.value) });
      default: return fmt.value(record.unit, record.value);
    }
  };
  const when = (record: RecordEntry) => record.at ? (record.at.length > 10 ? fmt.dayLong(record.at) : fmt.dayLong(record.at)) : "";
  return <ul className="stats-records">{records.map(record => {
    const body = <><span className="stats-record-label">{recordLabel(t, record.measure)}</span><strong>{text(record)}</strong>
      <small>{[record.subject || null, when(record)].filter(Boolean).join(" · ")}</small></>;
    return <li key={`${record.measure}-${record.subject}`}>{record.sessionId
      ? <button type="button" className="stats-record" onClick={() => drill.openSession(record.sessionId!)} title={t("Open the session")}>{body}</button>
      : <div className="stats-record">{body}</div>}</li>;
  })}</ul>;
}

/** The Overview page. */
export function OverviewPage() {
  const { t } = useText();
  const { fmt, frame, dispatch, today, weekStart } = useStatistics();
  const drill = useDrill();
  const colors = usePageColors();
  const { stackBy, unit } = frame.view;
  const stacked = useSeries(stackMetric[unit], stackBy, { extra: { limit: 8 } });
  const calendarRequest = { period: "365d", frequency: "day" as const, comparison: "none" as const };
  const calendar = useCalendar({ extra: calendarRequest });
  const projects = useTop("projects", "time", { extra: { limit: 5, comparison: "none" } });
  const models = useTop("models", "tokens", { extra: { limit: 5, comparison: "none" } });
  const tools = useTop("tools", "calls", { extra: { limit: 5, comparison: "none" } });
  const records = useRecords({ extra: { comparison: "none" } });
  const days = useMemo(() => (calendar.data?.days ?? []).map(day => ({ date: day.date, value: day.activeMs })), [calendar.data]);
  const stackKey: Record<StackBy, "provider" | "project" | "model"> = { provider: "provider", project: "project", model: "model" };
  const onLine = (line: { key: string; label: string }) => { if (line.key !== "other") drill.filterBy(stackKey[stackBy], line.key, line.label); };
  const unitOptions = [{ value: "tokens", label: t("Tokens") }, { value: "requests", label: t("Requests") }, { value: "time", label: t("Time") }] as const;
  const stackOptions = [{ value: "provider", label: t("Provider") }, { value: "project", label: t("Project") }, { value: "model", label: t("Model") }] as const;
  return <>
    <SummaryTiles ids={["sessions", "runs", "active-time", "your-prompts", "tokens", "cost"]} />
    <div className="stats-grid">
      <Block title={t("Activity over time")} span={12} minHeight={300} query={stacked}
        actions={<><Choice label={t("Show")} value={unit} onChange={value => dispatch({ type: "view", view: { unit: value } })} options={unitOptions} />
          <Choice label={t("Stack by")} value={stackBy} onChange={value => dispatch({ type: "view", view: { stackBy: value } })} options={stackOptions} /></>}
        empty={stacked.data !== undefined && stacked.data.series.length === 0}>
        {stacked.data && <SeriesChart result={stacked.data} brush height={280} ariaLabel={t("Activity")} onLine={onLine}
          unit={stacked.data.unit} />}
      </Block>
      <Block title={t("The year")} span={12} minHeight={150} query={calendar} empty={calendar.data !== undefined && calendar.data.days.length === 0}>
        {calendar.data && <div className="stats-calendar"><CalendarHeatmap data={days} from={addDays(today, -364)} to={today} ariaLabel={t("Active time per day over the last year")}
          describe={(date, value) => `${fmt.dayLong(date)}: ${value ? fmt.duration(value) : t("no activity")}`} onSelect={date => drill.pickDay(date)} weekStart={weekStart} /></div>}
      </Block>
      <Block title={t("Top projects")} span={4} minHeight={170} query={projects} empty={projects.data !== undefined && projects.data.rows.length === 0}
        actions={projects.data && projects.data.truncated ? <button type="button" className="stats-link" onClick={() => drill.goto("projects")}>{t("Show all")}</button> : undefined}>
        {projects.data && <RankedBars label={t("Projects by active time")} color={colors.at(0)} showSpark items={rankedOf(projects.data.rows, "projects", row => fmt.duration(row.timeMs))}
          onSelect={item => drill.filterBy("project", item.key, item.label)} selectLabel={item => t("Filter on {name}", { name: item.label })} />}
      </Block>
      <Block title={t("Top models")} span={4} minHeight={170} query={models} empty={models.data !== undefined && models.data.rows.length === 0}
        actions={models.data && models.data.truncated ? <button type="button" className="stats-link" onClick={() => drill.goto("models")}>{t("Show all")}</button> : undefined}>
        {models.data && <RankedBars label={t("Models by tokens")} color={colors.at(1)} showSpark items={rankedOf(models.data.rows, "models", row => fmt.compact(row.tokens))}
          onSelect={item => drill.filterBy("model", item.key.split("/").slice(1).join("/") || item.key, item.label)} selectLabel={item => t("Filter on {name}", { name: item.label })} />}
      </Block>
      <Block title={t("Top tools")} span={4} minHeight={170} query={tools} empty={tools.data !== undefined && tools.data.rows.length === 0}
        actions={tools.data && tools.data.truncated ? <button type="button" className="stats-link" onClick={() => drill.goto("tools")}>{t("Show all")}</button> : undefined}>
        {tools.data && <RankedBars label={t("Tools by calls")} color={colors.at(2)} showSpark items={rankedOf(tools.data.rows, "tools", row => fmt.compact(row.calls))}
          onSelect={item => { const kind = tools.data?.rows.find(row => row.key === item.key)?.detail; if (kind) drill.filterBy("toolKind", kind, filterValueLabel(t, "toolKind", kind)); }}
          selectLabel={item => t("Filter on {name}", { name: item.label })} />}
      </Block>
      <Block title={t("Records")} span={12} minHeight={96} query={records} empty={records.data !== undefined && records.data.records.length === 0}>
        {records.data && <RecordCards records={records.data.records} />}
      </Block>
    </div></>;
}
