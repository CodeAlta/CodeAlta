/** A chart option as data: what a plugin writes as JSON in its HTML, and what `Chart` takes. */
export type ChartOption = Readonly<Record<string, unknown>>;

/** The most a JSON option may hold. */
export type ChartOptionLimits = Readonly<{
  /** The deepest nesting of objects and arrays. */
  maxDepth: number;
  /** The most values (numbers, strings, objects, arrays) in the whole option. */
  maxNodes: number;
  /** The longest string, in UTF-16 units. */
  maxStringLength: number;
}>;

/** The limits `sanitizeChartOption` applies when it is given none. */
export const defaultChartOptionLimits: ChartOptionLimits = Object.freeze({ maxDepth: 16, maxNodes: 200_000, maxStringLength: 4096 });

/** The result of `sanitizeChartOption`: a clean copy of the option, or the first reason it was refused. */
export type SanitizedChartOption = Readonly<{ ok: true; option: ChartOption }> | Readonly<{ ok: false; error: string }>;

// Keys that would reach what is not drawing: a link out of the page, markup or style put into the page, a
// hook into the DOM, free-form graphics and the toolbox (its features save files and open windows).
const forbiddenKeys = new Set(["__proto__", "constructor", "prototype", "link", "sublink", "target", "sublinkTarget", "appendTo", "appendToBody",
  "extraCssText", "className", "renderMode", "graphic", "toolbox", "image"]);
const codeLike = /function\b|=>|\breturn\b|\bnew\s+\w|javascript:|<|>|\bon[a-z]+\s*=/i;

const isFormatterKey = (key: string) => key === "formatter" || key.endsWith("Formatter");

/**
 * Checks and copies the option of a chart that comes from JSON (the HTML of a plugin, the card of a turn).
 *
 * An option from code may hold functions; one from JSON is data only, so this refuses everything that is not
 * data: a function, a class instance, a key that is not about drawing (links, a toolbox, free graphics, a
 * class name, an image), a `formatter` that is not a template of `{b}`, `{c}` and plain text (no markup, no
 * script), a symbol or string that loads an image, and an option too large or too deep for the limits.
 * The copy shares nothing with the input.
 */
export function sanitizeChartOption(value: unknown, limits: ChartOptionLimits = defaultChartOptionLimits): SanitizedChartOption {
  let nodes = 0;
  const fail = (path: string, reason: string): never => { throw new OptionRefusal(`${path || "option"}: ${reason}`); };
  const copy = (item: unknown, path: string, depth: number, key: string): unknown => {
    if (++nodes > limits.maxNodes) fail(path, "the option holds too many values");
    if (depth > limits.maxDepth) fail(path, "the option is nested too deeply");
    if (item === null || typeof item === "boolean") return item;
    if (typeof item === "number") return Number.isFinite(item) ? item : null;
    if (typeof item === "string") {
      if (item.length > limits.maxStringLength) fail(path, "a string is too long");
      if (/^\s*image:\/\//i.test(item) || /javascript:/i.test(item)) fail(path, "a string loads an image or script");
      if (isFormatterKey(key) && codeLike.test(item)) fail(path, "a formatter must be a template, not code or markup");
      return item;
    }
    if (typeof item === "function" || typeof item === "symbol" || typeof item === "bigint") return fail(path, `a ${typeof item} is not data`);
    if (Array.isArray(item)) return item.map((entry, index) => copy(entry, `${path}[${index}]`, depth + 1, key));
    if (typeof item === "object") {
      const prototype = Object.getPrototypeOf(item);
      if (prototype !== Object.prototype && prototype !== null) return fail(path, "only plain objects are data");
      const result: Record<string, unknown> = {};
      for (const [name, entry] of Object.entries(item)) {
        if (entry === undefined) continue;
        if (forbiddenKeys.has(name)) fail(`${path}.${name}`, "this key is not allowed in a chart that comes from JSON");
        if (isFormatterKey(name) && typeof entry !== "string") fail(`${path}.${name}`, "a formatter must be a template string");
        result[name] = copy(entry, path ? `${path}.${name}` : name, depth + 1, name);
      }
      return result;
    }
    return fail(path, "this value is not data");
  };
  try {
    if (value === null || typeof value !== "object" || Array.isArray(value)) return { ok: false, error: "option: a chart option is an object" };
    return { ok: true, option: copy(value, "", 0, "") as ChartOption };
  } catch (error) {
    if (error instanceof OptionRefusal) return { ok: false, error: error.message };
    throw error;
  }
}

class OptionRefusal extends Error { }
