import assert from "node:assert/strict";
import test from "node:test";
import type { HistoryEntry } from "./timeline";
import { reconcileTimeline } from "./reconcileTimeline";
import { groupTimelineTools, toolGroupLimit } from "./toolGroups";

const entry = (offset: string, changes: Partial<HistoryEntry> = {}): HistoryEntry => ({ offset,
  eventType: "activity", kind: "ToolCall", providerId: "provider", runId: "run", sessionId: "session",
  activityId: `tool-${offset}`, parentActivityId: null, phase: "Completed", contentId: null, interactionId: null, sourceSessionId: null,
  timestamp: "2026-09-27T00:00:00Z", name: "Read", text: "Done", details: null,
  tool: null, files: null, images: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, ...changes });

test("adjacent known journal tools form bounded sub-card groups without changing records", () => {
  const entries = Array.from({ length: toolGroupLimit + 2 }, (_, index) => entry(String(index)));
  const rows = reconcileTimeline(entries, null);
  const groups = groupTimelineTools(rows, entries);
  assert.deepEqual(groups.map(group => group.rows.length), [toolGroupLimit, 2]);
  assert.ok(groups.every(group => group.tools));
  assert.deepEqual(groups.flatMap(group => group.rows), rows);
});

test("unknown identity and provider/run/parent boundaries prevent grouping", () => {
  for (const changes of [{ runId: null }, { runId: "other" }, { providerId: "other" },
    { sessionId: "other" }, { parentActivityId: "parent" }] satisfies Partial<HistoryEntry>[]) {
    const entries = [entry("1"), entry("2", changes)];
    assert.ok(groupTimelineTools(reconcileTimeline(entries, null), entries).every(group => group.rows.length === 1));
  }
  const entries = [entry("1"), entry("2", { eventType: "raw" }), entry("3")];
  assert.equal(groupTimelineTools(reconcileTimeline(entries, null), entries).length, 1);
});

test("journal plumbing between tool calls does not split their visual group", () => {
  const entries = [entry("1"), entry("2", { eventType: "raw" }), entry("3", { eventType: "notes" }),
    entry("4", { eventType: "sessionUpdate", kind: "UsageUpdated" }), entry("5")];
  assert.deepEqual(groupTimelineTools(reconcileTimeline(entries, null), entries).map(group => group.rows.length), [2]);
  // A window with other records is another array, as the history gives it: what a window says is worked out once.
  const spoken = entries.map((value, index) => index === 2 ? entry("3", { eventType: "contentCompleted", kind: "Assistant", text: "Explanation" }) : value);
  assert.deepEqual(groupTimelineTools(reconcileTimeline(spoken, null), spoken).map(group => group.rows.length), [1, 1, 1]);
});

test("records that show nothing between tool calls keep them in one group", () => {
  // What a provider writes around each call: its usage, its output, and reasoning without any text.
  const call = (id: string): HistoryEntry[] => [
    entry(`${id}0`, { activityId: id, phase: "Requested" }),
    entry(`${id}1`, { eventType: "sessionUpdate", kind: "UsageUpdated", activityId: null }),
    entry(`${id}2`, { activityId: id, phase: "Started" }),
    entry(`${id}3`, { activityId: id }),
    entry(`${id}4`, { eventType: "contentCompleted", kind: "ToolOutput", activityId: null, parentActivityId: id, text: "output" }),
    entry(`${id}5`, { eventType: "contentCompleted", kind: "Reasoning", activityId: null, text: "" }),
  ];
  const entries = [...call("1"), ...call("2"), ...call("3")];
  const rows = reconcileTimeline(entries, null);
  assert.deepEqual(groupTimelineTools(rows, entries).filter(group => group.tools).map(group => group.rows.length), [3]);
  // Reasoning that has something to show still separates the calls around it.
  const spoken = [...call("1"), entry("19", { eventType: "contentCompleted", kind: "Reasoning", activityId: null, text: "Thinking about it." }), ...call("2")];
  assert.deepEqual(groupTimelineTools(reconcileTimeline(spoken, null), spoken).filter(group => group.tools).map(group => group.rows.length), [1, 1]);
});

test("an image card between two tool calls ends the first group", () => {
  const output = entry("2", { eventType: "contentCompleted", kind: "ToolOutput", activityId: null, parentActivityId: "tool-1", contentId: "out",
    text: "Viewed.", images: [{ index: 0, title: "shot.png", mediaType: "image/png" }] });
  const entries = [entry("1", { name: "view_image" }), output, entry("3")];
  const rows = reconcileTimeline(entries, null);
  const groups = groupTimelineTools(rows, entries);

  assert.deepEqual(groups.map(group => [group.tools, group.rows.map(row => row.source === "history" ? row.item.category : row.source)]),
    [[true, ["tool"]], [false, ["image"]], [true, ["tool"]]]);
});

const live = (activityId: string, runId: string | null = "run", providerId = "provider") => ({ source: "liveTool" as const, key: activityId,
  row: { timestamp: null, sequence: null, activityId, providerId, runId, phase: "Started", name: "Read", isNameTruncated: false } });

test("a call that starts joins the calls of its run shown before it, and stays there once the journal has it", () => {
  const entries = [entry("1"), entry("2")];
  const rows = [...reconcileTimeline(entries, null), live("a"), live("b")];
  const groups = groupTimelineTools(rows, entries);
  assert.deepEqual(groups.map(group => [group.tools, group.rows.length]), [[true, 4]]);
  // The same calls, once they are written: the group is the same one, with the same first row.
  const written = [...entries, entry("3"), entry("4")];
  const after = groupTimelineTools(reconcileTimeline(written, null), written);
  assert.deepEqual(after.map(group => [group.key, group.rows.length]), [[groups[0].key, 4]]);
  // A call of the journal that follows a call still running joins it too.
  const mixed = [...reconcileTimeline([entry("1")], null), live("a"), ...reconcileTimeline([entry("5")], null)];
  assert.deepEqual(groupTimelineTools(mixed, [entry("1"), entry("5")]).map(group => group.rows.length), [3]);
});

test("a live call does not join the calls of another run, of another provider, or across a shown record", () => {
  const first = reconcileTimeline([entry("1")], null);
  for (const other of [live("a", "other"), live("a", "run", "other"), live("a", null)])
    assert.deepEqual(groupTimelineTools([...first, other], [entry("1")]).map(group => group.rows.length), [1, 1]);
  assert.deepEqual(groupTimelineTools([...first, live("a"), live("b"), live("c", "other")], [entry("1")]).map(group => group.rows.length), [3, 1]);
  // An answer of the assistant between the calls of the journal and the call that starts: a new group.
  const entries = [entry("1"), entry("2", { eventType: "contentCompleted", kind: "Assistant", activityId: null, contentId: "c", text: "Now the tests." })];
  assert.deepEqual(groupTimelineTools([...reconcileTimeline(entries, null), live("a")], entries).map(group => [group.tools, group.rows.length]), [[true, 1], [false, 1], [true, 1]]);
  // The limit of a group holds for live calls as well.
  const many = Array.from({ length: toolGroupLimit }, (_, index) => entry(String(index + 10)));
  assert.deepEqual(groupTimelineTools([...reconcileTimeline(many, null), live("a")], many).map(group => group.rows.length), [toolGroupLimit, 1]);
});
