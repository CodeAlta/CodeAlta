import { useShellLanguage } from "../shellLanguage";

/** A point of a sparkline: a number, or null for a gap. */
export type SparkValue = number | null;

/** The path (SVG `d`) of a line through the values, and the last point; empty when no value is a number. */
export type SparklineShape = Readonly<{ line: string; area: string; last: readonly [number, number] | null }>;

/**
 * Lays values out in a box of `width` by `height` (a margin of `pad` on each side): the first at the left, the
 * last at the right, the lowest at the bottom and the highest at the top. A gap (null, NaN) breaks the line; a
 * series that never moves is a line across the middle.
 */
export function sparklineShape(values: readonly SparkValue[], width: number, height: number, pad = 2): SparklineShape {
  const points = values.map(value => typeof value === "number" && Number.isFinite(value) ? value : null);
  const numbers = points.filter((value): value is number => value !== null);
  if (numbers.length === 0) return { line: "", area: "", last: null };
  const low = Math.min(...numbers), high = Math.max(...numbers), range = high - low;
  const x = (index: number) => points.length === 1 ? width / 2 : pad + (index * (width - 2 * pad)) / (points.length - 1);
  const y = (value: number) => range === 0 ? height / 2 : pad + (1 - (value - low) / range) * (height - 2 * pad);
  const at = (value: number) => Number(value.toFixed(2));
  let line = "", area = "", run: [number, number][] = [], last: [number, number] | null = null;
  const flush = () => {
    if (run.length === 0) return;
    line += run.map(([px, py], index) => `${index === 0 ? "M" : "L"}${at(px)} ${at(py)}`).join("");
    if (run.length > 1) area += `${run.map(([px, py], index) => `${index === 0 ? "M" : "L"}${at(px)} ${at(py)}`).join("")}L${at(run[run.length - 1][0])} ${height}L${at(run[0][0])} ${height}Z`;
    run = [];
  };
  points.forEach((value, index) => {
    if (value === null) { flush(); return; }
    last = [x(index), y(value)];
    run.push(last);
  });
  flush();
  return { line, area, last };
}

/** The properties of `Sparkline`. */
export type SparklineProps = Readonly<{
  values: readonly SparkValue[];
  /** Width in pixels (default 80). */
  width?: number;
  /** Height in pixels (default 24). */
  height?: number;
  /** Says what the line shows to a screen reader; without it the sparkline is decoration. */
  ariaLabel?: string;
  /** Fills the area under the line. */
  filled?: boolean;
  className?: string;
}>;

/**
 * A line of a few dozen points as one SVG path, light enough for each cell of a table or tile. It has no
 * axis and no tooltip; its color is the text color of its place unless the page sets `--sparkline-color`.
 */
export function Sparkline({ values, width = 80, height = 24, ariaLabel, filled, className }: SparklineProps) {
  const shape = sparklineShape(values, width, height);
  return <svg className={`sparkline${className ? ` ${className}` : ""}`} width={width} height={height} viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none"
    role={ariaLabel ? "img" : undefined} aria-label={ariaLabel} aria-hidden={ariaLabel ? undefined : true} focusable="false">
    {filled && shape.area && <path className="sparkline-area" d={shape.area} />}
    {shape.line && <path className="sparkline-line" d={shape.line} />}
    {shape.last && <circle className="sparkline-last" cx={shape.last[0]} cy={shape.last[1]} r={2} />}
  </svg>;
}

/** Whether a change is good or bad news; most numbers are `up` good, but a count of errors or of seconds is the other way. */
export type ChangeSense = "up" | "down" | "neutral";

/** How a change looks: its direction, its tone and the text of it. */
export type ChangeView = Readonly<{ direction: "up" | "down" | "flat"; tone: "good" | "bad" | "neutral"; text: string }>;

/**
 * The change from a compared value to the current one. `text` has its sign (`+12%`, `−3.5%`, `0%`) so the
 * direction is never only a color. Null when there is nothing to compare: the compared value is zero or missing.
 */
export function changeView(current: number, compared: number | null | undefined, goodWhen: ChangeSense, locale: string): ChangeView | null {
  if (compared === null || compared === undefined || !Number.isFinite(compared) || !Number.isFinite(current) || compared === 0) return null;
  const ratio = (current - compared) / Math.abs(compared);
  const direction = Math.abs(ratio) < 0.0005 ? "flat" : ratio > 0 ? "up" : "down";
  const tone = direction === "flat" || goodWhen === "neutral" ? "neutral" : direction === goodWhen ? "good" : "bad";
  const digits = Math.abs(ratio) >= 0.1 ? 0 : 1;
  const number = new Intl.NumberFormat(locale, { style: "percent", maximumFractionDigits: digits, signDisplay: "exceptZero" }).format(ratio);
  return { direction, tone, text: number };
}

/** The properties of `StatTile`. */
export type StatTileProps = Readonly<{
  label: string;
  /** The number as it should read (formatted by the caller: units, grouping). */
  value: string;
  /** The raw value, for the comparison with `compared`. */
  current?: number;
  /** The value of the compared period; the tile then shows the change. */
  compared?: number | null;
  /** Which direction is good news (default `up`). */
  goodWhen?: ChangeSense;
  /** What `compared` is, spoken with the change: "previous period" by default. */
  comparedLabel?: string;
  /** The points of the line under the number. */
  trend?: readonly SparkValue[];
}>;

/** A number with its label, its change against a compared period (an arrow and a sign, as well as a tone) and a sparkline. */
export function StatTile({ label, value, current, compared, goodWhen = "up", comparedLabel, trend }: StatTileProps) {
  const { t, locale } = useShellLanguage();
  const change = current === undefined ? null : changeView(current, compared, goodWhen, locale);
  const arrow = change?.direction === "up" ? "▲" : change?.direction === "down" ? "▼" : "▬";
  const spoken = change && t("{change} compared with {period}", { change: change.text, period: comparedLabel ?? t("the previous period") });
  return <div className="stat-tile">
    <span className="stat-tile-label">{label}</span>
    <span className="stat-tile-value">{value}</span>
    {change && <span className="stat-tile-change" data-tone={change.tone} data-direction={change.direction}>
      <span aria-hidden="true">{arrow} {change.text}</span><span className="chart-sr-only">{spoken}</span></span>}
    {trend && trend.length > 0 && <Sparkline values={trend} className="stat-tile-trend" filled />}
  </div>;
}
