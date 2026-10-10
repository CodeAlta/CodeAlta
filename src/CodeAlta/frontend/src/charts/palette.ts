import { parseColor } from "../colorPalette";

/** The hues Blueprint reserves for data, in the order series take them. */
export const seriesHues = ["cerulean", "forest", "gold", "vermilion", "violet", "turquoise", "rose", "lime", "sepia", "indigo"] as const;

/** How many series colors there are; a chart with more series starts again at the first. */
export const seriesColorCount = seriesHues.length;

/** The variable of a series color, counted from 1: `--chart-1` to `--chart-10`. */
export const seriesVariable = (index: number): string => `--chart-${(((index % seriesColorCount) + seriesColorCount) % seriesColorCount) + 1}`;

/** The variables of the five steps of the sequential ramp, from the lightest to the strongest. */
export const rampVariables: readonly string[] = Object.freeze([1, 2, 3, 4, 5].map(step => `--chart-seq-${step}`));

/** The variables of the diverging ramp: one end, the middle, the other end. */
export const divergingVariables: readonly string[] = Object.freeze(["--chart-diverge-low", "--chart-diverge-mid", "--chart-diverge-high"]);

/**
 * The colors a page that defines no variable falls back to: Blueprint's extended colors, the third step on the
 * light theme and the fourth on the dark one (the lighter step keeps its contrast on dark surfaces).
 */
export const fallbackSeries = Object.freeze({
  light: Object.freeze(["#147eb3", "#29a634", "#d1980b", "#d33d17", "#9d3f9d", "#00a396", "#db2c6f", "#8eb125", "#946638", "#7961db"]),
  dark: Object.freeze(["#3fa6da", "#43bf4d", "#f0b726", "#eb6847", "#bd6bbd", "#13c9ba", "#f5498b", "#b6d94c", "#af855a", "#9881f3"]),
}) as Readonly<Record<"light" | "dark", readonly string[]>>;

/** The color of the series with this index among `colors`, wrapping round. */
export const seriesColor = (colors: readonly string[], index: number): string =>
  colors[((index % colors.length) + colors.length) % colors.length];

const channels = (hex: string): [number, number, number] => [1, 3, 5].map(index => parseInt(hex.slice(index, index + 2), 16)) as [number, number, number];
const toHex = (values: readonly number[]) => `#${values.map(value => Math.round(Math.min(255, Math.max(0, value))).toString(16).padStart(2, "0")).join("")}`;

/** The color `amount` (0 to 1) of the way from `from` to `to`, mixed in sRGB; `from` when either is not a hexadecimal color. */
export function mixColors(from: string, to: string, amount: number): string {
  const left = parseColor(from), right = parseColor(to);
  if (!left || !right) return from;
  const [a, b] = [channels(left), channels(right)];
  return toHex(a.map((value, index) => value + (b[index] - value) * amount));
}

/**
 * A ramp of `steps` colors of one hue, from a faint tint of `surface` to the full `hue`; what a heat map uses so
 * that more is darker on the light theme and brighter on the dark one.
 */
export function sequentialRamp(hue: string, surface: string, steps = 5): string[] {
  if (steps < 2) return [hue];
  return Array.from({ length: steps }, (_, index) => mixColors(surface, hue, 0.15 + 0.85 * (index / (steps - 1))));
}

/** The order of the colors a series takes by name: the same name has the same color in every chart that lists the names alike. */
export function colorsByName(names: readonly string[], colors: readonly string[]): Readonly<Record<string, string>> {
  const result: Record<string, string> = {};
  let next = 0;
  for (const name of names) if (!Object.hasOwn(result, name)) result[name] = seriesColor(colors, next++);
  return result;
}
