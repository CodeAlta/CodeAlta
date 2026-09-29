import assert from "node:assert/strict";
import test from "node:test";
import type { HistoryEntry } from "./timeline";
import { reconcileTimeline } from "./reconcileTimeline";
import { groupTimelineTools } from "./toolGroups";

const entry = (offset: string, changes: Partial<HistoryEntry> = {}): HistoryEntry => ({ offset,
  eventType: "activity", kind: "ToolCall", providerId: "provider", runId: "run", sessionId: "session",
  activityId: `tool-${offset}`, parentActivityId: null, phase: "Completed", contentId: null, interactionId: null,
  timestamp: "2026-09-27T00:00:00Z", name: "Read", text: "Done", details: null,
  tool: null, files: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, ...changes });

test("adjacent known journal tools form bounded sub-card groups without changing records", () => {
  const entries = Array.from({ length: 14 }, (_, index) => entry(String(index)));
  const rows = reconcileTimeline(entries, null);
  const groups = groupTimelineTools(rows, entries);
  assert.deepEqual(groups.map(group => group.rows.length), [12, 2]);
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
  const rows = reconcileTimeline([entry("1")], null);
  rows.push({ source: "liveTool", key: "live", row: { timestamp: null, sequence: null, activityId: "live", providerId: "provider", runId: "run", phase: "Started", name: "Read", isNameTruncated: false } });
  assert.ok(groupTimelineTools(rows, [entry("1")]).every(group => group.rows.length === 1));
});

test("journal plumbing between tool calls does not split their visual group", () => {
  const entries = [entry("1"), entry("2", { eventType: "raw" }), entry("3", { eventType: "notes" }),
    entry("4", { eventType: "sessionUpdate", kind: "UsageUpdated" }), entry("5")];
  assert.deepEqual(groupTimelineTools(reconcileTimeline(entries, null), entries).map(group => group.rows.length), [2]);
  entries[2] = entry("3", { eventType: "contentCompleted", kind: "Assistant", text: "Explanation" });
  assert.deepEqual(groupTimelineTools(reconcileTimeline(entries, null), entries).map(group => group.rows.length), [1, 1, 1]);
});

test("retained live tool groups stay separate from journal groups and other runs", () => {
  const rows = reconcileTimeline([entry("1")], null);
  for (const [activityId, runId] of [["a", "run"], ["b", "run"], ["c", "other"]])
    rows.push({ source: "liveTool", key: activityId, row: { timestamp: null, sequence: null, activityId, providerId: "provider", runId, phase: "Started", name: "Read", isNameTruncated: false } });
  assert.deepEqual(groupTimelineTools(rows, [entry("1")]).map(group => group.rows.length), [1, 2, 1]);
});
