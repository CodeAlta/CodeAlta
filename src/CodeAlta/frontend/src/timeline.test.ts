import test from "node:test";
import assert from "node:assert/strict";
import type { HistoryResponse } from "#neoastra";
import { buildTimelineItems, formatDetails, latestNotes, writeMarkdown } from "./timeline";

type Entry = HistoryResponse["entries"][number];

test("typed completed output retains authoritative totals with its exact activity", () => {
  const activity = entry({ eventType: "activity", kind: "ToolCall", activityId: "tool", phase: "Completed", name: "alta", text: null });
  const output = entry({ offset: "2", kind: "ToolOutput", parentActivityId: "tool", text: "bounded preview", textTruncated: true,
    tool: { primary: null, isCommand: false, output: "bounded preview", outputLines: 27, outputBytes: 1434, fields: [] } });
  const items = buildTimelineItems([activity, output]);
  assert.equal(items.length, 1);
  assert.equal(items[0].toolOutputLines, 27);
  assert.equal(items[0].toolOutputBytes, 1434);
  assert.deepEqual(items[0].toolFields, [{ path: "content", text: "bounded preview", truncated: true }]);
  for (const patch of [{ sessionId: "other" }, { providerId: "other" }, { runId: "other" }, { parentActivityId: "other" }]) {
    const separate = buildTimelineItems([activity, { ...output, ...patch }]);
    assert.equal(separate.length, 2);
    assert.equal(separate[0].toolOutputBytes, undefined);
  }
  assert.equal(buildTimelineItems([activity, { ...output, tool: null }])[0].toolOutputBytes, undefined);
});

test("a prompt delivered by another session is shown as an agent message without its envelope", () => {
  const text = "[CodeAlta delegated-agent message]\r\nSource session: 01a105f5-60d1\r\nKind: answer\r\n\r\n[CodeAlta child-session answer update]\r\nRun: r\r\n\r\nPONG";
  const [item] = buildTimelineItems([entry({ eventType: "contentCompleted", kind: "User", text })]);

  assert.equal(item.category, "user");
  assert.equal(item.delegated, true);
  assert.equal(item.title, "Agent message");
  assert.equal(item.subtitle, "Answer · 01a105f5");
  assert.equal(item.markdown, "PONG");
  assert.ok(item.metadata.includes("From session: 01a105f5-60d1"));
  const [own] = buildTimelineItems([entry({ eventType: "contentCompleted", kind: "User", text: "PONG" })]);
  assert.equal(own.delegated, undefined);
  assert.equal(own.title, "You");
});

test("TUI status-only updates do not become timeline cards", () => {
  assert.deepEqual(buildTimelineItems(["Idle", "UsageUpdated", "Shutdown"].map(kind => entry({ eventType: "sessionUpdate", kind }))), []);
});

test("persisted tool inspection projects only supplied structured fields without changing raw details", () => {
  const details = '{"arguments":{"command":"<literal>\\n"},"result":{"content":"  output\\r\\n","detailedContent":"extra"},"error":{"message":"literal failure"}}';
  const item = buildTimelineItems([entry({ eventType: "activity", kind: "ToolCall", name: "literal_tool", phase: "Started", details })])[0]!;
  assert.equal(item.toolRecord?.fields.find(field => field.path === "result.content")?.text, "  output\r\n");
  assert.equal(item.toolRecord?.raw, details);
  assert.equal(item.details, formatDetails(details));
});

test("tool inspection refuses ambiguous, malformed, oversized and truncated JSON", () => {
  for (const details of ['{"arguments":1,"arguments":2}', '{"arguments":{"a":1,"\\u0061":2}}', '{', '[]', '{}',
    '{"result":{"content":42}}', ' '.repeat(8193), '{"arguments":' + '['.repeat(33) + '0' + ']'.repeat(33) + '}']) {
    const item = buildTimelineItems([entry({ eventType: "activity", kind: "ToolCall", details })])[0]!;
    assert.equal(item.toolRecord, undefined);
    assert.equal(item.details, formatDetails(details));
  }
  for (const patch of [{ detailsTruncated: true }, { eventType: "contentDelta" }, { kind: "Unknown" }])
    assert.equal(buildTimelineItems([entry({ eventType: "activity", kind: "ToolCall", details: '{"arguments":{}}', ...patch })])[0]?.toolRecord, undefined);
});

test("file changes inspect supplied paths and count only validated per-file hunks without changing raw Copy", () => {
  const details = JSON.stringify({ changes: [
    { path: "<script>literal</script>.ts", kind: { type: "update" }, diff: "@@ -1 +1,2 @@\n-old\n+new\n+line\n" },
    { path: "other.ts", kind: { type: "unknown-provider-kind" } },
  ] });
  const item = buildTimelineItems([entry({ eventType: "activity", kind: "FileChange", phase: "Failed", text: null, details })])[0]!;
  assert.equal(item.fileChanges?.rows.length, 2);
  assert.deepEqual(item.fileChanges?.rows[0], { index: 0, path: "<script>literal</script>.ts", kind: "update", diff: "@@ -1 +1,2 @@\n-old\n+new\n+line\n", counts: { added: 2, removed: 1 } });
  assert.equal(item.fileChanges?.rows[1]?.counts, null);
  assert.equal(item.copyMarkdown, formatDetails(details));
});

test("a user message lists its images and no other record does", () => {
  const images = [{ index: 0, title: "Screenshot", mediaType: "image/png" }, { index: 1, title: "Diagram", mediaType: null }];
  const [user, assistant, onlyImages] = buildTimelineItems([entry({ kind: "User", text: "Look", images }),
    entry({ offset: "2", contentId: "other", images }), entry({ offset: "3", contentId: "third", kind: "User", text: "", images: images.slice(0, 1) })]);
  assert.deepEqual(user.images, images);
  assert.equal(user.markdown, "Look");
  assert.equal(assistant.images, undefined);
  // A prompt of images only has no text, and its card still has its images.
  assert.equal(onlyImages.markdown, "");
  assert.deepEqual(onlyImages.images, images.slice(0, 1));
  assert.equal(buildTimelineItems([entry({ kind: "User", images: [{ index: 3, title: "Misplaced", mediaType: null }] })])[0].images, undefined);
});

test("the images a tool gave the model get a card after the tile of its call", () => {
  const images = [{ index: 0, title: "shot.png", mediaType: "image/png" }, { index: 1, title: "page", mediaType: "image/png" }];
  const activity = entry({ eventType: "activity", kind: "ToolCall", activityId: "tool", phase: "Completed", name: "view_image", text: null });
  const output = entry({ offset: "2", kind: "ToolOutput", parentActivityId: "tool", text: "Viewed image shot.png.\n[Image: shot.png (image/png, 640x480)]",
    details: '{"toolName":"view_image"}', images,
    tool: { primary: null, isCommand: false, output: "Viewed image shot.png.", outputLines: 2, outputBytes: 60, fields: [] } });

  const [tile, card, ...rest] = buildTimelineItems([activity, output]);

  // The tile keeps the text of the output; the card has the images and is read at the offset of the output.
  assert.equal(rest.length, 0);
  assert.equal(tile.category, "tool");
  assert.equal(tile.toolOutput, "Viewed image shot.png.");
  assert.equal(tile.images, undefined);
  assert.equal(card.category, "image");
  assert.equal(card.key, "2");
  assert.equal(card.icon, "fileImage");
  assert.equal(card.subtitle, "shot.png, page");
  assert.deepEqual(card.images, images);
  assert.equal(card.markdown, null);
  assert.equal(card.details, null);
  assert.equal(card.copyMarkdown, null);

  // An output without a tile on screen is the card itself, and an output without images adds nothing.
  const [alone] = buildTimelineItems([output]);
  assert.equal(alone.category, "image");
  assert.equal(buildTimelineItems([activity, { ...output, images: null }]).length, 1);
  // A malformed image list shows no image: the output stays the text of its tile.
  assert.equal(buildTimelineItems([activity, { ...output, images: [{ index: 4, title: "x", mediaType: null }] }]).length, 1);
});

function entry(overrides: Partial<Entry>): Entry {
  return {
    offset: "1", eventType: "contentCompleted", providerId: "provider", sessionId: "session", runId: "run",
    timestamp: "2026-09-22T10:00:00Z", kind: "Assistant", phase: null, contentId: "content", activityId: null,
    parentActivityId: null, interactionId: null, name: null, text: "Hello", details: null,
    tool: null, files: null, images: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, ...overrides,
  };
}

test("file inspection refuses malformed, oversized and truncated structures without hiding raw details", () => {
  for (const details of [null, "{", "[]", "{}", JSON.stringify({ diff: "aggregate only" }), " ".repeat(8193), JSON.stringify({ changes: [null, { path: 1 }, { path: "x".repeat(513) }] })]) {
    const item = buildTimelineItems([entry({ eventType: "activity", kind: "FileChange", details })])[0]!;
    assert.equal(item.fileChanges?.rows.length, 0); assert.equal(item.fileChanges?.partial, true);
    assert.equal(item.details, formatDetails(details));
  }
  const details = JSON.stringify({ path: " literal path ", operation: "unknown" });
  const item = buildTimelineItems([entry({ eventType: "sessionUpdate", kind: "DiffUpdated", details, bodyOmitted: true })])[0]!;
  assert.equal(item.fileChanges?.rows[0]?.path, " literal path "); assert.equal(item.fileChanges?.partial, true);
  assert.equal(item.fileChanges?.rows[0]?.kind, "unknown"); assert.equal(item.fileChanges?.rows[0]?.counts, null);
  assert.equal(buildTimelineItems([entry({ eventType: "activity", kind: "FileChange", details, detailsTruncated: true })])[0]?.fileChanges?.rows.length, 0);
  assert.equal(buildTimelineItems([entry({ details })])[0]?.fileChanges, undefined);
  const bounded = buildTimelineItems([entry({ eventType: "activity", kind: "FileChange", details: JSON.stringify({ changes: Array.from({ length: 33 }, () => ({ path: "same" })) }) })])[0]!;
  assert.equal(bounded.fileChanges?.rows.length, 32); assert.equal(bounded.fileChanges?.partial, true);
  assert.deepEqual(bounded.fileChanges?.rows.map(row => row.index), Array.from({ length: 32 }, (_, i) => i));
});

test("hunk counts are bounded textual counts and never guess missing or malformed diff totals", () => {
  for (const diff of ["", "binary content", "@@ -1 +1 @@\n-old", "@@ -1 +1 @@\n-old\n+new\n+extra", "@@ -1,9999 +1 @@\n-old\n+new", "x".repeat(4097), "\n".repeat(513), "@@ -1 +1 @@\n-old\n+new\ndiff --git a/b b/b"]) {
    const item = buildTimelineItems([entry({ eventType: "activity", kind: "FileChange", details: JSON.stringify({ changes: [{ path: "a", diff }] }) })])[0]!;
    assert.equal(item.fileChanges?.rows[0]?.counts, null, diff.slice(0, 60));
  }
  // The no-newline marker belongs to the final new-side line, not an earlier hunk.
  const details = JSON.stringify({ changes: [{ path: "a", diff: "--- a/a\r\n+++ b/a\r\n@@ -1,2 +1,2 @@\r\n context\r\n-old\r\n+new\r\n@@ -9,0 +9,1 @@\r\n+next\r\n\\ No newline at end of file\r\n" }] });
  const make = (sessionId: string, detailsValue = details) => buildTimelineItems([entry({ sessionId, eventType: "activity", kind: "FileChange", details: detailsValue })])[0]!;
  assert.deepEqual(make("one").fileChanges?.rows[0]?.counts, { added: 2, removed: 1 });
  assert.notEqual(make("one").fileChanges?.source, make("two").fileChanges?.source);
  assert.notEqual(make("one").fileChanges?.source, make("one", details.replace('"a"', '"b"')).fileChanges?.source);
});

test("hunk parser rejects ambiguous preambles, overlapping ranges and misplaced markers", () => {
  const hunk = "@@ -1 +1 @@\n-old\n+new\n";
  const counts = (diff: string) => buildTimelineItems([entry({ eventType: "activity", kind: "FileChange",
    details: JSON.stringify({ changes: [{ path: "a", diff }] }) })])[0].fileChanges?.rows[0]?.counts;
  for (const diff of [
    `diff --git a/a b/a\ndiff --git a/b b/b\n${hunk}`,
    `--- a/a\n+++ b/a\n--- a/b\n+++ b/b\n${hunk}`,
    `+++ b/a\n--- a/a\n${hunk}`, `--- a/a\n${hunk}`, `index abc..def\n${hunk}`,
    `${hunk}${hunk}`, `${hunk}@@ -1 +3 @@\n-old\n+new\n`,
    "@@ -0 +0 @@\n-old\n+new\n", "@@ -1,0 +1,0 @@\n",
    "@@ -1 +1 @@\n\\ No newline at end of file\n-old\n+new\n",
    `${hunk}\\ No newline at end of file\n\\ No newline at end of file\n`,
    `diff --git a/a b/a\nindex abc..def\n${hunk}`,
  ]) assert.equal(counts(diff), null, JSON.stringify(diff));
  for (const prefix of ["", "--- a/a\n+++ b/a\n", "diff --git a/a b/a\nindex abc..def 100644\n--- a/a\n+++ b/a\n"]) {
    assert.deepEqual(counts(prefix + hunk), { added: 1, removed: 1 });
    assert.deepEqual(counts((prefix + hunk).replaceAll("\n", "\r\n")), { added: 1, removed: 1 });
  }
  assert.deepEqual(counts("@@ -1 +1 @@\n-old\n\\ No newline at end of file\n+new\n\\ No newline at end of file"), { added: 1, removed: 1 });
  assert.deepEqual(counts(`${hunk}@@ -3 +3 @@\n-old\n+new`), { added: 2, removed: 2 });
  assert.deepEqual(counts("@@ -0,0 +1 @@\n+new"), { added: 1, removed: 0 });
  assert.deepEqual(counts("@@ -1 +0,0 @@\n-old"), { added: 0, removed: 1 });
});

test("malformed git metadata and premature no-newline markers never certify counts", () => {
  const hunk = "@@ -1 +1 @@\n-old\n+new\n";
  for (const diff of [
    `diff --git malformed\n--- a/a\n+++ b/a\n${hunk}`,
    `diff --git a/a b/a\nindex garbage\n--- a/a\n+++ b/a\n${hunk}`,
    "@@ -1,2 +1 @@\n-old\n\\ No newline at end of file\n-more\n+new",
    "@@ -1 +1,2 @@\n-old\n+new\n\\ No newline at end of file\n+more",
    `${hunk}\\ No newline at end of file\n@@ -3 +3 @@\n-old\n+new`,
  ]) {
    const item = buildTimelineItems([entry({ eventType: "activity", kind: "FileChange", details: JSON.stringify({ path: "a", diff }) })])[0];
    assert.equal(item.fileChanges?.rows[0]?.counts, null, JSON.stringify(diff));
  }
});

test("timeline replaces streamed deltas with completed content and preserves orphan streams", () => {
  const items = buildTimelineItems([
    entry({ offset: "1", eventType: "contentDelta", contentId: "complete", text: "partial" }),
    entry({ offset: "2", eventType: "contentCompleted", contentId: "complete", text: "final" }),
    entry({ offset: "3", eventType: "contentDelta", contentId: "stream", kind: "Reasoning", text: "one " }),
    entry({ offset: "4", eventType: "contentDelta", contentId: "stream", kind: "Reasoning", text: "two" }),
  ]);
  assert.equal(items.length, 2);
  assert.equal(items[0].markdown, "final");
  assert.equal(items[0].title, "Assistant");
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

test("timeline keeps prompt drill-down content but leaves usage in the status surface", () => {
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
  assert.equal(items.length, 1);
});

test("complete aggregate diff projects file rows and validated supplied hunk counts", () => {
  const details = JSON.stringify({ diff: "diff --git a/src/a.ts b/src/a.ts\n--- a/src/a.ts\n+++ b/src/a.ts\n@@ -1 +1,2 @@\n-old\n+new\n+line\n" });
  const item = buildTimelineItems([entry({ eventType: "sessionUpdate", kind: "DiffUpdated", details })])[0];
  assert.equal(item.category, "file");
  assert.equal(item.fileChanges?.rows[0].path, "src/a.ts");
  assert.deepEqual(item.fileChanges?.rows[0].counts, { added: 2, removed: 1 });
  assert.equal(item.details, formatDetails(details));
  assert.equal(buildTimelineItems([entry({ eventType: "sessionUpdate", kind: "DiffUpdated", details, detailsTruncated: true })])[0].fileChanges?.rows.length, 0);
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

test("FileChange adds explicit unavailable projection while preserving legacy fields, bounds and provider/run isolation", () => {
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
        // ToolCall's distinct supplied-message retention. Compare every legacy field,
        // plus the explicit unavailable projection for these truncated/missing records.
        const [generic] = buildTimelineItems([{ ...inputs[i], kind: "CommandExecution" }]);
        const kindLabel = inputs[i].kind === "FileChange" ? "File Change" : inputs[i].kind === "filechange" ? "Filechange" : "FILECHANGE";
        assert.deepEqual(item, { ...generic, category: "file", icon: "file",
          subtitle: `${phase} · ${kindLabel}`, detailsLabel: "File change record details",
          fileChanges: { source: "", rows: [], partial: true } });
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
  assert.deepEqual(buildTimelineItems([entry({ eventType: "notes", kind: "Set", text: "Sidebar only" })]), []);
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
