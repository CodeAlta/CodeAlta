import assert from "node:assert/strict";
import test from "node:test";
import type { HistoryResponse, SessionDisplayView } from "#neoastra";
import { reconcileTimeline } from "./reconcileTimeline";

type Entry = HistoryResponse["entries"][number];
const entry = (overrides: Partial<Entry> = {}): Entry => ({ offset: "1", eventType: "contentCompleted", providerId: "provider",
  sessionId: "session", runId: "run", timestamp: "2026-09-23T00:00:00Z", kind: "Assistant", phase: null,
  contentId: "content", activityId: null, parentActivityId: null, interactionId: null, name: null,
  tool: null, files: null, text: "persisted", details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, ...overrides });
const session = (overrides: Partial<SessionDisplayView> = {}): SessionDisplayView => ({ sessionId: "session", revision: "1",
  lifecycle: null, queuedPromptCount: null, configuration: { providerId: "provider", providerKey: null, modelId: null,
    reasoningEffort: null, agentPromptId: null }, statusKind: null, statusMessage: null,
  text: [{ runId: "run", contentId: "content", kind: "Assistant", text: "live", isComplete: true,
    isTruncated: false, startedWithDelta: false }], toolActivities: [], metadataTruncated: false,
  transportTruncated: false, evictedTextItems: "0", evictedToolActivities: "0", unsupportedEvents: "0", ...overrides });

test("a persisted completion replaces its matching live text, but not another run or provider-ambiguous identity", () => {
  assert.deepEqual(reconcileTimeline([entry()], session()).map(row => row.source), ["history"]);
  assert.deepEqual(reconcileTimeline([entry({ runId: "other" })], session()).map(row => row.source), ["history", "liveText"]);
  assert.deepEqual(reconcileTimeline([entry({ eventType: "contentDelta", offset: "1" }), entry({ providerId: "other", offset: "2" })], session())
    .map(row => row.source), ["history", "history", "liveText"]);
});

test("a live replacement supersedes incomplete journal deltas and tool starts without hiding completed history", () => {
  const tool = { providerId: "provider", runId: "run", activityId: "tool", phase: "Started", name: "Read", isNameTruncated: false };
  const view = session({ toolActivities: [tool] });
  const rows = reconcileTimeline([entry({ eventType: "contentDelta" }), entry({ offset: "2", eventType: "activity",
    kind: "ToolCall", activityId: "tool", contentId: null, phase: "Requested" })], view);
  assert.deepEqual(rows.map(row => row.source), ["liveTool", "liveText"]);
  assert.deepEqual(reconcileTimeline([entry({ offset: "2", eventType: "activity", kind: "ToolCall", activityId: "tool",
    contentId: null, phase: "Completed" })], view).map(row => row.source), ["history", "liveText"]);
});

test("no run identity never erases persisted content, and absent live state does not change history", () => {
  assert.deepEqual(reconcileTimeline([entry()], null).map(row => row.source), ["history"]);
  assert.deepEqual(reconcileTimeline([entry({ runId: null })], session({ text: [{ ...session().text[0], runId: null }] }))
    .map(row => row.source), ["history", "liveText"]);
  assert.deepEqual(reconcileTimeline([entry()], session({ configuration: null })).map(row => row.source), ["history", "liveText"]);
  assert.deepEqual(reconcileTimeline([entry({ eventType: "contentDelta" })], session({ text: [{ ...session().text[0], startedWithDelta: true }] }))
    .map(row => row.source), ["history", "liveText"]);
  assert.deepEqual(reconcileTimeline([entry({ eventType: "contentDelta" })], session({ text: [{ ...session().text[0], isTruncated: true }] }))
    .map(row => row.source), ["history", "liveText"]);
});
