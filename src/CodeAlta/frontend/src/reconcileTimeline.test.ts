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
  contentId: "content", activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, prompt: null, name: null,
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

// A prompt and an answer, as the journal and the live view of a session hold them.
const turn = (runId: string, providerId: string, at: number) => [
  entry({ offset: `${at}`, runId, providerId, contentId: `user:${runId}`, kind: "User", text: "the prompt", timestamp: `2026-09-23T00:00:0${at}Z` }),
  entry({ offset: `${at + 1}`, runId, providerId, contentId: `answer:${runId}`, kind: "Assistant", text: "the answer", timestamp: `2026-09-23T00:00:0${at + 1}Z` }),
];
const liveTurn = (runId: string) => [
  { ...session().text[0], runId, contentId: `user:${runId}`, kind: "User", text: "the prompt" },
  { ...session().text[0], runId, contentId: `answer:${runId}`, kind: "Assistant", text: "the answer" },
];
const shown = (rows: ReturnType<typeof reconcileTimeline>) => rows.map(row =>
  `${row.source}:${row.source === "history" ? row.item.markdown : row.source === "liveText" ? row.row.text : row.row.name}`);

test("a run the session's former provider wrote is shown once after the provider changed", () => {
  // A fallback: the prompt of the run that failed on one provider, then the run that goes on on the next one.
  const failed = entry({ offset: "1", runId: "failed", providerId: "fake-quota", contentId: "user:failed", kind: "User", text: "the prompt" });
  const next = turn("next", "claude-code", 2);
  const fallback = session({ configuration: { ...session().configuration!, providerId: "claude-code" },
    text: [{ ...session().text[0], runId: "failed", contentId: "user:failed", kind: "User", text: "the prompt" }, ...liveTurn("next")] });
  assert.deepEqual(shown(reconcileTimeline([failed, ...next], fallback)), ["history:the prompt", "history:the prompt", "history:the answer"]);
  // A provider chosen by hand after a turn: the turn the former provider answered keeps one prompt and one answer.
  const switched = session({ configuration: { ...session().configuration!, providerId: "codex" }, text: liveTurn("first") });
  assert.deepEqual(shown(reconcileTimeline(turn("first", "claude-code", 1), switched)), ["history:the prompt", "history:the answer"]);
  // The window that Load all messages grew holds older turns too: the turn is still shown once.
  const all = [...turn("older", "fake-quota", 1), ...turn("first", "claude-code", 3)];
  assert.deepEqual(shown(reconcileTimeline(all, switched)), ["history:the prompt", "history:the answer", "history:the prompt", "history:the answer"]);
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
  // The identity of a text is its run: a live view that does not say its provider still matches it.
  assert.deepEqual(reconcileTimeline([entry()], session({ configuration: null })).map(row => row.source), ["history"]);
  // The host reports startedWithDelta only while no final content has established a complete baseline.
  assert.deepEqual(reconcileTimeline([entry({ eventType: "contentDelta" })], session({ text: [{ ...session().text[0], isComplete: false, startedWithDelta: true }] }))
    .map(row => row.source), ["history", "liveText"]);
  assert.deepEqual(reconcileTimeline([entry({ eventType: "contentDelta" })], session({ text: [{ ...session().text[0], isTruncated: true }] }))
    .map(row => row.source), ["history", "liveText"]);
});
