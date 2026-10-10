// The module a plugin gets for `codealta`: what the application wrote for itself and lends beside its libraries. CodeAlta owns it and keeps it
// stable while React, Blueprint and FlexLayout follow the application's versions. The charts load ECharts when the first one is drawn.
export { altaInterfaceVersion as interfaceVersion } from "../pluginScript/versions";
export { useAlta } from "../pluginScript/PluginScript";
export { Code, Diagram, FileLink, Markdown, SessionLink, html, useRpc, useStream, useTheme, useVisible,
  type RpcResult, type StreamResult } from "../pluginScript/codealta";
export { Icon } from "../pluginScript/icons";
export { BrandIcon, brandIconNames, isBrandIcon } from "../BrandIcon";
export type { Alta, AltaContext, AltaHost, AltaRpc, AltaSignal, AltaTheme } from "../pluginScript/alta";
export { AltaError } from "../pluginScript/alta";
export { CalendarHeatmap, Chart, Sparkline, StatTile, WeekdayHourHeatmap, boxPlotOption, boxStats, histogram, histogramOption, quantile,
  type CalendarHeatmapProps, type ChartDatum, type ChartOption, type ChartProps, type SparklineProps, type StatTileProps, type WeekdayHourHeatmapProps } from "../charts";
