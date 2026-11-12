import { divergingVariables, fallbackSeries, rampVariables, seriesColorCount, seriesVariable, sequentialRamp } from "./palette";

/** What a chart takes from the page: every color it draws with and the font of its text, resolved to plain colors. */
export type ChartTokens = Readonly<{
  dark: boolean;
  fontFamily: string;
  text: string;
  muted: string;
  axis: string;
  grid: string;
  surface: string;
  tooltipBackground: string;
  tooltipBorder: string;
  series: readonly string[];
  ramp: readonly string[];
  diverging: readonly [string, string, string];
}>;

const fallbackText = { dark: "#f6f7f9", light: "#1c2127" };
const fallbackMuted = { dark: "#abb3bf", light: "#5f6b7c" };
const fallbackSurface = { dark: "#252a31", light: "#ffffff" };

/**
 * Reads the tokens of a chart from variables. `read` answers the plain color of a variable (`--chart-1`), or an
 * empty string when the page does not define it; a missing variable falls back to Blueprint's own color for the
 * theme, so a chart is readable on a page that has no stylesheet of the application.
 */
export function chartTokens(read: (variable: string) => string, dark: boolean, fontFamily = "sans-serif"): ChartTokens {
  const theme = dark ? "dark" : "light";
  const color = (variable: string, fallback: string) => read(variable).trim() || fallback;
  const surface = color("--chart-surface", fallbackSurface[theme]);
  const series = Array.from({ length: seriesColorCount }, (_, index) => color(seriesVariable(index), fallbackSeries[theme][index]));
  const fallbackRamp = sequentialRamp(series[0], surface, rampVariables.length);
  const ramp = rampVariables.map((variable, index) => color(variable, fallbackRamp[index]));
  const [low, mid, high] = divergingVariables;
  return {
    dark, fontFamily,
    text: color("--chart-text", fallbackText[theme]),
    muted: color("--chart-muted", fallbackMuted[theme]),
    axis: color("--chart-axis", dark ? "#5f6b7c" : "#abb3bf"),
    grid: color("--chart-grid", dark ? "#383e47" : "#e5e8eb"),
    surface,
    tooltipBackground: color("--chart-tooltip-bg", surface),
    tooltipBorder: color("--chart-tooltip-border", dark ? "#5f6b7c" : "#abb3bf"),
    series, ramp,
    diverging: [color(low, series[0]), color(mid, surface), color(high, series[3])],
  };
}

/** Whether the page is on its dark theme: Blueprint's `bp6-dark` class on the page or an ancestor of the element. */
export const isDarkTheme = (element: Element): boolean => element.closest(".bp6-dark") !== null;

/**
 * Reads the tokens from the page the element is in. A color the page writes with a function the chart library
 * does not parse (`color-mix`, `oklch`) is read back through the browser as a plain `#rrggbb` or `rgba()` color.
 */
export function readChartTokens(element: HTMLElement): ChartTokens {
  return readColorVariables(element, read => chartTokens(read, isDarkTheme(element), getComputedStyle(element).fontFamily));
}

/**
 * Gives `use` a reader of the colors of variables as the element sees them, as plain `#rrggbb` or `rgba()` colors (an
 * empty string for a variable the page does not define), and returns what it returns.
 */
export function readColorVariables<T>(element: HTMLElement, use: (read: (variable: string) => string) => T): T {
  const probe = element.ownerDocument.createElement("span");
  probe.style.cssText = "position:absolute;visibility:hidden;pointer-events:none;width:0;height:0";
  element.appendChild(probe);
  const canvas = element.ownerDocument.createElement("canvas").getContext("2d", { willReadFrequently: true });
  const view = element.ownerDocument.defaultView ?? window;
  try {
    return use(variable => {
      probe.style.color = "";
      probe.style.color = `var(${variable})`;
      const computed = view.getComputedStyle(probe).color;
      // A variable that is not defined leaves the color inherited: it must read as missing, not as the text color.
      if (view.getComputedStyle(element).getPropertyValue(variable).trim() === "") return "";
      if (!canvas) return computed;
      canvas.fillStyle = "#000000";
      canvas.fillStyle = computed;
      const plain = String(canvas.fillStyle);
      if (/^#[0-9a-f]{6}$|^rgba?\(/iu.test(plain)) return plain;
      // The browser keeps the color of a `color-mix` as `color(srgb …)`, which no chart library reads: the pixel it paints says it.
      canvas.clearRect(0, 0, 1, 1);
      canvas.fillRect(0, 0, 1, 1);
      const [red, green, blue, alpha] = canvas.getImageData(0, 0, 1, 1).data;
      return alpha === 255 ? `#${[red, green, blue].map(value => value.toString(16).padStart(2, "0")).join("")}`
        : `rgba(${red}, ${green}, ${blue}, ${Math.round(alpha / 255 * 1000) / 1000})`;
    });
  } finally {
    probe.remove();
  }
}
