// The charts alone, in a page, over literal data: no application host. Driven by charts.browser.test.ts
// through `window.chartsFixture`; every chart is mounted under React StrictMode, as the application is.
import { StrictMode, useState, type ReactNode } from "react";
import { createRoot } from "react-dom/client";
import * as echarts from "echarts/core";
import { ShellLanguageContext } from "../shellLanguage";
import { CalendarHeatmap, Chart, Sparkline, StatTile, WeekdayHourHeatmap, boxPlotOption, histogram, histogramOption, type ChartDatum, type ChartPeriod } from "./index";

// A deterministic series: the same numbers in every run.
let seed = 7;
const random = () => { seed = (seed * 16807) % 2147483647; return seed / 2147483647; };
const days = Array.from({ length: 60 }, (_, index) => { const day = new Date(Date.UTC(2026, 0, 1 + index)); return day.toISOString().slice(5, 10); });
const wave = (base: number, amplitude: number) => days.map((_, index) => Math.round(base + amplitude * Math.sin(index / 5) + random() * amplitude));
const names = ["read_file", "shell_command", "grep", "apply_patch", "list_dir", "webget"];

const lines = { tooltip: { trigger: "axis" }, legend: { data: ["Prompts", "Answers", "Errors"] }, grid: { left: 8, right: 16, top: 12, bottom: 56, containLabel: true },
  xAxis: { type: "category", data: days, boundaryGap: false }, yAxis: { type: "value" },
  dataZoom: [{ type: "slider", height: 18, bottom: 8 }, { type: "inside" }],
  series: [{ name: "Prompts", type: "line", data: wave(40, 12) }, { name: "Answers", type: "line", data: wave(30, 10) }, { name: "Errors", type: "line", data: wave(4, 3) }] };
const stacked = { tooltip: { trigger: "axis" }, legend: {}, grid: { left: 8, right: 16, top: 12, bottom: 8, containLabel: true },
  xAxis: { type: "category", data: days.slice(0, 30), boundaryGap: false }, yAxis: { type: "value" },
  series: ["Input", "Output", "Cached"].map(name => ({ name, type: "line", stack: "tokens", areaStyle: {}, data: wave(20, 8).slice(0, 30) })) };
const grouped = { tooltip: { trigger: "axis" }, legend: {}, grid: { left: 8, right: 16, top: 12, bottom: 8, containLabel: true },
  xAxis: { type: "category", data: ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"] }, yAxis: { type: "value" },
  series: ["Claude", "Codex", "Copilot"].map(name => ({ name, type: "bar", data: Array.from({ length: 7 }, () => Math.round(10 + random() * 40)) })) };
const ranked = { tooltip: { trigger: "axis", axisPointer: { type: "shadow" } }, grid: { left: 8, right: 24, top: 8, bottom: 8, containLabel: true },
  xAxis: { type: "value" }, yAxis: { type: "category", inverse: true, data: names },
  series: [{ name: "Calls", type: "bar", data: names.map((_, index) => 900 - index * 130), label: { show: true, position: "right" } }] };
const treemap = { tooltip: {}, series: [{ name: "Tokens", type: "treemap", roam: false, nodeClick: false, breadcrumb: { show: false },
  data: ["claude-fable", "gpt-6", "gemini", "mini", "local"].map((name, index) => ({ name, value: 1000 - index * 170 })), label: { show: true } }] };
const pie = { tooltip: { trigger: "item" }, legend: {}, series: [{ name: "Share", type: "pie", radius: ["45%", "70%"], data: names.slice(0, 4).map((name, index) => ({ name, value: 40 - index * 8 })) }] };
const scatter = { tooltip: {}, grid: { left: 8, right: 16, top: 12, bottom: 8, containLabel: true }, xAxis: { type: "value", name: "tokens" }, yAxis: { type: "value", name: "seconds" },
  series: [{ name: "Turns", type: "scatter", data: Array.from({ length: 80 }, () => { const x = random() * 1000; return [Math.round(x), Math.round(x / 40 + random() * 12)]; }) }] };
const durations = Array.from({ length: 400 }, () => Math.exp(random() * 9));
const histogramLog = histogramOption(histogram(durations, { bins: 12, scale: "log" }), { name: "Turns" });
const boxes = boxPlotOption(names.slice(0, 5).map(name => ({ name, values: Array.from({ length: 60 }, () => random() * 20 * (1 + names.indexOf(name))) })), { p90Name: "90th percentile", name: "Duration" });
const calendarOption = { tooltip: { formatter: "{c}" }, visualMap: { min: 0, max: 10, orient: "horizontal", left: "center", bottom: 0, show: false, inRange: { color: ["#1d7324", "#62d96b"] } },
  calendar: { range: "2026-01", cellSize: [16, 16], top: 24, left: 30 },
  series: [{ type: "heatmap", coordinateSystem: "calendar", data: days.slice(0, 31).map((day, index) => ["2026-" + day, index % 10]) }] };
const empty = { xAxis: { type: "category", data: ["a"] }, yAxis: {}, series: [{ name: "Late", type: "bar", data: [1] }] };
const late = { xAxis: { type: "category", data: ["a", "b"] }, yAxis: {}, series: [{ name: "Late", type: "bar", data: [3, 5] }] };

const calendarDays = Array.from({ length: 365 }, (_, index) => ({ date: new Date(Date.UTC(2025, 6, 1 + index)).toISOString().slice(0, 10), value: random() < 0.3 ? 0 : Math.round(random() ** 3 * 60) }));
const matrix = Array.from({ length: 7 }, (_, row) => Array.from({ length: 24 }, (_, hour) => Math.round(Math.max(0, Math.sin((hour - 6) / 3.8)) * (row > 4 ? 4 : 20) * random())));

const log: unknown[] = [];
const nothing = () => { };

function Gallery() {
  const [state, setState] = useState({ width: 520, lateVisible: false, lateOption: empty, showExtra: true });
  Object.assign(window, { chartsFixture: {
    log, set: (change: Partial<typeof state>) => setState(known => ({ ...known, ...change })),
    setTheme: (dark: boolean) => { document.documentElement.classList.toggle("bp6-dark", dark); document.documentElement.dataset.theme = dark ? "dark" : "light"; },
    setVariable: (name: string, value: string) => document.documentElement.style.setProperty(name, value),
    instance: (id: string) => { const dom = document.querySelector(`[data-chart="${id}"] .chart-surface`); return dom ? echarts.getInstanceByDom(dom as HTMLElement) : undefined; },
    lines, rawEcharts: echarts.version } });
  const box = (id: string, title: string, child: ReactNode) => <section className="gallery-box" data-chart={id} style={{ width: state.width }}><h4>{title}</h4>{child}</section>;
  const select = (id: string) => (datum: ChartDatum) => log.push({ select: id, name: datum.name, series: datum.seriesName, index: datum.dataIndex });
  const period = (id: string) => (value: ChartPeriod) => log.push({ period: id, from: value.from, to: value.to, start: value.start, end: value.end });
  return <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: nothing }}>
    <div className="gallery">
      {box("lines", "Activity", <Chart option={lines} ariaLabel="Prompts, answers and errors per day" group="page" height={260} onSelect={select("lines")} onPeriod={period("lines")} />)}
      {box("lines2", "Same cursor", <Chart option={{ ...grouped, tooltip: { trigger: "axis" } }} ariaLabel="A second chart in the group" group="page" height={200} />)}
      {box("stacked", "Tokens", <Chart option={stacked} ariaLabel="Tokens per day, stacked" height={220} />)}
      {box("grouped", "Per provider", <Chart option={grouped} ariaLabel="Prompts per weekday and provider" height={220} onSelect={select("grouped")} />)}
      {box("ranked", "Tools", <Chart option={ranked} ariaLabel="Calls per tool" height={220} />)}
      {box("treemap", "Models", <Chart option={treemap} ariaLabel="Tokens per model" height={220} />)}
      {box("pie", "Share", <Chart option={pie} ariaLabel="Share of calls" height={220} />)}
      {box("scatter", "Turns", <Chart option={scatter} ariaLabel="Seconds against tokens" height={220} />)}
      {box("histogram", "Durations (log)", <Chart option={histogramLog} ariaLabel="Turn durations on a logarithmic scale" height={220} />)}
      {box("boxes", "Tool time", <Chart option={boxes} ariaLabel="Duration of each tool" height={220} />)}
      {box("calendar-echarts", "ECharts calendar", <Chart option={calendarOption} ariaLabel="Calendar drawn by ECharts" height={160} />)}
      {box("canvas", "Canvas", <Chart option={lines} ariaLabel="The same line chart, in canvas" height={200} renderer="canvas" />)}
      {box("late", "Hidden then shown", <Chart option={state.lateOption} ariaLabel="A chart that starts hidden" height={160} visible={state.lateVisible} />)}
      {box("calendar", "Calendar", <CalendarHeatmap data={calendarDays} from="2025-07-01" to="2026-06-30" ariaLabel="Prompts per day over a year"
        onSelect={(date, value) => log.push({ day: date, value })} />)}
      {box("weekday", "Weekday by hour", <WeekdayHourHeatmap matrix={matrix} ariaLabel="Prompts per weekday and hour" onSelect={(row, hour, value) => log.push({ row, hour, value })} />)}
      {box("tiles", "Tiles", <div className="gallery-tiles">
        <StatTile label="Prompts" value="1,204" current={1204} compared={1000} trend={wave(40, 12)} />
        <StatTile label="Errors" value="31" current={31} compared={25} goodWhen="down" trend={wave(4, 3)} />
        <StatTile label="Time" value="3 h 12" current={192} compared={192} goodWhen="down" />
        <table><tbody>{names.slice(0, 3).map(name => <tr key={name}><td>{name}</td><td><Sparkline values={wave(5, 4).slice(0, 30)} ariaLabel={`${name} per day`} /></td></tr>)}</tbody></table>
      </div>)}
      <div className="gallery-key">
        {([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]).map(n => <i key={n} style={{ background: `var(--chart-${n})` }} title={`--chart-${n}`} />)}
        {([1, 2, 3, 4, 5]).map(n => <i key={`s${n}`} style={{ background: `var(--chart-seq-${n})` }} />)}
      </div>
    </div>
  </ShellLanguageContext.Provider>;
}

Object.assign(window, { chartsLate: late });
document.documentElement.classList.add("bp6-dark");
document.documentElement.dataset.theme = "dark";
createRoot(document.getElementById("root")!).render(<StrictMode><Gallery /></StrictMode>);
