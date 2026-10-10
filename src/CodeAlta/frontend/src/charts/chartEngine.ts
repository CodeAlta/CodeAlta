// The part of the charts that holds ECharts. It is loaded on the first chart (`import("./chartEngine")` in
// Chart.tsx), so the start of the application does not pay for it. Only the chart types, components and
// renderers the pages use are imported, from `echarts/core`: no geo or map part, which is where ECharts
// builds code at run time and the policy of the page (no `eval`) would refuse it.
import * as echarts from "echarts/core";
import { BarChart, BoxplotChart, CustomChart, HeatmapChart, LineChart, PieChart, ScatterChart, TreemapChart } from "echarts/charts";
import { AriaComponent, BrushComponent, CalendarComponent, DataZoomComponent, DatasetComponent, GridComponent, LegendComponent, MarkAreaComponent,
  MarkLineComponent, TitleComponent, TooltipComponent, TransformComponent, VisualMapComponent } from "echarts/components";
import { LabelLayout, UniversalTransition } from "echarts/features";
import { CanvasRenderer, SVGRenderer } from "echarts/renderers";

echarts.use([BarChart, BoxplotChart, CustomChart, HeatmapChart, LineChart, PieChart, ScatterChart, TreemapChart,
  AriaComponent, BrushComponent, CalendarComponent, DataZoomComponent, DatasetComponent, GridComponent, LegendComponent, MarkAreaComponent,
  MarkLineComponent, TitleComponent, TooltipComponent, TransformComponent, VisualMapComponent,
  LabelLayout, UniversalTransition, CanvasRenderer, SVGRenderer]);

/** One ECharts instance. */
export type ChartInstance = echarts.EChartsType;

/** The drawing technique: SVG stays sharp at any page zoom; canvas is for series too long for SVG. */
export type ChartRenderer = "svg" | "canvas";

/** Creates a chart in `dom` with a theme (plain data). */
export function createChart(dom: HTMLElement, theme: object, renderer: ChartRenderer): ChartInstance {
  // zrender reads the pixel ratio once, when it is loaded; the page zoom changes it, so it is given each time.
  return echarts.init(dom, theme, { renderer, devicePixelRatio: window.devicePixelRatio });
}

/** Connects the charts of a group, so that the cursor of one shows on the others. Charts join a group with `chart.group = id`. */
export function connectGroup(id: string): void {
  echarts.connect(id);
}
