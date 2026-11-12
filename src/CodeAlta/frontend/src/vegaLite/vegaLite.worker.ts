// The worker that draws the charts of `vega-lite` blocks, away from the page: a spec that takes too long is stopped with
// its worker (`vegaLite.ts`), and the window never waits for one. It answers each job with an SVG string or a refusal.
import * as vega from "vega";
import { secureVega, drawChart, type ChartJob } from "./vegaLiteDrawing";
import { chartRefusalOf } from "./vegaLiteSpec";

secureVega();

// What Vega measures text with, which its types do not declare (vega-scenegraph): the width of a text replaces its estimate.
type TextItem = object;
const { font, fontSize, textMetrics } = vega as unknown as { font(item: TextItem): string; fontSize(item: TextItem): number;
  textMetrics: { width: (item: TextItem, text?: unknown) => number } };

// A worker has no document, where Vega measures text: it measures it on a canvas of its own, in the fonts of the system.
const context = typeof OffscreenCanvas === "function" ? new OffscreenCanvas(1, 1).getContext("2d") : null;
if (context) {
  const widths = new Map<string, number>();
  textMetrics.width = (item: TextItem, text?: unknown) => {
    const value = text === null || text === undefined ? "" : String(text).trim();
    if (!value || fontSize(item) <= 0) return 0;
    const shape = font(item), key = `${shape}\n${value}`;
    let width = widths.get(key);
    if (width === undefined) {
      context.font = shape;
      width = context.measureText(value).width;
      if (widths.size > 10_000) widths.clear();
      widths.set(key, width);
    }
    return width;
  };
}

self.onmessage = (event: MessageEvent<{ id: number; job: ChartJob }>) => {
  const { id, job } = event.data;
  drawChart(job).then(svg => self.postMessage({ id, svg }), error => self.postMessage({ id, refused: chartRefusalOf(error) }));
};
