import { seriesColor } from "./palette";

type Dict = Record<string, unknown>;
const isDict = (value: unknown): value is Dict => typeof value === "object" && value !== null && !Array.isArray(value);
const asList = (value: unknown): unknown[] => Array.isArray(value) ? value : value === undefined || value === null ? [] : [value];

/** The range a data zoom shows, in percent of its axis. */
export type ZoomRange = Readonly<{ start: number; end: number }>;

/** What the user changed on a chart and the page keeps across a rebuild of it: the series they hid and the ranges they zoomed to. */
export type ChartViewState = Readonly<{ hidden: ReadonlySet<string>; zoom: readonly (ZoomRange | undefined)[] }>;

/** The state of a chart nobody has touched. */
export const emptyViewState: ChartViewState = Object.freeze({ hidden: new Set<string>(), zoom: Object.freeze([]) as readonly (ZoomRange | undefined)[] });

/** How a chart is prepared for the page it is on. */
export type PrepareContext = Readonly<{ reducedMotion: boolean; state: ChartViewState }>;

/**
 * The option ECharts is given for the option a page wrote: no animation when the user asks for less motion, the
 * text description of the chart on, tooltips kept inside the chart, the legend drawn by the component (so that it
 * is reachable by keyboard) with the series the user hid still hidden, and the zoom the user chose still chosen.
 */
export function prepareOption(option: Readonly<Record<string, unknown>>, context: PrepareContext): Dict {
  const result: Dict = { ...option };
  if (context.reducedMotion) { result.animation = false; }
  // The name of the chart is the one the page gives it (with its series): the description ECharts writes reads the internal columns of a stack ("is 0, 0, 0, NaN").
  result.aria = { enabled: true, ...(isDict(option.aria) ? option.aria : {}), label: { enabled: false } };
  if (option.tooltip !== undefined) result.tooltip = Array.isArray(option.tooltip) ? option.tooltip : { confine: true, ...(isDict(option.tooltip) ? option.tooltip : {}) };
  if (option.legend !== undefined) {
    const selected = Object.fromEntries([...context.state.hidden].map(name => [name, false]));
    const legends = asList(option.legend).map(legend => ({ ...(isDict(legend) ? legend : {}), show: false, selected: { ...(isDict(legend) && isDict(legend.selected) ? legend.selected : {}), ...selected } }));
    result.legend = Array.isArray(option.legend) ? legends : legends[0];
  }
  if (option.dataZoom !== undefined) {
    const zooms = asList(option.dataZoom).map((zoom, index) => {
      const range = context.state.zoom[index];
      if (!isDict(zoom) || !range) return zoom;
      const { startValue: _startValue, endValue: _endValue, ...rest } = zoom;
      return { ...rest, start: range.start, end: range.end };
    });
    result.dataZoom = Array.isArray(option.dataZoom) ? zooms : zooms[0];
  }
  return result;
}

const zoomBounds = (zoom: unknown): string => isDict(zoom) ? JSON.stringify([zoom.start, zoom.end, zoom.startValue, zoom.endValue]) : "";

/**
 * The state to keep when the page replaces its option. The zoom the user chose is kept unless the page itself
 * moved that zoom (its `start`, `end` or values changed), because then the page decides; hidden series stay hidden
 * only while the new option still has a series of that name.
 */
export function carryViewState(previous: Readonly<Record<string, unknown>>, next: Readonly<Record<string, unknown>>, state: ChartViewState): ChartViewState {
  const before = asList(previous.dataZoom), after = asList(next.dataZoom);
  const zoom = after.map((entry, index) => zoomBounds(entry) === zoomBounds(before[index]) ? state.zoom[index] : undefined);
  const names = new Set(legendEntries(next, []).map(entry => entry.name));
  return { hidden: new Set([...state.hidden].filter(name => names.has(name))), zoom };
}

/** The ranges of the data zooms of an option as ECharts reports it after the user moved them (`chart.getOption()`). */
export function zoomOfOption(option: Readonly<Record<string, unknown>>): (ZoomRange | undefined)[] {
  return asList(option.dataZoom).map(zoom => isDict(zoom) && typeof zoom.start === "number" && typeof zoom.end === "number"
    ? { start: zoom.start, end: zoom.end } : undefined);
}

/** The period a data zoom shows, in the units of its axis: a category label, a number or a time. */
export type ChartPeriod = Readonly<{ start: number; end: number; from: unknown; to: unknown }>;

/** The period of the first data zoom of an option as ECharts reports it; null when there is no zoom. */
export function periodOfOption(option: Readonly<Record<string, unknown>>): ChartPeriod | null {
  const zoom = asList(option.dataZoom).find(isDict);
  if (!zoom || typeof zoom.start !== "number" || typeof zoom.end !== "number") return null;
  const axes = asList(zoom.yAxisIndex !== undefined && zoom.xAxisIndex === undefined ? option.yAxis : option.xAxis).filter(isDict);
  const index = asList(zoom.yAxisIndex !== undefined && zoom.xAxisIndex === undefined ? zoom.yAxisIndex : zoom.xAxisIndex)[0];
  const axis = axes[typeof index === "number" ? index : 0];
  const data = axis && Array.isArray(axis.data) ? axis.data : null;
  const label = (value: unknown) => data && typeof value === "number" ? data[Math.round(value)] : value;
  const entry = (item: unknown) => isDict(item) ? item.value ?? item.name : item;
  return { start: zoom.start, end: zoom.end, from: entry(label(zoom.startValue)), to: entry(label(zoom.endValue)) };
}

/** One entry of the legend: a name, the color of its series and its place. */
export type LegendEntry = Readonly<{ name: string; color: string; index: number }>;

/**
 * The entries of the legend of an option: the names of its `legend.data`, or of its series, or (for the parts of
 * a whole) of the items of its one series. The colors are those ECharts gives, the series' own or the palette in order.
 */
export function legendEntries(option: Readonly<Record<string, unknown>>, palette: readonly string[]): LegendEntry[] {
  const series = asList(option.series).filter(isDict);
  const colors = Array.isArray(option.color) && option.color.length > 0 ? option.color.map(String) : palette;
  const pick = (index: number, explicit: unknown) => typeof explicit === "string" ? explicit : colors.length > 0 ? seriesColor(colors, index) : "currentColor";
  const colorOf = (item: Dict, index: number) => pick(index, isDict(item.itemStyle) && typeof item.itemStyle.color === "string" ? item.itemStyle.color
    : isDict(item.lineStyle) && typeof item.lineStyle.color === "string" ? item.lineStyle.color : item.color);
  const wholes = series.length === 1 && ["pie", "funnel", "sunburst"].includes(String(series[0].type));
  let entries: LegendEntry[];
  if (wholes) {
    entries = asList(series[0].data).filter(isDict).map((item, index) => ({ name: String(item.name ?? ""), color: pick(index, isDict(item.itemStyle) ? item.itemStyle.color : undefined), index }));
  } else {
    entries = series.flatMap((item, index) => typeof item.name === "string" && item.name !== "" ? [{ name: item.name, color: colorOf(item, index), index }] : []);
  }
  const legend = asList(option.legend).find(isDict);
  if (legend && Array.isArray(legend.data) && legend.data.length > 0) {
    const names = legend.data.map(item => isDict(item) ? String(item.name) : String(item));
    const known = new Map(entries.map(entry => [entry.name, entry]));
    entries = names.flatMap((name, position) => { const entry = known.get(name); return entry ? [entry] : [{ name, color: pick(position, undefined), index: position }]; });
  }
  return entries.filter((entry, position) => entries.findIndex(other => other.name === entry.name) === position);
}
