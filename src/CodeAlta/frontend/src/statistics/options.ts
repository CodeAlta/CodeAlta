import type { ChartTable } from "../charts";
import type { Formatter } from "./format";
import { addDays, dayDistance } from "./frame";
import { binOf, binSteps, type StepBin } from "./steps";
import type { BucketInfo, Coverage, DistributionResult, Frequency, QueryHeader, SeriesLine, SeriesResult } from "./types";

// The ECharts options of the pages: plain data plus the formatters of the canvas. Pure functions of a result, so each is tested
// without a browser. The colors are not here: a series takes the color its caller gives it (`color(key)`), which is the palette
// of the page by the order the canvas first saw the series in, so a provider has one color on every page.

/** The pixels a chart keeps around its plot; the hatch of the part not read yet is laid over the plot with them. */
export type PlotBox = Readonly<{ left: number; right: number; top: number; bottom: number }>;

/** What a time chart is made of. */
export type TimeSeriesSpec = Readonly<{
  result: SeriesResult;
  kind: "bar" | "area" | "line";
  /** Stacked bars and areas add the lines up; lines never stack. */
  stacked?: boolean;
  fmt: Formatter;
  /** The color of a series, by its key and its place. */
  color: (key: string, index: number) => string;
  /** The text of the line that shows the compared period. */
  previousName: string;
  /** The label of a line the plugin folded into `other`. */
  otherName: string;
  /** A slider under the chart that narrows the period. */
  brush?: boolean;
  /** Replaces the unit of the result (a ratio drawn from two series, for instance). */
  unit?: string;
  /** The color of the line of the compared period and of `other`. */
  muted: string;
  /** Writes the values and the axis without their sign: what is below the axis is still a count. */
  absolute?: boolean;
}>;

/** A chart option and where its plot sits in the box of the chart. */
export type PlottedOption = Readonly<{ option: Record<string, unknown>; plot: PlotBox; boundaryGap: boolean; table: ChartTable }>;

const axisLabelWidth = (text: string) => Math.min(80, Math.max(30, Math.round(text.length * 6.6 + 12)));

/** The bucket labels the axis shows and the long ones the tooltip and the table show. */
export function bucketLabels(buckets: readonly BucketInfo[], frequency: Frequency, fmt: Formatter): Readonly<{ short: string[]; long: string[] }> {
  const long = buckets.map(bucket => fmt.bucketLong(bucket, frequency));
  // Two buckets never share a name: the tooltip and the table are read by it.
  const seen = new Map<string, number>();
  const unique = long.map((name, index) => { const count = seen.get(name) ?? 0; seen.set(name, count + 1); return count === 0 ? name : `${name} (${index})`; });
  return { short: buckets.map(bucket => fmt.bucket(bucket, frequency)), long: unique };
}

/** The table of a time chart: one row per bucket, one column per line. */
export function seriesTable(result: SeriesResult, labels: readonly string[], previousName?: string): ChartTable {
  const lines = result.series;
  const hasPrevious = lines.some(line => line.previous);
  const columns = ["", ...lines.map(line => line.label || result.metric), ...(hasPrevious ? [previousName ?? "previous"] : [])];
  const rows = result.buckets.map((_, at) => [labels[at], ...lines.map(line => round(line.values[at] ?? 0)),
    ...(hasPrevious ? [round(lines.reduce((total, line) => total + (line.previous?.[at] ?? 0), 0))] : [])]);
  return { columns, rows };
}

/** A value rounded for a chart; a value that is not a number is a gap (`null`), not a zero. */
const round = (value: number): number => (Number.isFinite(value) ? Math.round(value * 10_000) / 10_000 : null) as number;

/** Stacked bars, stacked areas or lines of a series over time, with the compared period as a dashed line and an optional brush. */
export function timeSeriesOption(spec: TimeSeriesSpec): PlottedOption {
  const { result, fmt } = spec;
  const unit = spec.unit ?? result.unit;
  const frequency = result.query.frequency;
  const labels = bucketLabels(result.buckets, frequency, fmt);
  const stacked = spec.stacked ?? spec.kind !== "line";
  const maxValue = Math.max(0, ...(stacked ? result.buckets.map((_, at) => result.series.reduce((total, line) => total + (line.values[at] ?? 0), 0)) : result.series.flatMap(line => line.values)));
  const sign = (value: number) => spec.absolute ? Math.abs(value) : value;
  const left = axisLabelWidth(fmt.axis(unit, maxValue * 1.1));
  const plot: PlotBox = { left, right: 14, top: 12, bottom: spec.brush ? 58 : 30 };
  const boundaryGap = spec.kind === "bar";
  const series: Record<string, unknown>[] = result.series.map((line, index) => {
    const color = line.key === "other" ? spec.muted : spec.color(line.key, index);
    const base = { name: line.key === "other" ? spec.otherName : line.label || result.metric, data: line.values.map(round), color, emphasis: { focus: "series" } };
    if (spec.kind === "bar") return { ...base, type: "bar", ...(stacked ? { stack: "total" } : {}), barMaxWidth: 28 };
    if (spec.kind === "area") return { ...base, type: "line", ...(stacked ? { stack: "total" } : {}), areaStyle: { opacity: 0.32 }, lineStyle: { width: 1.5 }, showSymbol: false };
    return { ...base, type: "line", lineStyle: { width: 2 }, showSymbol: false };
  });
  const previous = result.series.filter(line => line.previous);
  if (previous.length > 0) {
    series.push({ name: spec.previousName, type: "line", color: spec.muted, symbol: "none", z: 10, data: result.buckets.map((_, at) => round(previous.reduce((total, line) => total + (line.previous?.[at] ?? 0), 0))),
      lineStyle: { type: "dashed", width: 1.5, opacity: 0.85 }, emphasis: { disabled: true } });
  }
  const option: Record<string, unknown> = {
    tooltip: { trigger: "axis", axisPointer: { type: spec.kind === "bar" ? "shadow" : "line" }, valueFormatter: (value: unknown) => typeof value === "number" ? fmt.value(unit, sign(value)) : String(value ?? "") },
    legend: result.series.length > 1 || previous.length > 0 ? { data: series.map(item => item.name as string) } : undefined,
    grid: { left: plot.left, right: plot.right, top: plot.top, bottom: plot.bottom, containLabel: false },
    xAxis: { type: "category", data: labels.long, boundaryGap, axisLabel: { hideOverlap: true, formatter: (_: string, index: number) => labels.short[index] ?? "" }, axisTick: { alignWithLabel: true } },
    yAxis: { type: "value", axisLabel: { formatter: (value: number) => fmt.axis(unit, sign(value)) }, ...(unit === "count" ? { minInterval: 1 } : {}) },
    series,
    ...(spec.brush ? { dataZoom: [{ type: "slider", xAxisIndex: 0, height: 16, bottom: 6, brushSelect: false, filterMode: "none", showDetail: false, moveHandleSize: 6 }] } : {}),
  };
  if (option.legend === undefined) delete option.legend;
  return { option, plot, boundaryGap, table: seriesTable(result, labels.long, spec.previousName) };
}

/** The days a brush over a time chart selects: from the start of its first bucket to the end of its last. Null when it selects nothing. */
export function periodOfBrush(result: Pick<SeriesResult, "buckets" | "query">, startPercent: number, endPercent: number): string | null {
  const count = result.buckets.length;
  if (count === 0) return null;
  const clamp = (value: number) => Math.min(count - 1, Math.max(0, value));
  const first = clamp(Math.round((startPercent / 100) * (count - 1))), last = clamp(Math.round((endPercent / 100) * (count - 1)));
  if (last < first) return null;
  const dayOf = (bucket: BucketInfo) => bucket.start.slice(0, 10);
  const from = dayOf(result.buckets[first]);
  const next = result.buckets[last + 1];
  let to = next ? addDays(dayOf(next), -(result.query.frequency === "hour" && dayOf(next) === dayOf(result.buckets[last]) ? 0 : 1)) : result.query.to;
  if (to < from) to = from;
  if (from === result.query.from && to === result.query.to) return null;
  return `${from}..${to}`;
}

/** How many of the first buckets are not fully read: the part to hatch. */
export function unreadBuckets(buckets: readonly BucketInfo[], coverage: Coverage): number {
  if (coverage.complete) return 0;
  if (!coverage.completeFrom) return coverage.historyState === "needs-choice" ? buckets.length : 0;
  let count = 0;
  for (const bucket of buckets) { if (bucket.start.slice(0, 10) < coverage.completeFrom) count++; else break; }
  return count;
}

/** The share of the width of a plot the unread buckets take: bars sit in cells, points on a line sit on the cell borders. */
export function hatchFraction(unread: number, buckets: number, boundaryGap: boolean): number {
  if (unread <= 0 || buckets <= 0) return 0;
  if (unread >= buckets) return 1;
  return boundaryGap ? unread / buckets : Math.min(1, unread / Math.max(1, buckets - 1));
}

/** The first day of the data a header says is read, for a note under a chart; null when everything is read. */
export const unreadUntil = (header: QueryHeader): string | null => header.coverage.complete ? null : header.coverage.completeFrom ?? null;

/** What an histogram of a distribution is drawn from. */
export type DistributionSpec = Readonly<{
  result: DistributionResult;
  fmt: Formatter;
  /** The name of the series, shown in the tooltip. */
  name: string;
  medianName: string;
  p90Name: string;
  maxBins?: number;
  /** Writes a value of the unit of the result; the unit of the result by default. */
  value?: (value: number) => string;
}>;

/** A histogram on a logarithmic scale (the steps are cut at equal ratios), with the median and the 90th percentile marked. */
export function distributionOption(spec: DistributionSpec): Readonly<{ option: Record<string, unknown>; bins: readonly StepBin[]; table: ChartTable }> {
  const { result, fmt } = spec;
  const write = spec.value ?? ((value: number) => fmt.value(result.unit === "micro-unit" ? "count" : result.unit, value));
  const bins = binSteps(result.steps, spec.maxBins ?? 24);
  const labels = bins.map(bin => `${write(bin.lower)}`);
  const marks = [[spec.medianName, result.p50], [spec.p90Name, result.p90]].flatMap(([name, value]) => {
    const at = binOf(bins, value as number | undefined);
    return at < 0 ? [] : [{ name: name as string, xAxis: at, label: { formatter: `${name as string} ${write(value as number)}`, position: "end", align: name === spec.medianName ? "right" : "left", distance: 4 } }];
  });
  const option = {
    tooltip: { trigger: "axis", axisPointer: { type: "shadow" }, valueFormatter: (value: unknown) => typeof value === "number" ? fmt.number(value) : String(value ?? "") },
    grid: { left: 40, right: 16, top: 28, bottom: 30, containLabel: false },
    xAxis: { type: "category", data: bins.map(bin => `${write(bin.lower)} – ${write(bin.upper)}`), boundaryGap: true, axisLabel: { interval: 0, hideOverlap: true, formatter: (_: string, index: number) => labels[index] ?? "" } },
    yAxis: { type: "value", minInterval: 1, axisLabel: { formatter: (value: number) => fmt.compact(value) } },
    series: [{ name: spec.name, type: "bar", barCategoryGap: "10%", data: bins.map(bin => bin.count),
      markLine: marks.length > 0 ? { symbol: "none", silent: true, lineStyle: { type: "dashed", width: 1.5 }, data: marks } : undefined }],
  };
  if (marks.length === 0) delete (option.series[0] as Record<string, unknown>).markLine;
  const table: ChartTable = { columns: ["", spec.name], rows: bins.map(bin => [`${write(bin.lower)} – ${write(bin.upper)}`, bin.count]) };
  return { option, bins, table };
}

/** Parts of a whole per item, as horizontal stacked bars: the four kinds of tokens of each model. */
export type PartsSpec = Readonly<{
  items: readonly Readonly<{ name: string; parts: readonly number[] }>[];
  partNames: readonly string[];
  fmt: Formatter;
  colors: readonly string[];
  unit: string;
}>;

/** One horizontal bar per item, in as many parts as `partNames`; the largest item first. */
export function partsOption(spec: PartsSpec): Readonly<{ option: Record<string, unknown>; table: ChartTable }> {
  const { fmt } = spec;
  const items = spec.items;
  const largest = Math.max(0, ...items.map(item => item.parts.reduce((total, part) => total + part, 0)));
  const option = {
    tooltip: { trigger: "axis", axisPointer: { type: "shadow" }, valueFormatter: (value: unknown) => typeof value === "number" ? fmt.value(spec.unit, value) : String(value ?? "") },
    legend: { data: [...spec.partNames] },
    grid: { left: 8, right: 16, top: 8, bottom: 24, containLabel: true },
    xAxis: { type: "value", max: largest > 0 ? undefined : 1, axisLabel: { formatter: (value: number) => fmt.axis(spec.unit, value) } },
    yAxis: { type: "category", inverse: true, data: items.map(item => item.name), axisLabel: { width: 130, overflow: "truncate" } },
    series: spec.partNames.map((name, index) => ({ name, type: "bar", stack: "parts", color: spec.colors[index % spec.colors.length], barMaxWidth: 26, emphasis: { focus: "series" },
      data: items.map(item => round(item.parts[index] ?? 0)) })),
  };
  return { option, table: { columns: ["", ...spec.partNames], rows: items.map(item => [item.name, ...spec.partNames.map((_, index) => round(item.parts[index] ?? 0))]) } };
}

/** A treemap of named values, with optional children. */
export type TreeItem = Readonly<{ name: string; value: number; children?: readonly TreeItem[] }>;

/** A treemap of the items: area is the value; two levels at most. */
export function treemapOption(items: readonly TreeItem[], name: string, fmt: Formatter, unit: string, colors: (name: string, index: number) => string): Readonly<{ option: Record<string, unknown>; table: ChartTable }> {
  const data = items.map((item, index) => ({ name: item.name, value: item.value, itemStyle: { color: colors(item.name, index) },
    ...(item.children && item.children.length > 0 ? { children: item.children.map(child => ({ name: child.name, value: child.value })) } : {}) }));
  const option = {
    tooltip: { trigger: "item", valueFormatter: (value: unknown) => typeof value === "number" ? fmt.value(unit, value) : String(value ?? "") },
    series: [{ name, type: "treemap", roam: false, nodeClick: false, breadcrumb: { show: false }, left: 0, right: 0, top: 0, bottom: 0,
      label: { show: true, formatter: "{b}", overflow: "truncate" }, upperLabel: { show: false }, itemStyle: { gapWidth: 2, borderRadius: 3 }, data }],
  };
  const rows: (string | number | null)[][] = items.flatMap(item => [[item.name, round(item.value)], ...(item.children ?? []).map(child => [`${item.name} / ${child.name}`, round(child.value)])]);
  return { option, table: { columns: ["", name], rows } };
}

/** A scatter of points on two logarithmic axes: what a prompt of this size brought back in time. */
export function scatterOption(points: readonly (readonly [number, number])[], name: string, xName: string, yName: string, fmt: Formatter, yUnit: string): Readonly<{ option: Record<string, unknown>; table: ChartTable }> {
  const usable = points.filter(([x, y]) => x > 0 && y > 0);
  const option = {
    tooltip: { trigger: "item", formatter: (params: { value?: unknown }) => { const [x, y] = (params.value as [number, number]) ?? [0, 0]; return `${xName}: ${fmt.number(x)}<br>${yName}: ${fmt.value(yUnit, y)}`; } },
    grid: { left: 60, right: 16, top: 16, bottom: 44, containLabel: false },
    xAxis: { type: "log", name: xName, nameLocation: "middle", nameGap: 26, axisLabel: { formatter: (value: number) => fmt.compact(value) } },
    yAxis: { type: "log", axisLabel: { formatter: (value: number) => fmt.axis(yUnit, value) } },
    series: [{ name, type: "scatter", symbolSize: 6, itemStyle: { opacity: 0.6 }, data: usable }],
  };
  return { option, table: { columns: [xName, yName], rows: usable.map(([x, y]) => [x, y]) } };
}

/** Two series divided: the ratio of each line of `numerator` to the same line of `denominator`, bucket by bucket (0 where there is no denominator). */
export function ratioSeries(numerator: SeriesResult, denominator: SeriesResult): SeriesResult {
  const bottoms = new Map(denominator.series.map(line => [line.key, line]));
  const series: SeriesLine[] = numerator.series.flatMap(line => {
    const bottom = bottoms.get(line.key);
    if (!bottom) return [];
    const values = line.values.map((value, at) => bottom.values[at] ? value / bottom.values[at] : Number.NaN);
    return [{ key: line.key, label: line.label, values, total: bottom.total ? line.total / bottom.total : 0 }];
  });
  return { ...numerator, metric: `${numerator.metric}/${denominator.metric}`, unit: "ratio", series };
}

/** The days between two dates, for the length of a custom period. */
export const daysBetween = (from: string, to: string) => dayDistance(from, to) + 1;
