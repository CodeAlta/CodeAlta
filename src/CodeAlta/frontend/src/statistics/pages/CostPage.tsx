import { useMemo } from "react";
import { Block, RankedBars } from "../blocks";
import { usePageColors } from "../colors";
import { unitLabel } from "../labels";
import { DistributionChart, SeriesChart, useDrill } from "../pageKit";
import { useCostEstimate, useDistribution, useModels, useProjects, useSeries } from "../queries";
import { useStatistics } from "../runtime";
import { useText } from "../text";
import type { SeriesResult } from "../types";
import { SummaryTiles } from "./OverviewPage";
import { costIn } from "./shared";

// Cost: what did it cost, where a cost is known? One block per unit, never one total: dollars and credits do not add up.

function UnitBlocks({ unit, over }: Readonly<{ unit: string; over: SeriesResult }>) {
  const { t } = useText();
  const { fmt } = useStatistics();
  const colors = usePageColors();
  const drill = useDrill();
  const models = useModels();
  const projects = useProjects();
  const perRun = useDistribution("run-cost", unit);
  const only = useMemo<SeriesResult>(() => ({ ...over, series: over.series.filter(line => line.key === unit) }), [over, unit]);
  const write = (value: number) => unit === "usd" ? fmt.cost(unit, value / 1_000_000) : fmt.number(value / 1_000_000);
  const modelItems = (models.data?.rows ?? []).map(row => ({ key: `${row.provider}/${row.model}`, label: row.model, detail: row.provider, value: costIn(row.costs, unit), text: fmt.cost(unit, costIn(row.costs, unit)), share: 0 }))
    .filter(item => item.value > 0).sort((a, b) => b.value - a.value).slice(0, 8);
  const projectItems = (projects.data?.rows ?? []).map(row => ({ key: row.project, label: row.name, value: costIn(row.costs, unit), text: fmt.cost(unit, costIn(row.costs, unit)), share: 0 }))
    .filter(item => item.value > 0).sort((a, b) => b.value - a.value).slice(0, 8);
  const name = unitLabel(t, unit);
  return <>
    <Block title={t("Cost in {unit}", { unit: name })} span={12} minHeight={260} empty={only.series.length === 0}>
      <SeriesChart result={only} height={240} ariaLabel={t("Cost in {unit}", { unit: name })} unit={unit} colorOf={() => colors.at(unit === "usd" ? 1 : 4)} />
    </Block>
    <Block title={t("By model")} span={4} minHeight={170} query={models} empty={modelItems.length === 0}>
      <RankedBars label={t("Cost by model in {unit}", { unit: name })} items={modelItems} color={colors.at(0)} onSelect={item => drill.filterBy("model", item.key.split("/").slice(1).join("/"), item.label)}
        selectLabel={item => t("Filter on {name}", { name: item.label })} />
    </Block>
    <Block title={t("By project")} span={4} minHeight={170} query={projects} empty={projectItems.length === 0}>
      <RankedBars label={t("Cost by project in {unit}", { unit: name })} items={projectItems} color={colors.at(2)} onSelect={item => drill.filterBy("project", item.key, item.label)}
        selectLabel={item => t("Filter on {name}", { name: item.label })} />
    </Block>
    <Block title={t("Cost of a run")} span={4} minHeight={170} query={perRun} empty={perRun.data !== undefined && perRun.data.count === 0}>
      {perRun.data && <DistributionChart result={perRun.data} name={t("Runs")} ariaLabel={t("Cost per run in {unit}, on a logarithmic scale", { unit: name })} value={write} height={170} />}
    </Block>
  </>;
}

/** The block of an estimate from public prices: separate, and marked as an estimate. */
function EstimateBlock() {
  const { t } = useText();
  const estimate = useCostEstimate();
  return <Block title={t("Estimate from public prices")} caption={t("Estimated, in US dollars")} span={12} minHeight={260} query={estimate}
    empty={estimate.data !== undefined && estimate.data.series.every(line => line.total === 0)} className="stats-estimate">
    {estimate.data && <SeriesChart result={estimate.data} height={240} ariaLabel={t("Estimated cost")} unit="usd" />}
  </Block>;
}

/** The Cost page. */
export function CostPage() {
  const { t } = useText();
  const { api } = useStatistics();
  const over = useSeries("cost", "unit", { main: true });
  const units = (over.data?.series ?? []).filter(line => line.total > 0 || over.data!.series.length === 1).map(line => line.key);
  return <>
    <SummaryTiles ids={["cost"]} />
    <div className="stats-grid">
      {over.data && units.length === 0 && <Block title={t("Cost")} span={12} minHeight={80} empty emptyText={t("No cost is reported for this period.")} />}
      {(over.loading || over.error) && <Block title={t("Cost")} span={12} minHeight={260} query={over} />}
      {over.data && units.map(unit => <UnitBlocks key={unit} unit={unit} over={over.data!} />)}
      {api.costEstimate && <EstimateBlock />}
    </div>
  </>;
}
