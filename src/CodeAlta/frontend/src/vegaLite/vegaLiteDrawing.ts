import * as vega from "vega";
import { expressionFunction, logger, parse, View, type Loader } from "vega";
import { expressionInterpreter } from "vega-interpreter";
import { compile, type TopLevelSpec } from "vega-lite";
import { dataFormatOf, maximumGeneratedItems } from "./vegaLiteSpec";

/**
 * What the worker draws: a Vega-Lite spec whose data files were read by the window (`files`, by their path as the
 * spec writes it), the configuration of the window's look, and the width a chart as wide as its container takes.
 */
export type ChartJob = Readonly<{ spec: Record<string, unknown>; files: Readonly<Record<string, string>>; config: Readonly<Record<string, unknown>>; width: number }>;

/**
 * The loader of a chart: it reads nothing, whatever the spec names (data, images, links). Its data is in the spec. An
 * address it is asked to check is none (an empty `href`): an image is not drawn and a link leads nowhere.
 */
export const refusingLoader: Loader = Object.freeze({
  options: {},
  load: () => Promise.reject(new Error("A chart reads no file and no address.")),
  sanitize: () => Promise.resolve({ href: "" }),
  http: () => Promise.reject(new Error("A chart reads no address.")),
  file: () => Promise.reject(new Error("A chart reads no file.")),
}) as unknown as Loader;

/**
 * The spec with the data of its files in place of their path: `{ "url": "out/a.csv" }` becomes the text of the file, read
 * as its format says, or as its extension names it. Data is found wherever the spec has one (layers, concatenations,
 * the `from` of a lookup).
 */
export function inlineData(spec: Record<string, unknown>, files: Readonly<Record<string, string>>): Record<string, unknown> {
  const visit = (node: unknown, key: string | null): unknown => {
    if (Array.isArray(node)) return node.map(item => visit(item, null));
    if (!node || typeof node !== "object") return node;
    const record = node as Record<string, unknown>;
    if (key === "data" && typeof record.url === "string") {
      const { url, format, ...rest } = record;
      const text = files[url as string];
      if (text === undefined) throw new Error(`The data of ${url as string} was not read.`);
      const given = format && typeof format === "object" ? format as Record<string, unknown> : {};
      return { ...rest, values: text, format: { ...given, type: typeof given.type === "string" ? given.type : dataFormatOf(url as string) } };
    }
    return Object.fromEntries(Object.entries(record).map(([name, value]) => [name, visit(value, name)]));
  };
  return visit(spec, null) as Record<string, unknown>;
}

/**
 * The rows of a delimited text (CSV, TSV) as objects of their header's names, each value a string, as d3-dsv parses them:
 * quoted fields with `""` for a quote, and line breaks inside quotes. d3-dsv builds its rows with `new Function`, which the
 * content security policy of the window refuses: this reader builds them field by field.
 */
export function parseDelimited(text: string, delimiter: string): Record<string, string>[] {
  const rows: string[][] = [];
  let row: string[] = [], field = "", quoted = false, index = 0, started = false;
  const end = () => { row.push(field); field = ""; };
  while (index < text.length) {
    const character = text[index];
    if (quoted) {
      if (character === "\"") {
        if (text[index + 1] === "\"") { field += "\""; index += 2; continue; }
        quoted = false; index++; continue;
      }
      field += character; index++; continue;
    }
    if (character === "\"" && field === "" && !started) { quoted = true; started = true; index++; continue; }
    if (character === delimiter) { end(); started = false; index++; continue; }
    if (character === "\n" || character === "\r") {
      end(); rows.push(row); row = []; started = false;
      index += character === "\r" && text[index + 1] === "\n" ? 2 : 1;
      continue;
    }
    field += character; started = true; index++;
  }
  if (started || field !== "" || row.length) { end(); rows.push(row); }
  const [header, ...body] = rows;
  if (!header) return [];
  return body.map(values => {
    const record: Record<string, string> = {};
    header.forEach((name, column) => { record[name] = values[column] ?? ""; });
    return record;
  });
}

// A reader of Vega for a delimited format: `dsv` names its delimiter in the format, `csv` and `tsv` have theirs.
function delimitedReader(delimiter: string | null) {
  const reader = (data: unknown, format: { delimiter?: string; header?: string[] } = {}) => {
    const by = delimiter ?? format.delimiter ?? ",";
    const header = Array.isArray(format.header) ? `${format.header.map(name => `"${String(name).replaceAll("\"", "\"\"")}"`).join(by)}\n` : "";
    return parseDelimited(header + String(data), by);
  };
  return Object.assign(reader, { responseType: "text" });
}

/**
 * Makes Vega safe to run on what a spec says, once, where the charts are drawn (Vega keeps its readers and its functions for
 * the whole worker): CSV and TSV are read without `new Function` ({@link parseDelimited}), and the expression functions that
 * build a value of any size, which a hostile spec would use to fill the memory of the worker, are bounded.
 */
export function secureVega() {
  const formats = (vega as unknown as { formats(name: string, reader: unknown): unknown }).formats;
  formats("dsv", delimitedReader(null));
  formats("csv", delimitedReader(","));
  formats("tsv", delimitedReader("\t"));
  const sequence = expressionFunction("sequence") as (...values: number[]) => number[];
  expressionFunction("sequence", (...values: number[]) => {
    const [start, stop, step = 1] = values.length === 1 ? [0, values[0]] : values;
    if (Math.abs((stop - start) / (step || 1)) > maximumGeneratedItems) throw new Error("A sequence is too long to draw.");
    return sequence(...values);
  });
  const pad = expressionFunction("pad") as (text: string, length: number, ...rest: unknown[]) => string;
  expressionFunction("pad", (text: string, length: number, ...rest: unknown[]) => {
    if (length > maximumGeneratedItems) throw new Error("A text is too long to draw.");
    return pad(text, length, ...rest);
  });
}

/**
 * Draws a chart as an SVG string, headless: Vega-Lite compiles the spec, and Vega runs its expressions through an
 * interpreter (no `eval`, no `new Function`) with a loader that reads nothing. Rejects with what refused the chart.
 */
export async function drawChart(job: ChartJob): Promise<string> {
  const sized = { ...job.spec };
  if (sized.width === "container") sized.width = job.width;
  if (sized.height === "container") sized.height = Math.round(job.width * 0.6);
  const spec = inlineData(sized, job.files) as unknown as TopLevelSpec;
  const quiet = logger(0);
  const compiled = compile(spec, { config: job.config as never, logger: quiet as never }).spec;
  // What the dataflow refuses (a value that cannot be parsed, an expression that fails) is said, not drawn half.
  let failure: unknown = null;
  const capture = logger(1);
  capture.error = (...values: readonly unknown[]) => { failure ??= values[0]; return capture; };
  const view = new View(parse(compiled, undefined, { ast: true }), { expr: expressionInterpreter, renderer: "none", loader: refusingLoader, logger: capture });
  try {
    const svg = await view.toSVG();
    if (failure !== null) throw failure instanceof Error ? failure : new Error(String(failure));
    return svg;
  } finally {
    view.finalize();
  }
}
