import assert from "node:assert/strict";
import test from "node:test";
import { activeIssueReference, insertIssueReference, insertIssueReferences } from "./issueReferences";

test("# opens the issue picker at a word start and carries the typed query", () => {
  assert.deepEqual(activeIssueReference("#", 1), { start: 0, end: 1, query: "" });
  assert.deepEqual(activeIssueReference("fix #12", 7), { start: 4, end: 7, query: "12" });
  assert.deepEqual(activeIssueReference("see (#crash", 11), { start: 5, end: 11, query: "crash" });
  // The caret in the middle of a token replaces the whole token.
  assert.deepEqual(activeIssueReference("fix #1234 now", 6), { start: 4, end: 9, query: "1" });
});

test("headings, anchors and identifiers are not issue triggers", () => {
  assert.equal(activeIssueReference("##", 2), null);
  assert.equal(activeIssueReference("##", 1), null);
  assert.equal(activeIssueReference("C#", 2), null);
  assert.equal(activeIssueReference("page.html#top", 13), null);
  assert.equal(activeIssueReference("# title", 7), null);
  assert.equal(activeIssueReference("fix #12 ", 8), null);
});

test("the chosen issue replaces the token with a Markdown link", () => {
  assert.deepEqual(insertIssueReference("fix #12 now", 4, 7, 123, "https://github.com/owner/repo/issues/123"),
    { text: "fix [#123](https://github.com/owner/repo/issues/123) now", caret: 52 });
  assert.equal(insertIssueReference("#", 0, 1, 0, "https://github.com/owner/repo/issues/0"), null);
  assert.deepEqual(insertIssueReference("#7", 0, 2, 7, "https://gitlab.example.com/group/sub/project/-/issues/7"),
    { text: "[#7](https://gitlab.example.com/group/sub/project/-/issues/7)", caret: 61 });
  assert.deepEqual(insertIssueReference("#", 0, 1, 42, "https://dev.azure.com/org/My%20Project/_workitems/edit/42"),
    { text: "[#42](https://dev.azure.com/org/My%20Project/_workitems/edit/42)", caret: 64 });
  assert.equal(insertIssueReference("#", 0, 1, 5, "http://github.com/owner/repo/issues/5"), null);
  assert.equal(insertIssueReference("#", 0, 1, 5, "https://user:pass@github.com/owner/repo/issues/5"), null);
  assert.equal(insertIssueReference("#", 0, 1, 5, "javascript:alert(1)"), null);
  assert.equal(insertIssueReference("#", 0, 1, 5, "https://github.com/owner/repo/issues/5) [x](y"), null);
});

test("issues chosen together become links separated by spaces, at the token or at the caret", () => {
  const one = { number: 1, url: "https://github.com/o/r/issues/1" }, two = { number: 2, url: "https://github.com/o/r/issues/2" };
  assert.deepEqual(insertIssueReferences("fix # now", 4, 5, [one, two]),
    { text: "fix [#1](https://github.com/o/r/issues/1) [#2](https://github.com/o/r/issues/2) now", caret: 79 });
  // At the caret, right after a word (the attach menu): a space first.
  assert.deepEqual(insertIssueReferences("fix", 3, 3, [one]), { text: "fix [#1](https://github.com/o/r/issues/1)", caret: 41 });
  assert.deepEqual(insertIssueReferences("", 0, 0, [one]), { text: "[#1](https://github.com/o/r/issues/1)", caret: 37 });
  assert.equal(insertIssueReferences("x", 1, 1, []), null);
  assert.equal(insertIssueReferences("x", 1, 1, [{ number: 3, url: "http://github.com/o/r/issues/3" }]), null);
});
