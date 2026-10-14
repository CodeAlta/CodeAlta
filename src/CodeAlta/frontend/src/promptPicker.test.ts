import assert from "node:assert/strict";
import test from "node:test";
import { activeIssueReference } from "./issueReferences";
import { activeProjectReference } from "./projectReferences";
import { dismissPromptTrigger, isPromptPickerReopenKey, promptTriggerDismissalHolds } from "./promptPicker";

// What the picker hook does on each key: open for a token at the caret unless that token was dismissed.
function opens(dismissal: ReturnType<typeof dismissPromptTrigger> | null, text: string) {
  const held = dismissal && promptTriggerDismissalHolds(dismissal, text) ? dismissal : null;
  const span = activeProjectReference(text, text.length);
  return !!span && span.start !== held?.start;
}

test("a dismissed trigger stays closed while typing on after it", () => {
  const text = "see @";
  const span = activeProjectReference(text, text.length)!;
  const dismissal = dismissPromptTrigger({ text, ...span });
  for (const typed of ["see @a", "see @ab", "see @abc", "see @abc def"]) assert.equal(opens(dismissal, typed), false, typed);
});

test("a new trigger opens after a dismissed one", () => {
  const text = "see @";
  const dismissal = dismissPromptTrigger({ text, ...activeProjectReference(text, text.length)! });
  assert.equal(opens(dismissal, "see @abc and @"), true);
});

test("deleting the dismissed trigger character ends the dismissal", () => {
  const text = "see @";
  const dismissal = dismissPromptTrigger({ text, ...activeProjectReference(text, text.length)! });
  assert.equal(promptTriggerDismissalHolds(dismissal, "see "), false);
  assert.equal(opens(null, "see @"), true);
  assert.equal(promptTriggerDismissalHolds(dismissal, "sea @"), false);
});

test("Ctrl+Space alone reopens a picker", () => {
  const key = { key: " ", ctrlKey: true, altKey: false, metaKey: false, shiftKey: false, repeat: false, isComposing: false };
  assert.equal(isPromptPickerReopenKey(key), true);
  for (const other of [{ key: "a" }, { ctrlKey: false }, { altKey: true }, { metaKey: true }, { shiftKey: true }, { repeat: true }, { isComposing: true }])
    assert.equal(isPromptPickerReopenKey({ ...key, ...other }), false, JSON.stringify(other));
});

test("only the word right after the trigger can reopen a picker", () => {
  // "@foo bar": the caret in foo finds the token with the text after @ as the query, in bar it finds none.
  const text = "see @foo bar";
  assert.deepEqual(activeProjectReference(text, 8), { start: 4, end: 8, query: "foo" });
  assert.deepEqual(activeProjectReference(text, 6), { start: 4, end: 8, query: "f" });
  assert.equal(activeProjectReference(text, 12), null);
  assert.equal(activeProjectReference(text, 9), null);
  assert.deepEqual(activeIssueReference("fix #12 now", 7), { start: 4, end: 7, query: "12" });
  assert.equal(activeIssueReference("fix #12 now", 11), null);
});
