/** Whether a fenced block holds a Vega-Lite spec: `vega-lite`, or `vegalite`, in any case. */
export function isVegaLiteLanguage(language: string): boolean {
  return /^vega-?lite$/iu.test(language);
}

/** A longer spec is left as a code block: data of that size belongs in a file of the project. */
export const maximumChartLength = 200_000;

/** The files a chart reads at most. */
export const maximumChartFiles = 8;

/** The largest file a chart reads, in bytes: `MaximumChartDataBytes` of the host. */
export const maximumChartFileBytes = 4 * 1024 * 1024;

/** What a generator, an axis or a bin of a chart makes at most (rows of a sequence, ticks, bins, steps). */
export const maximumGeneratedItems = 100_000;

/** The kinds of data file a chart reads, by extension, and the format Vega reads each in. */
const dataFormats: Readonly<Record<string, string>> = Object.freeze({ csv: "csv", tsv: "tsv", json: "json", geojson: "json", topojson: "topojson" });

/** The format Vega reads a data file in, by its extension; `json` for one it does not know. */
export function dataFormatOf(path: string): string {
  return dataFormats[/\.([a-z]+)$/iu.exec(path)?.[1].toLowerCase() ?? ""] ?? "json";
}

/** A spec that can be drawn, and the files it reads. */
export type PreparedChart = Readonly<{ spec: Record<string, unknown>; files: readonly string[] }>;

/** Why a spec is not drawn: one line, for the reader. */
export type ChartRefusal = Readonly<{ refused: string }>;

const isRecord = (value: unknown): value is Record<string, unknown> => !!value && typeof value === "object" && !Array.isArray(value);

/**
 * The path of a data file as a chart may name it: relative, below the folder it is read from, written with `/` or `\`.
 * Anything else is refused, with why: an address (`https:`, `file:`, `data:`), a full path, `~`, `..`, a query.
 */
export function chartDataPath(url: string): string | ChartRefusal {
  const shown = url.length > 80 ? `${url.slice(0, 79)}…` : url;
  if (/^https?:/iu.test(url)) return { refused: `Data from the web is not read: ${shown}` };
  if (!url.trim() || url.length > 1024 || /[\u0000-\u001f\u007f?#*"<>|]/u.test(url) || url.includes(":")) return { refused: `Not a file of the project: ${shown}` };
  const parts = url.replaceAll("\\", "/").split("/");
  while (parts[0] === "." && parts.length > 1) parts.shift();
  if (parts[0] === "" || parts[0] === "~" || parts.some(part => part === ".." || part === "." || !part.trim() || part !== part.trim() || part.endsWith(".")))
    return { refused: `Not a path inside the project: ${shown}` };
  const path = parts.join("/");
  if (!/\.(csv|tsv|json|geojson|topojson)$/iu.test(path)) return { refused: `Not a CSV, TSV or JSON file: ${shown}` };
  return path;
}

// Literal numbers that size what Vega makes: an axis of a billion ticks fills the memory of the worker before any time limit.
const sizingProperties: Readonly<Record<string, number>> = Object.freeze({ tickCount: 1_000, maxbins: 1_000, steps: 10_000, maxsteps: 10_000, minsteps: 10_000 });

// The size of a generator `{ start, stop, step }` when its numbers are written; null when it is no such generator.
function generated(value: Record<string, unknown>): number | null {
  const { start, stop, step } = value;
  if (typeof start !== "number" || typeof stop !== "number") return null;
  const by = typeof step === "number" && step !== 0 ? step : 1;
  return Math.abs((stop - start) / by);
}

// Why a spec would make more than a chart shows; null when nothing it writes does.
function oversized(spec: unknown): string | null {
  const stack: unknown[] = [spec];
  let visited = 0;
  while (stack.length) {
    const node = stack.pop();
    if (++visited > 1_000_000) return "The spec is too large to draw";
    if (Array.isArray(node)) { stack.push(...node); continue; }
    if (!isRecord(node)) continue;
    for (const [name, value] of Object.entries(node)) {
      const limit = sizingProperties[name];
      if (limit !== undefined && typeof value === "number" && Math.abs(value) > limit) return `${name} is larger than ${limit}`;
      if ((name === "sequence" || name === "keyvals") && isRecord(value) && (generated(value) ?? 0) > maximumGeneratedItems)
        return `A sequence of more than ${maximumGeneratedItems} values is not drawn`;
      if (name === "bin" && isRecord(value) && Array.isArray(value.extent) && typeof value.step === "number" && value.step > 0
        && typeof value.extent[0] === "number" && typeof value.extent[1] === "number" && (value.extent[1] - value.extent[0]) / value.step > maximumGeneratedItems)
        return `A bin of more than ${maximumGeneratedItems} steps is not drawn`;
      stack.push(value);
    }
  }
  return null;
}

/** The first line of what an error says, short; never empty. */
export function chartRefusalOf(error: unknown, fallback = "Not a chart that can be drawn"): string {
  const message = error instanceof Error ? error.message : typeof error === "string" ? error : "";
  const line = message.split(/\r?\n/u).map(value => value.trim()).find(value => value.length > 0)?.replace(/[:.]$/u, "") ?? "";
  if (!line) return fallback;
  return line.length > 160 ? `${line.slice(0, 159)}…` : line;
}

/**
 * Reads the text of a `vega-lite` block: a Vega-Lite spec in JSON, whose data files are named by a path that
 * {@link chartDataPath} accepts. A text that is no such spec is refused with why.
 */
export function prepareChart(text: string): PreparedChart | ChartRefusal {
  if (text.length > maximumChartLength) return { refused: `The spec is longer than ${maximumChartLength} characters` };
  let spec: unknown;
  try { spec = JSON.parse(text); } catch (error) { return { refused: `Not JSON: ${chartRefusalOf(error)}` }; }
  if (!isRecord(spec)) return { refused: "Not a Vega-Lite spec: the JSON is not an object" };
  // A spec of Vega itself is another language, which this block does not draw.
  if (typeof spec.$schema === "string" && /\/vega\/v\d/iu.test(spec.$schema)) return { refused: "A Vega spec, not a Vega-Lite one" };
  const tooLarge = oversized(spec);
  if (tooLarge) return { refused: tooLarge };
  const files = new Set<string>();
  const stack: [unknown, string | null][] = [[spec, null]];
  while (stack.length) {
    const [node, key] = stack.pop()!;
    if (Array.isArray(node)) { for (const item of node) stack.push([item, null]); continue; }
    if (!isRecord(node)) continue;
    if (key === "data" && "url" in node) {
      if (typeof node.url !== "string") return { refused: "The url of a data is not a text" };
      const path = chartDataPath(node.url);
      if (typeof path !== "string") return path;
      files.add(node.url);
      if (files.size > maximumChartFiles) return { refused: `A chart reads ${maximumChartFiles} files at most` };
    }
    for (const [name, value] of Object.entries(node)) stack.push([value, name]);
  }
  return { spec, files: [...files] };
}
