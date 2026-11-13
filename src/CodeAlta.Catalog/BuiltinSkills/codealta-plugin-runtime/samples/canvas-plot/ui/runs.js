// The dashboard of the canvas `test-runs`: the runs come from the plugin (plugin.cs) with `useRpc`, and Observable Plot draws them. Plot is
// the one the window lends to plugin scripts, imported by its name; it does not know the window, so each chart takes the font and the
// colors of `useTheme()` and is drawn again when the theme or the width of its card changes.
import { useEffect, useMemo, useRef } from "react";
import { barY, boxX, dot, lineY, plot, pointerX, ruleY, tip, windowY } from "@observablehq/plot";
import { VegaLite, html, useRpc, useTheme } from "codealta";

// One chart of Plot in an element of the component: `make(theme, width)` returns the element Plot made (an <svg>, or a <figure> when the
// chart has a legend), which replaces the one before. Never a string for innerHTML.
function PlotChart({ make }) {
  const theme = useTheme();
  const box = useRef(null);
  useEffect(() => {
    const element = box.current;
    if (!element) return undefined;
    let chart = null;
    const draw = () => {
      chart?.remove();
      chart = make(theme, element.clientWidth);
      element.replaceChildren(chart);
    };
    draw();
    const resized = new ResizeObserver(draw);
    resized.observe(element);
    return () => { resized.disconnect(); chart?.remove(); };
  }, [theme, make]);
  return html`<div ref=${box} style=${{ minWidth: 0 }} />`;
}

// What every chart takes from the window: Plot draws its text in `currentColor`, so `color` themes the axes and the labels.
const look = theme => ({ background: "transparent", color: theme.text, fontFamily: theme.fontFamily, fontSize: "12px", overflow: "visible" });

// The suites in the order of their names, so that a suite has the same color in every chart, the Vega-Lite one included.
const suitesOf = runs => [...new Set(runs.map(run => run.suite))].sort();

// The duration of each suite over the days, a moving average of five days over the runs themselves, and a tip under the pointer.
const durations = runs => (theme, width) => plot({
  width, height: 280, marginLeft: 48, style: look(theme),
  color: { domain: suitesOf(runs), range: theme.series, legend: true },
  x: { label: null }, y: { grid: true, label: "Seconds" },
  marks: [
    lineY(runs, windowY({ k: 5 }, { x: "date", y: "seconds", stroke: "suite", strokeWidth: 2 })),
    dot(runs, { x: "date", y: "seconds", fill: "suite", r: 2, fillOpacity: 0.6 }),
    tip(runs, pointerX({ x: "date", y: "seconds", stroke: "suite", fill: theme.tooltipBackground })),
  ],
});

// How much a run of each suite varies: a box per suite.
const spread = runs => (theme, width) => plot({
  width, height: 280, marginLeft: 96, marginBottom: 40, style: look(theme),
  color: { domain: suitesOf(runs), range: theme.series },
  x: { grid: true, label: "Seconds" }, y: { label: null },
  marks: [boxX(runs, { x: "seconds", y: "suite", fill: "suite", fillOpacity: 0.35, stroke: "suite" })],
});

// The failed tests of each day, a bar per suite.
const failures = runs => (theme, width) => plot({
  width, height: 240, marginLeft: 40, style: look(theme),
  color: { domain: suitesOf(runs), range: theme.series, legend: true },
  x: { label: null, interval: "day", tickFormat: "%b %d", ticks: 6 }, y: { grid: true, label: "Failed tests" },
  marks: [barY(runs.filter(run => run.failed > 0), { x: "date", y: "failed", fill: "suite" }), ruleY([0], { stroke: theme.axis })],
});

// The same runs as a Vega-Lite spec, drawn by the window in its colors: the mean duration of each suite, with the fastest and the
// slowest of its runs.
const meanSpec = runs => ({
  data: { values: runs.map(run => ({ suite: run.suite, seconds: run.seconds })) },
  width: 300, height: 200,
  encoding: { y: { field: "suite", type: "nominal", title: null } },
  layer: [
    { mark: "bar", encoding: { x: { aggregate: "mean", field: "seconds", type: "quantitative", title: "Mean seconds, and the range of the runs" },
      color: { field: "suite", type: "nominal", legend: null } } },
    { mark: { type: "rule", strokeWidth: 2 }, encoding: { x: { aggregate: "min", field: "seconds" }, x2: { aggregate: "max", field: "seconds" } } },
  ],
});

export default function TestRuns() {
  const theme = useTheme();
  const result = useRpc("runs.get", {});
  // Plot reads dates as Date objects; the plugin sends them as `yyyy-MM-dd`.
  const runs = useMemo(() => (result.data ?? []).map(run => ({ ...run, date: new Date(`${run.date}T00:00:00Z`) })), [result.data]);
  const charts = useMemo(() => ({ durations: durations(runs), spread: spread(runs), failures: failures(runs) }), [runs]);
  if (result.error) return html`<p role="alert" style=${{ padding: "16px 20px" }}>${result.error.message}</p>`;
  if (!result.data) return html`<p class="alta-muted" style=${{ padding: "16px 20px" }}>Loading the runs…</p>`;
  const card = { border: `1px solid ${theme.grid}`, borderRadius: "8px", padding: "12px 14px", minWidth: 0 };
  const title = text => html`<div style=${{ fontWeight: 600, marginBottom: "8px" }}>${text}</div>`;
  return html`<div style=${{ padding: "16px 20px", display: "grid", gridTemplateColumns: "3fr 2fr", gap: "16px", color: theme.text }}>
    <div style=${card}>${title("Duration of the runs, moving average of 5 days")}<${PlotChart} make=${charts.durations} /></div>
    <div style=${card}>${title("Spread of a run, by suite")}<${PlotChart} make=${charts.spread} /></div>
    <div style=${card}>${title("Failed tests per day")}<${PlotChart} make=${charts.failures} /></div>
    <div style=${card}>${title("Mean duration by suite (a Vega-Lite spec)")}<${VegaLite} spec=${meanSpec(result.data)} /></div>
  </div>`;
}