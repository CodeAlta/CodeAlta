import assert from "node:assert/strict";
import test from "node:test";
import { addComment, commentsFit, editComment, finishComment, moveComments, neighbourComment, questionTabTitle, reviewSnapshot, selectedChoice, submitStep,
  type ReviewComment } from "./askReview";
import { captureAskAction, captureFileReview, parseAskPage } from "./sessionAsks";

const comment = (id: number, line: number, text = "", done = false): ReviewComment => ({ id, line, text, done });

test("a line has one comment: adding again names the one that is there", () => {
  const first = addComment([], 4, 1)!;
  assert.deepEqual(first, { comments: [comment(1, 4)], id: 1 });
  const again = addComment(first.comments, 4, 2)!;
  assert.equal(again.id, 1);
  assert.equal(again.comments, first.comments);
  assert.equal(addComment(first.comments, 0, 3), null);
  const full = Array.from({ length: 200 }, (_, index) => comment(index + 1, index + 1, "x"));
  assert.equal(addComment(full, 500, 999), null, "an answer carries at most 200 comments");
  assert.equal(addComment(full, 7, 999)!.id, 7, "a full review still finds the comment of a line");
});

test("editing a finished comment makes it unfinished; finishing needs text", () => {
  let comments: readonly ReviewComment[] = [comment(1, 2)];
  comments = finishComment(comments, 1);
  assert.equal(comments[0].done, false, "an empty comment is not finished");
  comments = finishComment(editComment(comments, 1, "Split this step."), 1);
  assert.deepEqual(comments[0], comment(1, 2, "Split this step.", true));
  assert.equal(editComment(comments, 1, "Split this step.")[0].done, true, "the same text changes nothing");
  assert.equal(editComment(comments, 1, "Split it.")[0].done, false);
  assert.equal(editComment(comments, 1, "x".repeat(5000))[0].text.length, 4000);
});

test("comments follow their lines when the file is edited", () => {
  const comments = [comment(1, 3, "a"), comment(2, 9, "b")];
  const moved = moveComments(comments, id => id === 1 ? 5 : 9);
  assert.deepEqual(moved.map(item => item.line), [5, 9]);
  assert.equal(moveComments(moved, id => id === 1 ? 5 : 9), moved, "nothing moved, nothing changes");
  assert.deepEqual(moveComments(moved, () => null), moved, "a comment without a position stays where it was");
});

test("next and previous comment wrap around, from a line or from a comment", () => {
  const comments = [comment(3, 12), comment(1, 2), comment(2, 7)];
  assert.equal(neighbourComment([], { line: 1 }, 1), null);
  assert.equal(neighbourComment(comments, { line: 1 }, 1)!.id, 1);
  assert.equal(neighbourComment(comments, { line: 7 }, 1)!.id, 3);
  assert.equal(neighbourComment(comments, { line: 20 }, 1)!.id, 1, "after the last comes the first");
  assert.equal(neighbourComment(comments, { line: 1 }, -1)!.id, 3, "before the first comes the last");
  assert.equal(neighbourComment(comments, { line: 7, id: 2 }, 1)!.id, 3);
  assert.equal(neighbourComment(comments, { line: 12, id: 3 }, 1)!.id, 1);
  assert.equal(neighbourComment(comments, { line: 2, id: 1 }, -1)!.id, 3);
});

test("the review sends every comment that says something, in line order, finished or not", () => {
  const comments = [comment(1, 12, "Later", true), comment(2, 3, "  "), comment(3, 4, "Still typing")];
  assert.deepEqual(reviewSnapshot(comments, true), { fileModifiedAndSaved: true,
    comments: [{ line: 4, text: "Still typing" }, { line: 12, text: "Later" }] });
  assert.equal(commentsFit(comments), true);
  assert.equal(commentsFit(Array.from({ length: 5 }, (_, index) => comment(index, index + 1, "x".repeat(4000)))), false);
  // What the snapshot gives is what an answer accepts.
  assert.deepEqual(captureFileReview(reviewSnapshot(comments, false)).comments.map(item => item.line), [4, 12]);
});

test("question tabs point onwards and the last one closes", () => {
  assert.equal(questionTabTitle("Plan review", 0, 2), "Plan review →");
  assert.equal(questionTabTitle("Next step", 1, 2), "Next step ✓");
  assert.equal(questionTabTitle("Only", 0, 1), "Only ✓");
});

test("a choice question answers with the picked choice, or with its first", () => {
  const choices = [{ title: "Yes", description: null }, { title: "No", description: null }];
  assert.equal(selectedChoice({ choices }, undefined), 0);
  assert.equal(selectedChoice({ choices }, [1]), 1);
  assert.equal(selectedChoice({ choices }, [9]), 0, "a choice that does not exist is not an answer");
  assert.equal(selectedChoice({ choices: [] }, [0]), null);
});

test("submit shows every question once before it sends", () => {
  assert.deepEqual(submitStep(new Set([0]), 0, 3), { kind: "show", index: 1 });
  assert.deepEqual(submitStep(new Set([0, 1]), 1, 3), { kind: "show", index: 2 });
  assert.deepEqual(submitStep(new Set([0, 2]), 2, 3), { kind: "show", index: 1 }, "a skipped question is shown first");
  assert.deepEqual(submitStep(new Set([0, 1, 2]), 0, 3), { kind: "submit" });
  assert.deepEqual(submitStep(new Set(), 0, 1), { kind: "submit" }, "the question in front of the user counts as shown");
});

const epoch = "11111111-1111-4111-8111-111111111111";
const handle = Object.freeze({ operationId: epoch, runtimeInstanceId: epoch, attachmentGeneration: "1", providerId: "fixture", sessionId: "session", runId: "run",
  askId: epoch, responseGeneration: "0" });
const question = { title: "Plan review", question: "Does this plan match your intent?", description: null, choices: [{ title: "Yes", description: null }], freeform: null };
const page = (file: unknown) => ({ status: "ok", hostEpoch: epoch, sessionId: "session", hasMore: false, latest: null,
  head: { handle, state: "pending", request: { file, questions: [question] } } });

test("an ask may give a file to review: a relative path below the session's folder", () => {
  assert.equal(parseAskPage(page(null), epoch, "session").head!.request.file, null);
  assert.equal(parseAskPage(page(undefined), epoch, "session").head!.request.file, null);
  assert.deepEqual(parseAskPage(page({ path: ".alta/plans/2026-10-04-plan.md" }), epoch, "session").head!.request.file, { path: ".alta/plans/2026-10-04-plan.md" });
  for (const path of ["", "../plan.md", "/plan.md", "C:/plan.md", "a\\b.md", "a//b.md", "a/./b.md", "a\nb.md", "x".repeat(1001)])
    assert.throws(() => parseAskPage(page({ path }), epoch, "session"), Error, JSON.stringify(path));
  assert.throws(() => parseAskPage(page("plan.md"), epoch, "session"));
});

test("an answer carries the file review within the host's limits", () => {
  const answers = [{ questionIndex: 0, selectedChoiceIndexes: [0] }];
  assert.equal(captureAskAction(epoch, handle, answers, epoch).action.fileReview, null);
  const request = captureAskAction(epoch, handle, answers, epoch, { fileModifiedAndSaved: true,
    comments: [{ line: 9, text: "b" }, { line: 2, text: "a" }, { line: 9, text: "c" }] });
  assert.deepEqual(request.action.fileReview, { fileModifiedAndSaved: true, comments: [{ line: 2, text: "a" }, { line: 9, text: "b" }, { line: 9, text: "c" }] });
  assert.ok(Object.isFrozen(request.action.fileReview) && Object.isFrozen(request.action.fileReview!.comments));
  for (const comments of [[{ line: 0, text: "x" }], [{ line: 1.5, text: "x" }], [{ line: 1, text: "   " }], [{ line: 1, text: "x".repeat(4001) }],
    Array.from({ length: 201 }, (_, index) => ({ line: index + 1, text: "x" })), Array.from({ length: 5 }, (_, index) => ({ line: index + 1, text: "x".repeat(4000) }))])
    assert.throws(() => captureAskAction(epoch, handle, answers, epoch, { fileModifiedAndSaved: false, comments }));
});
