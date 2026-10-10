import { useMemo } from "react";
import { StatTile } from "../../charts";
import { Block, DataTable, RankedBars, type Column, type RankedItem } from "../blocks";
import { usePageColors } from "../colors";
import { filterValueLabel, triggerLabel } from "../labels";
import { SeriesChart } from "../pageKit";
import { useHealth } from "../queries";
import { useStatistics } from "../runtime";
import { useText } from "../text";
import type { ContextFillRow, SeriesResult } from "../types";

// Health: errors, interrupted runs, compactions, how full the context gets.

/** Draws the series of numbers a result holds beside its buckets, as the result of a series. */
function asSeries(health: NonNullable<ReturnType<typeof useHealth>["data"]>, key: string, label: string, values: readonly number[]): SeriesResult {
  return { query: health.query, metric: key, unit: "count", buckets: health.buckets, series: [{ key, label, values, total: values.reduce((sum, value) => sum + value, 0) }] };
}

/** The Health page. */
export function HealthPage() {
  const { t } = useText();
  const { fmt } = useStatistics();
  const colors = usePageColors();
  const health = useHealth({ main: true });
  const data = health.data;
  const errors = useMemo(() => data ? asSeries(data, "errors", t("Errors"), data.errors) : null, [data, t]);
  const interrupted = useMemo(() => data ? asSeries(data, "interrupted", t("Interrupted runs"), data.interruptedRuns) : null, [data, t]);
  const compactions = useMemo(() => data ? asSeries(data, "compactions", t("Compactions"), data.compactions) : null, [data, t]);
  const failed = useMemo<RankedItem[]>(() => (data?.failedTools ?? []).map(row => ({ key: row.tool, label: row.tool, detail: filterValueLabel(t, "toolKind", row.kind), value: row.failures,
    text: `${fmt.number(row.failures)} · ${fmt.percent(row.failureRate)}`, share: 0, spark: row.spark })), [data, t, fmt]);
  const triggers = useMemo<RankedItem[]>(() => (data?.compactionsByTrigger ?? []).map(item => ({ key: item.key, label: triggerLabel(t, item.key), value: item.value, text: fmt.number(item.value), share: 0 })), [data, t, fmt]);
  const fill: Column<ContextFillRow>[] = [
    { id: "model", header: t("Model"), sort: row => row.model, wide: true, render: row => <>{row.model}<small className="stats-cell-sub">{row.provider}</small></> },
    { id: "average", header: t("Average fill"), sort: row => row.average ?? 0, align: "end", render: row => row.average === undefined ? "–" : fmt.percent(row.average, 0) },
    { id: "highest", header: t("Highest fill"), sort: row => row.highest, align: "end", render: row => fmt.percent(row.highest, 0) },
    { id: "bar", header: "", render: row => <span className="stats-fill" aria-hidden="true"><i style={{ width: `${Math.min(100, row.highest * 100)}%` }} /></span> },
    { id: "samples", header: t("Requests"), sort: row => row.samples, align: "end", render: row => fmt.number(row.samples) },
  ];
  const empty = data !== undefined && data.runs === 0;
  return <>
    <div className="stats-tiles" data-refreshing={health.refreshing || health.placeholder || undefined}>
      {data && <>
        <StatTile label={t("Errors")} value={fmt.number(data.errors.reduce((sum, value) => sum + value, 0))} />
        <StatTile label={t("Errors per run")} value={fmt.percent(data.errorRate)} />
        <StatTile label={t("Interrupted runs")} value={fmt.number(data.interruptedRuns.reduce((sum, value) => sum + value, 0))} />
        <StatTile label={t("Compactions")} value={fmt.number(data.compactions.reduce((sum, value) => sum + value, 0))} />
        {data.tokensBeforeCompaction > 0 && <StatTile label={t("Context before and after")} value={`${fmt.compact(data.tokensBeforeCompaction)} → ${fmt.compact(data.tokensAfterCompaction)}`} />}
      </>}
    </div>
    <div className="stats-grid">
      <Block title={t("Errors")} span={6} minHeight={240} query={health} empty={empty}>
        {errors && <SeriesChart result={errors} height={220} ariaLabel={t("Errors")} colorOf={() => colors.bad} />}
      </Block>
      <Block title={t("Interrupted runs")} span={6} minHeight={240} query={health} empty={empty}>
        {interrupted && <SeriesChart result={interrupted} height={220} ariaLabel={t("Interrupted runs")} colorOf={() => colors.warn} />}
      </Block>
      <Block title={t("Tools that fail")} span={6} minHeight={200} query={health} empty={empty || failed.length === 0}>
        <RankedBars label={t("Failed calls by tool")} items={failed} color={colors.bad} showSpark />
      </Block>
      <Block title={t("Compactions")} span={6} minHeight={200} query={health} empty={empty || (data !== undefined && data.compactions.every(value => value === 0))}>
        {compactions && <><SeriesChart result={compactions} height={150} ariaLabel={t("Compactions")} colorOf={() => colors.at(0)} />
          <RankedBars label={t("Compactions by trigger")} items={triggers} color={colors.at(0)} /></>}
      </Block>
      <Block title={t("How full the context gets")} span={12} minHeight={160} query={health} empty={empty || (data !== undefined && data.contextByModel.length === 0)}>
        {data && <DataTable label={t("Fill of the context window by model")} columns={fill} rows={data.contextByModel} rowKey={row => `${row.provider}/${row.model}`} initialSort={{ column: "highest", descending: true }} />}
      </Block>
    </div></>;
}
