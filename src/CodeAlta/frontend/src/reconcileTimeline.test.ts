import assert from "node:assert/strict";
import test from "node:test";
import type { HistoryResponse, SessionDisplayView } from "#neoastra";
import { reconcileTimeline } from "./reconcileTimeline";

test("empty reasoning is hidden in saved and live timelines", () => {
  const blank = entry({ kind: "Reasoning", text: " \r\n\t" });
  assert.deepEqual(reconcileTimeline([blank], null), []);
  assert.deepEqual(reconcileTimeline([], session({ text: [{ ...session().text[0], kind: "ReasoningSummary", text: "  " }] })), []);
  assert.equal(reconcileTimeline([entry({ kind: "Reasoning", text: "Considering options" })], null).length, 1);
});

test("turn setup notices follow the user prompt without changing source timestamps", () => {
  const rows = reconcileTimeline([
    entry({ offset: "1", eventType: "system_prompt", text: "System prompt changed", timestamp: "2026-09-23T00:00:01Z" }),
    entry({ offset: "2", eventType: "sessionUpdate", kind: "ModelChanged", timestamp: "2026-09-23T00:00:02Z" }),
    entry({ offset: "3", kind: "User", timestamp: "2026-09-23T00:00:03Z" }),
  ], null);
  assert.deepEqual(rows.map(row => row.source === "history" && row.item.key), ["3", "1", "2"]);
  assert.equal(rows[0].source === "history" && rows[0].item.timestamp, "2026-09-23T00:00:03Z");
});

type Entry = HistoryResponse["entries"][number];
const entry = (overrides: Partial<Entry> = {}): Entry => ({ offset: "1", eventType: "contentCompleted", providerId: "provider",
  sessionId: "session", runId: "run", timestamp: "2026-09-23T00:00:00Z", kind: "Assistant", phase: null,
  contentId: "content", activityId: null, parentActivityId: null, interactionId: null, name: null,
  tool: null, files: null, images: null, text: "persisted", details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, ...overrides });
const session = (overrides: Partial<SessionDisplayView> = {}): SessionDisplayView => ({ sessionId: "session", revision: "1",
  lifecycle: null, queuedPromptCount: null, configuration: { providerId: "provider", providerKey: null, modelId: null,
    reasoningEffort: null, agentPromptId: null }, statusKind: null, statusMessage: null,
  text: [{ timestamp: null, sequence: null, runId: "run", contentId: "content", kind: "Assistant", text: "live", isComplete: true,
    isTruncated: false, startedWithDelta: false }], toolActivities: [], metadataTruncated: false,
  transportTruncated: false, evictedTextItems: "0", evictedToolActivities: "0", unsupportedEvents: "0", ...overrides });

test("a persisted completion replaces its matching live text, but not another run or provider-ambiguous identity", () => {
  assert.deepEqual(reconcileTimeline([entry()], session()).map(row => row.source), ["history"]);
  assert.deepEqual(reconcileTimeline([entry({ runId: "other" })], session()).map(row => row.source), ["history", "liveText"]);
  assert.deepEqual(reconcileTimeline([entry({ eventType: "contentDelta", offset: "1" }), entry({ providerId: "other", offset: "2" })], session())
    .map(row => row.source), ["history", "history", "liveText"]);
});

test("a live replacement supersedes incomplete journal deltas, and a tool call keeps one row and one key while it runs", () => {
  const tool = { timestamp: null, sequence: null, providerId: "provider", runId: "run", activityId: "tool", phase: "Started", name: "Read", isNameTruncated: false };
  const view = session({ toolActivities: [tool] });
  const key = 'tool:["provider","run","tool"]';
  // Before the journal shows the call, the live view does.
  assert.deepEqual(reconcileTimeline([], view).map(row => [row.source, row.key]), [["liveTool", key], ["liveText", 'text:["run","content","assistant"]']]);
  // The record of the call takes the row: it says more than the live one, and the phase is the later of the two.
  const requested = entry({ offset: "2", eventType: "activity", kind: "ToolCall", activityId: "tool", contentId: null, phase: "Requested" });
  const rows = reconcileTimeline([entry({ eventType: "contentDelta" }), requested], view);
  assert.deepEqual(rows.map(row => [row.source, row.key]), [["history", key], ["liveText", 'text:["run","content","assistant"]']]);
  assert.equal(rows[0].source === "history" && rows[0].item.toolPhase, "started");
  assert.equal(rows[0].source === "history" && rows[0].item.toolCall?.offset, "2");
  // The same view gives the same row, so that its tile is not drawn again.
  const again = reconcileTimeline([entry({ eventType: "contentDelta" }), requested], view);
  assert.equal(again[0].source === "history" && rows[0].source === "history" && again[0].item === rows[0].item, true);
  // The record that ended the call is not taken back by an older live phase, and the key is still the same without a live view.
  const completed = entry({ offset: "3", eventType: "activity", kind: "ToolCall", activityId: "tool", contentId: null, phase: "Completed" });
  const ended = reconcileTimeline([requested, completed], view);
  assert.deepEqual(ended.map(row => [row.source, row.key]), [["history", key], ["liveText", 'text:["run","content","assistant"]']]);
  assert.equal(ended[0].source === "history" && ended[0].item.toolPhase, "completed");
  assert.deepEqual(reconcileTimeline([requested, completed], null).map(row => row.key), [key]);
});

test("no run identity never erases persisted content, and absent live state does not change history", () => {
  assert.deepEqual(reconcileTimeline([entry()], null).map(row => row.source), ["history"]);
  assert.deepEqual(reconcileTimeline([entry({ runId: null })], session({ text: [{ ...session().text[0], runId: null }] }))
    .map(row => row.source), ["history", "liveText"]);
  assert.deepEqual(reconcileTimeline([entry()], session({ configuration: null })).map(row => row.source), ["history", "liveText"]);
  // The host reports startedWithDelta only while no final content has established a complete baseline.
  assert.deepEqual(reconcileTimeline([entry({ eventType: "contentDelta" })], session({ text: [{ ...session().text[0], isComplete: false, startedWithDelta: true }] }))
    .map(row => row.source), ["history", "liveText"]);
  assert.deepEqual(reconcileTimeline([entry({ eventType: "contentDelta" })], session({ text: [{ ...session().text[0], isTruncated: true }] }))
    .map(row => row.source), ["history", "liveText"]);
});
