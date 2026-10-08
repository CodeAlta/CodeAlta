import assert from "node:assert/strict";
import test from "node:test";
import type { HistoryResponse, SessionDisplayView } from "#neoastra";
import { reconcileTimeline } from "./reconcileTimeline";
import { groupTimelineTools } from "./toolGroups";
import { sameTimelineGroup, type TimelineGroupProps } from "./timelineGroupView";

type Entry = HistoryResponse["entries"][number];
const entry = (offset: string, overrides: Partial<Entry> = {}): Entry => ({ offset, eventType: "contentCompleted", providerId: "provider",
  sessionId: "session", runId: "run", timestamp: `2026-10-08T00:00:${offset.padStart(2, "0")}Z`, kind: "Assistant", phase: null,
  contentId: `content-${offset}`, activityId: null, parentActivityId: null, interactionId: null, sourceSessionId: null, name: null,
  tool: null, files: null, images: null, text: `message ${offset}`, details: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, ...overrides });
const call = (offset: string, id: string): Entry => entry(offset, { eventType: "activity", kind: "ToolCall", activityId: id, contentId: null, name: "shell_command", phase: "Completed", text: null });
const live = (text: string): SessionDisplayView => ({ sessionId: "session", revision: "1", lifecycle: null, queuedPromptCount: null,
  configuration: { providerId: "provider", providerKey: null, modelId: null, reasoningEffort: null, agentPromptId: null }, statusKind: null, statusMessage: null,
  text: [{ timestamp: "2026-10-08T00:01:00Z", sequence: "1", runId: "run", contentId: "streamed", kind: "Assistant", text, isComplete: false,
    isTruncated: false, startedWithDelta: true }], toolActivities: [], metadataTruncated: false,
  transportTruncated: false, evictedTextItems: "0", evictedToolActivities: "0", unsupportedEvents: "0" });

const stable = { sessionId: "session", canInspect: () => true, onOpenSource: () => {}, onOpenTool: () => {} };
const revision = { sessionId: "session", length: "100", lastWriteUtcTicks: "5" } as NonNullable<TimelineGroupProps["revision"]>;
function groups(entries: Entry[], view: SessionDisplayView | null, extra: Partial<TimelineGroupProps> = {}): TimelineGroupProps[] {
  return groupTimelineTools(reconcileTimeline(entries, view), entries)
    .map(group => ({ ...stable, group, revision: null, sources: new Map(), echoImages: new Map(), ...extra }));
}

test("a live update leaves every group but the one of the streamed text as it was", () => {
  const entries = [entry("1", { kind: "User" }), call("2", "a"), call("3", "b"), entry("4"), call("5", "c"), entry("6")];
  const before = groups(entries, live("Stream")), after = groups(entries, live("Streamed text"));
  assert.equal(before.length, after.length);
  // The rows and the groups are rebuilt as new objects; what they show is the same.
  assert.notEqual(before[0].group, after[0].group);
  assert.notEqual(before[0].group.rows[0], after[0].group.rows[0]);
  const same = before.map((group, index) => sameTimelineGroup(group, after[index]));
  assert.deepEqual(same, [true, true, true, true, true, false]);
  assert.equal(after.at(-1)!.group.rows[0].source, "liveText");
});

test("a group is rendered again when a row, its place in the journal or its images change", () => {
  const entries = [entry("1", { kind: "User" }), call("2", "a"), call("3", "b")];
  const [prompt, tools] = groups(entries, null);
  assert.equal(tools.group.rows.length, 2);
  assert.equal(sameTimelineGroup(tools, groups(entries, null)[1]), true);
  // The call ends in the journal: another record, another item.
  const ended = [entries[0], entries[1], call("3", "b"), call("4", "c")];
  assert.equal(sameTimelineGroup(tools, groups(ended, null)[1]), false);
  // Another session, or another way to open a tool, is another group.
  assert.equal(sameTimelineGroup(tools, { ...tools, sessionId: "other" }), false);
  assert.equal(sameTimelineGroup(tools, { ...tools, onOpenTool: () => {} }), false);
  // A refresh of the history gives a new revision and a new map: the row is the same while its range is.
  const sources = (start: string, end: string) => new Map([["1", { start, end }]]) as unknown as TimelineGroupProps["sources"];
  const placed = { ...prompt, revision, sources: sources("1", "9") };
  assert.equal(sameTimelineGroup(placed, { ...prompt, revision: { ...revision }, sources: sources("1", "9") }), true);
  assert.equal(sameTimelineGroup(placed, { ...prompt, revision, sources: sources("1", "12") }), false);
  assert.equal(sameTimelineGroup(placed, { ...prompt, revision: { ...revision, length: "200" }, sources: sources("1", "9") }), false);
  assert.equal(sameTimelineGroup(placed, prompt), false);
  // The images of a prompt being sent are compared by what they are, not by the map that holds them.
  const images = (key: string) => new Map([[prompt.group.rows[0].key, { key }]]) as unknown as TimelineGroupProps["echoImages"];
  assert.equal(sameTimelineGroup({ ...prompt, echoImages: images("one") }, { ...prompt, echoImages: images("one") }), true);
  assert.equal(sameTimelineGroup({ ...prompt, echoImages: images("one") }, { ...prompt, echoImages: images("two") }), false);
  assert.equal(sameTimelineGroup({ ...prompt, echoImages: images("one") }, prompt), false);
});

test("the rows of a window keep their items across live updates, pieces of text joined by the journal included", () => {
  const entries = [entry("1", { kind: "User" }),
    entry("2", { eventType: "contentDelta", contentId: "answer", text: "Hello " }), entry("3", { eventType: "contentDelta", contentId: "answer", text: "world" }),
    call("4", "a")];
  const first = reconcileTimeline(entries, live("a")), second = reconcileTimeline(entries, live("ab"));
  const items = (rows: typeof first) => rows.flatMap(row => row.source === "history" ? [row.item] : []);
  assert.deepEqual(items(first).map(item => item.markdown ?? item.category), ["message 1", "Hello world", "tool"]);
  assert.equal(items(first).length, items(second).length);
  items(first).forEach((item, index) => assert.equal(item, items(second)[index]));
  // Another window, even of the same records, is read again.
  assert.deepEqual(items(reconcileTimeline([...entries], null)).map(item => item.markdown ?? item.category), ["message 1", "Hello world", "tool"]);
});
