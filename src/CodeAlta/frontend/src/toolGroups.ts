import type { HistoryEntry } from "./timeline";
import type { ReconciledRow } from "./reconcileTimeline";

export type TimelineGroup = { key: string; tools: boolean; rows: ReconciledRow[] };

// Presentation only. Group membership comes from typed journal identities, never
// formatted metadata. Raw transport snapshots and status-only updates are not turns.
export function groupTimelineTools(rows: ReconciledRow[], entries: readonly HistoryEntry[]): TimelineGroup[] {
  const membership = new Map<string, string>();
  let previous: string | null = null, segment = 0;
  for (const entry of entries) {
    if (entry.eventType === "raw" || entry.eventType === "notes"
      || entry.eventType === "sessionUpdate" && ["usageupdated", "statuschanged"].includes(entry.kind?.toLowerCase() ?? "")) continue;
    if (["contentDelta", "contentCompleted"].includes(entry.eventType)
      && ["commandoutput", "tooloutput", "filechangeoutput"].includes(entry.kind?.toLowerCase() ?? "")
      && entry.parentActivityId && entries.some(parent => parent.eventType === "activity" && parent.activityId === entry.parentActivityId
        && parent.sessionId === entry.sessionId && parent.providerId === entry.providerId && parent.runId === entry.runId)) continue;
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
    // Live tools have only a retained-update order. Keep that separate from journal chronology.
    const identity = row.source === "history" && row.item.category === "tool" ? membership.get(row.item.key)
      : row.source === "liveTool" && row.row.runId ? `live:${JSON.stringify([row.row.providerId, row.row.runId])}` : undefined;
    const last = result.at(-1);
    if (identity && identity === lastIdentity && last && last.rows.length < 12) {
      last.rows.push(row); last.tools = true;
    } else result.push({ key: row.key, tools: row.source === "liveTool" || row.source === "history" && row.item.category === "tool", rows: [row] });
    lastIdentity = identity;
  }
  return result;
}
