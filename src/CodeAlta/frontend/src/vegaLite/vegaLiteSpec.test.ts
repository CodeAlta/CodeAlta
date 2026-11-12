import assert from "node:assert/strict";
import test from "node:test";
import { chartDataPath, chartRefusalOf, dataFormatOf, isVegaLiteLanguage, maximumChartFiles, maximumChartLength, prepareChart } from "./vegaLiteSpec";

const refusal = (value: unknown) => (value as { refused?: string }).refused;

test("a vega-lite block is fenced as vega-lite or vegalite, in any case, and nothing else", () => {
  for (const language of ["vega-lite", "vegalite", "Vega-Lite", "VEGALITE"]) assert.ok(isVegaLiteLanguage(language), language);
  for (const language of ["vega", "vega_lite", "vega-lite-5", "lite", "plot", "json", ""]) assert.ok(!isVegaLiteLanguage(language), language);
});

test("a data file is a relative path below the folder, of a kind of data the chart reads", () => {
  assert.equal(chartDataPath("out/results.csv"), "out/results.csv");
  assert.equal(chartDataPath("./out/results.tsv"), "out/results.tsv");
  assert.equal(chartDataPath("out\\data\\points.json"), "out/data/points.json");
  assert.equal(chartDataPath("world.topojson"), "world.topojson");
  assert.equal(chartDataPath("Shapes.GeoJSON"), "Shapes.GeoJSON");
  // An address of the web is never read, and says so.
  assert.match(refusal(chartDataPath("https://example.com/data.csv"))!, /^Data from the web is not read: https:\/\/example\.com\/data\.csv$/u);
  assert.match(refusal(chartDataPath("HTTP://example.com/a.csv"))!, /^Data from the web is not read/u);
  // Nothing outside the folder: a full path, a drive, the home folder, a parent, another scheme, a network path.
  for (const path of ["/etc/passwd.csv", "C:/data/a.csv", "C:\\data\\a.csv", "~/a.csv", "../a.csv", "out/../../a.csv", "out/./a.csv",
    "file:///C:/a.csv", "data:text/csv,a", "\\\\server\\share\\a.csv", "//server/share/a.csv", "a.csv:stream", " a.csv", "out /a.csv", "out./a.csv"]) {
    assert.ok(refusal(chartDataPath(path)), path);
  }
  // A query, a fragment, a wildcard or a control character names no file.
  for (const path of ["a.csv?x=1", "a.csv#top", "*.csv", "a\u0000.csv", "a\n.csv", ""]) assert.ok(refusal(chartDataPath(path)), JSON.stringify(path));
  // Only data.
  assert.equal(refusal(chartDataPath("notes.txt")), "Not a CSV, TSV or JSON file: notes.txt");
  assert.ok(refusal(chartDataPath("secrets.env")));
  assert.ok(refusal(chartDataPath("a".repeat(1025) + ".csv")));
});

test("a data file is read in the format of its extension", () => {
  assert.equal(dataFormatOf("a.csv"), "csv");
  assert.equal(dataFormatOf("a.TSV"), "tsv");
  assert.equal(dataFormatOf("a.json"), "json");
  assert.equal(dataFormatOf("a.geojson"), "json");
  assert.equal(dataFormatOf("a.topojson"), "topojson");
});

test("a spec is a JSON object of Vega-Lite; anything else is refused with one line that says why", () => {
  assert.match(refusal(prepareChart("{ \"mark\": "))!, /^Not JSON: /u);
  assert.equal(refusal(prepareChart("[1, 2]")), "Not a Vega-Lite spec: the JSON is not an object");
  assert.equal(refusal(prepareChart("42")), "Not a Vega-Lite spec: the JSON is not an object");
  assert.equal(refusal(prepareChart(JSON.stringify({ $schema: "https://vega.github.io/schema/vega/v6.json", marks: [] }))), "A Vega spec, not a Vega-Lite one");
  assert.equal(refusal(prepareChart(" ".repeat(maximumChartLength + 1))), `The spec is longer than ${maximumChartLength} characters`);
  const spec = { $schema: "https://vega.github.io/schema/vega-lite/v6.json", data: { values: [{ a: 1 }] }, mark: "bar" };
  assert.deepEqual(prepareChart(JSON.stringify(spec)), { spec, files: [] });
});

test("a spec names its files wherever it has data, each once, and refuses a file it may not read", () => {
  const spec = {
    data: { url: "out/a.csv" },
    transform: [{ lookup: "id", from: { data: { url: "out/names.json" }, key: "id", fields: ["name"] } }],
    layer: [{ mark: "line" }, { data: { url: "./out/b.tsv" }, mark: "point" }, { data: { url: "out/a.csv" }, mark: "rule" }],
  };
  assert.deepEqual((prepareChart(JSON.stringify(spec)) as unknown as { files: string[] }).files.sort(), ["./out/b.tsv", "out/a.csv", "out/names.json"]);
  // A url that is not data is not a file of the chart: an image mark reads nothing (the loader of the worker refuses it).
  assert.deepEqual((prepareChart(JSON.stringify({ data: { values: [{ u: "x.png" }] }, mark: "image", encoding: { url: { field: "u" } } })) as unknown as { files: string[] }).files, []);
  assert.match(refusal(prepareChart(JSON.stringify({ layer: [{ data: { url: "https://example.com/a.csv" }, mark: "bar" }] })))!, /^Data from the web is not read/u);
  assert.match(refusal(prepareChart(JSON.stringify({ data: { url: "../secret.csv" }, mark: "bar" })))!, /^Not a path inside the project/u);
  assert.equal(refusal(prepareChart(JSON.stringify({ data: { url: 3 }, mark: "bar" }))), "The url of a data is not a text");
  const many = { layer: Array.from({ length: maximumChartFiles + 1 }, (_, index) => ({ data: { url: `out/${index}.csv` }, mark: "bar" })) };
  assert.equal(refusal(prepareChart(JSON.stringify(many))), `A chart reads ${maximumChartFiles} files at most`);
});

test("a spec that would make far more than a chart shows is refused before it is drawn", () => {
  assert.equal(refusal(prepareChart(JSON.stringify({ data: { sequence: { start: 0, stop: 1e9 } }, mark: "point" }))), "A sequence of more than 100000 values is not drawn");
  assert.equal(refusal(prepareChart(JSON.stringify({ data: { sequence: { start: 0, stop: 1, step: 1e-9 } }, mark: "point" }))), "A sequence of more than 100000 values is not drawn");
  assert.equal(refusal(prepareChart(JSON.stringify({ mark: "bar", encoding: { x: { field: "a", axis: { tickCount: 1e8 } } } }))), "tickCount is larger than 1000");
  assert.equal(refusal(prepareChart(JSON.stringify({ mark: "bar", encoding: { x: { field: "a", bin: { maxbins: 1e7 } } } }))), "maxbins is larger than 1000");
  assert.equal(refusal(prepareChart(JSON.stringify({ mark: "bar", encoding: { x: { field: "a", bin: { extent: [0, 1], step: 1e-9 } } } }))), "A bin of more than 100000 steps is not drawn");
  assert.equal(refusal(prepareChart(JSON.stringify({ transform: [{ density: "a", steps: 1e7 }], mark: "area" }))), "steps is larger than 10000");
  // Ordinary sizes are drawn.
  assert.ok(!refusal(prepareChart(JSON.stringify({ data: { sequence: { start: 0, stop: 100, step: 0.1, as: "x" } }, mark: "line", encoding: { x: { field: "x", axis: { tickCount: 10 } } } }))));
});

test("a refusal is the first line of what was said, short", () => {
  assert.equal(chartRefusalOf(new Error("Invalid spec:\nmore")), "Invalid spec");
  assert.equal(chartRefusalOf(new Error("")), "Not a chart that can be drawn");
  assert.equal(chartRefusalOf({ odd: true }), "Not a chart that can be drawn");
  assert.equal(chartRefusalOf(new Error("x".repeat(500))).length, 160);
});
