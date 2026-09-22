import test from "node:test";
import assert from "node:assert/strict";
import type { HistoryResponse } from "#neoastra";
import { buildTimelineItems, formatDetails, latestNotes, writeMarkdown } from "./timeline";

type Entry = HistoryResponse["entries"][number];

function entry(overrides: Partial<Entry>): Entry {
  return {
    offset: "1", eventType: "contentCompleted", providerId: "provider", sessionId: "session", runId: "run",
    timestamp: "2026-09-22T10:00:00Z", kind: "Assistant", phase: null, contentId: "content", activityId: null,
    parentActivityId: null, interactionId: null, name: null, text: "Hello", details: null,
    textTruncated: false, detailsTruncated: false, bodyOmitted: false, ...overrides,
  };
}

test("timeline replaces streamed deltas with completed content and preserves orphan streams", () => {
  const items = buildTimelineItems([
    entry({ offset: "1", eventType: "contentDelta", contentId: "complete", text: "partial" }),
    entry({ offset: "2", eventType: "contentCompleted", contentId: "complete", text: "final" }),
    entry({ offset: "3", eventType: "contentDelta", contentId: "stream", kind: "Reasoning", text: "one " }),
    entry({ offset: "4", eventType: "contentDelta", contentId: "stream", kind: "Reasoning", text: "two" }),
  ]);
  assert.equal(items.length, 2);
  assert.equal(items[0].markdown, "final");
  assert.equal(items[0].title, "CodeAlta");
  assert.equal(items[1].markdown, "one two");
  assert.equal(items[1].title, "Reasoning");
  assert.equal(items[1].subtitle, "Streaming");
});

test("timeline maps prompt, file, usage, details, and identifiers to dedicated display models", () => {
  const items = buildTimelineItems([
    entry({ offset: "1", eventType: "system_prompt", kind: "session_start", name: "Default", text: "**Agent prompt:** Default" }),
    entry({ offset: "2", eventType: "activity", kind: "FileChange", phase: "Completed", name: "apply_patch", text: "Changed files", details: "{\"files\":[\"a.cs\"]}", activityId: "activity" }),
    entry({ offset: "3", eventType: "sessionUpdate", kind: "UsageUpdated", text: "**Context:** 10 / 100 tokens", interactionId: "interaction" }),
  ]);
  assert.deepEqual(items.map(item => item.category), ["prompt", "file", "status"]);
  assert.equal(items[0].title, "Prompt information");
  assert.match(items[1].details!, /\n  "files"/);
  assert.ok(items[1].metadata.includes("Activity: activity"));
  assert.ok(items[2].metadata.includes("Interaction: interaction"));
});

test("notes use the latest set or clear event", () => {
  assert.equal(latestNotes([
    entry({ eventType: "notes", kind: "Set", text: "# First" }),
    entry({ offset: "2", eventType: "notes", kind: "Cleared", text: "" }),
    entry({ offset: "3", eventType: "notes", kind: "Set", text: "# Current" }),
  ]), "# Current");
});

test("details formatting and clipboard outcomes are deterministic", async () => {
  assert.equal(formatDetails("{\"value\":1}"), "{\n  \"value\": 1\n}");
  assert.equal(formatDetails("literal"), "literal");
  let copied = "";
  assert.equal(await writeMarkdown(async text => { copied = text; }, "# Result"), "copied");
  assert.equal(copied, "# Result");
  assert.equal(await writeMarkdown(async () => { throw new Error("denied"); }, "x"), "failed");
});
