import assert from "node:assert/strict";
import { test } from "node:test";
import type { LocalSearchHit } from "#neoastra";
import { contentIcon, contentKindsFor, contentResults, contentTarget, hitExtra, markedParts } from "./contentResults";

const hit = (kind: string, ref: string, extra: Record<string, string> | null = null, more: Partial<LocalSearchHit> = {}): LocalSearchHit => ({
  kind, ref, title: ref, snippet: "a snippet", marks: [], at: "2026-10-17T10:00:00Z", match: "words", wordsRank: 1, meaningRank: null, rank: 1,
  sessionId: null, projectId: null, runId: null, role: null, extra: extra ? JSON.stringify(extra) : null, ...more,
});

test("the hits of the host go to their group, in the order the host ranked them", () => {
  const found = contentResults([hit("doc", "doc:a.md"), hit("message", "msg:s/1"), hit("tool", "tool:builtin/read_file"), hit("plan", "plan:p/x"), hit("skill", "skill:user/x"), hit("mcp", "mcp:slack")]);
  assert.deepEqual(found.conversations.map(result => result.hit.ref), ["msg:s/1", "plan:p/x"]);
  assert.deepEqual(found.docs.map(result => result.hit.ref), ["doc:a.md"]);
  assert.deepEqual(found.tools.map(result => result.hit.ref), ["tool:builtin/read_file", "mcp:slack"]);
  // The skills are the page's own group: the index does not add them twice.
  assert.equal(Object.values(found).flat().some(result => result.hit.kind === "skill"), false);
  assert.ok(found.docs[0].score < found.conversations[0].score, "the better ranked hit has the lower score");
  assert.ok(found.conversations[0].score >= 2000, "after the names that start with the words");
});

test("a category asks for its own kinds, every category for all of them", () => {
  assert.deepEqual(contentKindsFor("docs"), ["doc"]);
  assert.deepEqual(contentKindsFor("conversations"), ["message", "note", "plan", "task"]);
  assert.equal(contentKindsFor("all").length, 8);
  assert.deepEqual(contentKindsFor("files"), []);
});

test("Enter leads to the message, the page of the guide, the plan, or the settings of a server", () => {
  assert.deepEqual(contentTarget(hit("message", "msg:s1/c1", null, { sessionId: "s1", projectId: "p1" })), { kind: "message", ref: "msg:s1/c1", sessionId: "s1", projectId: "p1" });
  assert.deepEqual(contentTarget(hit("note", "note:s1", null, { sessionId: "s1" })), { kind: "session", sessionId: "s1", projectId: null });
  assert.deepEqual(contentTarget(hit("doc", "doc:worktrees.md#cleanup", { page: "worktrees.md", anchor: "cleanup" })), { kind: "doc", page: "worktrees.md", anchor: "cleanup" });
  assert.deepEqual(contentTarget(hit("doc", "doc:api/X", { api: "X" })).kind, "details");
  assert.deepEqual(contentTarget(hit("plan", "plan:p1/x", { id: "2026-10-17-x" }, { projectId: "p1" })), { kind: "file", projectId: "p1", path: ".alta/plans/2026-10-17-x.md" });
  assert.deepEqual(contentTarget(hit("task", "task:p1/t", { id: "t" }, { projectId: "p1" })), { kind: "file", projectId: "p1", path: ".alta/tasks/t.md" });
  assert.deepEqual(contentTarget(hit("mcp", "mcp:slack", { server: "slack" })), { kind: "settings", page: "mcp" });
  assert.deepEqual(contentTarget(hit("tool", "tool:builtin/grep")), { kind: "details", title: "tool:builtin/grep", text: "a snippet" });
});

test("the extra of a hit is read as strings, and anything else as nothing", () => {
  assert.deepEqual(hitExtra({ extra: '{"page":"a.md","n":3}' }), { page: "a.md" });
  assert.deepEqual(hitExtra({ extra: "[1]" }), {});
  assert.deepEqual(hitExtra({ extra: "{" }), {});
  assert.deepEqual(hitExtra({ extra: null }), {});
});

test("the marks of the host cut the snippet, and a mark out of it is left out", () => {
  assert.deepEqual(markedParts("the retry test", [{ start: 4, length: 5 }, { start: 40, length: 2 }]),
    [{ text: "the ", marked: false }, { text: "retry", marked: true }, { text: " test", marked: false }]);
  assert.deepEqual(markedParts("abc", []), [{ text: "abc", marked: false }]);
  assert.equal(contentIcon({ kind: "message", role: "assistant" }), "assistant");
  assert.equal(contentIcon({ kind: "message", role: "user" }), "user");
});
