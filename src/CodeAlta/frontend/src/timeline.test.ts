import test from "node:test";
import assert from "node:assert/strict";
import type { HistoryResponse } from "#neoastra";
import { buildTimelineItems, conversationChangedErrorKind, formatDetails, latestNotes, notSentSubtitle, unsentPrompt, writeMarkdown } from "./timeline";
import { translate } from "./localization";
import { changedParts, promptDocument, promptProblem, promptTokens, shortHash } from "./systemPrompt";

type Entry = HistoryResponse["entries"][number];

test("typed completed output retains authoritative totals with its exact activity", () => {
  const activity = entry({ eventType: "activity", kind: "ToolCall", activityId: "tool", phase: "Completed", name: "alta", text: null });
  const output = entry({ offset: "2", kind: "ToolOutput", parentActivityId: "tool", text: "bounded preview", textTruncated: true,
    tool: { primary: null, isCommand: false, output: "bounded preview", outputLines: 27, outputBytes: 1434, fields: [], added: null, removed: null, exitCode: null } });
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
  assert.equal(item.subtitle, "Answer");
  assert.equal(item.sourceSessionId, "01a105f5-60d1");
  assert.equal(item.markdown, "PONG");
  assert.ok(item.metadata.includes("From session: 01a105f5-60d1"));
  const [own] = buildTimelineItems([entry({ eventType: "contentCompleted", kind: "User", text: "PONG" })]);
  assert.equal(own.delegated, undefined);
  assert.equal(own.title, "You");
  // The prompt a parent session gives its sub-agent has no envelope: the host records who sent it.
  const [given] = buildTimelineItems([entry({ eventType: "contentCompleted", kind: "User", text: "Map the parser.", sourceSessionId: "01a105f5-60d1" })]);
  assert.deepEqual([given.delegated, given.title, given.subtitle, given.sourceSessionId, given.markdown, given.icon],
    [true, "Agent message", "Prompt", "01a105f5-60d1", "Map the parser.", "branch"]);
  // Only a prompt has a sender: the answer of the model in the same record shape is the assistant's.
  assert.equal(buildTimelineItems([entry({ eventType: "contentCompleted", kind: "Assistant", text: "Done.", sourceSessionId: "01a105f5-60d1" })])[0].delegated, undefined);
});

test("the error of a Claude Code provider without its CLI names the provider whose download the card proposes", () => {
  const [missing, other] = buildTimelineItems([
    entry({ offset: "1", eventType: "error", kind: "claude-code-not-found", providerId: "claude-work", text: "Claude Code was not found." }),
    entry({ offset: "2", eventType: "error", kind: null, providerId: "claude-work", text: "Another failure." }),
  ]);
  assert.deepEqual([missing.category, missing.missingCliProvider], ["error", "claude-work"]);
  assert.equal(other.missingCliProvider, undefined);
});

test("the prompt of a run that waited for its conversation and found it changed is marked as not sent, and given back once it is the last", () => {
  const prompt = (runId: string, offset: string, text = "Fix the parser.") =>
    entry({ offset, runId, eventType: "contentCompleted", kind: "User", text, contentId: `user:${runId}` });
  const changed = (runId: string, offset: string) => entry({ offset, runId, eventType: "error", kind: conversationChangedErrorKind, contentId: null,
    text: "Turns were added to the Claude Code conversation in VS Code while this run waited for it." });
  const turns = [
    prompt("r1", "1", "First."), entry({ offset: "2", runId: "r1", kind: "Assistant", text: "Done." }),
    prompt("r2", "3"), changed("r2", "4"),
    // Another failure is not that one: its prompt was sent.
    prompt("r3", "5", "Other."), entry({ offset: "6", runId: "r3", eventType: "error", kind: null, contentId: null, text: "The provider failed." }),
  ];
  const items = buildTimelineItems(turns);
  const users = items.filter(item => item.category === "user");
  assert.deepEqual(users.map(item => [item.markdown, item.notSent, item.subtitle]),
    [["First.", undefined, null], ["Fix the parser.", true, notSentSubtitle], ["Other.", undefined, null]]);
  assert.equal(items.find(item => item.category === "error" && item.key === "4")?.markdown,
    "Turns were added to the Claude Code conversation in VS Code while this run waited for it.", "its notice stays");
  assert.equal(buildTimelineItems(turns)[2], items[2], "the same row while nothing changed");
  for (const locale of ["es", "fr", "de", "ja", "zh-CN"] as const) assert.notEqual(translate(locale, notSentSubtitle), notSentSubtitle, locale);

  // Given back: the last prompt of the window, when its run was the one that never sent it.
  assert.equal(unsentPrompt(turns), null, "a prompt was sent after it");
  assert.deepEqual(unsentPrompt(turns.slice(0, 4)), { runId: "r2", offset: "3", text: "Fix the parser." });
  assert.equal(unsentPrompt(turns.slice(0, 2)), null);
  assert.equal(unsentPrompt([...turns.slice(0, 4), entry({ offset: "7", runId: "r4", eventType: "contentCompleted", kind: "Assistant", text: "A turn the CLI started." })])?.runId,
    "r2", "an answer without a prompt changes nothing");
  // Never in part: a prompt the window holds cut is not given back.
  assert.equal(unsentPrompt([prompt("r2", "3", "Fix"), changed("r2", "4")].map(value => value.offset === "3" ? { ...value, textTruncated: true } : value)), null);
});

test("a prompt made outside CodeAlta says where it was made", () => {
  const prompt = (contentId: string) => buildTimelineItems([entry({ eventType: "contentCompleted", kind: "User", text: "Fix it.", contentId })])[0];
  assert.deepEqual([prompt("external:vscode:01a1").title, prompt("external:vscode:01a1").subtitle, prompt("external:vscode:01a1").madeElsewhere],
    ["You", "in VS Code", "vscode"]);
  assert.equal(prompt("external:terminal:01a1").subtitle, "in a terminal");
  assert.equal(prompt("external:other:01a1").subtitle, "in another application");
  assert.equal(prompt("external:unknown:01a1").subtitle, "outside CodeAlta");
  // A prompt of CodeAlta, and the answer of a turn made elsewhere, say nothing of it.
  assert.deepEqual([prompt("user:01a1").subtitle, prompt("user:01a1").madeElsewhere], [null, undefined]);
  const [answer] = buildTimelineItems([entry({ eventType: "contentCompleted", kind: "Assistant", text: "Fixed.", contentId: "external:vscode:01a1" })]);
  assert.equal(answer.madeElsewhere, undefined);
  // The cannot-follow notice of a catch-up is a warning the timeline shows.
  const [notice] = buildTimelineItems([entry({ eventType: "sessionUpdate", kind: "Warning",
    text: "This conversation was changed outside CodeAlta in a way it cannot follow; the model has the full conversation." })]);
  assert.equal(notice.category, "error");
});

test("TUI status-only updates do not become timeline cards", () => {
  assert.deepEqual(buildTimelineItems(["Idle", "UsageUpdated", "Shutdown"].map(kind => entry({ eventType: "sessionUpdate", kind }))), []);
});

test("job cards retain prompt identity and every recorded fact for their details", () => {
  const text = "[CodeAlta background job]\nJob: job-1234\nTitle: Build tests\nCommand: dotnet test\nResult: failed (exit code 1) after 12 s\n\nOutput:\n```\nassertion failure\n```";
  const [item] = buildTimelineItems([entry({ eventType: "contentCompleted", kind: "User", text })]);
  assert.equal(item.category, "user");
  assert.equal(item.title, "Background job");
  assert.equal(item.summary, "Build tests");
  assert.equal(item.summaryIsCode, false);
  assert.equal(item.jobResult?.jobId, "job-1234");
  assert.equal(item.jobResult?.phase, "failed");
  assert.equal(item.jobResult?.command, "dotnet test");
  assert.match(item.jobResult!.output, /assertion failure/);
  assert.match(item.copyMarkdown!, /Build tests\n\nfailed \(exit code 1\) after 12 s\n\n```\ndotnet test/);
  const [untitled] = buildTimelineItems([entry({ eventType: "contentCompleted", kind: "User", text: text.replace("Title: Build tests\n", "") })]);
  assert.equal(untitled.summary, "dotnet test");
  assert.equal(untitled.summaryIsCode, true);
  assert.equal(buildTimelineItems([entry({ eventType: "contentCompleted", kind: "Assistant", text })])[0].jobResult, undefined);
});

test("the records of a tool call make one row, with what its details and its live output are found by", () => {
  const base = { eventType: "activity", kind: "ToolCall", activityId: "call", name: "shell_command", text: null } as const;
  const requested = entry({ ...base, offset: "10", phase: "Requested", timestamp: "2026-09-22T10:00:00Z" });
  const started = entry({ ...base, offset: "20", phase: "Started", timestamp: "2026-09-22T10:00:01Z" });
  const [running, ...none] = buildTimelineItems([requested, started]);
  assert.equal(none.length, 0, "A call that was requested and started is one row.");
  assert.equal(running.toolPhase, "started");
  assert.equal(running.timestamp, "2026-09-22T10:00:00Z", "The row stays where the call was requested.");
  const identity = running.toolCall!;
  assert.deepEqual([identity.providerId, identity.runId, identity.activityId, identity.kind, identity.name], ["provider", "run", "call", "ToolCall", "shell_command"]);
  assert.deepEqual([identity.offset, identity.outputOffset, identity.startedAt, identity.endedAt], ["20", null, "2026-09-22T10:00:00Z", null]);

  const completed = entry({ ...base, offset: "40", phase: "Completed", timestamp: "2026-09-22T10:00:05Z" });
  const output = entry({ offset: "30", kind: "ToolOutput", parentActivityId: "call", text: "exit_code: 1",
    tool: { primary: null, isCommand: false, output: "failed", outputLines: 1, outputBytes: 6, fields: [], added: null, removed: null, exitCode: 1 } });
  const [done, ...rest] = buildTimelineItems([requested, started, output, completed]);
  assert.equal(rest.length, 0);
  assert.equal(done.toolPhase, "completed");
  assert.equal(done.toolExitCode, 1);
  assert.equal(done.timestamp, "2026-09-22T10:00:00Z");
  assert.deepEqual([done.toolCall!.offset, done.toolCall!.outputOffset, done.toolCall!.startedAt, done.toolCall!.endedAt],
    ["40", "30", "2026-09-22T10:00:00Z", "2026-09-22T10:00:05Z"]);
  // The same records give the same row: a tile that did not change is not drawn again.
  assert.equal(buildTimelineItems([requested, started, output, completed])[0], done);
  // A window that starts after the call was started does not know when it started.
  assert.equal(buildTimelineItems([completed])[0].toolCall!.startedAt, null);
  // A late report of the start does not replace the record that ended the call.
  assert.equal(buildTimelineItems([requested, completed, started])[0].toolPhase, "completed");
  // A file change is no tool call, and a live row has no record yet.
  assert.equal(buildTimelineItems([entry({ ...base, kind: "FileChange", phase: "Completed" })])[0].toolCall, undefined);
  assert.equal(buildTimelineItems([entry({ ...base, offset: JSON.stringify(["provider", "run", "call"]), phase: "Started" })])[0].toolCall!.offset, null);
});

test("the copy of a card is its Markdown, without the JSON record of the event", () => {
  const details = JSON.stringify({ prompt_sender: "user", attachments: [{ name: "notes.txt" }] });
  // A message of the user, of the assistant, an error: what was written, and nothing of the record.
  for (const kind of ["User", "Assistant"]) {
    const [item] = buildTimelineItems([entry({ eventType: "contentCompleted", kind, contentId: `c-${kind}`, text: "Fix the **parser**.", details })]);
    assert.equal(item.copyMarkdown, "Fix the **parser**.");
    assert.equal(item.details, formatDetails(details), "the record is still in the details of the card");
  }
  assert.equal(buildTimelineItems([entry({ eventType: "error", kind: null, text: "The provider refused the request.", details })])[0].copyMarkdown,
    "The provider refused the request.");
  // A card that says nothing but its record has nothing to copy.
  assert.equal(buildTimelineItems([entry({ eventType: "interaction", kind: "Answered", text: null, details })])[0].copyMarkdown, null);
});

test("file changes inspect supplied paths, count only validated per-file hunks, and are copied as Markdown", () => {
  const details = JSON.stringify({ changes: [
    { path: "<script>literal</script>.ts", kind: { type: "update" }, diff: "@@ -1 +1,2 @@\n-old\n+new\n+line\n" },
    { path: "other.ts", kind: { type: "unknown-provider-kind" } },
  ] });
  const item = buildTimelineItems([entry({ eventType: "activity", kind: "FileChange", phase: "Failed", text: null, details })])[0]!;
  assert.equal(item.fileChanges?.rows.length, 2);
  assert.deepEqual(item.fileChanges?.rows[0], { index: 0, path: "<script>literal</script>.ts", kind: "update", diff: "@@ -1 +1,2 @@\n-old\n+new\n+line\n", counts: { added: 2, removed: 1 } });
  assert.equal(item.fileChanges?.rows[1]?.counts, null);
  assert.equal(item.copyMarkdown, "- `<script>literal</script>.ts` (update)\n\n```diff\n@@ -1 +1,2 @@\n-old\n+new\n+line\n```\n\n- `other.ts` (unknown-provider-kind)");
  // A diff that holds a fence of its own is fenced with a longer one.
  const fenced = buildTimelineItems([entry({ eventType: "activity", kind: "FileChange", phase: "Completed", text: null,
    details: JSON.stringify({ changes: [{ path: "readme.md", kind: { type: "update" }, diff: "@@ -1 +1 @@\n-```\n+````\n" }] }) })])[0]!;
  assert.equal(fenced.copyMarkdown, "- `readme.md` (update)\n\n`````diff\n@@ -1 +1 @@\n-```\n+````\n`````");
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

test("a user message lists its attached files and the identity of its Send", () => {
  const attachments = [{ title: "report.pdf", mediaType: "application/pdf" }, { title: "notes.txt", mediaType: "text/plain" }];
  const [user, assistant] = buildTimelineItems([entry({ kind: "User", text: "Read", attachments, clientRequestId: "send-1" }),
    entry({ offset: "2", contentId: "other", attachments, clientRequestId: "send-1" })]);
  assert.deepEqual(user.attachments, attachments);
  assert.equal(user.clientRequestId, "send-1");
  assert.equal(assistant.attachments, undefined);
  assert.equal(assistant.clientRequestId, undefined);
  assert.equal(buildTimelineItems([entry({ kind: "User", attachments: [{ title: "", mediaType: "text/plain" }] })])[0].attachments, undefined);
  assert.equal(buildTimelineItems([entry({ kind: "User", attachments: [{ title: "x", mediaType: "plain" }] })])[0].attachments, undefined);
});

test("the images a tool gave the model get a card after the tile of its call", () => {
  const images = [{ index: 0, title: "shot.png", mediaType: "image/png" }, { index: 1, title: "page", mediaType: "image/png" }];
  const activity = entry({ eventType: "activity", kind: "ToolCall", activityId: "tool", phase: "Completed", name: "view_image", text: null });
  const output = entry({ offset: "2", kind: "ToolOutput", parentActivityId: "tool", text: "Viewed image shot.png.\n[Image: shot.png (image/png, 640x480)]",
    details: '{"toolName":"view_image"}', images,
    tool: { primary: null, isCommand: false, output: "Viewed image shot.png.", outputLines: 2, outputBytes: 60, fields: [], added: null, removed: null, exitCode: null } });

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
    parentActivityId: null, interactionId: null, sourceSessionId: null, prompt: null, name: null, text: "Hello", details: null,
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

test("the row of a system prompt says what it is in one line, and leaves usage in the status surface", () => {
  const facts = { changed: false, hash: "sha256:4ADDDDB50A0D", sourcePath: "prompts/default.prompt.md", totalTokens: 11239, systemTokens: 1564, developerTokens: 9675,
    applied: true, lossy: false, added: null, removed: null };
  const items = buildTimelineItems([
    entry({ offset: "1", eventType: "system_prompt", kind: "session_start", name: "Default", text: null, prompt: facts }),
    entry({ offset: "2", eventType: "sessionUpdate", kind: "UsageUpdated",
      text: "Usage refreshed\n\n**Context:** 6,400 / 128,000 tokens (5%)\n\n**Model:** model-1\n\n**Input tokens:** 100\n\n**Output tokens:** 25\n\n**Cost:** 0.01" }),
    entry({ offset: "3", eventType: "system_prompt", kind: "changed", name: "Reviewer", text: null,
      prompt: { ...facts, changed: true, totalTokens: 950, applied: false, lossy: true, added: 12, removed: 3 } }),
    // A prompt whose replaced one is not in the page: the row asks for the size of its change.
    entry({ offset: "4", eventType: "system_prompt", kind: "changed", name: null, text: null, prompt: { ...facts, changed: true, sourcePath: null, totalTokens: 0 } }),
    entry({ offset: "5", eventType: "system_prompt", kind: "changed", name: " ", text: null }),
  ]);
  assert.equal(items.length, 4);
  assert.deepEqual(items.map(item => [item.category, item.title, item.subtitle]), [
    ["prompt", "System prompt recorded", "Default · 11.2k tokens"], ["prompt", "System prompt changed", "Reviewer · 950 tokens"],
    ["prompt", "System prompt changed", null], ["prompt", "System prompt changed", null]]);
  assert.deepEqual(items[0].prompt, { changed: false, name: "Default", sourcePath: "prompts/default.prompt.md", hash: "sha256:4ADDDDB50A0D",
    tokens: 11239, systemTokens: 1564, developerTokens: 9675, applied: true, lossy: false, change: null });
  assert.deepEqual([items[1].prompt!.change, promptProblem(items[1].prompt!), promptProblem({ applied: true, lossy: true }), promptProblem(items[0].prompt!)],
    [{ added: 12, removed: 3 }, "notApplied", "lossy", null]);
  assert.deepEqual([items[2].prompt!.change, items[2].prompt!.name, items[2].prompt!.sourcePath], [null, null, null]);
  // An entry without the facts of the host still says whether the prompt is the first of its session.
  assert.deepEqual([items[3].prompt!.changed, items[3].prompt!.tokens, items[3].prompt!.applied, items[3].prompt!.name], [true, 0, true, null]);
  // The row has no text: nothing of the prompt is in it, and there is nothing cut to copy.
  for (const item of items) assert.deepEqual([item.markdown, item.summary, item.detailMarkdown, item.details, item.copyMarkdown], [null, null, null, null, null]);
});

test("a prompt is one Markdown text with a title for each part, built the same way for the two sides of a comparison", () => {
  const titles = { system: "System message", developer: "Developer instructions" };
  assert.equal(promptDocument("\r\nYou are an agent.\r\n## Rules\r\n", "# Agent Prompt\n\nDo the work.\n\n", titles),
    "System message\n==============\n\nYou are an agent.\n## Rules\n\nDeveloper instructions\n======================\n\n# Agent Prompt\n\nDo the work.\n");
  assert.equal(promptDocument(null, "", { system: "系统", developer: "D" }), "系统\n===\n\n\n\nD\n===\n\n\n");
  assert.deepEqual(changedParts({ system: "A\r\n", developer: "B" }, { system: "\nA", developer: "B2" }), { system: false, developer: true });
  assert.deepEqual(changedParts({ system: null, developer: null }, { system: "A", developer: " " }), { system: true, developer: false });
  assert.deepEqual([shortHash("4ADDDDB50A0D2AE2"), shortHash("sha256:abcdef0123456789"), shortHash(""), promptTokens(11239), promptTokens(999), promptTokens(-4)],
    ["4addddb5", "abcdef01", "", "11.2k", "999", "0"]);
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

test("persisted ToolCall retains the exact otherwise-hidden Canceled message in details and Copy, without its record", () => {
  const text = "  Supplied cancellation message\r\nnot an inferred outcome  ";
  const details = JSON.stringify({ command: "echo fixture", result: { success: true } });
  const [item] = buildTimelineItems([entry({ eventType: "activity", kind: "ToolCall", phase: "Canceled",
    name: "shell_command", text, details })]);
  assert.equal(item.title, "shell_command");
  assert.equal(item.subtitle, "Canceled · Tool Call");
  assert.equal(item.summary, "echo fixture");
  assert.equal(item.markdown, null);
  assert.deepEqual({ detail: item.detailMarkdown, copy: item.copyMarkdown }, {
    detail: text, copy: `\`\`\`\necho fixture\n\`\`\`\n\n${text}`,
  });
  assert.equal(item.details, formatDetails(details));
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

test("a model the provider answered with instead of the requested one is the model of the line", () => {
  const [item] = buildTimelineItems([entry({ eventType: "sessionUpdate", kind: "ModelChanged",
    text: "The provider answered with claude-opus-4-8 instead of claude-opus-5-5.",
    details: JSON.stringify({ requestedModel: "claude-opus-5-5", effectiveModel: "claude-opus-4-8" }) })]);
  assert.equal(item.title, "Model · claude-opus-4-8");
  assert.equal(item.subtitle, "Instead of claude-opus-5-5");
  assert.equal(item.markdown, null);
});

test("a fallback after a failure shows why and the models left and taken", () => {
  const [item] = buildTimelineItems([entry({ eventType: "sessionUpdate", kind: "ProviderFallback",
    text: "Claude Code quota reached until 18:00, continuing on Codex GPT-6.1 high.",
    details: JSON.stringify({ reason: "QuotaExhausted", from: "claude-code:opus@high", to: "codex:gpt-6.1@high" }) })]);
  assert.equal(item.title, "Model changed after a failure");
  assert.equal(item.subtitle, "claude-code:opus@high → codex:gpt-6.1@high");
  assert.equal(item.icon, "model");
  assert.equal(item.category, "status");
  assert.equal(item.markdown, "Claude Code quota reached until 18:00, continuing on Codex GPT-6.1 high.");
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
        assert.deepEqual(item, { ...generic, toolCall: undefined, category: "file", icon: "file",
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

test("an edit shows the lines it added and removed, and its diff first among its fields", () => {
  const edit = entry({ eventType: "activity", kind: "ToolCall", phase: "Completed", name: "apply_patch", activityId: "edit", details: '{"toolName":"apply_patch"}',
    tool: { primary: null, isCommand: false, output: "Patch applied:", outputLines: 2, outputBytes: 30, added: 2, removed: 1, exitCode: null,
      fields: [{ path: "diff", text: "@@ -1 +1,2 @@\n-a\n+A\n+b\n", truncated: false }, { path: "arguments", text: "{}", truncated: false }] } });
  const read = entry({ offset: "2", eventType: "activity", kind: "ToolCall", phase: "Completed", name: "read_file", activityId: "read",
    tool: { primary: "a.txt", isCommand: false, output: "a", outputLines: 1, outputBytes: 1, added: null, removed: null, exitCode: null, fields: [] } });
  const [first, second] = buildTimelineItems([edit, read]);
  assert.deepEqual(first.toolChanges, { added: 2, removed: 1 });
  assert.equal(first.toolFields?.[0].path, "diff");
  assert.equal(second.toolChanges, undefined, "A call that changed no file has no counts.");
});
