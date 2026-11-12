import createDOMPurify, { type DOMPurify } from "dompurify";
import { chartTokens, readColorVariables, type ChartTokens } from "../charts/tokens";
import type { ChartJob } from "./vegaLiteDrawing";
import { chartDataPath, prepareChart, type ChartRefusal, type PreparedChart } from "./vegaLiteSpec";
import { startChartWorker } from "./vegaLiteWorkerStart";

export { isVegaLiteLanguage } from "./vegaLiteSpec";

/** How charts are drawn now: the configuration of Vega-Lite that gives them the look of the window, named by `key`. */
export type ChartAppearance = Readonly<{ key: string; config: Readonly<Record<string, unknown>>; width: number }>;

/** A data file a chart asked for: `ok` with its text, or why it was not read (a status of `markdownLinks.chartData`). */
export type ChartFile = Readonly<{ status: string; text?: string }>;

/**
 * Where the data files of the charts of a text are read: `read` reads a path below the folder that `key` names (the
 * folder of a session, or of a document of a project). Null where a text has no such folder: a chart that names a file
 * is refused.
 */
export type ChartData = Readonly<{ key: string; read(path: string): Promise<ChartFile> }>;

/** What a chart is in a render: its drawing or why it is not drawn, and whether it is to be drawn (again). */
export type ChartLookup = Readonly<{ svg: string | null; refused: string | null; pending: boolean }>;

/** The width of a chart whose spec says it is as wide as its container (`"width": "container"`), in the window. */
const containerWidth = 640;

/** The time a chart that reads files is shown before a render reads them again: a file that changed is drawn again. */
export const chartDataFreshness = 5_000;

/** The time a chart takes to draw at most: the worker that draws it is stopped, and the chart is refused. */
export const chartTimeLimit = 10_000;

/** A longer SVG is not shown: what a chart draws past it is no chart a reader can see. */
export const maximumChartSvgLength = 5_000_000;

// Why a chart is not drawn while the charts shown leave no room in the cache.
const tooMany = "Too many charts are shown at once";

/** What a reader sees when a file is not read, by the status of the host. */
export function chartFileRefusal(path: string, status: string): string {
  const shown = path.length > 80 ? `${path.slice(0, 79)}…` : path;
  switch (status) {
    case "not_found": return `${shown} was not found in the project`;
    case "outside": return `${shown} is not inside the project`;
    case "not_data": return `${shown} is not a CSV, TSV or JSON file`;
    case "too_large": return `${shown} is larger than 4 MB`;
    case "no_project": return `No project to read ${shown} from`;
    case "not_named": return `${shown} is read on the phone only for a chart of an answer or of what a script presented`;
    default: return `${shown} could not be read`;
  }
}

/**
 * The configuration of Vega-Lite for the colors of a page: its font, the color behind it, the text and the guides, and
 * the series of the charts of the window, which a spec may still override with a `config` of its own.
 */
export function vegaLiteConfig(tokens: ChartTokens, background: string): Record<string, unknown> {
  const { text, muted, axis, grid } = tokens;
  return {
    background, font: tokens.fontFamily, padding: 8,
    // A chart of categories along x is as wide as one of quantities: with the step of Vega-Lite (20 pixels a category) a
    // chart of a few bars is a narrow column whose labels overlap. Along y it keeps the step: a row for each category.
    view: { stroke: grid, continuousWidth: 440, continuousHeight: 260, discreteWidth: 440 },
    title: { color: text, subtitleColor: muted, fontSize: 14, fontWeight: 600, anchor: "start" },
    axis: { domainColor: axis, tickColor: axis, gridColor: grid, labelColor: muted, titleColor: text, labelFontSize: 11, titleFontSize: 12, titleFontWeight: 600 },
    legend: { labelColor: text, titleColor: text, labelFontSize: 11, titleFontSize: 12, titleFontWeight: 600 },
    header: { labelColor: text, titleColor: text },
    mark: { color: tokens.series[0] },
    // A rule is drawn to be read (a mean, a threshold): in the color of the labels, not the faint one of the axes.
    rule: { color: muted },
    text: { color: text },
    range: { category: [...tokens.series], ordinal: [...tokens.ramp], ramp: [...tokens.ramp], heatmap: [...tokens.ramp], diverging: [...tokens.diverging] },
  };
}

/**
 * How the window draws charts now: in its font and the colors of its theme (the variables of the charts of the window,
 * `charts/charts.css`, on the color behind a drawn block), as wide as `width` when a spec asks for the width of its
 * container. A chart is drawn again when the theme changes.
 */
export function vegaLiteAppearance(width?: number): ChartAppearance {
  const root = document.documentElement;
  const dark = root.dataset.theme !== "light";
  const fontFamily = getComputedStyle(root).fontFamily || "sans-serif";
  const config = readColorVariables(document.body ?? root, read => {
    const tokens = chartTokens(read, dark, fontFamily);
    return vegaLiteConfig(tokens, read("--panel-2") || tokens.surface);
  });
  const wide = width !== undefined && width > 0 ? Math.floor(width) : containerWidth;
  return { key: JSON.stringify([wide, config]), config, width: wide };
}

type Drawn = Readonly<{ svg: string } | { refused: string }>;
// A chart kept: what it drew, the digests of the files it read (never their text), when they were read, its size, and when a render
// last showed it.
type Entry = { result: Drawn; digests: Readonly<Record<string, string>> | null; checked: number; size: number; used: number };

/** The time a chart a render showed is kept, whatever the capacity of the cache: a page that shows more charts settles. */
export const chartInUse = 10_000;

// The SHA-256 of a text, to know that a file changed without keeping it.
async function digestOf(text: string): Promise<string> {
  const bytes = new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text)));
  return Array.from(bytes, byte => byte.toString(16).padStart(2, "0")).join("");
}

/** What draws a prepared chart with the text of its files: an SVG to show as it is, or why it is not drawn. */
export type ChartRenderer = (chart: PreparedChart, files: Readonly<Record<string, string>>, look: ChartAppearance) => Promise<Drawn>;

/**
 * Keeps the charts drawn from `vega-lite` blocks, by text, appearance and the folder their files are read from: Markdown is
 * rendered synchronously, so a chart is drawn once on the side and found here by every later render. At most `capacity`
 * charts and `maximumCharacters` of SVG are kept, the most recently used, but a chart a render showed in the last
 * {@link chartInUse} milliseconds is never dropped for another, which would draw it again and drop another, for ever: past four
 * times the capacity or twice the characters of such charts, a new chart is refused instead, until they are no longer shown. A
 * chart that reads files keeps their digests, is read again when a render finds it more than `freshness` milliseconds after its
 * files were read, and is drawn again only when one changed; until a chart is drawn for a new appearance, its drawing for the one
 * before is shown.
 */
export function createChartCache(render: ChartRenderer, limits: { capacity?: number; maximumCharacters?: number; freshness?: number; now?: () => number } = {}) {
  const capacity = limits.capacity ?? 48;
  const maximumCharacters = limits.maximumCharacters ?? 24_000_000;
  const freshness = limits.freshness ?? chartDataFreshness;
  const now = limits.now ?? Date.now;
  const entries = new Map<string, Entry>();
  // The latest drawing of a text, whatever its appearance: shown while the chart is drawn for another one.
  const latest = new Map<string, string>();
  const drawing = new Map<string, Promise<void>>();
  let characters = 0;
  const textKey = (text: string, data: ChartData | null) => `${data?.key ?? ""}\n${text}`;
  const entryKey = (text: string, look: ChartAppearance, data: ChartData | null) => `${look.key}\n${textKey(text, data)}`;

  // Drops the oldest charts that no render showed lately, until the cache is within its capacity.
  function trim() {
    const recent = now() - chartInUse;
    for (const [oldest, value] of entries) {
      if (entries.size <= capacity && characters <= maximumCharacters) break;
      if (value.used > recent) continue;
      entries.delete(oldest);
      characters -= value.size;
    }
  }
  // Whether the charts shown lately leave no room for another drawing.
  const full = () => entries.size >= capacity * 4 || characters > maximumCharacters * 2;
  function store(key: string, text: string, data: ChartData | null, entry: Entry) {
    const old = entries.get(key);
    if (old) { characters -= old.size; entries.delete(key); }
    if (entry.size > maximumCharacters) return;
    trim();
    if ("svg" in entry.result && full()) entry = { ...entry, result: { refused: tooMany }, digests: null, size: tooMany.length };
    entries.set(key, entry);
    characters += entry.size;
    trim();
    if ("svg" in entry.result) latest.set(textKey(text, data), key);
    while (latest.size > capacity * 8) latest.delete(latest.keys().next().value!);
  }
  function found(key: string): Entry | undefined {
    const entry = entries.get(key);
    if (entry) { entries.delete(key); entries.set(key, entry); entry.used = now(); } // Most recently used last.
    return entry;
  }
  async function readFiles(chart: PreparedChart, data: ChartData | null): Promise<Readonly<{ files: Record<string, string> }> | ChartRefusal> {
    const files: Record<string, string> = {};
    for (const url of chart.files) {
      const path = chartDataPath(url) as string;
      if (!data) return { refused: chartFileRefusal(path, "no_project") };
      let reply: ChartFile;
      try { reply = await data.read(path); } catch { reply = { status: "failed" }; }
      if (reply.status !== "ok" || typeof reply.text !== "string") return { refused: chartFileRefusal(path, reply.status) };
      files[url] = reply.text;
    }
    return { files };
  }
  const same = (left: Readonly<Record<string, string>>, right: Readonly<Record<string, string>>) =>
    Object.keys(left).length === Object.keys(right).length && Object.entries(left).every(([name, value]) => right[name] === value);
  const digestsOf = async (files: Readonly<Record<string, string>>) =>
    Object.fromEntries(await Promise.all(Object.entries(files).map(async ([url, text]) => [url, await digestOf(text)] as const)));

  return {
    /** The chart of a text for a render: drawn, refused, or not drawn yet (with its drawing for another appearance, if any). */
    lookup(text: string, look: ChartAppearance, data: ChartData | null): ChartLookup {
      const entry = found(entryKey(text, look, data));
      if (entry) {
        const pending = entry.digests !== null && now() - entry.checked > freshness;
        return "svg" in entry.result ? { svg: entry.result.svg, refused: null, pending } : { svg: null, refused: entry.result.refused, pending };
      }
      const before = latest.get(textKey(text, data));
      const shown = before !== undefined ? entries.get(before) : undefined;
      return { svg: shown && "svg" in shown.result ? shown.result.svg : null, refused: null, pending: true };
    },
    /** Draws the chart of a text, or reads its files again when they may have changed; never rejects. */
    draw(text: string, look: ChartAppearance, data: ChartData | null): Promise<void> {
      const key = entryKey(text, look, data);
      const known = entries.get(key);
      if (known && (known.digests === null || now() - known.checked <= freshness)) return Promise.resolve();
      let work = drawing.get(key);
      if (!work) {
        work = (async () => {
          const chart = prepareChart(text);
          const used = now();
          if ("refused" in chart) { store(key, text, data, { result: chart, digests: null, checked: used, size: chart.refused.length, used }); return; }
          const read = chart.files.length ? await readFiles(chart, data) : null;
          const checked = now();
          if (read && "refused" in read) { store(key, text, data, { result: read, digests: {}, checked, size: read.refused.length, used }); return; }
          const files = read ? read.files : null;
          const digests = files ? await digestsOf(files) : null;
          const before = entries.get(key);
          if (before && before.digests && digests && same(before.digests, digests)) { before.checked = checked; return; }
          // The charts shown lately leave no room: this one is refused before it is drawn, until they are no longer shown.
          trim();
          if (!before && full()) { store(key, text, data, { result: { refused: tooMany }, digests: null, checked, size: tooMany.length, used }); return; }
          let result: Drawn;
          try { result = await render(chart, files ?? {}, look); } catch { result = { refused: "The chart could not be drawn" }; }
          const size = "svg" in result ? result.svg.length : result.refused.length;
          store(key, text, data, { result, digests, checked, size, used });
        })().catch(() => { /* Never rejects: the chart stays code. */ }).finally(() => drawing.delete(key));
        drawing.set(key, work);
      }
      return work;
    },
    /** Charts and SVG characters kept. */
    usage: () => ({ entries: entries.size, characters }),
  };
}

type Answer = Readonly<{ id: number; svg?: string; refused?: string }>;

/**
 * Draws charts in a worker of their own, one at a time: a chart that takes more than `timeLimit` milliseconds is refused
 * and its worker stopped, and the next chart starts another. A worker that cannot start or that fails refuses its chart.
 */
export function createChartWorkerClient(start: () => Worker, timeLimit = chartTimeLimit) {
  let worker: Worker | null = null;
  let sequence = 0;
  let queue: Promise<unknown> = Promise.resolve();
  const answers = new Map<number, (answer: Drawn) => void>();
  const stop = (refused: string) => {
    worker?.terminate();
    worker = null;
    for (const [, settle] of answers) settle({ refused });
    answers.clear();
  };
  function started(): Worker {
    if (worker) return worker;
    const created = start();
    created.onmessage = (event: MessageEvent<Answer>) => {
      const settle = answers.get(event.data?.id);
      if (!settle) return;
      answers.delete(event.data.id);
      settle(typeof event.data.svg === "string" ? { svg: event.data.svg } : { refused: event.data.refused || "The chart could not be drawn" });
    };
    created.onerror = event => { event.preventDefault(); if (worker === created) stop("The chart could not be drawn"); };
    created.onmessageerror = () => { if (worker === created) stop("The chart could not be drawn"); };
    return worker = created;
  }
  function run(job: ChartJob): Promise<Drawn> {
    return new Promise<Drawn>(resolve => {
      const id = ++sequence;
      const timer = setTimeout(() => { if (answers.has(id)) stop(`The chart took more than ${Math.round(timeLimit / 1000)} seconds to draw`); }, timeLimit);
      answers.set(id, answer => { clearTimeout(timer); resolve(answer); });
      try { started().postMessage({ id, job }); } catch { answers.delete(id); clearTimeout(timer); resolve({ refused: "The chart could not be drawn" }); }
    });
  }
  return {
    /** Draws a chart after the ones asked before; never rejects. */
    draw(job: ChartJob): Promise<Drawn> {
      const work = queue.then(() => run(job));
      queue = work.catch(() => undefined);
      return work;
    },
    /** Stops the worker, if one runs. */
    stop: () => stop("The chart was not drawn"),
  };
}

/** A value of CSS as the browser reads it: its escapes decoded (`\\75 rl(` is `url(`) and its comments removed. */
export function cssValue(value: string): string {
  return value.replace(/\/\*[\s\S]*?(?:\*\/|$)/gu, "")
    .replace(/\\(?:([0-9a-f]{1,6})[ \t\n\r\f]?|([^\n\r\f0-9a-f]))/giu, (_match, hex: string | undefined, character: string | undefined) => {
      if (hex === undefined) return character ?? "";
      const point = parseInt(hex, 16);
      return point > 0 && point <= 0x10ffff && !(point >= 0xd800 && point <= 0xdfff) ? String.fromCodePoint(point) : "\ufffd";
    });
}

// The functions of CSS that name a resource, whatever their case: what an attribute of a chart may not hold, but for a reference
// to the chart itself.
const resourceFunction = /(?:url|image-set|image|cross-fade|element|src|paint)\s*\(/iu;
// A reference to an element of the chart, and nothing else: `url(#id)`, in any case, quoted or not.
const ownReference = /^\s*url\(\s*(['"]?)#([\w\-.:]{1,256})\1\s*\)\s*$/iu;

/**
 * The SVG of a chart as the window shows it: SVG only (no script, no handler, no style element), without a link, an image
 * or a reference to anything outside it, and with the ids of its clips and gradients made its own, so that two charts of
 * a page never use each other's. An attribute that names a resource (`url(`, `URL(`, `\\75 rl(`, `image-set(`…) is kept only
 * when it is a reference to an element of the chart, rewritten to its own id; a `style` keeps none.
 */
export function createChartSanitizer(view: () => Window & typeof globalThis = () => window) {
  let purifier: DOMPurify | undefined;
  let prefix = "";
  let sequence = 0;
  return (svg: string): string => {
    if (!purifier) {
      purifier = createDOMPurify(view());
      purifier.addHook("uponSanitizeAttribute", (_node, data) => {
        if (data.attrName === "id") { data.attrValue = `${prefix}${data.attrValue}`; return; }
        const value = cssValue(data.attrValue);
        if (!resourceFunction.test(value)) return;
        const reference = data.attrName === "style" ? null : ownReference.exec(value);
        if (reference) data.attrValue = `url(#${prefix}${reference[2]})`;
        else data.keepAttr = false;
      });
    }
    prefix = `codealta-chart-${++sequence}-`;
    try {
      return purifier.sanitize(svg, { USE_PROFILES: { svg: true, svgFilters: true },
        FORBID_TAGS: ["a", "image", "use", "foreignObject", "style", "script", "feImage"], FORBID_ATTR: ["href", "xlink:href"] }) as unknown as string;
    } finally { prefix = ""; }
  };
}

/** Draws a chart in the worker, on the look of the window, and sanitizes what it draws. */
export function chartRenderer(client: ReturnType<typeof createChartWorkerClient>, sanitize: (svg: string) => string): ChartRenderer {
  return async (chart, files, look) => {
    const drawn = await client.draw({ spec: chart.spec, files, config: look.config, width: look.width });
    if (!("svg" in drawn)) return drawn;
    if (drawn.svg.length > maximumChartSvgLength) return { refused: "The chart is too large to show" };
    const svg = sanitize(drawn.svg);
    return /^<svg[\s>]/u.test(svg) ? { svg } : { refused: "The chart could not be drawn" };
  };
}

/** The charts of the window, drawn in a worker that starts with the first one. */
export const charts = createChartCache(chartRenderer(createChartWorkerClient(startChartWorker), createChartSanitizer()));
