import assert from "node:assert/strict";
import test from "node:test";
import { findKeyKept, findMatches, findPattern, isFindKey } from "./documentationFind";

test("a text of the page is found whatever the case of its letters, and its spaces match any spaces", () => {
  assert.deepEqual(findMatches("A prompt queue, the Prompt\nQueue and PROMPT   queue.", "prompt queue"), [{ start: 2, end: 14 }, { start: 20, end: 32 }, { start: 37, end: 51 }]);
  assert.deepEqual(findMatches("Ünïcode ünïcode", "ÜNÏCODE"), [{ start: 0, end: 7 }, { start: 8, end: 15 }]);
  // What a regular expression would read is a plain letter here.
  assert.deepEqual(findMatches("a.b axb (c) [d]", "a.b"), [{ start: 0, end: 3 }]);
  assert.deepEqual(findMatches("a.b axb (c) [d]", "(c) [d]"), [{ start: 8, end: 15 }]);
  assert.equal(findPattern("   "), null);
  assert.deepEqual(findMatches("anything", ""), []);
  assert.deepEqual(findMatches("aaaa", "a", 3).length, 3, "No more places than asked are found.");
});

test("Ctrl+F finds in the page, and Cmd+F on macOS; the window leaves Ctrl+F to the Documentation tab only", () => {
  const key = (modifiers: Partial<{ ctrlKey: boolean; metaKey: boolean; altKey: boolean; shiftKey: boolean }>, name = "f") =>
    ({ key: name, ctrlKey: false, metaKey: false, altKey: false, shiftKey: false, ...modifiers });
  assert.equal(isFindKey(key({ ctrlKey: true }), false), true);
  assert.equal(isFindKey(key({ ctrlKey: true }, "F"), false), true);
  assert.equal(isFindKey(key({ metaKey: true }), true), true);
  assert.equal(isFindKey(key({ ctrlKey: true }), true), false, "Ctrl+F on macOS is a key of the text.");
  assert.equal(isFindKey(key({ metaKey: true }), false), false);
  for (const other of [key({ ctrlKey: true, shiftKey: true }), key({ ctrlKey: true, altKey: true }), key({ ctrlKey: true }, "g"), key({})])
    assert.equal(isFindKey(other, false), false, JSON.stringify(other));
  const within = (inTab: boolean) => ({ closest: (selector: string) => selector === "[data-find-keys]" && inTab ? {} : null }) as unknown as Element;
  assert.equal(findKeyKept(key({ ctrlKey: true }), within(true)), true);
  assert.equal(findKeyKept(key({ ctrlKey: true }), within(false)), false, "Elsewhere Ctrl+F searches the sessions.");
  assert.equal(findKeyKept(key({ ctrlKey: true }), null), false);
  assert.equal(findKeyKept(key({ ctrlKey: true }, "g"), within(true)), false);
});
