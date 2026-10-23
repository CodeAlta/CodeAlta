import assert from "node:assert/strict";
import test from "node:test";
import { findKeyKept, findMatches, findPattern, findPatternInvalid, isFindKey, maximumFindMatches } from "./findInPage";

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

test("the options of the search: the case of the letters, a whole word, a regular expression", () => {
  const options = (value: Partial<{ matchCase: boolean; wholeWord: boolean; regex: boolean }>) => ({ matchCase: false, wholeWord: false, regex: false, ...value });
  const find = (line: string, text: string, value: Parameters<typeof options>[0]) => findMatches(line, text, maximumFindMatches, options(value)).map(({ start, end }) => line.slice(start, end));
  assert.deepEqual(find("Review the review, REVIEW", "Review", { matchCase: true }), ["Review"]);
  assert.deepEqual(find("review reviews preview review_x review-y", "review", { wholeWord: true }), ["review", "review"],
    "A letter, a digit or _ beside it makes it part of another word; a - does not.");
  assert.deepEqual(find("été étés", "été", { wholeWord: true }), ["été"], "A word of other letters than English ones.");
  assert.deepEqual(find("c12 c3 cx", "c\\d+", { regex: true }), ["c12", "c3"]);
  assert.deepEqual(find("C12 c3", "c\\d+", { regex: true, matchCase: true }), ["c3"]);
  assert.deepEqual(find("ab abc", "ab|abc", { regex: true, wholeWord: true }), ["ab", "abc"], "The whole expression is a word, not its last part.");
  assert.deepEqual(find("a.b axb", "a.b", { regex: true }), ["a.b", "axb"]);
  assert.deepEqual(find("a-b a_b", "a\\-b", { regex: true }), ["a-b"], "An escape the Unicode mode refuses is read as the text editor reads it.");
  assert.deepEqual(find("bbb", "a*", { regex: true }), [], "A place of no letter is no place.");
  assert.equal(findPatternInvalid("(a", options({ regex: true })), true);
  assert.deepEqual(find("(a", "(a", { regex: true }), []);
  assert.equal(findPatternInvalid("(a", options({})), false, "Without a regular expression, ( is a letter.");
  assert.deepEqual(find("(a", "(a", {}), ["(a"]);
  assert.equal(findPattern("", options({ regex: true })), null);
});

test("Ctrl+F finds in the page, and Cmd+F on macOS; the window leaves Ctrl+F only to a page that keeps it (data-find-keys)", () => {
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
