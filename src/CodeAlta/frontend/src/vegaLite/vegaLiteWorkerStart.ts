/**
 * Starts the worker that draws the charts: a file of its own, with Vega and Vega-Lite, which the window loads only when it
 * draws its first chart. The browser tests of the window replace this module with one that starts their bundle of it.
 */
export function startChartWorker(): Worker {
  return new Worker(new URL("./vegaLite.worker.ts", import.meta.url), { type: "module", name: "Charts" });
}
