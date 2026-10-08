import type { ProviderUsageLimit, SessionUsageRateLimits, SessionUsageRateWindow } from "#neoastra";
import type { MessageKey } from "./localization";

/** One limit of a subscription as the views show it: a window of time or a quota, whoever reported it. */
export type UsageLimit = Readonly<{ id: string; name: string | null; usedPercent: number | null; resetsAt: string | null; windowMinutes: number | null;
  used: number | null; total: number | null; unit: string | null; unlimited: boolean; remaining: number | null }>;

/** The provider types that are a subscription whose usage the host can read. */
export const hasSubscriptionUsage = (type: string | null | undefined) => type === "codex" || type === "copilot" || type === "claude-code";

/** How old the limits a session reported may be before the provider is asked instead. */
export const liveLimitsLifetime = 15 * 60 * 1000;

const minute = 60 * 1000;
const near = (minutes: number, expected: number) => minutes >= expected * .95 && minutes <= expected * 1.05;

/** What a limit is called, and what it is narrower for (a model, a feature) when it is. */
export function limitLabel(limit: Pick<UsageLimit, "id" | "name" | "unit" | "windowMinutes">): { key: MessageKey; scope: string | null } {
  const scope = limit.name?.trim() || null;
  switch (limit.id) {
    case "premium_interactions": return { key: limit.unit === "credits" ? "AI credits" : "Premium requests", scope };
    case "chat": return { key: "Chat", scope };
    case "completions": return { key: "Code completions", scope };
    case "extra_usage": return { key: "Extra usage", scope };
    case "credits": return { key: "Credits", scope };
  }
  const minutes = limit.windowMinutes;
  if (!minutes) return { key: "Usage limit", scope };
  if (near(minutes, 5 * 60)) return { key: "5-hour limit", scope };
  if (near(minutes, 24 * 60)) return { key: "Daily limit", scope };
  if (near(minutes, 7 * 24 * 60)) return { key: "Weekly limit", scope };
  if (minutes >= 28 * 24 * 60 && minutes <= 31 * 24 * 60) return { key: "Monthly limit", scope };
  return { key: "Usage limit", scope };
}

/** The color of a meter: calm, then a warning from three quarters, then danger from nine tenths. */
export const limitIntent = (percent: number | null): "none" | "warning" | "danger" =>
  percent === null ? "none" : percent >= 90 ? "danger" : percent >= 75 ? "warning" : "none";

/** How much of the period of a limit has gone by, from 0 to 100, when its length and its end are known. */
export function elapsedPercent(limit: Pick<UsageLimit, "resetsAt" | "windowMinutes">, now: number): number | null {
  if (!limit.resetsAt || !limit.windowMinutes) return null;
  const end = new Date(limit.resetsAt).getTime();
  if (Number.isNaN(end)) return null;
  const left = (end - now) / (limit.windowMinutes * minute);
  return left < 0 || left > 1 ? null : Math.round((1 - left) * 1000) / 10;
}

/** The time left before a limit starts over, as "3 d", "1 d 4 h", "2 h 14 min" or "9 min"; null once it has passed. */
export function timeLeft(resetsAt: string | null, now: number): string | null {
  if (!resetsAt) return null;
  const end = new Date(resetsAt).getTime();
  if (Number.isNaN(end) || end <= now) return null;
  const minutes = Math.max(1, Math.round((end - now) / minute));
  const days = Math.floor(minutes / (24 * 60)), hours = Math.floor(minutes % (24 * 60) / 60), rest = minutes % 60;
  if (days >= 2) return hours >= 12 ? `${days + 1} d` : `${days} d`;
  if (days === 1) return hours ? `1 d ${hours} h` : "1 d";
  if (hours) return rest ? `${hours} h ${rest} min` : `${hours} h`;
  return `${rest} min`;
}

/** When a limit starts over, as the reader says it: the time today, the day and the time this week, the date after. */
export function resetMoment(resetsAt: string | null, now: number, locale?: string): string | null {
  if (!resetsAt) return null;
  const end = new Date(resetsAt);
  if (Number.isNaN(end.getTime())) return null;
  const today = new Date(now);
  const time = end.toLocaleTimeString(locale, { hour: "2-digit", minute: "2-digit" });
  if (end.toDateString() === today.toDateString()) return time;
  const days = (end.getTime() - now) / (24 * 60 * minute);
  if (days > 0 && days < 6.5) return `${end.toLocaleDateString(locale, { weekday: "long" })} ${time}`;
  // A quota of a billing period ends at midnight: its day says it all.
  const midnight = end.getHours() === 0 && end.getMinutes() === 0 || end.getUTCHours() === 0 && end.getUTCMinutes() === 0;
  const date = end.toLocaleDateString(locale, { month: "long", day: "numeric" });
  return midnight ? date : `${date} ${time}`;
}

/** The plan of an account as a name: the provider sends an identifier such as `individual_max`. */
export function planLabel(plan: string | null | undefined): string | null {
  const words = plan?.replace(/[_-]+/g, " ").trim();
  return words ? words[0].toUpperCase() + words.slice(1) : null;
}

/** A count of requests or credits, grouped as the reader's language groups numbers. */
export const unitCount = (value: number, locale?: string) => Math.round(value).toLocaleString(locale);

/** The limits of a provider answer, as the views show them. */
export const providerLimits = (limits: readonly ProviderUsageLimit[]): UsageLimit[] => limits.map(limit => ({ ...limit }));

function sessionWindow(id: string, window: SessionUsageRateWindow | null): UsageLimit | null {
  if (!window || window.usedPercent === null && !window.resetsAt) return null;
  const minutes = window.windowDurationMinutes && /^\d{1,7}$/.test(window.windowDurationMinutes) ? Number(window.windowDurationMinutes) : null;
  return { id, name: null, usedPercent: window.usedPercent, resetsAt: window.resetsAt, windowMinutes: minutes || null,
    used: null, total: null, unit: null, unlimited: false, remaining: null };
}

/** The limits the turns of a session reported: its two windows, the shorter one first. */
export function sessionLimits(rateLimits: SessionUsageRateLimits | null | undefined): UsageLimit[] {
  if (!rateLimits) return [];
  return [sessionWindow("session:primary", rateLimits.primary), sessionWindow("session:secondary", rateLimits.secondary)].filter((limit): limit is UsageLimit => !!limit);
}

/**
 * Whether the limits a session reported are the ones to show: they have a figure, and they are recent. Otherwise the
 * provider is asked, when it can be.
 */
export function liveLimitsAreCurrent(limits: readonly UsageLimit[], updatedAt: string | null | undefined, now: number): boolean {
  if (!limits.some(limit => limit.usedPercent !== null)) return false;
  const at = updatedAt ? new Date(updatedAt).getTime() : Number.NaN;
  return !Number.isNaN(at) && now - at >= -minute && now - at < liveLimitsLifetime;
}

/** Canonical English lines of the limits, for the Markdown copy of the usage window. */
export function limitsMarkdown(limits: readonly UsageLimit[], plan: string | null, now: number): string[] {
  if (!limits.length) return [];
  const english: Partial<Record<MessageKey, string>> = {};
  const lines = ["", "## Subscription usage", ""];
  if (plan) lines.push(`- Plan: ${plan}`);
  for (const limit of limits) {
    const { key, scope } = limitLabel(limit);
    const name = `${english[key] ?? key}${scope ? ` (${scope})` : ""}`;
    const parts = [limit.unlimited ? "unlimited" : limit.usedPercent !== null ? `${Math.round(limit.usedPercent)}% used` : null,
      limit.used !== null && limit.total !== null ? `${unitCount(limit.used, "en-US")} of ${unitCount(limit.total, "en-US")} ${limit.unit ?? ""}`.trim() : null,
      limit.remaining !== null ? `${unitCount(limit.remaining, "en-US")} left` : null,
      limit.resetsAt && timeLeft(limit.resetsAt, now) ? `resets in ${timeLeft(limit.resetsAt, now)}` : null].filter(Boolean);
    lines.push(`- ${name}: ${parts.join(" · ") || "—"}`);
  }
  return lines;
}
