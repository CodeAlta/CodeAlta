import type { SessionDisplayText, SessionDisplayToolActivity } from "#neoastra";
import { buildTimelineItems, type HistoryEntry } from "./timeline";

// The same typed-event presentation as persisted history; no second live-message UI.
function displayEntry(values: Partial<HistoryEntry>): HistoryEntry {
  return { offset: "", sessionId: "", providerId: "", runId: null, activityId: null,
    contentId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, eventType: "contentCompleted",
    kind: null, name: null, phase: null, text: null, timestamp: "", details: null,
    textTruncated: false, detailsTruncated: false, bodyOmitted: false, files: null, tool: null, images: null, ...values };
}

export function liveTextItem(row: SessionDisplayText) {
  return buildTimelineItems([displayEntry({ offset: JSON.stringify([row.runId, row.contentId, row.kind]),
    eventType: row.isComplete ? "contentCompleted" : "contentDelta", contentId: row.contentId,
    runId: row.runId, kind: row.kind, text: row.text, timestamp: row.timestamp ?? "", textTruncated: row.isTruncated })])[0];
}

export function liveToolItem(row: SessionDisplayToolActivity) {
  return buildTimelineItems([displayEntry({ offset: JSON.stringify([row.providerId, row.runId, row.activityId]),
    eventType: "activity", providerId: row.providerId, runId: row.runId, activityId: row.activityId,
    kind: "ToolCall", name: row.name, phase: row.phase, timestamp: row.timestamp ?? "" })])[0];
}
