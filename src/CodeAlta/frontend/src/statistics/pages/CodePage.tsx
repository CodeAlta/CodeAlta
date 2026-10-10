import { useMemo } from "react";
import { Block, RankedBars, type RankedItem } from "../blocks";
import { usePageColors } from "../colors";
import { SeriesChart, useDrill } from "../pageKit";
import { useDetails, useSeries } from "../queries";
import { useStatistics } from "../runtime";
import { useText } from "../text";
import { SummaryTiles } from "./OverviewPage";
import { combineSeries, firstQuery } from "./shared";

// Code: what changed in the files — files touched, lines added and removed?

/** The Code page. */
export function CodePage() {
  const { t } = useText();
  const { fmt } = useStatistics();
  const colors = usePageColors();
  const drill = useDrill();
  const added = useSeries("lines-added", null, { main: true });
  const removed = useSeries("lines-removed", null);
  const addedByProject = useSeries("lines-added", "project", { extra: { comparison: "none", limit: 10 } });
  const removedByProject = useSeries("lines-removed", "project", { extra: { comparison: "none", limit: 10 } });
  const extensions = useDetails("changed-file-extension", { extra: { limit: 10, comparison: "none" } });
  const lines = useMemo(() => added.data && removed.data ? combineSeries([{ result: added.data, key: "added", label: t("Added") }, { result: removed.data, key: "removed", label: t("Removed"), negate: true }], "lines") : null,
    [added.data, removed.data, t]);
  const byProject = useMemo<RankedItem[]>(() => {
    if (!addedByProject.data || !removedByProject.data) return [];
    const totals = new Map<string, { label: string; value: number }>();
    for (const result of [addedByProject.data, removedByProject.data]) for (const line of result.series) {
      const known = totals.get(line.key) ?? { label: line.label, value: 0 };
      known.value += line.total;
      totals.set(line.key, known);
    }
    return [...totals].map(([key, { label, value }]) => ({ key, label, value, text: fmt.number(value), share: 0 })).filter(item => item.key !== "other" && item.value > 0).sort((a, b) => b.value - a.value).slice(0, 8);
  }, [addedByProject.data, removedByProject.data, fmt]);
  const kinds = (extensions.data?.rows ?? []).map(row => ({ key: row.name, label: row.name, value: row.count, text: `${fmt.number(row.count)} · ${fmt.percent(row.share, 0)}`, share: row.share }));
  return <>
    <SummaryTiles ids={["files-changed", "lines-added", "lines-removed"]} />
    <div className="stats-grid">
      <Block title={t("Lines added and removed")} span={12} minHeight={280} query={firstQuery(added, removed)} empty={lines !== null && lines.series.every(line => line.total === 0)}>
        {lines && <SeriesChart result={lines} height={260} ariaLabel={t("Lines added above the axis and removed below")} absolute
          colorOf={line => line.key === "added" ? colors.good : colors.bad} />}
      </Block>
      <Block title={t("By project")} span={6} minHeight={180} query={firstQuery(addedByProject, removedByProject)} empty={byProject.length === 0}>
        <RankedBars label={t("Lines changed by project")} items={byProject} color={colors.at(0)} onSelect={item => drill.filterBy("project", item.key, item.label)}
          selectLabel={item => t("Filter on {name}", { name: item.label })} />
      </Block>
      <Block title={t("By kind of file")} span={6} minHeight={180} query={extensions} empty={kinds.length === 0}>
        <RankedBars label={t("Files changed by extension")} items={kinds} color={colors.at(1)} />
      </Block>
    </div></>;
}
