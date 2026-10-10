import type { Comparison, Frequency, Origin, RequestFrequency, StatisticsFilter, StatisticsRequest, ToolKind, WeekDayName } from "./types";

// The frame of the canvas: the bar every page shares (period, frequency, comparison, filters) and the page. It is a plain
// value, changed by `frameReducer`, written to and read from a short query string so that a reload keeps it, and turned into
// the request of every question by `requestOf`.

/** The pages of the canvas, in the order of its tabs. */
export const pageIds = ["overview", "activity", "models", "cost", "tools", "prompts", "agents", "code", "projects", "sessions", "health"] as const;
/** One page of the canvas. */
export type PageId = typeof pageIds[number];

/** The periods the bar offers by name. */
export const periodPresets = ["today", "7d", "30d", "90d", "month", "last-month", "year", "all"] as const;
/** A period the bar offers by name. */
export type PeriodPreset = typeof periodPresets[number];

/** A period: one of the names, or two dates (`yyyy-MM-dd`). */
export type PeriodChoice = Readonly<{ kind: "preset"; preset: PeriodPreset } | { kind: "custom"; from: string; to: string }>;

/** The filters of the frame, in the order of the "+ Filter" menu. */
export const filterKeys = ["space", "project", "provider", "model", "effort", "origin", "toolKind"] as const;
/** One kind of filter. */
export type FilterKey = typeof filterKeys[number];
/** The value of a filter, and the name it is shown with. */
export type FilterEntry = Readonly<{ value: string; label?: string }>;
/** The filters in force: at most one value for each kind. */
export type Filters = Readonly<Partial<Record<FilterKey, FilterEntry>>>;

/** What a block with a unit switch shows. */
export type UsageUnit = "tokens" | "requests" | "time";
/** How the activity chart of the Overview is stacked. */
export type StackBy = "provider" | "project" | "model";

/** The choices of the blocks that have a control of their own, kept with the frame. */
export type FrameView = Readonly<{ stackBy: StackBy; unit: UsageUnit }>;

/** Everything the bar and the tabs hold. */
export type Frame = Readonly<{
  page: PageId;
  period: PeriodChoice;
  frequency: RequestFrequency;
  comparison: Comparison;
  filters: Filters;
  view: FrameView;
}>;

/** The origins and kinds of tool a filter can take. */
export const originValues: readonly Origin[] = ["you", "agent", "automation", "reminder"];
/** The kinds of tool a filter can take. */
export const toolKindValues: readonly ToolKind[] = ["files", "search", "shell", "web", "alta", "mcp", "skill", "other"];

/** The frame a canvas opens with: the last 30 days, the Overview, no comparison. */
export const defaultFrame: Frame = Object.freeze<Frame>({
  page: "overview", period: { kind: "preset", preset: "30d" }, frequency: "auto", comparison: "none", filters: {}, view: { stackBy: "provider", unit: "tokens" },
});

/** What changes a frame. */
export type FrameAction =
  | Readonly<{ type: "page"; page: PageId }>
  | Readonly<{ type: "period"; period: PeriodChoice }>
  | Readonly<{ type: "frequency"; frequency: RequestFrequency }>
  | Readonly<{ type: "comparison"; comparison: Comparison }>
  | Readonly<{ type: "filter"; key: FilterKey; entry: FilterEntry | null }>
  | Readonly<{ type: "view"; view: Partial<FrameView> }>
  | Readonly<{ type: "reset"; frame: Frame }>;

/**
 * The next frame. A change of period resets a frequency that no longer fits it to `auto`; a filter on the same value changes
 * nothing, and the same frame is returned (so that the state does not change and nothing is asked again).
 */
export function frameReducer(frame: Frame, action: FrameAction): Frame {
  switch (action.type) {
    case "page": return frame.page === action.page ? frame : { ...frame, page: action.page };
    case "period": {
      if (samePeriod(frame.period, action.period)) return frame;
      const days = knownDays(action.period);
      const frequency = days !== null && frame.frequency !== "auto" && !allowedFrequencies(days).includes(frame.frequency) ? "auto" : frame.frequency;
      return { ...frame, period: action.period, frequency };
    }
    case "frequency": return frame.frequency === action.frequency ? frame : { ...frame, frequency: action.frequency };
    case "comparison": return frame.comparison === action.comparison ? frame : { ...frame, comparison: action.comparison };
    case "filter": {
      const current = frame.filters[action.key];
      if (action.entry === null) {
        if (!current) return frame;
        const { [action.key]: _removed, ...rest } = frame.filters;
        return { ...frame, filters: rest };
      }
      if (current && current.value === action.entry.value && current.label === action.entry.label) return frame;
      return { ...frame, filters: { ...frame.filters, [action.key]: action.entry } };
    }
    case "view": return { ...frame, view: { ...frame.view, ...action.view } };
    case "reset": return action.frame;
  }
}

/** Whether two periods are the same. */
export function samePeriod(left: PeriodChoice, right: PeriodChoice): boolean {
  return left.kind === "preset" ? right.kind === "preset" && left.preset === right.preset : right.kind === "custom" && left.from === right.from && left.to === right.to;
}

/** The period as the questions take it: `30d`, `last-month`, `all`, or `from..to`. */
export function periodText(period: PeriodChoice): string {
  return period.kind === "preset" ? period.preset : `${period.from}..${period.to}`;
}

/** The number of days of a period that is known without asking (everything but `all`); null for `all`. */
export function knownDays(period: PeriodChoice, today?: string): number | null {
  if (period.kind === "custom") return dayDistance(period.from, period.to) + 1;
  switch (period.preset) {
    case "today": return 1;
    case "7d": return 7;
    case "30d": return 30;
    case "90d": return 90;
    case "all": return null;
    case "month": case "last-month": case "year": {
      if (!today) return period.preset === "year" ? 365 : 30;
      const range = presetRange(period.preset, today);
      return range ? dayDistance(range.from, range.to) + 1 : null;
    }
  }
}

/** The days a period covers, `yyyy-MM-dd`; null for `all`, which depends on the first day with data. */
export function resolvePeriod(period: PeriodChoice, today: string): Readonly<{ from: string; to: string }> | null {
  if (period.kind === "custom") return { from: period.from, to: period.to };
  return presetRange(period.preset, today);
}

// No period the bar offers by name is a week: none depends on the first day of the week.
function presetRange(preset: PeriodPreset, today: string): Readonly<{ from: string; to: string }> | null {
  switch (preset) {
    case "today": return { from: today, to: today };
    case "7d": return { from: addDays(today, -6), to: today };
    case "30d": return { from: addDays(today, -29), to: today };
    case "90d": return { from: addDays(today, -89), to: today };
    case "month": return { from: `${today.slice(0, 7)}-01`, to: today };
    case "last-month": {
      const first = addMonths(`${today.slice(0, 7)}-01`, -1);
      return { from: first, to: addDays(addMonths(first, 1), -1) };
    }
    case "year": return { from: `${today.slice(0, 4)}-01-01`, to: today };
    case "all": return null;
  }
}

/** The frequency `auto` picks: hours for a day, days up to 90 days, weeks up to a year, months beyond. */
export function autoFrequency(days: number): Frequency {
  return days <= 1 ? "hour" : days <= 90 ? "day" : days <= 366 ? "week" : "month";
}

/** The frequency in force: the one chosen, or the one `auto` picks for `days` (`day` when the length is not known). */
export function resolveFrequency(frequency: RequestFrequency, days: number | null): Frequency {
  return frequency !== "auto" ? frequency : days === null ? "day" : autoFrequency(days);
}

/** The frequencies worth offering for a period of `days` days: not hours over a month, not days over two years, not a month in a week. */
export function allowedFrequencies(days: number | null): readonly Frequency[] {
  if (days === null) return ["day", "week", "month", "year"];
  return (["hour", "day", "week", "month", "year"] as const).filter(frequency => {
    switch (frequency) {
      case "hour": return days <= 31;
      case "day": return days <= 731;
      case "week": return days >= 7;
      case "month": return days >= 28;
      case "year": return days >= 365;
    }
  });
}

/** The number of buckets a period of `days` days has at a frequency, at most. */
export function bucketCount(days: number, frequency: Frequency): number {
  switch (frequency) {
    case "hour": return days * 24;
    case "day": return days;
    case "week": return Math.ceil(days / 7) + 1;
    case "month": return Math.ceil(days / 28) + 1;
    case "year": return Math.ceil(days / 365) + 1;
  }
}

const weekDays: readonly WeekDayName[] = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

/** The name of a day of the week, 0 for Sunday, as the host reads it. */
export const weekDayName = (index: number): WeekDayName => weekDays[((index % 7) + 7) % 7];

/** The day of the week a name of the host stands for, 0 for Sunday; undefined when it is not the name of a day. */
export function weekDayIndex(name: string | null | undefined): number | undefined {
  const index = name ? weekDays.findIndex(day => day.toLowerCase() === name.toLowerCase()) : -1;
  return index < 0 ? undefined : index;
}

/** The filters of a frame as the request writes them. */
export function filterOf(filters: Filters): StatisticsFilter {
  const result: Record<string, string> = {};
  for (const key of filterKeys) {
    const entry = filters[key];
    if (entry) result[key] = entry.value;
  }
  return result as StatisticsFilter;
}

/** The request of a page: the period, the frequency, the comparison and the filters of the frame, and what the page adds. */
export function requestOf(frame: Frame, weekStart: number, extra: Partial<StatisticsRequest> = {}): StatisticsRequest {
  const filter = filterOf(frame.filters);
  return {
    period: periodText(frame.period), frequency: frame.frequency, comparison: frame.comparison,
    ...(Object.keys(filter).length > 0 ? { filter } : {}), weekStart: weekDayName(weekStart), ...extra,
  };
}

/** The frame a canvas opens with when the window shows a space or the menu of a project opened it. */
export function initialFrame(context: Readonly<{ spaceId?: string | null; spaceName?: string | null; projectId?: string | null; projectName?: string | null }>): Frame {
  let filters: Filters = {};
  if (context.projectId) filters = { ...filters, project: { value: context.projectId, label: context.projectName ?? undefined } };
  else if (context.spaceId) filters = { ...filters, space: { value: context.spaceId, label: context.spaceName ?? undefined } };
  return { ...defaultFrame, filters };
}

// The frame as a query string: `page=cost&period=30d&freq=week&cmp=previousPeriod&project=<id>&project.label=Name&stack=model&unit=time`.

/** Writes a frame as a query string. */
export function encodeFrame(frame: Frame): string {
  const query = new URLSearchParams();
  query.set("page", frame.page);
  query.set("period", periodText(frame.period));
  query.set("freq", frame.frequency);
  query.set("cmp", frame.comparison);
  for (const key of filterKeys) {
    const entry = frame.filters[key];
    if (!entry) continue;
    query.set(key, entry.value);
    if (entry.label) query.set(`${key}.label`, entry.label);
  }
  query.set("stack", frame.view.stackBy);
  query.set("unit", frame.view.unit);
  return query.toString();
}

const dateText = /^\d{4}-\d{2}-\d{2}$/;
const isOneOf = <T extends string>(values: readonly T[], value: string | null): value is T => value !== null && (values as readonly string[]).includes(value);

/** Reads a frame from a query string; what is missing or not valid keeps the value of `fallback`, so a damaged text never fails. */
export function decodeFrame(text: string | null | undefined, fallback: Frame = defaultFrame): Frame {
  if (!text) return fallback;
  let query: URLSearchParams;
  try { query = new URLSearchParams(text); } catch { return fallback; }
  const page = query.get("page");
  const periodValue = query.get("period");
  let period = fallback.period;
  if (isOneOf(periodPresets, periodValue)) period = { kind: "preset", preset: periodValue };
  else if (periodValue && periodValue.includes("..")) {
    const [from, to] = periodValue.split("..");
    if (dateText.test(from) && dateText.test(to) && from <= to) period = { kind: "custom", from, to };
  }
  const frequency = query.get("freq");
  const comparison = query.get("cmp");
  const filters: Record<string, FilterEntry> = {};
  for (const key of filterKeys) {
    const value = query.get(key);
    if (!value || value.length > 300) continue;
    if (key === "origin" && !isOneOf(originValues, value)) continue;
    if (key === "toolKind" && !isOneOf(toolKindValues, value)) continue;
    const label = query.get(`${key}.label`);
    filters[key] = label ? { value, label: label.slice(0, 200) } : { value };
  }
  const stack = query.get("stack"), unit = query.get("unit");
  return {
    page: isOneOf(pageIds, page) ? page : fallback.page,
    period,
    frequency: isOneOf(["auto", "hour", "day", "week", "month", "year"] as const, frequency) ? frequency : fallback.frequency,
    comparison: isOneOf(["none", "previousPeriod", "samePeriodLastYear"] as const, comparison) ? comparison : fallback.comparison,
    filters,
    view: {
      stackBy: isOneOf(["provider", "project", "model"] as const, stack) ? stack : fallback.view.stackBy,
      unit: isOneOf(["tokens", "requests", "time"] as const, unit) ? unit : fallback.view.unit,
    },
  };
}

// Dates are plain `yyyy-MM-dd` text computed in UTC, so no time zone or daylight saving moves a day.

/** The number of days from `from` to `to` (negative when `to` is first). */
export function dayDistance(from: string, to: string): number {
  return Math.round((Date.parse(`${to}T00:00:00Z`) - Date.parse(`${from}T00:00:00Z`)) / 86_400_000);
}

/** A day moved by a number of days. */
export function addDays(day: string, days: number): string {
  return new Date(Date.parse(`${day}T00:00:00Z`) + days * 86_400_000).toISOString().slice(0, 10);
}

/** A day moved by a number of months, clamped to the length of the month. */
export function addMonths(day: string, months: number): string {
  const [year, month, date] = day.split("-").map(Number);
  const moved = new Date(Date.UTC(year, month - 1 + months, 1));
  const last = new Date(Date.UTC(moved.getUTCFullYear(), moved.getUTCMonth() + 1, 0)).getUTCDate();
  moved.setUTCDate(Math.min(date, last));
  return moved.toISOString().slice(0, 10);
}

/** Today as `yyyy-MM-dd` in the time zone of the page. */
export function localToday(now: Date = new Date()): string {
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
}

/** The first day of the week of a locale, 0 for Sunday; Monday when the browser cannot tell. */
export function firstDayOfWeek(locale: string): number {
  try {
    const info = (new Intl.Locale(locale) as unknown as { getWeekInfo?: () => { firstDay: number }; weekInfo?: { firstDay: number } });
    const first = info.getWeekInfo?.().firstDay ?? info.weekInfo?.firstDay;
    if (typeof first === "number") return first % 7;
  } catch { /* an unknown locale */ }
  return 1;
}
