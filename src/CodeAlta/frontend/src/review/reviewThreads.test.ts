import assert from "node:assert/strict";
import test from "node:test";
import { blockCommented, blockRange, lastCommentableLine, canSubmitReview, compareThreads, filterThreads, groupThreads, isPlanPath, listedThreads, neighbourThread, orderThreads, placementBlock,
  readThread, suggestionDiff, textLines, threadChip, threadCounts, threadNumber, threadsOnLines, type ReviewThread } from "./reviewThreads";
import { reviewPromptSummary } from "./reviewPrompt";

const thread = (id: string, path: string, startLine: number, more: Partial<ReviewThread> = {}): ReviewThread => ({ id, target: "file", path, side: "new", startLine,
  endLine: more.endLine ?? startLine, anchor: "", body: id, suggestion: null, state: "draft", outdated: false, replies: [], round: null, createdAt: "", updatedAt: "", ...more });

test("threads are in path order, the whole file first, then by line, the old side first, then as they were written", () => {
  const threads = [thread("c10", "b.md", 3), thread("c2", "b.md", 3), thread("c3", "a.md", 9), thread("c4", "b.md", 0, { endLine: 0 }),
    thread("c5", "b.md", 3, { side: "old" }), thread("c6", "b.md", 1, { endLine: 5 }), thread("c7", "b.md", 1)];
  assert.deepEqual(orderThreads(threads).map(item => item.id), ["c3", "c4", "c7", "c6", "c5", "c2", "c10"]);
  assert.equal(threadNumber("c12"), 12);
  assert.ok(compareThreads(thread("c9", "a", 1), thread("c10", "a", 1)) < 0, "c9 before c10: by number, not by text");
});

test("a thread of the host is read only when its state, side, target and lines are ones the page knows", () => {
  const wire = { id: "c1", target: "file", path: "a.md", side: "new", startLine: 2, endLine: 3, anchor: "x", body: "b", suggestion: null, state: "open", outdated: false,
    replies: [{ author: "agent", sessionId: "s", body: "done", at: "t" }, { author: "robot", sessionId: null, body: "?", at: "t" }], round: "r1", createdAt: "", updatedAt: "" };
  const read = readThread(wire)!;
  assert.equal(read.state, "open");
  assert.deepEqual(read.replies.map(reply => reply.author), ["agent", "user"]);
  for (const broken of [{ state: "lost" }, { side: "left" }, { target: "pr" }, { startLine: 4 }, { startLine: -1 }])
    assert.equal(readThread({ ...wire, ...broken }), null, JSON.stringify(broken));
});

test("filters keep a state, the outdated ones or all; active is everything not resolved", () => {
  const threads = [thread("c1", "a", 1), thread("c2", "a", 2, { state: "open" }), thread("c3", "a", 3, { state: "addressed" }), thread("c4", "a", 4, { state: "resolved" }),
    thread("c5", "a", 5, { state: "open", outdated: true }), thread("c6", "a", 6, { state: "resolved", outdated: true })];
  const ids = (filter: Parameters<typeof filterThreads>[1]) => filterThreads(threads, filter).map(item => item.id);
  assert.deepEqual(ids("active"), ["c1", "c2", "c3", "c5"]);
  assert.deepEqual(ids("draft"), ["c1"]);
  assert.deepEqual(ids("open"), ["c2", "c5"]);
  assert.deepEqual(ids("resolved"), ["c4", "c6"]);
  assert.deepEqual(ids("outdated"), ["c5"], "a resolved thread is no longer outdated");
  assert.equal(ids("all").length, 6);
  assert.deepEqual(threadCounts(threads), { draft: 1, open: 2, addressed: 1, resolved: 2, outdated: 1, active: 4, total: 6 });
  assert.deepEqual(threads.map(threadChip), ["draft", "sent", "addressed", "resolved", "outdated", "resolved"]);
});

test("the threads a surface draws are on its side and lines, not resolved, not outdated", () => {
  const threads = [thread("c1", "a", 2), thread("c2", "a", 0, { endLine: 0 }), thread("c3", "a", 3, { side: "old" }), thread("c4", "a", 4, { outdated: true }),
    thread("c5", "a", 5, { state: "resolved" }), thread("c6", "b", 1)];
  assert.deepEqual(threadsOnLines(threads, "new", "a").map(item => item.id), ["c1"]);
  assert.deepEqual(threadsOnLines(threads, "old").map(item => item.id), ["c3"]);
  assert.deepEqual(threadsOnLines(threads).map(item => item.id), ["c1", "c6"]);
});

test("threads are grouped by file, outdated ones apart, and n/p go through them in that order, wrapping around", () => {
  const threads = [thread("c1", "b", 4), thread("c2", "a", 9, { state: "open", outdated: true }), thread("c3", "a", 2), thread("c4", "b", 1)];
  const groups = groupThreads(threads);
  assert.deepEqual(groups.files.map(group => [group.path, group.threads.map(item => item.id)]), [["a", ["c3"]], ["b", ["c4", "c1"]]]);
  assert.deepEqual(groups.outdated.map(item => item.id), ["c2"]);
  const listed = listedThreads(threads);
  assert.deepEqual(listed.map(item => item.id), ["c3", "c4", "c1", "c2"]);
  assert.equal(neighbourThread(listed, { id: "c1" }, 1)?.id, "c2");
  assert.equal(neighbourThread(listed, { id: "c2" }, 1)?.id, "c3", "wraps after the last");
  assert.equal(neighbourThread(listed, { id: "c3" }, -1)?.id, "c2", "wraps before the first");
  assert.equal(neighbourThread(listed, { path: "b", line: 2 }, 1)?.id, "c1", "from a line: the next thread after it");
  assert.equal(neighbourThread(listed, { path: "b", line: 2 }, -1)?.id, "c4");
  assert.equal(neighbourThread([], { id: "c1" }, 1), null);
});

test("lines of a text are cut as the host cuts them, LF, CRLF or CR", () => {
  assert.equal(textLines("a\r\nb\nc\rd", 2, 3), "b\nc");
  assert.equal(textLines("a\nb", 2, 9), "b");
  assert.equal(textLines("a", 0, 1), "");
});

test("a suggestion is a small diff: common lines kept, the others taken out and put in; empty takes everything out", () => {
  assert.deepEqual(suggestionDiff("a\nb\nc", "a\nB\nc"), [{ kind: "same", text: "a" }, { kind: "removed", text: "b" }, { kind: "added", text: "B" }, { kind: "same", text: "c" }]);
  assert.deepEqual(suggestionDiff("a\nb", ""), [{ kind: "removed", text: "a" }, { kind: "removed", text: "b" }]);
  assert.deepEqual(suggestionDiff("a", "a\nb"), [{ kind: "same", text: "a" }, { kind: "added", text: "b" }]);
});

test("a comment-only round needs a comment or an overall comment; a verdict needs nothing", () => {
  assert.equal(canSubmitReview({ draft: 0, open: 0 }, "commented", " "), false);
  assert.equal(canSubmitReview({ draft: 0, open: 0 }, "commented", "Looks fine"), true);
  assert.equal(canSubmitReview({ draft: 1, open: 0 }, "commented", ""), true);
  assert.equal(canSubmitReview({ draft: 0, open: 0 }, "approved", ""), true);
  assert.equal(isPlanPath(".alta/plans/2026-10-20-x.md"), true);
  assert.equal(isPlanPath("doc/x.md"), false);
});

test("a card goes after the widest block that starts in its thread and holds its last line, else the narrowest that holds it", () => {
  // A paragraph 1-2, a list item 4-8 holding a sub-item 6-8 and its paragraph 6-6, a table row 10-10.
  const blocks = [{ start: 1, end: 2 }, { start: 4, end: 8 }, { start: 6, end: 8 }, { start: 6, end: 6 }, { start: 10, end: 10 }];
  assert.equal(placementBlock(blocks, { startLine: 4, endLine: 8 }), 1, "the item with its sub-item");
  assert.equal(placementBlock(blocks, { startLine: 1, endLine: 8 }), 1, "a range of blocks ends after its last block");
  assert.equal(placementBlock(blocks, { startLine: 6, endLine: 8 }), 2);
  assert.equal(placementBlock(blocks, { startLine: 5, endLine: 5 }), 1, "a line inside a block: the block");
  assert.equal(placementBlock(blocks, { startLine: 9, endLine: 9 }), -1);
  assert.deepEqual(blockRange({ start: 6, end: 8 }, { start: 1, end: 2 }), { start: 1, end: 8 });
  assert.equal(blockCommented({ start: 4, end: 8 }, { startLine: 8, endLine: 9 }), true);
  assert.equal(blockCommented({ start: 4, end: 8 }, { startLine: 9, endLine: 9 }), false);
});

test("a review round prompt is recognized by its heading and round line, nothing else is", () => {
  assert.deepEqual(reviewPromptSummary("# Review of your changes: changes requested\n\nRound 2 · 3 comments, 1 still open from earlier rounds · reviewed snapshot `4f2a9c1`\n\nText"),
    { subject: { kind: "changes" }, verdict: "changes_requested", round: 2, comments: 3, earlier: 1 });
  assert.deepEqual(reviewPromptSummary("# Review of `.alta/plans/p.md`: approved\n\nRound 1 · 1 comment\n"),
    { subject: { kind: "file", path: ".alta/plans/p.md" }, verdict: "approved", round: 1, comments: 1, earlier: 0 });
  assert.equal(reviewPromptSummary("# Review of the changes of your branch: comments\n\nRound 3 · 0 comments")?.verdict, "commented");
  assert.equal(reviewPromptSummary("# Review of my essay: great"), null);
  assert.equal(reviewPromptSummary("Please # Review of your changes: approved"), null);
});

test("the empty line an editor shows after a final line break takes no comment", () => {
  assert.equal(lastCommentableLine(5, true), 4);
  assert.equal(lastCommentableLine(5, false), 5);
  assert.equal(lastCommentableLine(1, true), 1, "an empty file has its one line");
});
