import type { ChartTokens } from "./tokens";

/** An ECharts theme, which is plain data. */
export type EChartsTheme = Readonly<Record<string, unknown>>;

/** The theme of ECharts for the tokens of a page: the series palette, the text, the axes, the tooltip and the legend. */
export function buildChartTheme(tokens: ChartTokens): EChartsTheme {
  const text = { color: tokens.text, fontFamily: tokens.fontFamily };
  const muted = { color: tokens.muted, fontFamily: tokens.fontFamily };
  const axis = {
    axisLine: { show: true, lineStyle: { color: tokens.axis } },
    axisTick: { show: true, lineStyle: { color: tokens.axis } },
    axisLabel: { show: true, ...muted },
    splitLine: { show: true, lineStyle: { color: tokens.grid } },
    splitArea: { show: false },
    nameTextStyle: muted,
  };
  return {
    darkMode: tokens.dark,
    color: [...tokens.series],
    backgroundColor: "transparent",
    textStyle: text,
    title: { textStyle: { ...text, fontWeight: 600 }, subtextStyle: muted },
    line: { symbol: "circle", symbolSize: 5, showSymbol: false, lineStyle: { width: 2 }, emphasis: { focus: "series" } },
    bar: { itemStyle: { borderRadius: [2, 2, 0, 0] } },
    boxplot: { itemStyle: { color: withAlpha(tokens.series[0], 0.25), borderColor: tokens.series[0], borderWidth: 1.5 } },
    pie: { itemStyle: { borderColor: tokens.surface, borderWidth: 1 } },
    treemap: { itemStyle: { borderColor: tokens.surface }, breadcrumb: { itemStyle: { color: tokens.grid, textStyle: text } } },
    categoryAxis: { ...axis, splitLine: { show: false } },
    valueAxis: axis,
    logAxis: axis,
    timeAxis: { ...axis, splitLine: { show: false } },
    legend: { textStyle: text, pageTextStyle: text, pageIconColor: tokens.text, pageIconInactiveColor: tokens.muted, inactiveColor: tokens.axis },
    tooltip: {
      backgroundColor: tokens.tooltipBackground, borderColor: tokens.tooltipBorder, borderWidth: 1, textStyle: text,
      axisPointer: {
        lineStyle: { color: tokens.axis, width: 1 }, crossStyle: { color: tokens.axis, width: 1 },
        label: { color: tokens.text, backgroundColor: tokens.tooltipBackground, borderColor: tokens.tooltipBorder, borderWidth: 1 },
      },
    },
    axisPointer: { lineStyle: { color: tokens.axis }, crossStyle: { color: tokens.axis }, label: { color: tokens.text, backgroundColor: tokens.tooltipBackground } },
    dataZoom: {
      textStyle: muted, borderColor: tokens.grid, backgroundColor: "transparent", dataBackground: { lineStyle: { color: tokens.axis }, areaStyle: { color: tokens.grid } },
      selectedDataBackground: { lineStyle: { color: tokens.series[0] }, areaStyle: { color: tokens.series[0], opacity: 0.25 } },
      fillerColor: withAlpha(tokens.series[0], 0.18), handleStyle: { color: tokens.series[0], borderColor: tokens.series[0] },
      moveHandleStyle: { color: tokens.axis }, brushStyle: { color: withAlpha(tokens.series[0], 0.2) },
    },
    brush: { brushStyle: { color: withAlpha(tokens.series[0], 0.2), borderColor: tokens.series[0] } },
    visualMap: { textStyle: muted, color: [...tokens.ramp].reverse() },
    calendar: {
      itemStyle: { color: "transparent", borderColor: tokens.grid, borderWidth: 1 }, splitLine: { lineStyle: { color: tokens.axis } },
      dayLabel: muted, monthLabel: muted, yearLabel: muted,
    },
    markLine: { lineStyle: { color: tokens.axis }, label: muted },
    markArea: { itemStyle: { color: withAlpha(tokens.series[0], 0.1) }, label: muted },
    aria: { decal: { show: false } },
  };
}

/** A color with an opacity: `rgba()` for a hexadecimal color, the color itself for any other. */
export function withAlpha(color: string, alpha: number): string {
  const match = /^#([0-9a-f]{6})$/i.exec(color);
  if (!match) return color;
  const value = parseInt(match[1], 16);
  return `rgba(${value >> 16}, ${(value >> 8) & 255}, ${value & 255}, ${alpha})`;
}
