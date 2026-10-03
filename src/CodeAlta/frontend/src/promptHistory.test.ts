import assert from "node:assert/strict";
import test from "node:test";
import { createPromptHistory, noPromptRecall, recallPrompt } from "./promptHistory";

test("history keeps sent prompts per session, bounded, without blank or repeated entries", () => {
  const history = createPromptHistory(3);
  for (const text of ["one", "one", "  ", "two", "three", "four"]) history.add("a", text);
  history.add("b", "other");
  assert.deepEqual(history.list("a"), ["two", "three", "four"]);
  assert.deepEqual(history.list("b"), ["other"]);
  assert.deepEqual(history.list("missing"), []);
});

test("Alt+Up walks back and stops at the oldest; Alt+Down returns to the draft being typed", () => {
  const entries = ["first", "second"];
  assert.equal(recallPrompt([], noPromptRecall, -1, "draft"), null);
  assert.equal(recallPrompt(entries, noPromptRecall, 1, "draft"), null);
  const newest = recallPrompt(entries, noPromptRecall, -1, "draft")!;
  assert.deepEqual(newest, { text: "second", state: { index: 1, draft: "draft" } });
  const oldest = recallPrompt(entries, newest.state, -1, "second")!;
  assert.deepEqual(oldest, { text: "first", state: { index: 0, draft: "draft" } });
  assert.equal(recallPrompt(entries, oldest.state, -1, "first"), null);
  const forward = recallPrompt(entries, oldest.state, 1, "first")!;
  assert.deepEqual(forward, { text: "second", state: { index: 1, draft: "draft" } });
  assert.deepEqual(recallPrompt(entries, forward.state, 1, "second"), { text: "draft", state: noPromptRecall });
});
