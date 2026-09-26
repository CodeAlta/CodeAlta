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

test("journal identities containing separators cannot alias another provider/run or tool", () => {
  const rows = buildTimelineItems([
    entry({ offset: "1", eventType: "contentDelta", providerId: "p\0r", runId: "x", contentId: "c", text: "stream" }),
    entry({ offset: "2", providerId: "p", runId: "r\0x", contentId: "c", text: "complete" }),
    entry({ offset: "3", eventType: "activity", providerId: "p\0r", runId: "x", activityId: "tool", kind: "ToolCall", phase: "Started" }),
    entry({ offset: "4", eventType: "activity", providerId: "p", runId: "r\0x", activityId: "tool", kind: "ToolCall", phase: "Completed" }),
  ]);
  assert.deepEqual(rows.map(row => row.key), ["1", "2", "3", "4"]);
  const output = buildTimelineItems([
    entry({ offset: "5", eventType: "activity", providerId: "p\0r", runId: "x", activityId: "tool", kind: "ToolCall", phase: "Started" }),
    entry({ offset: "6", providerId: "p", runId: "r\0x", eventType: "contentCompleted", kind: "ToolOutput",
      contentId: "output", parentActivityId: "tool", text: "Distinct tool result" }),
  ]);
  assert.deepEqual(output.map(row => row.key), ["5", "6"]);
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

test("persisted ToolCall retains the exact otherwise-hidden Canceled message in details and Copy", () => {
  const text = "  Supplied cancellation message\r\nnot an inferred outcome  ";
  const details = JSON.stringify({ command: "echo fixture", result: { success: true } });
  const [item] = buildTimelineItems([entry({ eventType: "activity", kind: "ToolCall", phase: "Canceled",
    name: "shell_command", text, details })]);
  assert.equal(item.title, "shell_command");
  assert.equal(item.subtitle, "Canceled · Tool Call");
  assert.equal(item.summary, "echo fixture");
  assert.equal(item.markdown, null);
  assert.deepEqual({ detail: item.detailMarkdown, copy: item.copyMarkdown }, {
    detail: text, copy: `\`\`\`\necho fixture\n\`\`\`\n\n${text}\n\n${formatDetails(details)}`,
  });
});

test("supplied tool messages preserve phase, bounds, branches and provider/run separation", () => {
  const base = entry({ eventType: "activity", kind: "ToolCall", activityId: "same", name: "fixture",
    details: JSON.stringify({ command: "literal command", result: { output: "DO NOT PROJECT", success: true } }) });
  const inputs = ["Started", "Progressed", "Completed"].map((phase, i) => ({ ...base, phase, offset: String(i),
    providerId: i === 1 ? "other" : "provider", runId: i === 2 ? "other-run" : "run",
    text: `  supplied ${phase}\r\n\t`, textTruncated: true, detailsTruncated: true, bodyOmitted: true }));
  const items = buildTimelineItems(inputs);
  assert.equal(items.length, inputs.length);
  items.forEach((item, i) => {
    assert.equal(item.detailMarkdown, inputs[i].text);
    assert.equal(item.markdown, null);
    assert.equal(item.subtitle, `${inputs[i].phase} · Tool Call`);
    assert.equal(item.summary, "literal command");
    assert.equal(item.truncated, true);
    assert.equal(item.bodyOmitted, true);
    assert.ok(item.metadata.includes(`Provider: ${inputs[i].providerId}`));
    assert.ok(item.metadata.includes(`Run: ${inputs[i].runId}`));
    inputs.forEach((input, j) => assert.equal(item.copyMarkdown!.includes(input.text), i === j));
  });
  for (const text of [null, "", " \r\n "]) {
    const [item] = buildTimelineItems([{ ...base, phase: "Canceled", text }]);
    assert.equal(item.detailMarkdown, text || null);
    assert.equal(item.markdown, null);
  }
  for (const overrides of [{ phase: "Failed" }, { details: null }, { details: '{"command":"cut' }]) {
    const [item] = buildTimelineItems([{ ...base, phase: "Canceled", text: "supplied", ...overrides,
      detailsTruncated: true, bodyOmitted: true }]);
    assert.equal(item.markdown, "supplied");
    assert.equal(item.detailMarkdown, null);
    assert.equal(item.copyMarkdown!.split("supplied").length - 1, 1);
    assert.equal(item.truncated, true);
    assert.equal(item.bodyOmitted, true);
  }
  for (const kind of ["FileChange", "CommandExecution", "WebSearch"]) {
    const [item] = buildTimelineItems([{ ...base, kind, phase: "Canceled", text: "unchanged hidden" }]);
    assert.equal(item.detailMarkdown, null);
    assert.equal(item.markdown, null);
    assert.equal(item.copyMarkdown!.includes("unchanged hidden"), false);
  }
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

for (const details of ['{"files":["literal.cs"]}', '{"command":"literal command"}']) {
  test(`FileChange disclosure identifies the record with ${details}`, () => {
    const [item] = buildTimelineItems([entry({ eventType: "activity", kind: "FileChange", details })]);
    assert.equal(item.detailsLabel, "File change record details");
  });
}

test("FileChange label preserves every other field, bounds and provider/run isolation", () => {
  for (const phase of ["Started", "Completed", "Failed", "Canceled"]) {
    for (const details of [null, '{"path":"cut', '{"files":["../literal.cs"]}', '{"command":"literal command"}']) {
      const inputs = ["FileChange", "filechange", "FILECHANGE"].map((kind, i) => entry({
        eventType: "activity", kind, phase, details, offset: String(i), activityId: "same", name: "fixture",
        providerId: i === 1 ? "other-provider" : "provider", runId: i === 2 ? "other-run" : "run",
        text: `supplied ${i}`, textTruncated: true, detailsTruncated: true, bodyOmitted: true,
      }));
      const items = buildTimelineItems(inputs);
      assert.equal(items.length, inputs.length);
      items.forEach((item, i) => {
        // CommandExecution shares the pre-existing generic activity presentation, without
        // ToolCall's distinct supplied-message retention. Only file classification/label differ.
        const [generic] = buildTimelineItems([{ ...inputs[i], kind: "CommandExecution" }]);
        const kindLabel = inputs[i].kind === "FileChange" ? "File Change" : inputs[i].kind === "filechange" ? "Filechange" : "FILECHANGE";
        assert.deepEqual(item, { ...generic, category: "file", icon: "file",
          subtitle: `${phase} · ${kindLabel}`, detailsLabel: "File change record details" });
      });
    }
  }
  for (const kind of ["ToolCall", "CommandExecution", "WebSearch"]) {
    for (const details of [null, '{"command":"literal command"}']) {
      const [item] = buildTimelineItems([entry({ eventType: "activity", kind, details })]);
      assert.equal(item.detailsLabel, details ? "Command and result" : "Tool details");
    }
  }
  for (const input of [entry({ kind: "FileChangeOutput" }),
    entry({ eventType: "sessionUpdate", kind: "DiffUpdated" }), entry({ kind: "FileChange" })]) {
    assert.equal(buildTimelineItems([input])[0].detailsLabel, "Details");
  }
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
