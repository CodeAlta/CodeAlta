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

test("timeline maps prompts and usage to compact summaries with drill-down content", () => {
  const items = buildTimelineItems([
    entry({ offset: "1", eventType: "system_prompt", kind: "session_start", name: "Default",
      text: "**Reason:** session_start\n\n**Provider mapping:** native · applied\n\n**Approximate tokens:** 14 total (3 system, 11 developer)\n\n**Change:** initial\n\n### System message\n\nLong prompt" }),
    entry({ offset: "2", eventType: "sessionUpdate", kind: "UsageUpdated",
      text: "Usage refreshed\n\n**Context:** 6,400 / 128,000 tokens (5%)\n\n**Model:** model-1\n\n**Input tokens:** 100\n\n**Output tokens:** 25\n\n**Cost:** 0.01" }),
  ]);
  assert.equal(items[0].title, "System prompt recorded");
  assert.equal(items[0].subtitle, "Default · 14 tokens · Session start");
  assert.equal(items[0].summary, "Provider mapping: native · applied");
  assert.equal(items[0].markdown, null);
  assert.match(items[0].detailMarkdown!, /Long prompt/);
  assert.equal(items[1].title, "Context 6,400 / 128,000 tokens (5%)");
  assert.equal(items[1].summary, "model-1 · 100 in · 25 out · 0.01 cost");
  assert.equal(items[1].detailsLabel, "Usage details");
});

test("tool activities expose the tool and first command while folding duplicate lifecycle/output events", () => {
  const details = JSON.stringify({ toolName: "shell_command", arguments: { command: "dotnet test -c Release\necho ignored" }, result: { success: true, output: "Passed" } });
  const items = buildTimelineItems([
    entry({ offset: "1", eventType: "activity", kind: "ToolCall", phase: "Started", activityId: "tool-1", name: "shell_command", text: null, details }),
    entry({ offset: "2", eventType: "contentCompleted", kind: "ToolOutput", contentId: "output", parentActivityId: "tool-1", text: "Passed", details }),
    entry({ offset: "3", eventType: "activity", kind: "ToolCall", phase: "Completed", activityId: "tool-1", name: "shell_command", text: null, details }),
  ]);
  assert.equal(items.length, 1);
  assert.equal(items[0].title, "shell_command");
  assert.equal(items[0].subtitle, "Completed · Tool Call");
  assert.equal(items[0].summary, "dotnet test -c Release");
  assert.equal(items[0].summaryIsCode, true);
  assert.match(items[0].details!, /"command": "dotnet test/);
});

test("model changes show provider, model, and reasoning without a verbose primary body", () => {
  const [item] = buildTimelineItems([entry({ eventType: "sessionUpdate", kind: "ModelChanged", text: "Model selection changed.",
    details: JSON.stringify({ providerKey: "openai", modelId: "gpt-5", reasoningEffort: "high" }) })]);
  assert.equal(item.title, "Model · gpt-5");
  assert.equal(item.subtitle, "Provider openai");
  assert.equal(item.summary, "Reasoning: High");
  assert.equal(item.markdown, null);
});

test("raw persistence records are not rendered as unavailable provider cards", () => {
  const items = buildTimelineItems([
    entry({ eventType: "raw", text: null, details: null, bodyOmitted: true }),
    entry({ offset: "2", eventType: "error", text: "Visible error" }),
  ]);
  assert.equal(items.length, 1);
  assert.equal(items[0].title, "Error");
});

test("timeline preserves bounded details and identifiers behind disclosure", () => {
  const [item] = buildTimelineItems([
    entry({ eventType: "activity", kind: "FileChange", phase: "Completed", name: "apply_patch", text: "Changed files", details: "{\"files\":[\"a.cs\"]}", activityId: "activity" }),
  ]);
  assert.equal(item.category, "file");
  assert.match(item.details!, /\n  "files"/);
  assert.ok(item.metadata.includes("Activity: activity"));
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
