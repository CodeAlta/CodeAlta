/** A bin of a histogram: the values from `from` (included) to `to` (excluded, but the last bin includes its end) and how many fall in it. */
export type HistogramBin = Readonly<{ from: number; to: number; count: number }>;

/** Values of a histogram: each value once, or a value with how many times it happened. */
export type HistogramInput = readonly number[] | readonly Readonly<{ value: number; count: number }>[];

/** How the bins are cut. */
export type HistogramOptions = Readonly<{
  /** The number of bins (default 20). */
  bins?: number;
  /** `log` cuts the bins at equal ratios; only the positive values count then. Default `linear`. */
  scale?: "linear" | "log";
  /** The edges of the range to cover; the extremes of the values when not given. */
  min?: number;
  max?: number;
}>;

/** The bins of a histogram, and what was left out of them. */
export type Histogram = Readonly<{ bins: readonly HistogramBin[]; total: number; excluded: number }>;

/**
 * Counts values into bins of equal width (`linear`) or equal ratio (`log`: the axis a duration or a size that
 * spans orders of magnitude needs). `excluded` counts the values that were not placed: not finite, outside the
 * range given, or, on a log axis, not above zero.
 */
export function histogram(input: HistogramInput, options: HistogramOptions = {}): Histogram {
  const count = Math.max(1, Math.floor(options.bins ?? 20));
  const log = options.scale === "log";
  const points = (input as readonly (number | { value: number; count: number })[]).map(item => typeof item === "number" ? { value: item, count: 1 } : item)
    .filter(item => item.count > 0);
  const usable = points.filter(item => Number.isFinite(item.value) && (!log || item.value > 0));
  let excluded = points.reduce((sum, item) => sum + item.count, 0) - usable.reduce((sum, item) => sum + item.count, 0);
  if (usable.length === 0) return { bins: [], total: 0, excluded };
  let min = options.min ?? Math.min(...usable.map(item => item.value));
  let max = options.max ?? Math.max(...usable.map(item => item.value));
  if (log) min = Math.max(min, Number.MIN_VALUE);
  if (max <= min) { max = log ? min * 2 : min + 1; }
  const project = (value: number) => log ? Math.log(value) : value;
  const lower = project(min), width = (project(max) - lower) / count;
  const edges = Array.from({ length: count + 1 }, (_, index) => index === count ? max : log ? Math.exp(lower + width * index) : lower + width * index);
  edges[0] = min;
  const counts = new Array<number>(count).fill(0);
  let total = 0;
  for (const { value, count: weight } of usable) {
    if (value < min || value > max) { excluded += weight; continue; }
    const index = Math.min(count - 1, Math.floor((project(value) - lower) / width));
    counts[index] += weight; total += weight;
  }
  return { bins: counts.map((amount, index) => ({ from: edges[index], to: edges[index + 1], count: amount })), total, excluded };
}

/** The quartiles and the extremes of a series of values, with its 90th percentile. */
export type BoxStats = Readonly<{ count: number; min: number; q1: number; median: number; q3: number; p90: number; max: number }>;

/** The value at the fraction `p` (0 to 1) of sorted values, interpolated between neighbors. */
export function quantile(sorted: readonly number[], p: number): number {
  if (sorted.length === 0) return NaN;
  const position = (sorted.length - 1) * Math.min(1, Math.max(0, p));
  const below = Math.floor(position), above = Math.ceil(position);
  return sorted[below] + (sorted[above] - sorted[below]) * (position - below);
}

/** The five numbers of a box plot and the 90th percentile of the values; null when there is no finite value. */
export function boxStats(values: readonly number[]): BoxStats | null {
  const sorted = values.filter(Number.isFinite).sort((a, b) => a - b);
  if (sorted.length === 0) return null;
  return { count: sorted.length, min: sorted[0], q1: quantile(sorted, 0.25), median: quantile(sorted, 0.5), q3: quantile(sorted, 0.75),
    p90: quantile(sorted, 0.9), max: sorted[sorted.length - 1] };
}

/** One box of a box plot: a name and its values, or the numbers when they were computed elsewhere. */
export type BoxItem = Readonly<{ name: string; values: readonly number[] } | { name: string; stats: BoxStats }>;

/** How a box plot is laid out. */
export type BoxPlotOptions = Readonly<{
  /** Boxes run from left to right when true (the names on the vertical axis). Default false. */
  horizontal?: boolean;
  /** The name of the 90th percentile marker. */
  p90Name?: string;
  /** The name of the box series. */
  name?: string;
  /** A logarithmic value axis. */
  log?: boolean;
}>;

/**
 * An option for a box per item: the box is the quartiles with the median, the whiskers are the extremes, and a
 * marker shows the 90th percentile. Plain data: it can be written as JSON.
 */
export function boxPlotOption(items: readonly BoxItem[], options: BoxPlotOptions = {}): Record<string, unknown> {
  const rows = items.flatMap(item => { const stats = "stats" in item ? item.stats : boxStats(item.values); return stats ? [{ name: item.name, stats }] : []; });
  const names = rows.map(row => row.name);
  const category = { type: "category", data: names, axisTick: { alignWithLabel: true } };
  const value = { type: options.log ? "log" : "value" };
  return {
    tooltip: { trigger: "item" },
    grid: { left: 8, right: 16, top: 16, bottom: 8, containLabel: true },
    xAxis: options.horizontal ? value : category,
    yAxis: options.horizontal ? { ...category, inverse: true } : value,
    series: [
      { name: options.name ?? "Distribution", type: "boxplot", data: rows.map(row => [row.stats.min, row.stats.q1, row.stats.median, row.stats.q3, row.stats.max]) },
      { name: options.p90Name ?? "P90", type: "scatter", symbol: "diamond", symbolSize: 8,
        data: rows.map((row, index) => options.horizontal ? [row.stats.p90, index] : [index, row.stats.p90]) },
    ],
  };
}

/** How a histogram is drawn. */
export type HistogramOptionOptions = Readonly<{ name?: string; label?: (from: number, to: number) => string }>;

/** An option that draws the bins of a histogram as bars, labeled with their ranges. */
export function histogramOption(result: Histogram, options: HistogramOptionOptions = {}): Record<string, unknown> {
  const label = options.label ?? ((from: number, to: number) => `${compact(from)}–${compact(to)}`);
  return {
    tooltip: { trigger: "axis", axisPointer: { type: "shadow" } },
    grid: { left: 8, right: 16, top: 16, bottom: 8, containLabel: true },
    xAxis: { type: "category", data: result.bins.map(bin => label(bin.from, bin.to)), axisLabel: { hideOverlap: true } },
    yAxis: { type: "value", minInterval: 1 },
    series: [{ name: options.name ?? "Count", type: "bar", barCategoryGap: "8%", data: result.bins.map(bin => bin.count) }],
  };
}

/** A number in few characters: `1.2k`, `3.4M`, `0.25`. */
export function compact(value: number): string {
  const size = Math.abs(value);
  if (size >= 1e9) return `${trim(value / 1e9)}G`;
  if (size >= 1e6) return `${trim(value / 1e6)}M`;
  if (size >= 1e4) return `${trim(value / 1e3)}k`;
  return trim(value);
}
const trim = (value: number) => String(Number(value.toPrecision(3)));
