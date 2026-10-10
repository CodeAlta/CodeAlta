// What other parts of the application (and, lent to them, plugins) import from the charts.
// `Chart` holds ECharts in a chunk of its own, loaded when the first chart is shown; everything else here is light.
export { Chart, type ChartDatum, type ChartProps } from "./Chart";
export type { ChartRenderer } from "./chartEngine";
export { CalendarHeatmap, WeekdayHourHeatmap, type CalendarHeatmapProps, type WeekdayHourHeatmapProps } from "./Heatmaps";
export { Sparkline, StatTile, changeView, sparklineShape, type ChangeSense, type ChangeView, type SparklineProps, type SparkValue, type StatTileProps } from "./Sparkline";
export { boxPlotOption, boxStats, compact, histogram, histogramOption, quantile,
  type BoxItem, type BoxPlotOptions, type BoxStats, type Histogram, type HistogramBin, type HistogramInput, type HistogramOptions } from "./distribution";
export { calendarLayout, heatLevels, levelOf, type DayValue, type HeatCell } from "./heat";
export { colorsByName, divergingVariables, fallbackSeries, mixColors, rampVariables, sequentialRamp, seriesColor, seriesColorCount, seriesHues, seriesVariable } from "./palette";
export { defaultChartOptionLimits, sanitizeChartOption, type ChartOption, type ChartOptionLimits, type SanitizedChartOption } from "./sanitize";
export { tableFromOption, tableToText, type ChartTable } from "./table";
export { buildChartTheme, type EChartsTheme } from "./theme";
export { chartTokens, readChartTokens, type ChartTokens } from "./tokens";
export { legendEntries, periodOfOption, type ChartPeriod, type LegendEntry } from "./viewState";
