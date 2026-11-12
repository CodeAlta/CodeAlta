import assert from "node:assert/strict";
import test from "node:test";
import { setFlagsFromString } from "node:v8";
import { runInNewContext } from "node:vm";
import { chartTokens } from "../charts/tokens";
import { chartFileRefusal, cssValue, createChartCache, createChartWorkerClient, vegaLiteConfig, type ChartAppearance, type ChartData, type ChartFile } from "./vegaLite";

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

test("the cache keeps the newest charts, within a number and a size, once no render shows the older ones", async () => {
  let time = 0;
  const cache = createChartCache(async chart => ({ svg: `<svg>${"x".repeat(Number(chart.spec.size))}</svg>` }), { capacity: 2, maximumCharacters: 1_000, now: () => time });
  for (const size of [10, 20, 30]) { await cache.draw(spec({ size }), dark, null); time += 20_000; }
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
test("a value of CSS is read as the browser reads it: escapes decoded, comments removed", () => {
  assert.equal(cssValue("\\75 rl(x)"), "url(x)");
  assert.equal(cssValue("\\000055RL(x)"), "URL(x)");
  assert.equal(cssValue("\\u\\r\\l(x)"), "url(x)");
  assert.equal(cssValue("u/* a */rl(x)"), "url(x)");
  assert.equal(cssValue("\\0 \\110000 x"), "\ufffd\ufffdx");
  assert.equal(cssValue("rgb(1, 2, 3)"), "rgb(1, 2, 3)");
});
test("a page with more charts than the cache holds settles: the charts it shows keep their drawing", async () => {
  let draws = 0;
  const cache = createChartCache(async chart => { draws++; return { svg: `<svg>${String(chart.spec.n)}</svg>` }; });
  const texts = Array.from({ length: 49 }, (_, index) => spec({ n: index }));
  // A render looks up every chart of the page, then draws what is pending, and renders again: as `MarkdownContent` does.
  let rounds = 0, pending = texts.length;
  while (pending && rounds++ < 10) {
    const waiting = texts.filter(text => cache.lookup(text, dark, null).pending);
    pending = waiting.length;
    await Promise.all(waiting.map(text => cache.draw(text, dark, null)));
  }
  assert.ok(rounds <= 3, `settled after ${rounds} renders`);
  assert.equal(draws, 49, "each chart is drawn once");
  assert.ok(texts.every(text => cache.lookup(text, dark, null).svg !== null));
});

test("past what it can keep of charts in use, a new chart is refused instead of taking the drawing of another", async () => {
  let time = 0;
  const cache = createChartCache(async () => ({ svg: "<svg/>" }), { capacity: 2, maximumCharacters: 1_000, now: () => time });
  const texts = Array.from({ length: 12 }, (_, index) => spec({ n: index }));
  for (const text of texts) { cache.lookup(text, dark, null); await cache.draw(text, dark, null); }
  const shown = texts.map(text => cache.lookup(text, dark, null));
  assert.ok(shown.slice(0, 8).every(found => found.svg === "<svg/>" && !found.pending), "the charts in use keep their drawing, up to four times the capacity");
  assert.ok(shown.slice(8).every(found => found.refused === "Too many charts are shown at once" && !found.pending), JSON.stringify(shown.slice(8)));
  // Charts no longer used for a while make room again.
  time = 60_000;
  const later = spec({ n: "later" });
  await cache.draw(later, dark, null);
  assert.equal(cache.lookup(later, dark, null).svg, "<svg/>");
  assert.ok(cache.usage().entries <= 8);
});

test("a chart that read files keeps a digest of their text, not the text", async () => {
  setFlagsFromString("--expose-gc");
  const gc = runInNewContext("gc") as () => void;
  const cache = createChartCache(async () => ({ svg: "<svg/>" }));
  // A large text may be kept outside the heap of V8 (an external string): both are counted.
  const used = () => { const memory = process.memoryUsage(); return memory.heapUsed + memory.external; };
  gc();
  const before = used();
  for (let index = 0; index < 16; index++) {
    // A flat text of its own (`repeat` would make a rope that shares its parts).
    const { data } = folder({ "out/a.csv": { status: "ok", text: `a\n${Buffer.alloc(2_000_000, 97 + index).toString("latin1")}` } }, `session:${index}`);
    await cache.draw(withFile, dark, data);
  }
  // Buffers of the digests are released at a later collection.
  for (let pass = 0; pass < 3; pass++) { await new Promise(resolve => setImmediate(resolve)); gc(); }
  const kept = used() - before;
  assert.ok(kept < 8_000_000, `the cache keeps ${kept} bytes for 16 charts of 2 MB files`);
});
