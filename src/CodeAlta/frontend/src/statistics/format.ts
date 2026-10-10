import type { BucketInfo, Frequency } from "./types";

// Numbers, durations, dates and costs as the user's locale writes them. Everything here is pure: `createFormatter` closes over a
// locale, and nothing reads the clock or the page.

/** The words a formatter needs from the translation. */
export type FormatterWords = Readonly<{ credits: (amount: string) => string; none: string }>;

/** Writes the numbers of the canvas. */
export type Formatter = Readonly<{
  locale: string;
  /** `1,204`: grouped, no decimals unless the number is small. */
  number(value: number): string;
  /** `1.2M`, `12k`, `340`. */
  compact(value: number): string;
  /** A ratio as a percentage: `42%`, `3.5%`. */
  percent(ratio: number, digits?: number): string;
  /** `850 ms`, `4.2 s`, `4 min 12 s`, `1 h 05`. */
  duration(milliseconds: number): string;
  /** A duration for a mark of an axis, in one unit: `100 ms`, `10 s`, `17 min`, `2.8 h`. */
  durationMark(milliseconds: number): string;
  /** `12 KB`, `1.4 GB`. */
  bytes(value: number): string;
  /** A cost in its unit: `$1,204.50`, `340 AI credits`. */
  cost(unit: string, value: number): string;
  /** A cost for a tile: the number alone for credits (the unit is in the label), dollars without cents above a thousand. */
  costShort(unit: string, value: number): string;
  /** A day without its year: `12 Apr`. */
  day(date: string): string;
  /** A day with its year: `12 Apr 2026`. */
  dayLong(date: string): string;
  /** A range of days, the year written once when it is the same. */
  range(from: string, to: string): string;
  /** A value in the unit a result names: `count`, `ms`, `tokens`, `bytes`, `lines`, `cost`, `usd`, `AI credits`, `ratio`. */
  value(unit: string, value: number): string;
  /** The same for an axis: short. */
  axis(unit: string, value: number): string;
  /** The label of a bucket. */
  bucket(bucket: BucketInfo, frequency: Frequency): string;
  /** The text of a bucket, with its year: for a tooltip and a table. */
  bucketLong(bucket: BucketInfo, frequency: Frequency): string;
}>;

const parts = (start: string) => {
  const [date, time = "00:00"] = start.split("T");
  return { date, time: time.slice(0, 5) };
};

/** Creates the formatter of a locale. */
export function createFormatter(locale: string, words: FormatterWords): Formatter {
  const safe = (() => { try { return Intl.NumberFormat.supportedLocalesOf([locale]).length > 0 ? locale : "en"; } catch { return "en"; } })();
  const grouped = new Intl.NumberFormat(safe, { maximumFractionDigits: 0 });
  const small = new Intl.NumberFormat(safe, { maximumFractionDigits: 2 });
  const compactFormat = new Intl.NumberFormat(safe, { notation: "compact", maximumFractionDigits: 1 });
  const oneDecimal = new Intl.NumberFormat(safe, { minimumFractionDigits: 1, maximumFractionDigits: 1 });
  const dayFormat = new Intl.DateTimeFormat(safe, { day: "numeric", month: "short", timeZone: "UTC" });
  const dayLongFormat = new Intl.DateTimeFormat(safe, { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" });
  const monthFormat = new Intl.DateTimeFormat(safe, { month: "short", timeZone: "UTC" });
  const monthLongFormat = new Intl.DateTimeFormat(safe, { month: "short", year: "numeric", timeZone: "UTC" });
  const usd = new Intl.NumberFormat(safe, { style: "currency", currency: "USD", maximumFractionDigits: 2 });
  const pad = (value: number) => String(value).padStart(2, "0");
  const asDate = (date: string) => new Date(`${date.slice(0, 10)}T00:00:00Z`);
  const dayText = (date: string) => dayFormat.format(asDate(date));
  const dayLongText = (date: string) => dayLongFormat.format(asDate(date));

  const number = (value: number) => !Number.isFinite(value) ? words.none : Math.abs(value) < 100 && !Number.isInteger(value) ? small.format(value) : grouped.format(value);
  const compact = (value: number) => !Number.isFinite(value) ? words.none : Math.abs(value) < 1000 ? small.format(Math.round(value * 10) / 10) : compactFormat.format(value);
  const duration = (milliseconds: number) => {
    if (!Number.isFinite(milliseconds)) return words.none;
    const ms = Math.max(0, milliseconds);
    if (ms < 1000) return `${grouped.format(Math.round(ms))} ms`;
    if (ms < 10_000) return `${oneDecimal.format(ms / 1000)} s`;
    const seconds = Math.round(ms / 1000);
    if (seconds < 60) return `${seconds} s`;
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return `${minutes} min ${pad(seconds % 60)} s`;
    return `${grouped.format(Math.floor(minutes / 60))} h ${pad(minutes % 60)}`;
  };
  const upToOneDecimal = new Intl.NumberFormat(safe, { maximumFractionDigits: 1 });
  const durationMark = (milliseconds: number) => {
    if (!Number.isFinite(milliseconds)) return words.none;
    const ms = Math.max(0, milliseconds);
    if (ms < 1000) return `${upToOneDecimal.format(ms)} ms`;
    if (ms < 120_000) return `${upToOneDecimal.format(ms / 1000)} s`;
    if (ms < 7_200_000) return `${grouped.format(Math.round(ms / 60_000))} min`;
    return `${upToOneDecimal.format(ms / 3_600_000)} h`;
  };
  const bytes = (value: number) => {
    if (!Number.isFinite(value)) return words.none;
    const units = ["B", "KB", "MB", "GB", "TB"];
    let size = Math.abs(value), index = 0;
    while (size >= 1024 && index < units.length - 1) { size /= 1024; index++; }
    return `${index === 0 ? grouped.format(size) : small.format(Math.round(size * 10) / 10)} ${units[index]}`;
  };
  const cost = (unit: string, value: number) => {
    if (!Number.isFinite(value)) return words.none;
    if (unit.toLowerCase() === "usd") return usd.format(value);
    return words.credits(Math.abs(value) < 100 ? small.format(value) : grouped.format(value));
  };
  const usdWhole = new Intl.NumberFormat(safe, { style: "currency", currency: "USD", maximumFractionDigits: 0 });
  const costShort = (unit: string, value: number) => !Number.isFinite(value) ? words.none : unit.toLowerCase() === "usd" ? (Math.abs(value) >= 1000 ? usdWhole : usd).format(value) : (Math.abs(value) >= 10000 ? compact(value) : Math.abs(value) < 100 ? small.format(value) : grouped.format(value));
  const percent = (ratio: number, digits?: number) => !Number.isFinite(ratio) ? words.none
    : new Intl.NumberFormat(safe, { style: "percent", maximumFractionDigits: digits ?? (Math.abs(ratio) >= 0.1 ? 0 : 1) }).format(ratio);
  const value = (unit: string, amount: number) => {
    switch (unit) {
      case "ms": return duration(amount);
      case "tokens": return compact(amount);
      case "bytes": return bytes(amount);
      case "usd": case "AI credits": return cost(unit, amount);
      case "cost": return number(amount);
      case "ratio": return percent(amount);
      default: return number(amount);
    }
  };
  const axis = (unit: string, amount: number) => {
    switch (unit) {
      case "ms": return duration(amount);
      case "bytes": return bytes(amount);
      case "usd": case "AI credits": return unit === "usd" ? (Math.abs(amount) >= 100 && Number.isInteger(amount) ? usdWhole : usd).format(amount) : compact(amount);
      case "ratio": return percent(amount, 0);
      default: return compact(amount);
    }
  };
  const bucket = (info: BucketInfo, frequency: Frequency) => {
    const { date, time } = parts(info.start);
    switch (frequency) {
      case "hour": return time;
      case "day": case "week": return dayText(date);
      case "month": return monthFormat.format(asDate(date));
      case "year": return date.slice(0, 4);
    }
  };
  const bucketLong = (info: BucketInfo, frequency: Frequency) => {
    const { date, time } = parts(info.start);
    switch (frequency) {
      case "hour": return `${dayLongText(date)} ${time}`;
      case "day": return dayLongText(date);
      case "week": return dayLongText(date);
      case "month": return monthLongFormat.format(asDate(date));
      case "year": return date.slice(0, 4);
    }
  };
  const range = (from: string, to: string) => {
    if (from === to) return dayLongText(from);
    return from.slice(0, 4) === to.slice(0, 4) ? `${dayText(from)} – ${dayLongText(to)}` : `${dayLongText(from)} – ${dayLongText(to)}`;
  };
  return { locale: safe, number, compact, percent, duration, durationMark, bytes, cost, costShort, day: dayText, dayLong: dayLongText, range, value, axis, bucket, bucketLong };
}

/** A text with its first letter in capitals, for a phrase that starts a sentence ("about 40 seconds left."). */
export function sentenceCase(text: string, locale: string): string {
  return text.length === 0 ? text : text.charAt(0).toLocaleUpperCase(locale) + text.slice(1);
}

/** A day `yyyymmdd` as `yyyy-MM-dd`. */
export function dayOfNumber(day: number): string {
  const text = String(day).padStart(8, "0");
  return `${text.slice(0, 4)}-${text.slice(4, 6)}-${text.slice(6, 8)}`;
}

/** A time left in seconds, rounded as a person says it: `less than a minute`, `about 40 seconds`, `about 3 minutes`; the pieces are for the caller to word. */
export function etaParts(seconds: number): Readonly<{ unit: "second" | "minute" | "hour"; count: number }> {
  if (seconds < 60) return { unit: "second", count: Math.max(5, Math.round(seconds / 5) * 5) };
  if (seconds < 3600) return { unit: "minute", count: Math.max(1, Math.round(seconds / 60)) };
  return { unit: "hour", count: Math.max(1, Math.round(seconds / 3600)) };
}
