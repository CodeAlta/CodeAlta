/** A day of a calendar heat map: its date as `YYYY-MM-DD` and its value. */
export type DayValue = Readonly<{ date: string; value: number }>;

/** The number of steps of the ramp of a heat map; level 0 is "nothing". */
export const heatLevels = 5;

const dayMilliseconds = 86_400_000;

/** A date as `YYYY-MM-DD`, read as a day of the calendar (no time zone). Null for text that is not a real date. */
export function parseDay(text: string): number | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(text);
  if (!match) return null;
  const time = Date.UTC(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  return formatDay(time) === text ? time : null;
}

/** The day, as `YYYY-MM-DD`, of a UTC time. */
export function formatDay(time: number): string {
  return new Date(time).toISOString().slice(0, 10);
}

/**
 * The level (0 to `heatLevels`) of each value: 0 for nothing, and the others by how the values spread, so that
 * one very busy day does not turn every other day pale. The thresholds are quantiles of the positive values.
 */
export function levelOf(values: readonly number[]): (value: number) => number {
  const positive = values.filter(value => Number.isFinite(value) && value > 0).sort((a, b) => a - b);
  if (positive.length === 0) return () => 0;
  const thresholds = Array.from({ length: heatLevels - 1 }, (_, index) => {
    const position = (positive.length - 1) * ((index + 1) / heatLevels);
    const below = Math.floor(position), above = Math.ceil(position);
    return positive[below] + (positive[above] - positive[below]) * (position - below);
  });
  return value => {
    if (!(value > 0)) return 0;
    let level = 1;
    for (const threshold of thresholds) if (value > threshold) level++;
    return level;
  };
}

/** A square of a heat map: where it is, what it stands for and how strong it is. */
export type HeatCell = Readonly<{ key: string; column: number; row: number; value: number | null; level: number; label: string }>;

/** A month name placed over the column where the month starts. */
export type MonthLabel = Readonly<{ column: number; text: string }>;

/** The squares of the days from `from` to `to` (both included, `YYYY-MM-DD`), in columns of weeks, and the months over them. */
export type CalendarLayout = Readonly<{ cells: readonly HeatCell[]; columns: number; months: readonly MonthLabel[] }>;

/**
 * Lays out a calendar heat map: one column per week, one row per weekday, the week starting on `weekStart`
 * (0 = Sunday, 1 = Monday). `describe` writes the label of a day (its date and value in words).
 * Throws `RangeError` when a date is not a real date or `to` is before `from`.
 */
export function calendarLayout(from: string, to: string, data: readonly DayValue[], weekStart: number,
  describe: (date: string, value: number | null) => string, monthName: (month: number) => string): CalendarLayout {
  const first = parseDay(from), last = parseDay(to);
  if (first === null || last === null || last < first) throw new RangeError("A calendar needs two dates, the second not before the first.");
  const values = new Map<string, number>();
  for (const item of data) if (Number.isFinite(item.value)) values.set(item.date, (values.get(item.date) ?? 0) + item.value);
  const level = levelOf([...values.values()]);
  const cells: HeatCell[] = [], months: MonthLabel[] = [];
  const offset = (new Date(first).getUTCDay() - weekStart + 7) % 7;
  let lastMonth = -1;
  for (let time = first, index = 0; time <= last; time += dayMilliseconds, index++) {
    const date = formatDay(time);
    const position = index + offset;
    const column = Math.floor(position / 7);
    const value = values.get(date) ?? null;
    cells.push({ key: date, column, row: position % 7, value, level: value === null ? 0 : level(value), label: describe(date, value) });
    const month = new Date(time).getUTCMonth();
    if (month !== lastMonth) { months.push({ column, text: monthName(month) }); lastMonth = month; }
  }
  const columns = cells.length === 0 ? 0 : cells[cells.length - 1].column + 1;
  // A name needs two columns: a month that is cut at the start of the range makes room for the next one.
  return { cells, columns, months: months.filter((label, index) => index === months.length - 1 || months[index + 1].column - label.column >= 2) };
}

/** The cell reached from the one at (`column`, `row`) by an arrow key; the same one when there is none that way. */
export function neighbor(cells: readonly HeatCell[], current: HeatCell, key: string): HeatCell {
  const at = (column: number, row: number) => cells.find(cell => cell.column === column && cell.row === row);
  switch (key) {
    case "ArrowLeft": return at(current.column - 1, current.row) ?? current;
    case "ArrowRight": return at(current.column + 1, current.row) ?? current;
    case "ArrowUp": return at(current.column, current.row - 1) ?? current;
    case "ArrowDown": return at(current.column, current.row + 1) ?? current;
    case "Home": return cells.find(cell => cell.row === current.row) ?? current;
    case "End": return [...cells].reverse().find(cell => cell.row === current.row) ?? current;
    default: return current;
  }
}

/** The squares of a weekday-by-hour heat map from 168 numbers (7 rows of 24 hours, the first row being `weekStart`). */
export function weekdayHourLayout(matrix: readonly (readonly number[])[], describe: (weekday: number, hour: number, value: number) => string): readonly HeatCell[] {
  const level = levelOf(matrix.flat());
  const cells: HeatCell[] = [];
  for (let row = 0; row < 7; row++) for (let hour = 0; hour < 24; hour++) {
    const value = matrix[row]?.[hour] ?? 0;
    cells.push({ key: `${row}-${hour}`, column: hour, row, value, level: level(value), label: describe(row, hour, value) });
  }
  return cells;
}
