/** How a log row is coloured and filtered: errors (and fatal), warnings, and everything quieter. */
export type LogTone = "info" | "warning" | "error";

export function logLevelTone(level: string): LogTone {
  const name = level.toLowerCase();
  return name.startsWith("err") || name.startsWith("fatal") || name.startsWith("crit") ? "error" : name.startsWith("warn") ? "warning" : "info";
}

const pad = (value: number, length = 2) => String(value).padStart(length, "0");

/** The local time of day of a log row, to the millisecond; the source text is kept when it is not a date. */
export function logTime(timestamp: string): { label: string; title: string } {
  const date = new Date(timestamp);
  if (!Number.isFinite(date.getTime())) return { label: timestamp, title: timestamp };
  return { label: `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}.${pad(date.getMilliseconds(), 3)}`,
    title: `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}` };
}

/** A captured row as the filters read it; `feature` is absent from the rows of an older host. */
export interface LogRowFields { readonly level: string; readonly logger: string; readonly text: string; readonly feature?: string | null }

/** What the viewer shows: a level tone or all, a feature id or all (""), and words to find in the message or the logger. */
export interface LogFilter { readonly level: "all" | LogTone; readonly feature: string; readonly query: string }

/** The feature of a row: the one the host gave, else "system", as the host files a logger it knows nothing of. */
export const logRowFeature = (row: LogRowFields) => row.feature || "system";

/** The rows a filter keeps, in their order. */
export function filterLogRows<T extends LogRowFields>(rows: readonly T[], filter: LogFilter): T[] {
  const text = filter.query.trim().toLowerCase();
  return rows.filter(row => (filter.level === "all" || logLevelTone(row.level) === filter.level)
    && (!filter.feature || logRowFeature(row) === filter.feature)
    && (!text || row.text.toLowerCase().includes(text) || row.logger.toLowerCase().includes(text)));
}

/** How many rows each feature has, to show beside its name. */
export function countLogFeatures(rows: readonly LogRowFields[]): ReadonlyMap<string, number> {
  const counts = new Map<string, number>();
  for (const row of rows) counts.set(logRowFeature(row), (counts.get(logRowFeature(row)) ?? 0) + 1);
  return counts;
}

/** A feature of the log and its own level, null when it takes the level of every feature. */
export interface LogFeatureLevel { readonly id: string; readonly level: string | null }

/**
 * The levels to save after one choice: `feature` null changes the level of every feature, else the one of that
 * feature; a feature given the level of every feature (`null`) has none of its own.
 */
export function changeLogLevels(current: { level: string; features: readonly LogFeatureLevel[] }, feature: string | null, level: string | null)
  : { level: string; features: LogFeatureLevel[] } {
  if (feature === null) return { level: level ?? current.level, features: current.features.filter(item => item.level !== null).map(item => ({ id: item.id, level: item.level })) };
  return { level: current.level, features: current.features.map(item => ({ id: item.id, level: item.id === feature ? level : item.level })).filter(item => item.level !== null) };
}
