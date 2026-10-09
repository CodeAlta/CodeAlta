import type { SessionRuntimeActivityResponse } from "#neoastra";

export const recentSessionCountKey = "codealta.desktop.recent-session-count.v1";
export const defaultRecentSessionCount = 6;
/** How many sub-agents the Explorer lists under a session. */
export const subAgentCountKey = "codealta.desktop.sub-agent-count.v1";
export const defaultSubAgentCount = 4;
export function validRecentSessionCount(value: number) { return Number.isInteger(value) && value >= 1 && value <= 50; }
type StoredCount = { value: number; notice?: string; issue?: "invalid" | "unavailable" };
function readCount(read: () => string | null, label: string, fallback: number): StoredCount {
  try {
    const raw = read();
    if (raw === null) return { value: fallback };
    if (/^(?:[1-9]|[1-4][0-9]|50)$/.test(raw)) return { value: Number(raw) };
    return { value: fallback, issue: "invalid", notice: `${label}: invalid saved preference; using ${fallback}. Not overwritten.` };
  } catch { return { value: fallback, issue: "unavailable", notice: `${label}: local storage unavailable; using ${fallback}. Not saved.` }; }
}
export const readRecentSessionCount = (read: () => string | null): StoredCount => readCount(read, "Recent session count", defaultRecentSessionCount);
export const readSubAgentCount = (read: () => string | null): StoredCount => readCount(read, "Sub-agent count", defaultSubAgentCount);
const decimal = (value: unknown) => typeof value === "string" && /^(0|[1-9][0-9]{0,18})$/.test(value) && BigInt(value) <= 9223372036854775807n;
export function activityTicks(value: unknown): bigint | null {
  if (typeof value !== "string") return null;
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})\.(\d{7})([+-])(\d{2}):(\d{2})$/.exec(value);
  if (!match) return null;
  const [, y, m, d, h, min, s, fraction, , oh, om] = match;
  const year = Number(y), month = Number(m), day = Number(d);
  const days = new Date(Date.UTC(year, month, 0)).getUTCDate();
  if (year <= 1 || month < 1 || month > 12 || day < 1 || day > days || Number(h) > 23 || Number(min) > 59 || Number(s) > 59
    || Number(oh) > 14 || Number(om) > 59 || Number(oh) === 14 && Number(om) !== 0) return null;
  const milliseconds = Date.parse(value);
  return Number.isFinite(milliseconds) ? BigInt(milliseconds) * 10000n + BigInt(fraction.slice(3)) : null;
}
export function validActivity(value: unknown): value is SessionRuntimeActivityResponse {
  if (!value || typeof value !== "object") return false;
  const row = value as SessionRuntimeActivityResponse;
  return row.source === "admitted_agent_event" && decimal(row.admittedEvents) && decimal(row.omittedEvents)
    && (row.timestamp === null ? row.admittedEvents === "0" : row.admittedEvents !== "0" && activityTicks(row.timestamp) !== null);
}
// Stable unknowns and equal timestamps retain the existing loaded order. No saved date fallback.
export function orderObservedActivity<T>(rows: readonly T[], timestamp: (row: T) => string | null): T[] {
  return rows.map(row => ({ row, ticks: activityTicks(timestamp(row)) })).sort((a, b) =>
    a.ticks === b.ticks ? 0 : a.ticks === null ? 1 : b.ticks === null ? -1 : a.ticks > b.ticks ? -1 : 1).map(item => item.row);
}
