import { chartWorkerFile } from "./chartWorkerFile";

/**
 * Starts the worker that draws the charts: a file of its own, with Vega and Vega-Lite, which the window loads only when it draws its
 * first chart. It is named, not made with `import.meta.url`, which every bundle of the tests would warn about. The browser tests of
 * the window replace this module with one that starts their bundle of it.
 */
export function startChartWorker(): Worker {
  return new Worker(chartWorkerFile, { type: "module", name: "Charts" });
}
