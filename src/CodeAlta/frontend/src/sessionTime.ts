import type { Locale } from "./localization";

// Presentation only: preserve the existing parser, rounded duration thresholds and caller clock.
// Tooltips retain the exact source (including offset); unsupported Intl falls back to that source.
export function sessionTime(value: string | null | undefined, locale: Locale, now = Date.now()): { label: string; title: string; dateTime?: string } {
  if (value == null) return { label: "", title: "", dateTime: undefined };
  const date = new Date(value);
  const timestamp = date.getTime();
  if (!Number.isFinite(timestamp)) return { label: value, title: value, dateTime: undefined };

  const seconds = Math.round(Math.abs(now - timestamp) / 1000);
  const future = timestamp > now;
  const result = { label: value, title: value, dateTime: date.toISOString() };
  if (!Number.isFinite(now)) return result;
  try {
    if (!Intl.RelativeTimeFormat.supportedLocalesOf(locale).length) return result;
    const units = [[31536000, "year"], [2592000, "month"], [604800, "week"],
      [86400, "day"], [3600, "hour"], [60, "minute"], [1, "second"]] as const;
    const [duration, unit] = units.find(([size]) => seconds >= size) ?? units[6];
    const count = seconds < 5 ? 0 : Math.floor(seconds / duration) * (future ? 1 : -1);
    result.label = new Intl.RelativeTimeFormat(locale, { numeric: "auto", style: "short" }).format(count, unit);
  } catch { /* Missing Intl/locale data must not invent a time or use the browser's default language. */ }
  return result;
}

// Timeline headings remain absolute local dates, not relative activity/liveness claims.
export function timelineTime(value: string | null | undefined, locale: Locale): { label: string; title: string; dateTime?: string } {
  if (value == null) return { label: "", title: "", dateTime: undefined };
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) return { label: value, title: value, dateTime: undefined };
  const result = { label: value, title: value, dateTime: date.toISOString() };
  try {
    if (Intl.DateTimeFormat.supportedLocalesOf(locale).length)
      result.label = new Intl.DateTimeFormat(locale, { dateStyle: "medium", timeStyle: "short" }).format(date);
  } catch { /* Preserve the source if Intl or the selected locale is unavailable. */ }
  return result;
}
