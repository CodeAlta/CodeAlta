import { useMemo } from "react";
import { Sparkline } from "../../charts";
import { Block, Choice, DataTable, type Column } from "../blocks";
import { usePageColors } from "../colors";
import { SeriesChart, useDrill } from "../pageKit";
import { StatChart } from "../StatChart";
import { partsOption, ratioSeries } from "../options";
import { useDistribution, useModels, useSeries } from "../queries";
import { useStatistics } from "../runtime";
import { useText } from "../text";
import type { EffortRow, ModelRow } from "../types";
import { DistributionChart } from "../pageKit";
import { costIn, costUnits, firstQuery } from "./shared";
import type { UsageUnit } from "../frame";

// Models: which providers, models and efforts do I use, for how many tokens and how long?

const unitMetric: Record<UsageUnit, string> = { tokens: "tokens", requests: "requests", time: "active-time" };

/** The Models page. */
export function ModelsPage() {
  const { t } = useText();
  const { fmt, frame, dispatch } = useStatistics();
  const colors = usePageColors();
  const drill = useDrill();
  const unit = frame.view.unit;
  const over = useSeries(unitMetric[unit], "model", { main: true, extra: { limit: 8 } });
  const models = useModels();
  const cacheRead = useSeries("cache-read-tokens", "model", { extra: { limit: 6, comparison: "none" } });
  const input = useSeries("input-tokens", "model", { extra: { limit: 6, comparison: "none" } });
  const request = useDistribution("request-input", null);
  const cache = useMemo(() => cacheRead.data && input.data ? ratioSeries(cacheRead.data, input.data) : null, [cacheRead.data, input.data]);
  const parts = useMemo(() => models.data ? partsOption({ fmt, unit: "tokens", partNames: [t("Fresh input"), t("Cached input"), t("Cache written"), t("Output")],
    colors: [colors.at(0), colors.at(5), colors.at(2), colors.at(1)],
    items: models.data.rows.slice(0, 10).map(row => ({ name: row.model, parts: [row.freshInputTokens, row.cacheReadTokens, row.cacheWriteTokens, row.outputTokens] })) }) : null, [models.data, fmt, t, colors]);
  const costs = costUnits(models.data?.rows ?? []);
  const unitOptions = [{ value: "tokens", label: t("Tokens") }, { value: "requests", label: t("Requests") }, { value: "time", label: t("Time") }] as const;

  const columns: Column<ModelRow>[] = [
    { id: "model", header: t("Model"), sort: row => row.model, wide: true, render: row => <button type="button" className="stats-cell-link" onClick={() => drill.filterBy("model", row.model, row.model)}
      title={t("Filter on {name}", { name: row.model })}>{row.model}<small>{row.provider}</small></button> },
    { id: "requests", header: t("Requests"), sort: row => row.requests, align: "end", render: row => fmt.number(row.requests) },
    { id: "input", header: t("Input"), sort: row => row.inputTokens, align: "end", render: row => fmt.compact(row.inputTokens) },
    { id: "cache", header: t("Cached"), sort: row => row.cacheShare, align: "end", render: row => fmt.percent(row.cacheShare, 0) },
    { id: "output", header: t("Output"), sort: row => row.outputTokens, align: "end", render: row => fmt.compact(row.outputTokens) },
    { id: "reasoning", header: t("Reasoning"), sort: row => row.reasoningTokens, align: "end", render: row => fmt.compact(row.reasoningTokens) },
    { id: "time", header: t("Time"), sort: row => row.activeMs, align: "end", render: row => fmt.duration(row.activeMs) },
    ...costs.map((cost): Column<ModelRow> => ({ id: `cost-${cost}`, header: cost === "usd" ? t("Cost (USD)") : t("Cost (credits)"), sort: row => costIn(row.costs, cost), align: "end",
      render: row => costIn(row.costs, cost) ? fmt.cost(cost, costIn(row.costs, cost)) : "–" })),
    { id: "trend", header: "", render: row => <Sparkline values={row.spark} width={64} height={20} ariaLabel={t("Tokens of {name} over time", { name: row.model })} /> },
  ];
  const effortColumns: Column<EffortRow>[] = [
    { id: "model", header: t("Model"), sort: row => row.model, wide: true, render: row => <>{row.model}<small className="stats-cell-sub">{row.provider}</small></> },
    { id: "effort", header: t("Effort"), sort: row => row.effort, render: row => row.effort || "–" },
    { id: "requests", header: t("Requests"), sort: row => row.requests, align: "end", render: row => fmt.number(row.requests) },
    { id: "tokens", header: t("Tokens"), sort: row => row.tokens, align: "end", render: row => fmt.compact(row.tokens) },
    { id: "share", header: t("Reasoning share"), sort: row => row.reasoningShare, align: "end", render: row => fmt.percent(row.reasoningShare, 0) },
  ];
  return <div className="stats-grid">
    <Block title={t("Tokens by model")} span={12} minHeight={300} query={over} empty={over.data !== undefined && over.data.series.length === 0}
      actions={<Choice label={t("Show")} value={unit} onChange={value => dispatch({ type: "view", view: { unit: value } })} options={unitOptions} />}>
      {over.data && <SeriesChart result={over.data} kind="area" height={270} ariaLabel={t("By model")} onLine={line => { if (line.key !== "other") drill.filterBy("model", line.key, line.label); }} />}
    </Block>
    <Block title={t("What tokens are made of")} span={6} minHeight={280} query={models} empty={models.data !== undefined && models.data.rows.length === 0}>
      {parts && <StatChart option={parts.option} table={parts.table} ariaLabel={t("Kinds of tokens by model")} height={260} group="models-parts" />}
    </Block>
    <Block title={t("Cache")} span={6} minHeight={280} query={firstQuery(cacheRead, input)} empty={cache !== null && cache.series.length === 0}>
      {cache && <SeriesChart result={cache} kind="line" stacked={false} height={260} ariaLabel={t("Share of input read from the cache, by model")} />}
    </Block>
    <Block title={t("Size of a request")} span={6} minHeight={260} query={request} empty={request.data !== undefined && request.data.count === 0}>
      {request.data && <DistributionChart result={request.data} name={t("Requests")} ariaLabel={t("Input tokens per request, on a logarithmic scale")} value={value => fmt.compact(value)} />}
    </Block>
    <Block title={t("Reasoning effort")} span={6} minHeight={260} query={models} empty={models.data !== undefined && models.data.efforts.length === 0}>
      {models.data && <DataTable label={t("Models by reasoning effort")} columns={effortColumns} rows={models.data.efforts} rowKey={row => `${row.provider}/${row.model}/${row.effort}`} initialSort={{ column: "tokens", descending: true }} limit={8} />}
    </Block>
    <Block title={t("The models")} span={12} minHeight={200} query={models} empty={models.data !== undefined && models.data.rows.length === 0}>
      {models.data && <DataTable label={t("Models")} columns={columns} rows={models.data.rows} rowKey={row => `${row.provider}/${row.model}`} initialSort={{ column: "input", descending: true }} />}
    </Block>
  </div>;
}
