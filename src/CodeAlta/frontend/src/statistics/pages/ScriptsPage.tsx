import { useMemo } from "react";
import { Sparkline, StatTile } from "../../charts";
import { Block, DataTable, RankedBars, type Column, type RankedItem } from "../blocks";
import { usePageColors } from "../colors";
import { scriptKindLabel, scriptLabel, scriptOriginLabel, scriptStatusLabel, unitLabel } from "../labels";
import { DistributionChart, SeriesChart, useDrill } from "../pageKit";
import { useDetails, useDistribution, useScripts, useSeries } from "../queries";
import { useStatistics } from "../runtime";
import { useText } from "../text";
import type { ScriptAdoptionRow, ScriptRow, ScriptRunSample, ScriptTurnsRow } from "../types";
import { combineSeries, costIn, firstQuery } from "./shared";

// Scripts: how much work runs as scripts of Code Mode, which scripts, how they end, and what their sessions cost.

/** The Scripts page. */
export function ScriptsPage() {
  const { t } = useText();
  const { fmt, providerName } = useStatistics();
  const colors = usePageColors();
  const drill = useDrill();
  const scripts = useScripts({ main: true, extra: { limit: 100, comparison: "none" } });
  const completed = useSeries("script-runs-completed", null);
  const failed = useSeries("script-runs-failed", null);
  const cancelled = useSeries("script-runs-cancelled", null);
  const interrupted = useSeries("script-runs-interrupted", null);
  const kinds = useSeries("script-runs", "script-kind", { extra: { comparison: "none" } });
  const steps = useDetails("script-step-failed", { extra: { limit: 8, comparison: "none" } });
  const errors = useDetails("script-error", { extra: { limit: 8, comparison: "none" } });
  const duration = useDistribution("script-run-duration", null, { extra: { comparison: "none" } });
  const fanOut = useDistribution("script-run-sessions", null, { extra: { comparison: "none" } });

  const outcomes = useMemo(() => completed.data && failed.data && cancelled.data && interrupted.data ? combineSeries([
    { result: completed.data, key: "completed", label: t("Completed") }, { result: failed.data, key: "failed", label: t("Failed") },
    { result: cancelled.data, key: "cancelled", label: t("Stopped") }, { result: interrupted.data, key: "interrupted", label: t("Interrupted") }], "script-runs") : null,
  [completed.data, failed.data, cancelled.data, interrupted.data, t]);
  const outcomeColor = (line: { key: string }) => line.key === "completed" ? colors.good : line.key === "failed" ? colors.bad : line.key === "cancelled" ? colors.at(5) : colors.warn;
  const kindColor = (line: { key: string }) => line.key === "library" ? colors.at(0) : line.key === "inline" ? colors.at(4) : colors.at(6);
  const toItems = (rows: readonly { name: string; count: number; share: number }[] | undefined, label: (name: string) => string = name => name): RankedItem[] =>
    (rows ?? []).map(row => ({ key: row.name, label: label(row.name), value: row.count, text: `${fmt.number(row.count)} · ${fmt.percent(row.share, 0)}`, share: row.share }));
  const stepLabel = (name: string) => { const slash = name.indexOf("/"); return slash < 0 ? name : `${name.slice(slash + 1)} · ${name.slice(0, slash)}`; };

  const data = scripts.data;
  const totals = data?.totals;
  const units = useMemo(() => [...new Set((data?.scripts ?? []).flatMap(row => row.sessionCosts.map(cost => cost.unit)))].sort(), [data]);
  const scriptColumns: Column<ScriptRow>[] = [
    { id: "script", header: t("Script"), sort: row => row.script, wide: true, render: row => scriptLabel(t, row.script) },
    { id: "runs", header: t("Runs"), sort: row => row.runs, align: "end", render: row => fmt.number(row.runs) },
    { id: "failures", header: t("Failures"), sort: row => row.failureRate, align: "end", render: row => row.failed ? `${fmt.number(row.failed)} · ${fmt.percent(row.failureRate)}` : "–" },
    { id: "p50", header: t("Median"), sort: row => row.p50Ms ?? 0, align: "end", render: row => row.p50Ms === undefined ? "–" : fmt.duration(row.p50Ms) },
    { id: "p90", header: t("90th percentile"), sort: row => row.p90Ms ?? 0, align: "end", render: row => row.p90Ms === undefined ? "–" : fmt.duration(row.p90Ms) },
    { id: "sessions", header: t("Sessions"), sort: row => row.sessions, align: "end", render: row => row.sessions ? fmt.number(row.sessions) : "–" },
    { id: "tokens", header: t("Tokens"), sort: row => row.sessionTokens, align: "end", render: row => row.sessionTokens ? fmt.compact(row.sessionTokens) : "–" },
    ...units.map((unit): Column<ScriptRow> => ({ id: `cost-${unit}`, header: unit === "usd" ? t("Cost (USD)") : t("Cost (credits)"), sort: row => costIn(row.sessionCosts, unit), align: "end",
      render: row => costIn(row.sessionCosts, unit) ? fmt.cost(unit, costIn(row.sessionCosts, unit)) : "–" })),
    { id: "last", header: t("Last run"), sort: row => row.lastRun ?? "", align: "end", render: row => row.lastRun ? <time dateTime={row.lastRun} title={fmt.dateTime(row.lastRun)}>{fmt.dayLong(row.lastRun.slice(0, 10))}</time> : "–" },
    { id: "trend", header: "", render: row => <Sparkline values={row.spark} width={64} height={20} ariaLabel={t("Runs of {name} over time", { name: scriptLabel(t, row.script) })} /> },
  ];
  const adoptionColumns: Column<ScriptAdoptionRow>[] = [
    { id: "model", header: t("Model"), sort: row => row.model, wide: true, render: row => <>{row.model}<small className="stats-cell-sub">{providerName(row.provider)}</small></> },
    { id: "turns", header: t("Turns"), sort: row => row.turns, align: "end", render: row => fmt.number(row.turns) },
    { id: "with", header: t("With a script"), sort: row => row.share, align: "end", render: row => row.turnsWithScript ? `${fmt.number(row.turnsWithScript)} · ${fmt.percent(row.share)}` : "–" },
    { id: "runs", header: t("Script runs"), sort: row => row.scriptRuns, align: "end", render: row => row.scriptRuns ? fmt.number(row.scriptRuns) : "–" },
    { id: "calls", header: t("Calls per run"), sort: row => row.callsPerRun ?? 0, align: "end", render: row => row.callsPerRun === undefined ? "–" : fmt.number(row.callsPerRun) },
  ];
  const turnColumns: Column<ScriptTurnsRow>[] = [
    { id: "kind", header: t("Turns"), wide: true, render: row => row.withScript ? t("With a script") : t("Without a script") },
    { id: "count", header: t("Runs"), align: "end", render: row => fmt.number(row.turns) },
    { id: "calls", header: t("Tool calls"), align: "end", render: row => fmt.number(row.toolCalls) },
    { id: "requests", header: t("Requests"), align: "end", render: row => fmt.number(row.requests) },
    { id: "tokens", header: t("Tokens"), align: "end", render: row => fmt.compact(row.tokens) },
    { id: "duration", header: t("Duration"), align: "end", render: row => fmt.duration(row.durationMs) },
  ];
  const recentColumns: Column<ScriptRunSample>[] = [
    { id: "script", header: t("Script"), sort: row => row.script, wide: true, render: row => <button type="button" className="stats-cell-link" onClick={() => drill.openSession(row.sessionId)} title={t("Open the session")}>
      {scriptLabel(t, row.script)}{row.title ? <small className="stats-cell-sub">{row.title}</small> : null}</button> },
    { id: "status", header: t("Status"), sort: row => row.status, render: row => row.error ? `${scriptStatusLabel(t, row.status)} · ${row.error}` : scriptStatusLabel(t, row.status) },
    { id: "origin", header: t("Started by"), sort: row => row.origin, render: row => scriptOriginLabel(t, row.origin) },
    { id: "start", header: t("Started"), sort: row => row.start, align: "end", render: row => <time dateTime={row.start}>{fmt.dateTime(row.start)}</time> },
    { id: "duration", header: t("Duration"), sort: row => row.durationMs ?? -1, align: "end", render: row => row.durationMs === undefined ? "–" : fmt.duration(row.durationMs) },
    { id: "calls", header: t("Calls"), sort: row => row.calls, align: "end", render: row => fmt.number(row.calls) },
    { id: "sessions", header: t("Sessions"), sort: row => row.sessions, align: "end", render: row => row.sessions ? fmt.number(row.sessions) : "–" },
  ];

  return <>
    {scripts.error || scripts.loading || !totals ? <div className="stats-tiles" aria-busy={scripts.loading || undefined}>
      {scripts.error ? <Block title={t("Script runs")} span={12} minHeight={60} query={scripts} /> : [0, 1, 2, 3, 4, 5].map(index => <div key={index} className="stat-tile stats-tile-skeleton bp6-skeleton" aria-hidden="true" />)}
    </div> : <div className="stats-tiles" data-refreshing={scripts.refreshing || scripts.placeholder || undefined}>
      <StatTile label={t("Script runs")} value={fmt.number(totals.runs)} />
      <StatTile label={t("Success rate")} value={totals.successRate === undefined ? "–" : fmt.percent(totals.successRate)} />
      <StatTile label={t("Calls made by scripts")} value={fmt.compact(totals.calls)} />
      <StatTile label={t("Sessions started")} value={fmt.number(totals.sessions)} />
      <StatTile label={t("Tokens of their sessions")} value={fmt.compact(totals.sessionTokens)} />
      {totals.sessionCosts.map(cost => <StatTile key={cost.unit} label={`${t("Cost of their sessions")} · ${unitLabel(t, cost.unit)}`} value={fmt.costShort(cost.unit, cost.total)} />)}
      <StatTile label={t("Waiting for you")} value={fmt.duration(totals.waitMs)} />
    </div>}
    <div className="stats-grid">
      <Block title={t("Runs by outcome")} span={6} minHeight={240} query={firstQuery(completed, failed, cancelled, interrupted)} empty={outcomes !== null && outcomes.series.every(line => line.total === 0)}>
        {outcomes && <SeriesChart result={outcomes} ariaLabel={t("Script runs by outcome")} colorOf={outcomeColor} />}
      </Block>
      <Block title={t("Library and inline scripts")} span={6} minHeight={240} query={kinds} empty={kinds.data !== undefined && kinds.data.series.every(line => line.total === 0)}>
        {kinds.data && <SeriesChart result={kinds.data} ariaLabel={t("Script runs, library and inline")} name={line => scriptKindLabel(t, line.key)} colorOf={kindColor} />}
      </Block>
      <Block title={t("The scripts")} span={12} minHeight={200} query={scripts} empty={data !== undefined && data.scripts.length === 0}>
        {data && <DataTable label={t("Scripts")} columns={scriptColumns} rows={data.scripts} rowKey={row => row.script} initialSort={{ column: "runs", descending: true }} />}
      </Block>
      <Block title={t("Failing steps")} span={6} minHeight={170} query={steps} empty={steps.data !== undefined && steps.data.rows.length === 0}>
        <RankedBars label={t("Steps of library scripts that failed")} items={toItems(steps.data?.rows, stepLabel)} color={colors.bad} />
      </Block>
      <Block title={t("Why runs fail")} span={6} minHeight={170} query={errors} empty={errors.data !== undefined && errors.data.rows.length === 0}>
        <RankedBars label={t("Errors of the runs that failed")} items={toItems(errors.data?.rows)} color={colors.warn} />
      </Block>
      <Block title={t("How long a script run takes")} span={6} minHeight={240} query={duration} empty={duration.data !== undefined && duration.data.count === 0}>
        {duration.data && <DistributionChart result={duration.data} name={t("Script runs")} ariaLabel={t("How long a script run takes, on a logarithmic scale")} height={220} group="scripts-duration" />}
      </Block>
      <Block title={t("Sessions per run")} span={6} minHeight={240} query={fanOut} empty={fanOut.data !== undefined && fanOut.data.count === 0}>
        {fanOut.data && <DistributionChart result={fanOut.data} name={t("Script runs")} ariaLabel={t("Sessions a script run started")} height={220} group="scripts-sessions" />}
      </Block>
      <Block title={t("Scripts by model")} span={6} minHeight={170} query={scripts} empty={data !== undefined && data.adoption.every(row => row.turns === 0)}>
        {data && <DataTable label={t("Scripts by model")} columns={adoptionColumns} rows={data.adoption} rowKey={row => `${row.provider}/${row.model}`} initialSort={{ column: "turns", descending: true }} limit={10} />}
      </Block>
      <Block title={t("A turn, with a script and without")} span={6} minHeight={170} query={scripts} empty={data !== undefined && data.turns.length === 0}>
        {data && <DataTable label={t("A turn, with a script and without")} columns={turnColumns} rows={data.turns} rowKey={row => String(row.withScript)} />}
      </Block>
      <Block title={t("Recent runs")} span={12} minHeight={170} query={scripts} empty={data !== undefined && data.recent.length === 0}>
        {data && <DataTable label={t("Recent runs")} columns={recentColumns} rows={data.recent} rowKey={row => `${row.sessionId}/${row.runId}`} initialSort={{ column: "start", descending: true }} limit={20} />}
      </Block>
    </div>
  </>;
}
