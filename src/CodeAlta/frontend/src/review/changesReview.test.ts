import assert from "node:assert/strict";
import test from "node:test";
import { changesWaitForReview, nextReviewThread, reviewHeightEstimate, reviewKeyAction, reviewOrder, reviewTargetOfScope, threadCountsByFile, typingTarget,
  viewedFiles } from "./changesReview";
import type { ReviewThread } from "./reviewThreads";

const thread = (id: string, path: string, startLine: number, more: Partial<ReviewThread> = {}): ReviewThread => ({ id, target: "changes", path, side: "new", startLine,
  endLine: more.endLine ?? startLine, anchor: "", body: id, suggestion: null, state: "open", outdated: false, replies: [], round: null, createdAt: "", updatedAt: "", ...more });

test("the uncommitted changes and the branch are reviewed; a commit is not", () => {
  assert.deepEqual(reviewTargetOfScope({ kind: "head" }), { kind: "changes" });
  assert.deepEqual(reviewTargetOfScope({ kind: "branch" }), { kind: "branch" });
  assert.equal(reviewTargetOfScope({ kind: "commit" }), null);
});

test("a file row counts the comments that are not resolved, and its drafts", () => {
  const counts = threadCountsByFile([thread("c1", "a", 1), thread("c2", "a", 2, { state: "draft" }), thread("c3", "a", 3, { state: "resolved" }), thread("c4", "b", 1, { state: "addressed" })]);
  assert.deepEqual(counts.get("a"), { active: 2, drafts: 1 });
  assert.deepEqual(counts.get("b"), { active: 1, drafts: 0 });
  assert.equal(counts.get("c"), undefined);
});

test("a file counts as viewed only at the revision it was viewed at, and only for its own target", () => {
  const files = [{ path: "a", revision: "r1" }, { path: "b", revision: "r2" }, { path: "c", revision: "r3" }];
  const viewed = [{ target: "changes", path: "a", revision: "r1" }, { target: "changes", path: "b", revision: "old" }, { target: "branch", path: "c", revision: "r3" }];
  assert.deepEqual([...viewedFiles(viewed, { kind: "changes" }, files)], ["a"], "b changed since it was viewed; c was viewed in the branch");
  assert.deepEqual([...viewedFiles(viewed, { kind: "branch" }, files)], ["c"]);
  assert.equal(viewedFiles(viewed, null, files).size, 0);
});

test("n and p go through the threads in the order of the files of the list, then those of other files, outdated ones last, wrapping around", () => {
  const threads = [thread("c1", "z.ts", 5), thread("c2", "a.ts", 9), thread("c3", "a.ts", 2, { side: "old" }), thread("c4", "z.ts", 1, { outdated: true }),
    thread("c5", "m.ts", 1, { state: "resolved" }), thread("c6", "gone.ts", 1)];
  // The list shows z.ts before a.ts (a tree, a filter): its order wins over the order of the paths.
  const order = reviewOrder(threads, [{ path: "z.ts" }, { path: "a.ts" }, { path: "m.ts" }]);
  assert.deepEqual(order.map(item => item.id), ["c1", "c3", "c2", "c6", "c4"]);
  assert.equal(nextReviewThread(order, { id: "c1" }, 1)?.id, "c3");
  assert.equal(nextReviewThread(order, { id: "c1" }, -1)?.id, "c4", "wraps before the first");
  assert.equal(nextReviewThread(order, { path: "a.ts" }, 1)?.id, "c3", "from a file: its first thread");
  assert.equal(nextReviewThread(order, { path: "a.ts" }, -1)?.id, "c1", "from a file, backwards: the thread before its first");
  assert.equal(nextReviewThread(order, { path: "none.ts" }, 1)?.id, "c1");
  assert.equal(nextReviewThread([], { id: "c1" }, 1), null);
});

test("the keys of the review need no modifier and are never taken from a field", () => {
  const key = (value: string, more: Partial<{ ctrlKey: boolean; metaKey: boolean; altKey: boolean; repeat: boolean }> = {}) =>
    reviewKeyAction({ key: value, ctrlKey: false, metaKey: false, altKey: false, ...more }, false);
  assert.deepEqual(["c", "n", "p", "]", "[", "v", "x"].map(value => key(value)), ["comment", "nextThread", "previousThread", "nextFile", "previousFile", "viewed", null]);
  assert.equal(key("c", { ctrlKey: true }), null);
  assert.equal(key("n", { metaKey: true }), null);
  assert.equal(key("[", { altKey: true }), null);
  assert.equal(key("c", { repeat: true }), null, "a held c opens one comment");
  assert.equal(key("n", { repeat: true }), "nextThread");
  assert.equal(reviewKeyAction({ key: "c", ctrlKey: false, metaKey: false, altKey: false }, true), null);
});

test("a field takes the keys, the read-only text of a diff does not, a comment's text area does", () => {
  const element = (...matches: string[]) => ({ closest: (selector: string) => selector.split(",").map(part => part.trim()).some(part => matches.includes(part)) ? {} : null });
  assert.equal(typingTarget(element("input")), true);
  assert.equal(typingTarget(element("textarea", ".monaco-editor")), false, "the text area of a read-only Monaco editor");
  assert.equal(typingTarget(element("textarea", ".monaco-editor", ".review-zone")), true, "a comment drawn in a zone of the editor");
  assert.equal(typingTarget(element(".review-thread")), false, "a card itself");
  assert.equal(typingTarget(element("button")), false);
  assert.equal(typingTarget(null), false);
});

test("the changes wait for a review when the session reviews before commit, has changes, no request and no run", () => {
  const base = { reviewBeforeCommit: true, changedFiles: 3, requested: false, running: false };
  assert.equal(changesWaitForReview(base), true);
  assert.equal(changesWaitForReview({ ...base, reviewBeforeCommit: false }), false);
  assert.equal(changesWaitForReview({ ...base, reviewBeforeCommit: null }), false);
  assert.equal(changesWaitForReview({ ...base, changedFiles: 0 }), false);
  assert.equal(changesWaitForReview({ ...base, changedFiles: null }), false);
  assert.equal(changesWaitForReview({ ...base, requested: true }), false);
  assert.equal(changesWaitForReview({ ...base, running: true }), false);
  assert.equal(reviewHeightEstimate(2), 192);
  assert.equal(reviewHeightEstimate(-1), 0);
});
