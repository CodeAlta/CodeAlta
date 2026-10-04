import type { HistoryResponse, SessionDisplayText, SessionDisplayToolActivity, SessionDisplayView } from "#neoastra";
import { buildTimelineItems, type TimelineItem } from "./timeline";

export type ReconciledRow =
  | { source: "history"; key: string; item: TimelineItem }
  | { source: "liveText"; key: string; row: SessionDisplayText }
  | { source: "liveTool"; key: string; row: SessionDisplayToolActivity };

// Source timestamps and first-publication sequences keep streaming rows in place.
// Only a run-scoped, unambiguous match can replace a journal row.
export function reconcileTimeline(entries: HistoryResponse["entries"], live: SessionDisplayView | null): ReconciledRow[] {
  const historical = buildTimelineItems(entries);
  if (!live) return orderTimelineRows(historical.map(item => ({ source: "history", key: `history:${item.key}`, item })));
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
  const coveredText = new Set<string>();
  // Looked up by journal offset: a long window must not be searched once per record.
  const itemAt = new Map(historical.map(item => [item.key, item]));
  const entryAt = new Map(entries.map(entry => [entry.offset, entry]));
  for (const entry of entries) {
    if (entry.eventType === "contentDelta" && entry.runId && entry.contentId) {
      const key = textKey(entry.runId, entry.contentId, entry.kind ?? "");
      const row = textMatches.get(key);
      const item = itemAt.get(entry.offset);
      if (row && item && !completed.has(key)) {
        const persisted = item.markdown ?? "";
        if (row.isComplete && !row.isTruncated || row.text.startsWith(persisted)) hidden.add(entry.offset);
        else if (persisted.startsWith(row.text)) coveredText.add(key);
      }
    }
    if (entry.eventType === "activity" && entry.runId && entry.activityId &&
      activityMatches.has(toolKey(entry.providerId, entry.runId, entry.activityId)) &&
      !terminal.has(toolKey(entry.providerId, entry.runId, entry.activityId))) hidden.add(entry.offset);
  }
  const result: ReconciledRow[] = historical.filter(item => !hidden.has(item.key))
    .map(item => {
      const entry = entryAt.get(item.key);
      const text = entry?.runId && entry.contentId ? textKey(entry.runId, entry.contentId, entry.kind ?? "") : null;
      const tool = entry?.runId && entry.activityId ? toolKey(entry.providerId, entry.runId, entry.activityId) : null;
      const key = text && textMatches.has(text) && (completed.has(text) || coveredText.has(text)) ? `text:${text}`
        : tool && activityMatches.has(tool) && terminal.has(tool) ? `tool:${tool}` : `history:${item.key}`;
      return { source: "history", key, item };
    });
  for (const row of live.toolActivities) {
    if (row.runId && terminal.has(toolKey(row.providerId, row.runId, row.activityId))) continue;
    result.push({ source: "liveTool", key: `tool:${toolKey(row.providerId, row.runId, row.activityId)}`, row });
  }
  for (const row of live.text) {
    if (row.kind.toLowerCase().startsWith("reasoning") && !row.text.trim()) continue;
    if (row.runId && (completed.has(textKey(row.runId, row.contentId, row.kind)) || coveredText.has(textKey(row.runId, row.contentId, row.kind)))) continue;
    result.push({ source: "liveText", key: `text:${textKey(row.runId, row.contentId, row.kind)}`, row });
  }
  return orderTimelineRows(result);
}

// Parsing a timestamp is the cost of ordering a long timeline; an item or a live row keeps its own.
const times = new WeakMap<object, number>();
export function orderTimelineRows(result: ReconciledRow[]): ReconciledRow[] {
  const timestamp = (row: ReconciledRow) => {
    const subject = row.source === "history" ? row.item : row.row;
    let time = times.get(subject);
    if (time === undefined) times.set(subject, time = Date.parse(row.source === "history" ? row.item.timestamp : row.row.timestamp ?? ""));
    return time;
  };
  result.sort((a, b) => {
    const time = timestamp(a) - timestamp(b);
    if (Number.isFinite(time) && time !== 0) return time;
    if (a.source !== "history" && b.source !== "history" && a.row.sequence && b.row.sequence) {
      const left = BigInt(a.row.sequence), right = BigInt(b.row.sequence);
      return left < right ? -1 : left > right ? 1 : 0;
    }
    return 0;
  });
  // Preparation records precede the provider's persisted user echo. Present that
  // contiguous setup block after its prompt, without falsifying source timestamps
  // or moving notices across assistant/tool/other turn boundaries.
  const setup = (row: ReconciledRow) => row.source === "history" &&
    (row.item.category === "prompt" || row.item.eventType === "sessionUpdate" && row.item.icon === "model");
  for (let index = 0; index < result.length; index++) {
    const row = result[index];
    if (!(row.source === "history" ? row.item.category === "user" : row.source === "liveText" && row.row.kind.toLowerCase() === "user")) continue;
    let start = index;
    while (start > 0 && setup(result[start - 1])) start--;
    if (start !== index) { result.splice(index, 1); result.splice(start, 0, row); }
  }
  return result;
}

function textKey(runId: string | null, contentId: string, kind: string): string {
  return JSON.stringify([runId, contentId, kind.toLowerCase()]);
}

function toolKey(providerId: string, runId: string | null, activityId: string): string {
  return JSON.stringify([providerId, runId, activityId]);
}
