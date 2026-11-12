import assert from "node:assert/strict";
import test from "node:test";
import { drawChart, inlineData, parseDelimited, refusingLoader, secureVega, type ChartJob } from "./vegaLiteDrawing";

secureVega();

const config = { font: "Segoe UI", background: "#2f343c", range: { category: ["#3fa6da", "#43bf4d"] } };
const job = (spec: Record<string, unknown>, files: Record<string, string> = {}): ChartJob => ({ spec, files, config, width: 500 });

// Runs `work` where any attempt to reach the network, or to compile code from text, fails the test.
async function guarded<T>(work: () => Promise<T>): Promise<{ value?: T; error?: unknown; attempts: string[] }> {
  const attempts: string[] = [];
  const saved = { fetch: globalThis.fetch, Function: globalThis.Function, eval: globalThis.eval, XMLHttpRequest: (globalThis as { XMLHttpRequest?: unknown }).XMLHttpRequest };
  globalThis.fetch = (async (input: unknown) => { attempts.push(`fetch ${String(input)}`); throw new Error("no network"); }) as typeof fetch;
  globalThis.Function = new Proxy(saved.Function, { construct() { attempts.push("new Function"); throw new Error("no code"); }, apply() { attempts.push("Function"); throw new Error("no code"); } });
  globalThis.eval = (() => { attempts.push("eval"); throw new Error("no code"); }) as typeof eval;
  try { return { value: await work(), attempts }; } catch (error) { return { error, attempts }; }
  finally { Object.assign(globalThis, saved); }
}

test("a chart is drawn headless to an SVG string, with its data files in place of their paths, and reaches nothing", async () => {
  const spec = {
    data: { url: "out/results.csv" },
    transform: [{ calculate: "datum.value * 2", as: "double" }, { filter: "datum.double > 1 && test(/^[ab]/, datum.name)" }],
    mark: "bar", encoding: { x: { field: "name", type: "nominal" }, y: { field: "double", type: "quantitative" }, color: { field: "name" },
      href: { field: "link" }, tooltip: { field: "name" } },
  };
  const { value: svg, error, attempts } = await guarded(() => drawChart(job(spec, { "out/results.csv": "name,value,link\na,1,https://example.com/a\nb,3,https://example.com/b\nc,9,x" })));
  assert.equal(error, undefined, String(error));
  assert.deepEqual(attempts, [], "no request, no code compiled from text");
  assert.match(svg!, /^<svg [^>]*class="marks"/u);
  assert.ok(svg!.includes('fill="#2f343c"'), "the background of the window");
  assert.ok(svg!.includes("#3fa6da") && svg!.includes("#43bf4d"), "the palette of the window");
  assert.ok(svg!.includes("Segoe UI"), "the font of the window");
  assert.ok(svg!.includes('xlink:href=""') && !/href="[^"]/u.test(svg!), "a link leads nowhere (the window removes it too)");
  assert.ok(!svg!.includes(">c<"), "the filter ran, through the interpreter");
});

test("delimited text is read field by field, as d3-dsv reads it, without compiling code", async () => {
  const { value, attempts } = await guarded(async () => parseDelimited('a,b,c\r\n1,"x, ""y""",\n2,"multi\nline",z\n', ","));
  assert.deepEqual(attempts, []);
  assert.deepEqual(value, [{ a: "1", b: 'x, "y"', c: "" }, { a: "2", b: "multi\nline", c: "z" }]);
  assert.deepEqual(parseDelimited("a\tb\n1\t2", "\t"), [{ a: "1", b: "2" }]);
  assert.deepEqual(parseDelimited("a,b\n1", ","), [{ a: "1", b: "" }]);
  assert.deepEqual(parseDelimited("", ","), []);
  assert.deepEqual(parseDelimited("a\n", ","), []);
  // A column named as a property of every object adds nothing to the objects.
  assert.equal(Object.getPrototypeOf(parseDelimited("__proto__\nx", ",")[0]), Object.prototype);
});

test("TSV and JSON files are read as their extension says, or as the spec's format says", async () => {
  const tsv = await drawChart(job({ data: { url: "a.tsv" }, mark: "point", encoding: { x: { field: "x", type: "quantitative" } } }, { "a.tsv": "x\ty\n1\t2\n" }));
  assert.match(tsv, /^<svg/u);
  const json = await drawChart(job({ data: { url: "a.json" }, mark: "text", encoding: { text: { field: "label" } } }, { "a.json": JSON.stringify([{ label: "from-json" }]) }));
  assert.ok(json.includes("from-json"));
  const declared = await drawChart(job({ data: { url: "a.json", format: { type: "csv" } }, mark: "text", encoding: { text: { field: "label" } } }, { "a.json": "label\nas-csv\n" }));
  assert.ok(declared.includes("as-csv"));
  assert.deepEqual(inlineData({ layer: [{ data: { url: "a.csv", name: "n" } }] }, { "a.csv": "x\n1" }),
    { layer: [{ data: { name: "n", values: "x\n1", format: { type: "csv" } } }] });
  assert.throws(() => inlineData({ data: { url: "missing.csv" } }, {}), /missing\.csv was not read/u);
});

test("a chart as wide as its container takes the width it is given", async () => {
  const svg = await drawChart(job({ width: "container", data: { values: [{ x: 1 }] }, mark: "point", encoding: { x: { field: "x", type: "quantitative" } } }));
  const width = Number(/^<svg [^>]*width="(\d+)"/u.exec(svg)?.[1]);
  assert.ok(width >= 500 && width < 560, `width ${width}`);
});

test("what the spec names to load is never loaded: images, links and data the window did not read", async () => {
  const image = { data: { values: [{ u: "https://example.com/a.png", x: 1 }, { u: "file:///C:/secret.png", x: 2 }] }, mark: "image",
    encoding: { url: { field: "u" }, x: { field: "x", type: "quantitative" } } };
  const { error, attempts, value } = await guarded(() => drawChart(job(image)));
  assert.equal(error, undefined, String(error));
  assert.deepEqual(attempts, []);
  assert.ok(!/href="[^"]/u.test(value!), "no image address in the drawing: an image is never loaded (the window removes the element too)");
  await assert.rejects(refusingLoader.load("https://example.com/a.csv"), /reads no file and no address/u);
  assert.deepEqual(await refusingLoader.sanitize("https://example.com/", { context: "href" }), { href: "" });
});

test("a spec that fails is refused with what Vega said, and expressions cannot build values of any size", async () => {
  await assert.rejects(drawChart(job({ data: { values: [{ a: 1 }] }, mark: "point", transform: [{ calculate: "length(sequence(0, 1e9))", as: "n" }],
    encoding: { x: { field: "n", type: "quantitative" } } })), /sequence is too long/u);
  await assert.rejects(drawChart(job({ data: { values: [{ a: 1 }] }, mark: "point", transform: [{ calculate: "pad('x', 1e9)", as: "n" }],
    encoding: { x: { field: "n" } } })), /text is too long/u);
  // Ordinary sequences still work.
  const svg = await drawChart(job({ data: { values: [{ a: 1 }] }, mark: "text", transform: [{ calculate: "length(sequence(0, 10))", as: "n" }], encoding: { text: { field: "n" } } }));
  assert.ok(svg.includes(">10<"));
  await assert.rejects(drawChart(job({ data: { values: [{ a: 1 }] }, mark: "point", transform: [{ calculate: "datum.a +", as: "n" }] })));
});
// Each spec makes a little more than a limit lets it: refused before it is made, as one that makes far more would be. A worker
// that fills its memory takes the whole window down, so no limit may be left to the time the worker is given.
const one = { data: { values: [{ a: 1 }] } };
const refusals: [string, Record<string, unknown>, RegExp][] = [
  ["timeSequence", { ...one, mark: "text", transform: [{ calculate: "length(timeSequence('day', datetime(1500, 0, 1), datetime(2000, 0, 1)))", as: "n" }], encoding: { text: { field: "n" } } }, /sequence is too long/u],
  ["utcSequence", { ...one, mark: "text", transform: [{ calculate: "length(utcSequence('hour', 0, 1e12))", as: "n" }], encoding: { text: { field: "n" } } }, /sequence is too long/u],
  ["format width", { ...one, mark: "text", transform: [{ calculate: "format(1, '2000d')", as: "n" }], encoding: { text: { field: "n" } } }, /format is too wide/u],
  ["join", { ...one, mark: "text", transform: [{ calculate: "length(join(sequence(0, 100000), pad('', 20, '-')))", as: "n" }], encoding: { text: { field: "n" } } }, /text is too long/u],
  ["replace", { ...one, mark: "text", transform: [{ calculate: "length(replace(pad('', 2000, 'x'), /x/g, pad('', 1000, 'y')))", as: "n" }], encoding: { text: { field: "n" } } }, /text is too long/u],
  ["text built across transforms", { ...one, mark: "text",
    transform: [{ calculate: "pad('', 8000, 'x')", as: "s" }, ...Array.from({ length: 10 }, () => ({ calculate: "datum.s + datum.s", as: "s" })), { calculate: "length(datum.s)", as: "n" }],
    encoding: { text: { field: "n" } } }, /makes (a longer text|more values) than it can draw/u],
  ["sequence generator", { data: { sequence: { start: 0, stop: 150_000, as: "x" } }, mark: "tick", encoding: { x: { field: "x", type: "quantitative" } } }, /sequence is too long/u],
  ["fold", { data: { values: Array.from({ length: 1100 }, (_, index) => ({ i: index })) }, mark: "tick",
    transform: [{ fold: Array.from({ length: 1000 }, (_, index) => `f${index}`) }], encoding: { x: { field: "value", type: "quantitative" } } }, /more rows than it can draw/u],
  ["flatten", { data: { values: Array.from({ length: 1100 }, () => ({ a: 1 })) }, mark: "tick",
    transform: [{ calculate: "sequence(0, 1000)", as: "list" }, { flatten: ["list"] }], encoding: { x: { field: "list", type: "quantitative" } } }, /than it can draw/u],
  ["impute", { data: { sequence: { start: 0, stop: 1100, as: "i" } }, mark: "line",
    transform: [{ calculate: "datum.i", as: "key" }, { calculate: "'g' + datum.i", as: "group" }, { impute: "i", key: "key", groupby: ["group"] }],
    encoding: { x: { field: "key", type: "quantitative" }, y: { field: "i", type: "quantitative" }, detail: { field: "group" } } }, /more rows than it can draw/u],
  ["pivot", { data: { sequence: { start: 0, stop: 1100, as: "i" } }, mark: "tick",
    transform: [{ calculate: "'k' + datum.i", as: "key" }, { calculate: "'g' + datum.i", as: "group" }, { pivot: "key", value: "i", groupby: ["group"] }],
    encoding: { x: { field: "k1", type: "quantitative" } } }, /more rows than it can draw/u],
  ["quantile", { data: { values: [{ v: 1 }, { v: 2 }] }, mark: "tick", transform: [{ quantile: "v", step: 1e-6 }], encoding: { x: { field: "value", type: "quantitative" } } }, /quantile of more than/u],
  ["regression order", { data: { values: [{ x: 1, y: 2 }, { x: 2, y: 3 }] }, mark: "line", transform: [{ regression: "y", on: "x", method: "poly", order: 30 }],
    encoding: { x: { field: "x", type: "quantitative" }, y: { field: "y", type: "quantitative" } } }, /order larger than 20/u],
  ["graticule", { data: { graticule: { step: [0.02, 0.02] } }, mark: "geoshape", projection: { type: "equalEarth" } }, /more lines than it can draw/u],
  ["ticks of time", { data: { values: [{ t: "1900-01-01" }, { t: "2000-01-01" }] }, mark: "point",
    encoding: { x: { field: "t", type: "temporal", axis: { tickCount: { interval: "day", step: 1 } } } } }, /more ticks than it can draw/u],
];
for (const [name, spec, why] of refusals) {
  test(`what a spec makes past a limit is refused before it is made: ${name}`, async () => {
    await assert.rejects(drawChart(job(spec)), why);
  });
}

test("a data file of more rows than a chart reads is refused, as it is read", async () => {
  const rows = "x\n" + "1\n".repeat(200_001);
  await assert.rejects(drawChart(job({ data: { url: "a.csv" }, mark: "tick", encoding: { x: { field: "x", type: "quantitative" } } }, { "a.csv": rows })), /more than 200000 rows/u);
  await assert.rejects(drawChart(job({ data: { url: "a.json" }, mark: "tick", encoding: { x: { field: "x", type: "quantitative" } } },
    { "a.json": JSON.stringify(Array.from({ length: 200_001 }, () => ({ x: 1 }))) })), /more than 200000 rows/u);
});

test("ordinary charts stay within the limits: many rows, folds, sequences of time and ticks", async () => {
  const rows = Array.from({ length: 5_000 }, (_, index) => ({ day: `2026-01-${String(1 + index % 28).padStart(2, "0")}`, a: index % 7, b: index % 5, c: index % 3 }));
  const svg = await drawChart(job({ data: { values: rows }, transform: [{ fold: ["a", "b", "c"] }, { calculate: "length(timeSequence('day', datetime(2026, 0, 1), datetime(2026, 11, 31)))", as: "days" }],
    mark: "line", encoding: { x: { field: "day", type: "temporal", axis: { tickCount: { interval: "week", step: 1 } } }, y: { aggregate: "mean", field: "value" }, color: { field: "key" } } }));
  assert.match(svg, /^<svg/u);
});
