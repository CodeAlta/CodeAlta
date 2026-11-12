import assert from "node:assert/strict";
import test from "node:test";
import { chartTokens } from "../charts/tokens";
import { chartFileRefusal, createChartCache, createChartWorkerClient, vegaLiteConfig, type ChartAppearance, type ChartData, type ChartFile } from "./vegaLite";

const dark: ChartAppearance = { key: "dark", config: { background: "#2f343c" }, width: 640 };
const light: ChartAppearance = { key: "light", config: { background: "#ffffff" }, width: 640 };
const spec = (extra: Record<string, unknown> = {}) => JSON.stringify({ mark: "bar", data: { values: [{ a: 1 }] }, ...extra });
const withFile = JSON.stringify({ mark: "bar", data: { url: "out/a.csv" } });

// A folder of files that a test changes, and the reads it was asked for.
function folder(files: Record<string, ChartFile>, key = "session:s1") {
  const reads: string[] = [];
  const data: ChartData = { key, read: async path => { reads.push(path); return files[path] ?? { status: "not_found" }; } };
  return { data, reads, files };
}

test("a chart is drawn once per text, appearance and folder, then found by later renders", async () => {
  const drawn: string[] = [];
  const cache = createChartCache(async (chart, files, look) => { drawn.push(`${look.key}:${JSON.stringify(chart.spec.mark)}:${JSON.stringify(files)}`); return { svg: `<svg>${look.key}</svg>` }; });
  assert.deepEqual(cache.lookup(spec(), dark, null), { svg: null, refused: null, pending: true }, "not drawn yet");
  await Promise.all([cache.draw(spec(), dark, null), cache.draw(spec(), dark, null)]);
  assert.deepEqual(cache.lookup(spec(), dark, null), { svg: "<svg>dark</svg>", refused: null, pending: false });
  await cache.draw(spec(), dark, null);
  assert.deepEqual(drawn, ['dark:"bar":{}']);
  // Another appearance (the theme changed) is another drawing; until it is drawn, the one before is shown.
  assert.deepEqual(cache.lookup(spec(), light, null), { svg: "<svg>dark</svg>", refused: null, pending: true });
  await cache.draw(spec(), light, null);
  assert.deepEqual(cache.lookup(spec(), light, null), { svg: "<svg>light</svg>", refused: null, pending: false });
  assert.equal(drawn.length, 2);
});

test("a spec that is refused stays refused, with why, and is not drawn again", async () => {
  let calls = 0;
  const cache = createChartCache(async () => { calls++; return { refused: "Invalid specification" }; });
  await cache.draw("{ not json", dark, null);
  assert.match(cache.lookup("{ not json", dark, null).refused!, /^Not JSON: /u);
  await cache.draw(spec(), dark, null);
  assert.deepEqual(cache.lookup(spec(), dark, null), { svg: null, refused: "Invalid specification", pending: false });
  await cache.draw(spec(), dark, null);
  assert.equal(calls, 1);
  // A renderer that throws refuses its chart.
  const failing = createChartCache(async () => { throw new Error("worker gone"); });
  await failing.draw(spec(), dark, null);
  assert.equal(failing.lookup(spec(), dark, null).refused, "The chart could not be drawn");
});

test("a chart reads the files it names in its folder, and is refused with why when one cannot be read", async () => {
  const received: Record<string, string>[] = [];
  const cache = createChartCache(async (_chart, files) => { received.push({ ...files }); return { svg: "<svg/>" }; });
  const { data, reads } = folder({ "out/a.csv": { status: "ok", text: "a\n1" } });
  await cache.draw(withFile, dark, data);
  assert.deepEqual(reads, ["out/a.csv"]);
  assert.deepEqual(received, [{ "out/a.csv": "a\n1" }]);
  // Without a folder, a chart that names a file is refused; another folder is another chart.
  await cache.draw(withFile, dark, null);
  assert.equal(cache.lookup(withFile, dark, null).refused, "No project to read out/a.csv from");
  for (const [status, said] of [["not_found", "out/a.csv was not found in the project"], ["outside", "out/a.csv is not inside the project"],
    ["not_data", "out/a.csv is not a CSV, TSV or JSON file"], ["too_large", "out/a.csv is larger than 4 MB"], ["failed", "out/a.csv could not be read"]]) {
    const other = folder({ "out/a.csv": { status } }, `session:${status}`);
    await cache.draw(withFile, dark, other.data);
    assert.equal(cache.lookup(withFile, dark, other.data).refused, said, status);
  }
  // A path the chart may not name is never asked for.
  const outside = folder({});
  await cache.draw(JSON.stringify({ mark: "bar", data: { url: "../secret.csv" } }), dark, outside.data);
  assert.deepEqual(outside.reads, []);
  assert.equal(chartFileRefusal("x".repeat(100) + ".csv", "not_found").length, "… was not found in the project".length + 79);
});

test("a chart that reads files reads them again when a render finds it later, and is drawn again only when one changed", async () => {
  let time = 0, draws = 0;
  const cache = createChartCache(async (_chart, files) => { draws++; return { svg: `<svg>${files["out/a.csv"]}</svg>` }; }, { freshness: 5_000, now: () => time });
  const { data, reads, files } = folder({ "out/a.csv": { status: "ok", text: "a\n1" } });
  await cache.draw(withFile, dark, data);
  assert.equal(cache.lookup(withFile, dark, data).pending, false);
  time = 4_000;
  assert.equal(cache.lookup(withFile, dark, data).pending, false, "fresh");
  await cache.draw(withFile, dark, data);
  assert.equal(reads.length, 1, "a fresh chart is not read again");
  time = 6_000;
  assert.deepEqual(cache.lookup(withFile, dark, data), { svg: "<svg>a\n1</svg>", refused: null, pending: true }, "shown while it is read again");
  await cache.draw(withFile, dark, data);
  assert.deepEqual([reads.length, draws], [2, 1], "the same data is not drawn again");
  assert.equal(cache.lookup(withFile, dark, data).pending, false);
  time = 12_000;
  files["out/a.csv"] = { status: "ok", text: "a\n2" };
  await cache.draw(withFile, dark, data);
  assert.deepEqual([reads.length, draws], [3, 2]);
  assert.equal(cache.lookup(withFile, dark, data).svg, "<svg>a\n2</svg>");
  // A chart with its data in the spec is never read again.
  await cache.draw(spec(), dark, data);
  time = 100_000;
  assert.equal(cache.lookup(spec(), dark, data).pending, false);
});

test("the cache keeps the newest charts, within a number and a size", async () => {
  const cache = createChartCache(async chart => ({ svg: `<svg>${"x".repeat(Number(chart.spec.size))}</svg>` }), { capacity: 2, maximumCharacters: 1_000 });
  for (const size of [10, 20, 30]) await cache.draw(spec({ size }), dark, null);
  assert.equal(cache.usage().entries, 2);
  assert.equal(cache.lookup(spec({ size: 10 }), dark, null).svg, null, "the oldest is gone");
  await cache.draw(spec({ size: 2_000 }), dark, null);
  assert.equal(cache.lookup(spec({ size: 2_000 }), dark, null).svg, null, "a drawing larger than the cache is not kept");
  assert.ok(cache.usage().characters <= 1_000);
});

// A worker of tests: it answers what `answer` says, or nothing.
class FakeWorker {
  onmessage: ((event: MessageEvent) => void) | null = null;
  onerror: ((event: { preventDefault(): void }) => void) | null = null;
  onmessageerror: (() => void) | null = null;
  terminated = false;
  posted: { id: number; job: unknown }[] = [];
  constructor(private readonly answer: (job: { id: number; job: { spec: Record<string, unknown> } }) => object | null) { }
  postMessage(message: { id: number; job: { spec: Record<string, unknown> } }) {
    this.posted.push(message);
    const reply = this.answer(message);
    if (reply) queueMicrotask(() => this.onmessage?.({ data: { id: message.id, ...reply } } as MessageEvent));
  }
  terminate() { this.terminated = true; }
}

test("charts are drawn one at a time in a worker; one that takes too long is refused and its worker stopped", async () => {
  const started: FakeWorker[] = [];
  const answer = (message: { job: { spec: Record<string, unknown> } }) => message.job.spec.hang ? null : message.job.spec.fail ? { refused: "Invalid spec" } : { svg: "<svg/>" };
  const client = createChartWorkerClient(() => { const worker = new FakeWorker(answer); started.push(worker); return worker as unknown as Worker; }, 50);
  const job = (spec: Record<string, unknown>) => ({ spec, files: {}, config: {}, width: 640 });
  assert.deepEqual(await client.draw(job({})), { svg: "<svg/>" });
  assert.deepEqual(await client.draw(job({ fail: true })), { refused: "Invalid spec" });
  assert.equal(started.length, 1, "one worker for every chart");
  const [hung, after] = await Promise.all([client.draw(job({ hang: true })), client.draw(job({}))]);
  assert.deepEqual(hung, { refused: "The chart took more than 0 seconds to draw" });
  assert.ok(started[0].terminated, "the worker of a chart that hangs is stopped");
  assert.deepEqual(after, { svg: "<svg/>" }, "the next chart starts another worker");
  assert.equal(started.length, 2);
  // A worker that fails refuses its chart.
  const failing = client.draw(job({ hang: true }));
  await new Promise(resolve => setTimeout(resolve, 0));
  started[1].onerror!({ preventDefault() { /* Nothing to prevent. */ } });
  assert.deepEqual(await failing, { refused: "The chart could not be drawn" });
});

test("a chart takes the font, the text and guide colors and the series of the window", () => {
  const tokens = chartTokens(() => "", true, "Segoe UI");
  const config = vegaLiteConfig(tokens, "#2f343c") as { background: string; font: string; axis: Record<string, string>; range: { category: string[] }; mark: { color: string } };
  assert.equal(config.background, "#2f343c");
  assert.equal(config.font, "Segoe UI");
  assert.deepEqual(config.range.category, [...tokens.series]);
  assert.equal(config.mark.color, tokens.series[0]);
  assert.equal(config.axis.labelColor, tokens.muted);
  assert.equal(config.axis.gridColor, tokens.grid);
  // The light theme has its own colors.
  const light = vegaLiteConfig(chartTokens(() => "", false, "Segoe UI"), "#ffffff") as { range: { category: string[] }; title: { color: string } };
  assert.notDeepEqual(light.range.category, config.range.category);
  assert.notEqual(light.title.color, (config as unknown as { title: { color: string } }).title.color);
});
