import type { HistoryEntry } from "./timeline";
import type { ReconciledRow } from "./reconcileTimeline";

export type TimelineGroup = { key: string; tools: boolean; rows: ReconciledRow[] };

// Presentation only. Group membership comes from typed journal identities, never
// formatted metadata. Even an invisible non-activity record breaks chronology.
export function groupTimelineTools(rows: ReconciledRow[], entries: readonly HistoryEntry[]): TimelineGroup[] {
  const membership = new Map<string, string>();
  let previous: string | null = null, segment = 0;
  for (const entry of entries) {
    const identity = entry.eventType === "activity" && entry.activityId && entry.runId && entry.providerId && entry.sessionId
      && entry.kind?.toLowerCase() !== "filechange"
      ? JSON.stringify([entry.sessionId, entry.providerId, entry.runId, entry.parentActivityId]) : null;
    if (identity === null || identity !== previous) segment++;
    previous = identity;
    if (identity !== null) membership.set(entry.offset, `${segment}:${identity}`);
  }
  const result: TimelineGroup[] = [];
  let lastIdentity: string | undefined;
  for (const row of rows) {
    const identity = row.source === "history" && row.item.category === "tool" ? membership.get(row.item.key) : undefined;
    const last = result.at(-1);
    if (identity && identity === lastIdentity && last && last.rows.length < 12) {
      last.rows.push(row); last.tools = true;
    } else result.push({ key: row.key, tools: row.source === "history" && row.item.category === "tool", rows: [row] });
    lastIdentity = identity;
  }
  return result;
}
