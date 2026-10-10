import { useMemo, useRef, useState, type KeyboardEvent } from "react";
import { useShellLanguage } from "../shellLanguage";
import { calendarLayout, neighbor, weekdayHourLayout, type DayValue, type HeatCell } from "./heat";

// The two heat maps are drawn here, not by ECharts: a square per day or per hour is a few hundred SVG
// elements, each of which can take the focus, be read by a screen reader and be clicked, which a chart
// drawn into one picture cannot offer.

type Label = Readonly<{ at: number; text: string }>;

type HeatGridProps = Readonly<{
  cells: readonly HeatCell[];
  columns: number;
  rows: number;
  topLabels: readonly Label[];
  leftLabels: readonly Label[];
  ariaLabel: string;
  onSelect?: (cell: HeatCell) => void;
  className: string;
}>;

const size = 13, gap = 3, step = size + gap, leftMargin = 30, topMargin = 16;

function HeatGrid({ cells, columns, rows, topLabels, leftLabels, ariaLabel, onSelect, className }: HeatGridProps) {
  const [current, setCurrent] = useState<string | null>(null);
  const svg = useRef<SVGSVGElement>(null);
  const focused = cells.find(cell => cell.key === current) ?? cells.find(cell => cell.value !== null) ?? cells[0];
  const move = (event: KeyboardEvent<SVGRectElement>, cell: HeatCell) => {
    if (event.key === "Enter" || event.key === " ") {
      if (onSelect) { event.preventDefault(); onSelect(cell); }
      return;
    }
    const next = neighbor(cells, cell, event.key);
    if (next === cell && !["Home", "End"].includes(event.key)) return;
    event.preventDefault();
    setCurrent(next.key);
    svg.current?.querySelector<SVGRectElement>(`[data-key="${CSS.escape(next.key)}"]`)?.focus();
  };
  const width = leftMargin + columns * step - gap, height = topMargin + rows * step - gap;
  return <svg ref={svg} className={`heat ${className}`} role="group" aria-label={ariaLabel} width={width} height={height} viewBox={`0 0 ${width} ${height}`}>
    {topLabels.map(label => <text key={`c${label.at}`} className="heat-label" x={leftMargin + label.at * step} y={topMargin - 5}>{label.text}</text>)}
    {leftLabels.map(label => <text key={`r${label.at}`} className="heat-label" x={0} y={topMargin + label.at * step + size - 2}>{label.text}</text>)}
    {cells.map(cell => <rect key={cell.key} data-key={cell.key} className="heat-cell" data-level={cell.level} rx={2}
      x={leftMargin + cell.column * step} y={topMargin + cell.row * step} width={size} height={size}
      role={onSelect ? "button" : "img"} aria-label={cell.label} tabIndex={cell === focused ? 0 : -1}
      onFocus={() => setCurrent(cell.key)} onKeyDown={event => move(event, cell)} onClick={onSelect ? () => onSelect(cell) : undefined}>
      <title>{cell.label}</title>
    </rect>)}
  </svg>;
}

/** The properties of `CalendarHeatmap`. */
export type CalendarHeatmapProps = Readonly<{
  /** The days that have a value; a day not listed is empty. Dates are `YYYY-MM-DD`. */
  data: readonly DayValue[];
  /** The first day shown and the last (`YYYY-MM-DD`). */
  from: string;
  to: string;
  /** What the map is, for a screen reader. */
  ariaLabel: string;
  /** Writes the label of a day, shown on hover and read aloud; the date and the value by default. */
  describe?: (date: string, value: number | null) => string;
  /** A click, or Enter or Space, on a day. */
  onSelect?: (date: string, value: number | null) => void;
  /** The day a week starts on, 0 for Sunday (default 1, Monday). */
  weekStart?: number;
}>;

/**
 * A year of days as squares, a column per week, from faint to strong in the ramp of the page. One tab stop; the
 * arrow keys move between days, Enter or Space selects one. Draws plain SVG: it costs no chart library.
 * Throws `RangeError` when `from` or `to` is not a date or `to` is before `from`.
 */
export function CalendarHeatmap({ data, from, to, ariaLabel, describe, onSelect, weekStart = 1 }: CalendarHeatmapProps) {
  const { locale } = useShellLanguage();
  const layout = useMemo(() => {
    const day = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeZone: "UTC" });
    const month = new Intl.DateTimeFormat(locale, { month: "short", timeZone: "UTC" });
    const write = describe ?? ((date: string, value: number | null) => `${day.format(new Date(`${date}T00:00:00Z`))}: ${(value ?? 0).toLocaleString(locale)}`);
    return calendarLayout(from, to, data, weekStart, write, index => month.format(new Date(Date.UTC(2024, index, 1))));
  }, [data, from, to, weekStart, locale, describe]);
  const dayNames = useMemo(() => {
    const formatter = new Intl.DateTimeFormat(locale, { weekday: "short", timeZone: "UTC" });
    return [0, 1, 2, 3, 4, 5, 6].map(row => formatter.format(new Date(Date.UTC(2024, 0, 7 + ((weekStart + row) % 7)))));
  }, [locale, weekStart]);
  const valueOf = new Map(layout.cells.map(cell => [cell.key, cell.value]));
  return <HeatGrid cells={layout.cells} columns={layout.columns} rows={7} className="heat-calendar" ariaLabel={ariaLabel}
    topLabels={layout.months.map(label => ({ at: label.column, text: label.text }))}
    leftLabels={[1, 3, 5].map(row => ({ at: row, text: dayNames[row] }))}
    onSelect={onSelect ? cell => onSelect(cell.key, valueOf.get(cell.key) ?? null) : undefined} />;
}

/** The properties of `WeekdayHourHeatmap`. */
export type WeekdayHourHeatmapProps = Readonly<{
  /** Seven rows (the first being `weekStart`) of 24 numbers, one per hour of the day. */
  matrix: readonly (readonly number[])[];
  /** What the map is, for a screen reader. */
  ariaLabel: string;
  /** Writes the label of a square, shown on hover and read aloud; the day, the hour and the value by default. */
  describe?: (weekday: number, hour: number, value: number) => string;
  /** A click, or Enter or Space, on a square: its row (0 to 6, from `weekStart`), its hour and its value. */
  onSelect?: (weekday: number, hour: number, value: number) => void;
  /** The day a week starts on, 0 for Sunday (default 1, Monday). */
  weekStart?: number;
}>;

/** The weekday by hour of the day, in the same squares and with the same keys as `CalendarHeatmap`. */
export function WeekdayHourHeatmap({ matrix, ariaLabel, describe, onSelect, weekStart = 1 }: WeekdayHourHeatmapProps) {
  const { locale } = useShellLanguage();
  const dayNames = useMemo(() => {
    const formatter = new Intl.DateTimeFormat(locale, { weekday: "short", timeZone: "UTC" });
    return [0, 1, 2, 3, 4, 5, 6].map(row => formatter.format(new Date(Date.UTC(2024, 0, 7 + ((weekStart + row) % 7)))));
  }, [locale, weekStart]);
  const cells = useMemo(() => {
    const write = describe ?? ((row: number, hour: number, value: number) => `${dayNames[row]} ${String(hour).padStart(2, "0")}:00: ${value.toLocaleString(locale)}`);
    return weekdayHourLayout(matrix, write);
  }, [matrix, describe, dayNames, locale]);
  return <HeatGrid cells={cells} columns={24} rows={7} className="heat-weekday" ariaLabel={ariaLabel}
    topLabels={[0, 6, 12, 18].map(hour => ({ at: hour, text: String(hour).padStart(2, "0") }))}
    leftLabels={[0, 2, 4, 6].map(row => ({ at: row, text: dayNames[row] }))}
    onSelect={onSelect ? cell => onSelect(cell.row, cell.column, cell.value ?? 0) : undefined} />;
}
