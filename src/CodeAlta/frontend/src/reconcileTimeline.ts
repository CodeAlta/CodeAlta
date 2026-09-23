import type { HistoryResponse, SessionDisplayText, SessionDisplayToolActivity, SessionDisplayView } from "#neoastra";
import { buildTimelineItems, type TimelineItem } from "./timeline";

export type ReconciledRow =
  | { source: "history"; key: string; item: TimelineItem }
  | { source: "liveText"; key: string; row: SessionDisplayText }
  | { source: "liveTool"; key: string; row: SessionDisplayToolActivity };

// The display window has no timestamps or shared text/tool ordering. Retain journal order and
// place unmatched, recently updated live rows at the tail; never claim an exact interleaving.
// Only a run-scoped, unambiguous match can replace a journal row.
export function reconcileTimeline(entries: HistoryResponse["entries"], live: SessionDisplayView | null): ReconciledRow[] {
  const historical = buildTimelineItems(entries);
  if (!live) return historical.map(item => ({ source: "history", key: `history:${item.key}`, item }));
  const providers = new Map<string, Set<string>>();
  for (const entry of entries) {
    if (!entry.runId || !entry.contentId || !entry.kind || !["contentDelta", "contentCompleted"].includes(entry.eventType)) continue;
    const key = textKey(entry.runId, entry.contentId, entry.kind);
    const values = providers.get(key) ?? new Set<string>();
    values.add(entry.providerId);
    providers.set(key, values);
  }
  const textMatches = new Map<string, SessionDisplayText>();
  for (const row of live.text) {
    if (row.runId && live.configuration?.providerId &&
      providers.get(textKey(row.runId, row.contentId, row.kind))?.has(live.configuration.providerId) &&
      providers.get(textKey(row.runId, row.contentId, row.kind))?.size === 1)
      textMatches.set(textKey(row.runId, row.contentId, row.kind), row);
  }
  const completed = new Set(entries.filter(entry => entry.eventType === "contentCompleted" && entry.runId && entry.contentId &&
    textMatches.has(textKey(entry.runId, entry.contentId, entry.kind ?? "")))
    .map(entry => textKey(entry.runId!, entry.contentId!, entry.kind!)));
  const activityMatches = new Map(live.toolActivities.filter(row => row.runId).map(row => [toolKey(row.providerId, row.runId!, row.activityId), row]));
  const terminal = new Set(entries.filter(entry => entry.eventType === "activity" && entry.runId && entry.activityId &&
    ["completed", "failed", "canceled"].includes(entry.phase?.toLowerCase() ?? ""))
    .map(entry => toolKey(entry.providerId, entry.runId!, entry.activityId!)));
  const hidden = new Set<string>();
  for (const entry of entries) {
    if (entry.eventType === "contentDelta" && entry.runId && entry.contentId &&
      textMatches.has(textKey(entry.runId, entry.contentId, entry.kind ?? "")) &&
      textMatches.get(textKey(entry.runId, entry.contentId, entry.kind ?? ""))?.isComplete &&
      !textMatches.get(textKey(entry.runId, entry.contentId, entry.kind ?? ""))?.isTruncated &&
      !textMatches.get(textKey(entry.runId, entry.contentId, entry.kind ?? ""))?.startedWithDelta &&
      !completed.has(textKey(entry.runId, entry.contentId, entry.kind ?? ""))) hidden.add(entry.offset);
    if (entry.eventType === "activity" && entry.runId && entry.activityId &&
      activityMatches.has(toolKey(entry.providerId, entry.runId, entry.activityId)) &&
      !terminal.has(toolKey(entry.providerId, entry.runId, entry.activityId))) hidden.add(entry.offset);
  }
  const result: ReconciledRow[] = historical.filter(item => !hidden.has(item.key))
    .map(item => ({ source: "history", key: `history:${item.key}`, item }));
  for (const row of live.toolActivities) {
    if (row.runId && terminal.has(toolKey(row.providerId, row.runId, row.activityId))) continue;
    result.push({ source: "liveTool", key: `tool:${toolKey(row.providerId, row.runId, row.activityId)}`, row });
  }
  for (const row of live.text) {
    if (row.runId && completed.has(textKey(row.runId, row.contentId, row.kind))) continue;
    result.push({ source: "liveText", key: `text:${textKey(row.runId, row.contentId, row.kind)}`, row });
  }
  return result;
}

function textKey(runId: string | null, contentId: string, kind: string): string {
  return JSON.stringify([runId, contentId, kind.toLowerCase()]);
}

function toolKey(providerId: string, runId: string | null, activityId: string): string {
  return JSON.stringify([providerId, runId, activityId]);
}
