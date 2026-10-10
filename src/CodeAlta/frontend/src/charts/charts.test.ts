import assert from "node:assert/strict";
import test from "node:test";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { contrast } from "../colorPalette";
import { boxPlotOption, boxStats, histogram, histogramOption, quantile } from "./distribution";
import { calendarLayout, levelOf, neighbor, parseDay, weekdayHourLayout } from "./heat";
import { colorsByName, fallbackSeries, mixColors, sequentialRamp, seriesColor, seriesVariable } from "./palette";
import { sanitizeChartOption } from "./sanitize";
import { changeView, sparklineShape, StatTile, Sparkline } from "./Sparkline";
import { tableFromOption, tableToText } from "./table";
import { buildChartTheme } from "./theme";
import { chartTokens } from "./tokens";
import { carryViewState, emptyViewState, legendEntries, periodOfOption, prepareOption, zoomOfOption } from "./viewState";

test("series colors are Blueprint's data hues in order, numbered from 1, wrapping round", () => {
  assert.equal(seriesVariable(0), "--chart-1");
  assert.equal(seriesVariable(9), "--chart-10");
  assert.equal(seriesVariable(10), "--chart-1");
  assert.equal(seriesVariable(-1), "--chart-10");
  assert.deepEqual(fallbackSeries.light, ["#147eb3", "#29a634", "#866103", "#d33d17", "#9d3f9d", "#00a396", "#db2c6f", "#5a701a", "#946638", "#7961db"]);
  assert.equal(seriesColor(fallbackSeries.dark, 11), fallbackSeries.dark[1]);
});

test("dark series colors keep their contrast on the dark surface, and every color of both themes is told apart", () => {
  for (const color of fallbackSeries.dark) assert.ok(contrast(color, "#252a31") >= 3, `${color} on the dark surface`);
  for (const theme of [fallbackSeries.light, fallbackSeries.dark]) assert.equal(new Set(theme).size, theme.length);
});

test("the same name has the same color in every chart that lists the names alike", () => {
  const colors = colorsByName(["read", "write", "read", "edit"], fallbackSeries.light);
  assert.deepEqual(colors, { read: fallbackSeries.light[0], write: fallbackSeries.light[1], edit: fallbackSeries.light[2] });
});

test("a ramp runs from a tint of the surface to the full hue", () => {
  const ramp = sequentialRamp("#29a634", "#ffffff", 5);
  assert.equal(ramp.length, 5);
  assert.equal(ramp[4], "#29a634");
  assert.ok(contrast(ramp[0], "#ffffff") < contrast(ramp[4], "#ffffff"));
  assert.equal(mixColors("#000000", "#ffffff", 0.5), "#808080");
  assert.equal(mixColors("not a color", "#ffffff", 0.5), "not a color");
});

test("tokens come from the variables of the page and fall back to Blueprint's colors where the page has none", () => {
  const page: Record<string, string> = { "--chart-1": "#010203", "--chart-text": "#eeeeee" };
  const tokens = chartTokens(name => page[name] ?? "", true, "Inter");
  assert.equal(tokens.series[0], "#010203");
  assert.equal(tokens.series[1], fallbackSeries.dark[1]);
  assert.equal(tokens.text, "#eeeeee");
  assert.equal(tokens.fontFamily, "Inter");
  assert.equal(tokens.ramp.length, 5);
  const light = chartTokens(() => "", false);
  assert.equal(light.series[0], fallbackSeries.light[0]);
  assert.notEqual(light.text, tokens.text);
});

test("the ECharts theme carries the palette, the text and the axes of the tokens", () => {
  const tokens = chartTokens(name => name === "--chart-3" ? "#abcdef" : "", true);
  const theme = buildChartTheme(tokens) as Record<string, any>;
  assert.equal(theme.darkMode, true);
  assert.equal(theme.color[2], "#abcdef");
  assert.equal(theme.color.length, 10);
  assert.equal(theme.textStyle.color, tokens.text);
  assert.equal(theme.valueAxis.splitLine.lineStyle.color, tokens.grid);
  assert.equal(theme.tooltip.backgroundColor, tokens.tooltipBackground);
  assert.equal(buildChartTheme(chartTokens(() => "", false)).darkMode, false);
});

test("a histogram counts every value once into bins of equal width", () => {
  const result = histogram([1, 2, 2, 3, 9, 10], { bins: 3, min: 1, max: 10 });
  assert.deepEqual(result.bins.map(bin => bin.count), [4, 0, 2]);
  assert.equal(result.total, 6);
  assert.equal(result.bins[0].from, 1);
  assert.equal(result.bins[2].to, 10);
  assert.equal(result.excluded, 0);
});

test("a histogram on a log axis cuts at equal ratios and leaves out what is not above zero", () => {
  const result = histogram([0, -1, 1, 10, 100, 1000], { bins: 3, scale: "log" });
  assert.equal(result.excluded, 2);
  assert.equal(result.bins.reduce((sum, bin) => sum + bin.count, 0), 4);
  const ratios = result.bins.map(bin => bin.to / bin.from);
  for (const ratio of ratios) assert.ok(Math.abs(ratio - ratios[0]) < 1e-9);
  assert.deepEqual(result.bins.map(bin => bin.count), [1, 1, 2]);
});

test("a histogram takes counts as well as values, and a range that cuts values off reports them", () => {
  const result = histogram([{ value: 5, count: 3 }, { value: 50, count: 2 }, { value: 500, count: 7 }], { bins: 2, min: 0, max: 100 });
  assert.deepEqual(result.bins.map(bin => bin.count), [3, 2]);
  assert.equal(result.excluded, 7);
  assert.equal(histogram([]).bins.length, 0);
  assert.equal(histogram([Number.NaN, 3]).excluded, 1);
  assert.equal(histogram([4, 4, 4], { bins: 4 }).total, 3);
});

test("the histogram option is plain data a chart can draw", () => {
  const option = histogramOption(histogram([1, 2, 3, 4], { bins: 2 }));
  assert.equal(sanitizeChartOption(option).ok, true);
  assert.equal((option.series as { data: number[] }[])[0].data.length, 2);
});

test("box stats are the quartiles by interpolation, the 90th percentile and the extremes", () => {
  const stats = boxStats([5, 1, 2, 3, 4, 6, 7, 8, 9, 10])!;
  assert.deepEqual([stats.min, stats.q1, stats.median, stats.q3, stats.max, stats.count], [1, 3.25, 5.5, 7.75, 10, 10]);
  assert.equal(stats.p90, 9.1);
  assert.equal(boxStats([]), null);
  assert.equal(boxStats([Number.NaN]), null);
  assert.equal(quantile([4], 0.9), 4);
  assert.ok(Number.isNaN(quantile([], 0.5)));
});

test("a box plot option has a box and a 90th percentile marker for each item that has values", () => {
  const option = boxPlotOption([{ name: "read", values: [1, 2, 3, 4, 5] }, { name: "none", values: [] }, { name: "edit", stats: { count: 3, min: 1, q1: 2, median: 3, q3: 4, p90: 5, max: 6 } }]) as Record<string, any>;
  assert.deepEqual(option.xAxis.data, ["read", "edit"]);
  assert.deepEqual(option.series[0].data[0], [1, 2, 3, 4, 5]);
  assert.deepEqual(option.series[1].data, [[0, 4.6], [1, 5]]);
  const sideways = boxPlotOption([{ name: "a", values: [1, 2] }], { horizontal: true }) as Record<string, any>;
  assert.equal(sideways.yAxis.data[0], "a");
  assert.deepEqual(sideways.series[1].data[0], [1.9, 0]);
  assert.equal(sanitizeChartOption(option).ok, true);
});

test("the sanitizer copies plain data and refuses what could run, load or navigate", () => {
  const good = { xAxis: { type: "category", data: ["a", "b"] }, yAxis: {}, tooltip: { formatter: "{b}: {c} ms" }, series: [{ type: "bar", data: [1, 2, null, Number.NaN] }] };
  const result = sanitizeChartOption(good);
  assert.ok(result.ok);
  if (result.ok) {
    assert.deepEqual((result.option.series as { data: unknown[] }[])[0].data, [1, 2, null, null]);
    assert.notEqual(result.option, good);
    assert.notEqual(result.option.series, good.series);
  }
  const refused = (value: unknown) => { const r = sanitizeChartOption(value); assert.equal(r.ok, false, JSON.stringify(value)); return r.ok ? "" : r.error; };
  assert.match(refused({ tooltip: { formatter: () => "x" } }), /formatter/);
  assert.match(refused({ tooltip: { formatter: "function(p){return p}" } }), /formatter/);
  assert.match(refused({ tooltip: { formatter: "<img src=x onerror=alert(1)>" } }), /formatter/);
  assert.match(refused({ series: [{ label: { formatter: "(p) => 1" } }] }), /series\[0\]\.label\.formatter/);
  assert.match(refused({ title: { text: "x", link: "https://example.invalid" } }), /link/);
  assert.match(refused({ toolbox: { feature: { saveAsImage: {} } } }), /toolbox/);
  assert.match(refused({ graphic: [] }), /graphic/);
  assert.match(refused({ tooltip: { className: "x", appendTo: "body" } }), /not allowed/);
  assert.match(refused({ series: [{ symbol: "image://https://x.invalid/a.png" }] }), /image/);
  assert.match(refused(JSON.parse('{"__proto__": {"polluted": true}}')), /__proto__/);
  assert.match(refused({ series: [{ data: [new Date()] }] }), /plain/);
  assert.match(refused({ series: [{ data: [() => 1] }] }), /function/);
  assert.match(refused([]), /object/);
  assert.match(refused("{}"), /object/);
});

test("the sanitizer holds an option to its limits of depth, size and string length", () => {
  let deep: unknown = 1;
  for (let level = 0; level < 20; level++) deep = { next: deep };
  assert.match((sanitizeChartOption({ series: deep }) as { error: string }).error, /deeply/);
  assert.match((sanitizeChartOption({ series: [{ data: new Array(50).fill(1) }] }, { maxDepth: 8, maxNodes: 20, maxStringLength: 10 }) as { error: string }).error, /too many/);
  assert.match((sanitizeChartOption({ title: { text: "x".repeat(11) } }, { maxDepth: 8, maxNodes: 20, maxStringLength: 10 }) as { error: string }).error, /too long/);
  assert.equal(sanitizeChartOption({ series: [{ data: new Array(1000).fill(1) }] }).ok, true);
});

test("the table of a chart is the data the chart draws", () => {
  const bars = tableFromOption({ xAxis: { type: "category", data: ["Mon", "Tue"] }, yAxis: {}, series: [{ name: "Read", type: "bar", data: [1, 2] }, { name: "Write", type: "line", data: [{ value: 3 }, null] }] });
  assert.deepEqual(bars.columns, ["", "Read", "Write"]);
  assert.deepEqual(bars.rows, [["Mon", 1, 3], ["Tue", 2, null]]);
  const pie = tableFromOption({ series: [{ name: "Tokens", type: "pie", data: [{ name: "a", value: 5 }, { name: "b", value: 7 }] }] });
  assert.deepEqual(pie.columns, ["", "Tokens"]);
  assert.deepEqual(pie.rows, [["a", 5], ["b", 7]]);
  const tree = tableFromOption({ series: [{ type: "treemap", data: [{ name: "src", children: [{ name: "a.ts", value: 4 }] }, { name: "doc", value: 1 }] }] });
  assert.deepEqual(tree.rows, [["src / a.ts", 4], ["doc", 1]]);
  const points = tableFromOption({ xAxis: { type: "time" }, yAxis: {}, series: [{ name: "A", data: [["2026-01-01", 1], ["2026-01-02", 2]] }, { name: "B", data: [["2026-01-02", 5]] }] });
  assert.deepEqual(points.rows, [["2026-01-01", 1, null], ["2026-01-02", 2, 5]]);
  const heat = tableFromOption({ series: [{ name: "Count", type: "heatmap", data: [[0, 1, 5], [1, 1, 6]] }] });
  assert.deepEqual(heat.columns, ["x", "y", "Count"]);
  const box = tableFromOption({ xAxis: { type: "category", data: ["read"] }, yAxis: {}, series: [{ name: "Time", type: "boxplot", data: [[1, 2, 3, 4, 5]] }] });
  assert.deepEqual(box.columns, ["", "Time min", "Time Q1", "Time median", "Time Q3", "Time max"]);
  assert.deepEqual(box.rows, [["read", 1, 2, 3, 4, 5]]);
  const dataset = tableFromOption({ dataset: { source: [["day", "n"], ["a", 1]] } });
  assert.deepEqual(dataset, { columns: ["day", "n"], rows: [["a", 1]] });
  assert.deepEqual(tableFromOption({ dataset: { source: [{ day: "a", n: 1 }] } }).columns, ["day", "n"]);
  assert.deepEqual(tableFromOption({}), { columns: [], rows: [] });
  assert.equal(tableToText({ columns: ["", "n"], rows: [["a\tb", 1], ["c", null]] }), "\tn\na b\t1\nc\t");
});

test("the option is prepared for the page: motion, description, tooltip, legend and zoom", () => {
  const option = { tooltip: {}, legend: { data: ["a"] }, dataZoom: [{ type: "slider", startValue: 3, endValue: 9 }, { type: "inside" }], series: [{ name: "a" }] };
  const plain = prepareOption(option, { reducedMotion: false, state: emptyViewState }) as Record<string, any>;
  assert.equal(plain.animation, undefined);
  assert.equal(plain.aria.enabled, true);
  assert.equal(plain.tooltip.confine, true);
  assert.equal(plain.legend.show, false);
  const calm = prepareOption(option, { reducedMotion: true, state: { hidden: new Set(["a"]), zoom: [{ start: 10, end: 40 }] } }) as Record<string, any>;
  assert.equal(calm.animation, false);
  assert.equal(calm.legend.selected.a, false);
  assert.deepEqual([calm.dataZoom[0].start, calm.dataZoom[0].end, calm.dataZoom[0].startValue], [10, 40, undefined]);
  assert.equal(calm.dataZoom[1].start, undefined);
  assert.equal((option.legend as { show?: boolean }).show, undefined, "the page's option is not changed");
});

test("what the user zoomed and hid is kept when the page replaces its option, unless the page moves the zoom itself", () => {
  const state = { hidden: new Set(["a", "gone"]), zoom: [{ start: 10, end: 40 }] };
  const before = { dataZoom: [{ start: 0, end: 100 }], series: [{ name: "a" }, { name: "b" }] };
  const same = carryViewState(before, { dataZoom: [{ start: 0, end: 100 }], series: [{ name: "a" }] }, state);
  assert.deepEqual(same.zoom, [{ start: 10, end: 40 }]);
  assert.deepEqual([...same.hidden], ["a"]);
  const moved = carryViewState(before, { dataZoom: [{ start: 50, end: 100 }], series: [{ name: "a" }] }, state);
  assert.deepEqual(moved.zoom, [undefined]);
});

test("the period of a zoom is read in the units of its axis", () => {
  const categories = { xAxis: [{ type: "category", data: ["Mon", "Tue", "Wed"] }], dataZoom: [{ start: 0, end: 66, startValue: 0, endValue: 1 }] };
  assert.deepEqual(periodOfOption(categories), { start: 0, end: 66, from: "Mon", to: "Tue" });
  assert.deepEqual(periodOfOption({ xAxis: [{ type: "time" }], dataZoom: [{ start: 10, end: 20, startValue: 1000, endValue: 2000 }] }), { start: 10, end: 20, from: 1000, to: 2000 });
  assert.equal(periodOfOption({}), null);
  assert.deepEqual(zoomOfOption(categories), [{ start: 0, end: 66 }]);
});

test("the legend lists the series, or the parts of a whole, with the colors ECharts gives them", () => {
  const palette = ["#111111", "#222222", "#333333"];
  assert.deepEqual(legendEntries({ series: [{ name: "a" }, { name: "b", lineStyle: { color: "#abcdef" } }, { data: [1] }] }, palette),
    [{ name: "a", color: "#111111", index: 0 }, { name: "b", color: "#abcdef", index: 1 }]);
  assert.deepEqual(legendEntries({ series: [{ type: "pie", data: [{ name: "x", value: 1 }, { name: "y", value: 2 }] }] }, palette).map(entry => entry.color), ["#111111", "#222222"]);
  assert.deepEqual(legendEntries({ color: ["#fff000"], series: [{ name: "a" }] }, palette)[0].color, "#fff000");
  assert.deepEqual(legendEntries({ legend: { data: ["b", "a"] }, series: [{ name: "a" }, { name: "b" }] }, palette).map(entry => entry.name), ["b", "a"]);
});

test("a sparkline is one path: gaps break it, a flat series runs across the middle, a single point is a point", () => {
  const rising = sparklineShape([0, 1, 2], 100, 20, 0);
  assert.equal(rising.line, "M0 20L50 10L100 0");
  assert.deepEqual(rising.last, [100, 0]);
  assert.equal((sparklineShape([1, null, 3], 100, 20, 0).line.match(/M/g) ?? []).length, 2);
  assert.equal(sparklineShape([5, 5], 100, 20, 0).line, "M0 10L100 10");
  assert.deepEqual(sparklineShape([7], 100, 20).last, [50, 10]);
  assert.deepEqual(sparklineShape([], 100, 20), { line: "", area: "", last: null });
  assert.deepEqual(sparklineShape([null, Number.NaN], 100, 20), { line: "", area: "", last: null });
});

test("a change has a sign and a direction as well as a tone, and the tone follows what is good news", () => {
  assert.deepEqual(changeView(112, 100, "up", "en"), { direction: "up", tone: "good", text: "+12%" });
  assert.deepEqual(changeView(112, 100, "down", "en"), { direction: "up", tone: "bad", text: "+12%" });
  assert.equal(changeView(97, 100, "down", "en")!.tone, "good");
  assert.equal(changeView(97, 100, "up", "en")!.text, "-3%");
  assert.equal(changeView(100, 100, "up", "en")!.direction, "flat");
  assert.equal(changeView(5, 3, "neutral", "en")!.tone, "neutral");
  assert.equal(changeView(5, 0, "up", "en"), null);
  assert.equal(changeView(5, null, "up", "en"), null);
});

test("a tile shows its number, its change in words for a screen reader, and its line", () => {
  const markup = renderToStaticMarkup(createElement(StatTile, { label: "Prompts", value: "1,204", current: 1204, compared: 1000, trend: [1, 3, 2, 5] }));
  assert.match(markup, /Prompts/);
  assert.match(markup, /1,204/);
  assert.match(markup, /▲ \+20%/);
  assert.match(markup, /\+20% compared with the previous period/);
  assert.match(markup, /<path class="sparkline-line"/);
  const decorative = renderToStaticMarkup(createElement(Sparkline, { values: [1, 2] }));
  assert.match(decorative, /aria-hidden="true"/);
  assert.doesNotMatch(decorative, /role="img"/);
});

test("a calendar has a column per week, starts weeks where asked and writes the months once", () => {
  const layout = calendarLayout("2026-01-01", "2026-03-15", [{ date: "2026-01-05", value: 4 }, { date: "2026-01-05", value: 2 }, { date: "2026-02-01", value: 1 }],
    1, (date, value) => `${date}=${value}`, month => `m${month}`);
  assert.equal(layout.cells.length, 74);
  const first = layout.cells[0];
  assert.deepEqual([first.key, first.column, first.row], ["2026-01-01", 0, 3], "a Thursday, with weeks from Monday");
  const monday = layout.cells.find(cell => cell.key === "2026-01-05")!;
  assert.deepEqual([monday.column, monday.row, monday.value], [1, 0, 6], "values of a day add up");
  assert.equal(monday.label, "2026-01-05=6");
  assert.equal(layout.cells.find(cell => cell.key === "2026-01-06")!.value, null);
  assert.deepEqual(layout.months.map(label => label.text), ["m0", "m1", "m2"]);
  assert.equal(layout.columns, layout.cells[73].column + 1);
  const sunday = calendarLayout("2026-01-01", "2026-01-31", [], 0, () => "", () => "").cells[0];
  assert.equal(sunday.row, 4);
  assert.throws(() => calendarLayout("2026-02-01", "2026-01-01", [], 1, () => "", () => ""), RangeError);
  assert.throws(() => calendarLayout("2026-02-30", "2026-03-01", [], 1, () => "", () => ""), RangeError);
});

test("heat levels spread by quantiles, so one busy day does not turn the others pale", () => {
  const level = levelOf([1, 2, 3, 4, 5, 6, 7, 8, 9, 1000]);
  assert.equal(level(0), 0);
  assert.equal(level(1), 1);
  assert.equal(level(1000), 5);
  assert.ok(level(7) >= 4, "a day in the top of the usual days is strong");
  assert.equal(levelOf([])(3), 0);
  assert.equal(parseDay("2026-02-29"), null);
  assert.notEqual(parseDay("2028-02-29"), null);
});

test("arrow keys move between squares and stay on the edge", () => {
  const layout = calendarLayout("2026-01-05", "2026-01-18", [], 1, () => "", () => "");
  const start = layout.cells[0];
  assert.equal(neighbor(layout.cells, start, "ArrowRight").key, "2026-01-12");
  assert.equal(neighbor(layout.cells, start, "ArrowDown").key, "2026-01-06");
  assert.equal(neighbor(layout.cells, start, "ArrowLeft"), start);
  assert.equal(neighbor(layout.cells, start, "ArrowUp"), start);
  assert.equal(neighbor(layout.cells, start, "End").key, "2026-01-12");
  assert.equal(neighbor(layout.cells, start, "x"), start);
});

test("the weekday by hour map has 168 squares and reads missing rows as zero", () => {
  const cells = weekdayHourLayout([[0, 5], [1]], (row, hour, value) => `${row}/${hour}/${value}`);
  assert.equal(cells.length, 168);
  assert.deepEqual([cells[1].column, cells[1].row, cells[1].value, cells[1].label], [1, 0, 5, "0/1/5"]);
  assert.equal(cells[24].value, 1);
  assert.equal(cells[167].value, 0);
});
