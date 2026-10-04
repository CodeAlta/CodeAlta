import type { HistoryEntry } from "./timeline";
import type { ReconciledRow } from "./reconcileTimeline";

export type TimelineGroup = { key: string; tools: boolean; rows: ReconciledRow[] };

/** Most tool calls shown in one group card; a longer run of calls continues in the next card. */
export const toolGroupLimit = 60;

// Presentation only. Group membership comes from typed journal identities, never
// formatted metadata. Consecutive tool calls of one run share a group; only a record the
// timeline shows between them (an assistant message, a file change, a status row) ends it.
// Records that show nothing (raw transport snapshots, usage updates, tool output attached to
// its call, reasoning without text) do not.
export function groupTimelineTools(rows: ReconciledRow[], entries: readonly HistoryEntry[]): TimelineGroup[] {
  const membership = new Map<string, string>();
  const shown = new Set<string>();
  for (const row of rows) if (row.source === "history" && row.item.category !== "tool") shown.add(row.item.key);
  let previous: string | null = null, segment = 0;
  for (const entry of entries) {
    const identity = entry.eventType === "activity" && entry.activityId && entry.runId && entry.providerId && entry.sessionId
      && entry.kind?.toLowerCase() !== "filechange"
      ? JSON.stringify([entry.sessionId, entry.providerId, entry.runId, entry.parentActivityId]) : null;
    if (identity === null) {
      // An activity that cannot be grouped (no run, a file change) or a shown record ends the run of calls.
      if (entry.eventType === "activity" || shown.has(entry.offset)) { segment++; previous = null; }
      continue;
    }
    if (identity !== previous) segment++;
    previous = identity;
    membership.set(entry.offset, `${segment}:${identity}`);
  }
  const result: TimelineGroup[] = [];
  let lastIdentity: string | undefined;
  for (const row of rows) {
    // Live tools have only a retained-update order. Keep that separate from journal chronology.
    const identity = row.source === "history" && row.item.category === "tool" ? membership.get(row.item.key)
      : row.source === "liveTool" && row.row.runId ? `live:${JSON.stringify([row.row.providerId, row.row.runId])}` : undefined;
    const last = result.at(-1);
    if (identity && identity === lastIdentity && last && last.rows.length < toolGroupLimit) {
      last.rows.push(row); last.tools = true;
    } else result.push({ key: row.key, tools: row.source === "liveTool" || row.source === "history" && row.item.category === "tool", rows: [row] });
    lastIdentity = identity;
  }
  return result;
}
