/**
 * The file of the worker that draws the charts in the built application, below the folder of the entry documents (the window's
 * and the phone's, which serves `assets/`). The build emits `vegaLite.worker.ts` there (vite.config.ts) and the page starts it by
 * this name, so the two agree.
 */
export const chartWorkerFile = "assets/chart-worker.js";
