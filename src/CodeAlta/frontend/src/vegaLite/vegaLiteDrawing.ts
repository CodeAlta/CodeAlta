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
      // The header is no row of data.
      if (rows.length > maximumRows + 1) throw new Error(`The data has more than ${maximumRows} rows`);
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

// What a chart makes at most, wherever it comes from (the rows of a transform, the items of the marks, the values of the
// expressions): a worker that fills its memory takes the whole window down with it, so what would go past it is refused before it
// is made. A worker stopped by its time limit is no bound on memory.
export const maximumTuples = 1_000_000;
// The longest text an expression makes, and the size of a text or a list from which it is counted against the budget of a chart.
const maximumText = 1_000_000;
const countedSize = 64;
// The rows of a data file or of inline data, once read.
export const maximumRows = 200_000;

/** What a chart may still make: the budget of one drawing, which its View holds. */
type Budget = { left: number };
const budgetOf = Symbol("codealta.chart.budget");

function tooLarge(what: string): never {
  throw new Error(`The chart makes ${what} than it can draw`);
}

// Counts what a drawing makes against its budget; refuses what would go past it.
function charge(budget: Budget | undefined, count: number, what = "more values") {
  if (!budget || count <= 0) return;
  budget.left -= count;
  if (budget.left < 0) tooLarge(what);
}

// Counts a value an expression made: a long text, or a long list.
function chargeValue(budget: Budget, value: unknown): unknown {
  if (typeof value === "string") {
    if (value.length > maximumText) tooLarge("a longer text");
    if (value.length > countedSize) charge(budget, value.length);
  } else if (Array.isArray(value) && value.length > countedSize) charge(budget, value.length);
  return value;
}

/**
 * The interpreter of the expressions of one drawing: Vega's (no `eval`, no `new Function`), which counts what its expressions make
 * against the budget of the chart. Text built with `+` across transforms, or a list made for each row, is refused once it passes it.
 */
export function budgetedInterpreter(budget: Budget): typeof expressionInterpreter {
  type Interpreter = Record<string, (ctx: unknown, expr: unknown) => (...values: unknown[]) => unknown>;
  const base = expressionInterpreter as unknown as Interpreter;
  const counted = (name: string) => (ctx: unknown, expr: unknown) => {
    const run = base[name](ctx, expr);
    return (...values: unknown[]) => chargeValue(budget, run(...values));
  };
  return {
    ...base,
    operator: counted("operator"), parameter: counted("parameter"), event: counted("event"), handler: counted("handler"),
    encode(ctx: unknown, encode: { channels: Record<string, unknown> }) {
      const run = base.encode(ctx, encode) as (item: Record<string, unknown>, _: unknown) => number;
      return (item: Record<string, unknown>, _: unknown) => {
        const changed = run(item, _);
        for (const name in encode.channels) chargeValue(budget, item[name]);
        return changed;
      };
    },
  } as unknown as typeof expressionInterpreter;
}

// The milliseconds of a unit of time at least (a month of 28 days), to count what a sequence or ticks of time make.
const unitMilliseconds: Readonly<Record<string, number>> = Object.freeze({ millisecond: 1, milliseconds: 1, second: 1e3, seconds: 1e3, minute: 6e4, minutes: 6e4,
  hour: 3.6e6, hours: 3.6e6, day: 8.64e7, date: 8.64e7, dayofyear: 8.64e7, week: 6.048e8, month: 2.4192e9, quarter: 7.6896e9, year: 3.1536e10 });

/** How many values a sequence of time makes at most, from its unit and step; null for a unit Vega does not know. */
export function timeSteps(unit: unknown, start: unknown, stop: unknown, step: unknown = 1): number | null {
  const per = typeof unit === "string" ? unitMilliseconds[unit.toLowerCase()] : undefined;
  if (!per) return null;
  const from = +(start as number), to = +(stop as number), by = Math.abs(+(step as number)) || 1;
  return Number.isFinite(from) && Number.isFinite(to) ? Math.abs(to - from) / (per * by) : 0;
}

// The width of a d3 format specifier ("08.2f" is 8), which pads a number with that many characters.
const formatWidth = (specifier: unknown) => Number(/^(?:.?[<>=^])?[+\-( ]?[$#]?0?(\d+)?/u.exec(String(specifier ?? ""))?.[1] ?? 0);

// A transform of Vega, checked before it runs: `check` throws when what it would make passes a limit.
type TransformType = { prototype: { transform(this: unknown, _: Record<string, unknown>, pulse: Pulse): unknown } };
type Pulse = { source?: object[]; dataflow?: Record<symbol, Budget | undefined> };
type Accessor = (tuple: object) => unknown;

function guard(transforms: Record<string, TransformType>, name: string, check: (_: Record<string, any>, rows: object[], budget: Budget | undefined) => void) {
  const type = transforms[name];
  if (!type) return;
  const run = type.prototype.transform;
  type.prototype.transform = function (this: unknown, _: Record<string, unknown>, pulse: Pulse) {
    check(_, pulse?.source ?? [], pulse?.dataflow?.[budgetOf]);
    return run.call(this, _, pulse);
  };
}

// The number of distinct values of some accessors over rows, or of their combinations.
function distinct(rows: object[], accessors: readonly Accessor[]): number {
  if (!accessors.length) return rows.length ? 1 : 0;
  const seen = new Set<string>();
  for (const row of rows) seen.add(accessors.length === 1 ? String(accessors[0](row)) : JSON.stringify(accessors.map(accessor => accessor(row))));
  return seen.size;
}

// The ticks an axis or a legend makes: a count, or a step of time over the domain of its scale.
function tickCount(_: Record<string, any>) {
  const count = _.count;
  if (typeof count === "number") { if (count > 10_000) tooLarge("more ticks"); return; }
  if (!count || typeof count !== "object" || typeof _.scale?.domain !== "function") return;
  const domain = (_.scale.domain() as unknown[]).map(value => +(value as number)).filter(Number.isFinite);
  if (domain.length < 2) return;
  const steps = timeSteps(count.interval, Math.min(...domain), Math.max(...domain), count.step);
  if (steps !== null && steps > 10_000) tooLarge("more ticks");
}

/**
 * Makes Vega safe to run on what a spec says, once, where the charts are drawn (Vega keeps its readers, its functions and its
 * transforms for the whole worker). CSV and TSV are read without `new Function` ({@link parseDelimited}), and no reader gives more
 * than {@link maximumRows} rows. What a spec could make of any size, which would fill the memory of the worker and take the window
 * with it, is refused before it is made: the expression functions that build a list or a text (`sequence`, `timeSequence`,
 * `utcSequence`, `pad`, `format`, `join`, `replace`), and the transforms whose output a number of the spec or the shape of the
 * data multiplies (`sequence`, `fold`, `flatten`, `impute`, `pivot`, `aggregate` with `cross`, `kde`, `density`, `quantile`,
 * `regression`, `graticule`, the ticks of axes and legends), counted against the budget of the chart with the items of its marks.
 */
export function secureVega() {
  const vegaModule = vega as unknown as { formats(name: string, reader?: unknown): unknown; transforms: Record<string, TransformType> };
  const formats = vegaModule.formats;
  formats("dsv", delimitedReader(null));
  formats("csv", delimitedReader(","));
  formats("tsv", delimitedReader("\t"));
  for (const name of ["json", "topojson"]) {
    const read = formats(name) as ((data: unknown, format: unknown) => unknown) & { responseType?: string };
    formats(name, Object.assign((data: unknown, format: unknown) => {
      const rows = read(data, format);
      if (Array.isArray(rows) && rows.length > maximumRows) throw new Error(`The data has more than ${maximumRows} rows`);
      return rows;
    }, { responseType: read.responseType }));
  }

  const bound = <T extends (...values: any[]) => unknown>(name: string, check: (...values: Parameters<T>) => void) => {
    const original = expressionFunction(name) as T;
    // A function of Vega reads its context from `this` (the locale of `format`): it is called as Vega calls it.
    expressionFunction(name, function (this: unknown, ...values: Parameters<T>) { check(...values); return original.apply(this, values); });
  };
  bound("sequence", (...values: number[]) => {
    const [start, stop, step = 1] = values.length === 1 ? [0, values[0]] : values;
    if (!(Math.abs((stop - start) / (step || 1)) <= maximumGeneratedItems)) throw new Error("A sequence is too long to draw");
  });
  for (const name of ["timeSequence", "utcSequence"]) {
    bound(name, (unit: unknown, start: unknown, stop: unknown, step?: unknown) => {
      const steps = timeSteps(unit, start, stop, step);
      if (steps !== null && !(steps <= maximumGeneratedItems)) throw new Error("A sequence is too long to draw");
    });
  }
  bound("pad", (_text: unknown, length: unknown) => { if (!(Number(length) <= 10_000)) throw new Error("A text is too long to draw"); });
  bound("format", (_value: unknown, specifier: unknown) => { if (formatWidth(specifier) > 1_000) throw new Error("A format is too wide to draw"); });
  bound("join", (list: unknown, separator: unknown = ",") => {
    if (!Array.isArray(list)) return;
    let length = Math.max(0, list.length - 1) * String(separator).length;
    for (const item of list) if ((length += String(item ?? "").length) > maximumText) throw new Error("A text is too long to draw");
    if (length > maximumText) throw new Error("A text is too long to draw");
  });
  bound("replace", (text: unknown, _pattern: unknown, replacement: unknown) => {
    const source = String(text ?? ""), by = typeof replacement === "string" ? replacement.length : 0;
    // Each place of the text may become the replacement: the longest the result can be.
    if (source.length + (source.length + 1) * by > maximumText) throw new Error("A text is too long to draw");
  });

  const transforms = vegaModule.transforms;
  const rowsOf = (count: number, budget: Budget | undefined, what = "more rows") => {
    if (!(count <= maximumTuples)) tooLarge(what);
    charge(budget, count, what);
  };
  guard(transforms, "sequence", _ => {
    const step = +_.step || 1;
    if (!(Math.abs((+_.stop - +_.start) / step) <= maximumGeneratedItems)) throw new Error("A sequence is too long to draw");
  });
  guard(transforms, "fold", (_, rows, budget) => rowsOf(rows.length * ((_.fields as unknown[] | undefined)?.length ?? 0), budget));
  guard(transforms, "flatten", (_, rows, budget) => {
    const fields = (_.fields ?? []) as Accessor[];
    let count = 0;
    for (const row of rows) {
      let longest = 0;
      for (const field of fields) { const value = field(row); if (Array.isArray(value)) longest = Math.max(longest, value.length); }
      if ((count += longest) > maximumTuples) tooLarge("more rows");
    }
    charge(budget, count);
  });
  guard(transforms, "impute", (_, rows, budget) => {
    const keys = new Set(rows.map(row => String((_.key as Accessor)(row))));
    for (const value of (_.keyvals ?? []) as unknown[]) keys.add(String(value));
    rowsOf(distinct(rows, (_.groupby ?? []) as Accessor[]) * keys.size, budget);
  });
  guard(transforms, "pivot", (_, rows, budget) => rowsOf(distinct(rows, (_.groupby ?? []) as Accessor[]) * Math.min(+_.limit || Infinity, distinct(rows, [_.field as Accessor])), budget));
  guard(transforms, "aggregate", (_, rows, budget) => {
    if (!_.cross) return;
    rowsOf(((_.groupby ?? []) as Accessor[]).reduce((product, field) => product * distinct(rows, [field]), 1), budget);
  });
  for (const name of ["kde", "density"]) {
    guard(transforms, name, _ => {
      for (const key of ["steps", "minsteps", "maxsteps"]) if (+_[key] > 10_000) throw new Error(`${key} is larger than 10000`);
    });
  }
  guard(transforms, "quantile", _ => {
    const count = Array.isArray(_.probs) ? _.probs.length : 1 / (+_.step || 0.01);
    if (!(count <= maximumGeneratedItems)) throw new Error(`A quantile of more than ${maximumGeneratedItems} values is not drawn`);
  });
  guard(transforms, "regression", _ => { if (+_.order > 20) throw new Error("A regression of an order larger than 20 is not drawn"); });
  guard(transforms, "graticule", _ => {
    const lines = (step: unknown) => Array.isArray(step) && step.length === 2 ? 360 / Math.abs(+step[0] || 360) + 180 / Math.abs(+step[1] || 180) : 0;
    if (lines(_.step) + lines(_.stepMajor) + lines(_.stepMinor) > 10_000 || (_.precision !== undefined && !(+_.precision >= 0.01)))
      tooLarge("more lines");
  });
  for (const name of ["axisticks", "legendentries"]) guard(transforms, name, _ => tickCount(_));
  // Every item of the marks of a chart is counted against its budget.
  guard(transforms, "datajoin", (_, rows, budget) => charge(budget, rows.length, "more marks"));
}

/**
 * Draws a chart as an SVG string, headless: Vega-Lite compiles the spec, and Vega runs its expressions through an
 * interpreter (no `eval`, no `new Function`), within the budget of the chart ({@link secureVega}), with a loader that reads
 * nothing. Rejects with what refused the chart.
 */
export async function drawChart(job: ChartJob): Promise<string> {
  const sized = { ...job.spec };
  if (sized.width === "container") sized.width = job.width;
  if (sized.height === "container") sized.height = Math.round(job.width * 0.6);
  const spec = inlineData(sized, job.files) as unknown as TopLevelSpec;
  const quiet = logger(0);
  const compiled = compile(spec, { config: job.config as never, logger: quiet as never }).spec;
  // What the dataflow refuses (a value that cannot be parsed, an expression that fails, a limit) is said, not drawn half.
  let failure: unknown = null;
  const capture = logger(1);
  capture.error = (...values: readonly unknown[]) => { failure ??= values[0]; return capture; };
  const budget: Budget = { left: maximumTuples * 4 };
  const view = new View(parse(compiled, undefined, { ast: true }), { expr: budgetedInterpreter(budget), renderer: "none", loader: refusingLoader, logger: capture });
  (view as unknown as Record<symbol, Budget>)[budgetOf] = budget;
  const refused = () => failure instanceof Error ? failure : new Error(String(failure));
  try {
    let svg: string;
    // A refused transform leaves its marks without data: what failed after it is not the reason.
    try { svg = await view.toSVG(); } catch (error) { throw failure !== null ? refused() : error; }
    if (failure !== null) throw refused();
    return svg;
  } finally {
    view.finalize();
  }
}
