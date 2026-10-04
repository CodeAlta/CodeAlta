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
