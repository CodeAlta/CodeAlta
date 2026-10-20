import assert from "node:assert/strict";
import test from "node:test";
import { questionTabTitle, selectedChoice, submitStep } from "./askReview";
import { captureAskAction, parseAskPage } from "./sessionAsks";

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
const page = (review: unknown, questions: unknown[] = [question]) => ({ status: "ok", hostEpoch: epoch, sessionId: "session", hasMore: false, latest: null,
  head: { handle, state: "pending", request: { review, questions } } });
const fileReview = (path: unknown) => ({ kind: "file", path, title: null, message: null, projectId: "p", checkout: null });

test("a request may ask to review a file: a relative path below the checkout", () => {
  assert.equal(parseAskPage(page(null), epoch, "session").head!.request.review, null);
  assert.equal(parseAskPage(page(undefined), epoch, "session").head!.request.review, null);
  assert.equal(parseAskPage(page(fileReview(".alta/plans/2026-10-04-plan.md")), epoch, "session").head!.request.review!.path, ".alta/plans/2026-10-04-plan.md");
  for (const path of ["", "../plan.md", "/plan.md", "C:/plan.md", "a\\b.md", "a//b.md", "a/./b.md", "a\nb.md", "x".repeat(1001)])
    assert.throws(() => parseAskPage(page(fileReview(path)), epoch, "session"), Error, JSON.stringify(path));
  assert.throws(() => parseAskPage(page("plan.md"), epoch, "session"));
  assert.throws(() => parseAskPage(page({ kind: "diff", path: null }), epoch, "session"));
  // A review needs no question; a plain question does.
  assert.equal(parseAskPage(page({ kind: "changes", path: null }, []), epoch, "session").head!.request.review!.kind, "changes");
  assert.throws(() => parseAskPage(page(null, []), epoch, "session"));
});

test("the answer of a review carries its verdict, the comments being in the review store", () => {
  const answers = [{ questionIndex: 0, selectedChoiceIndexes: [0] }];
  assert.equal(captureAskAction(epoch, handle, answers, epoch).action.review, null);
  const request = captureAskAction(epoch, handle, answers, epoch, { verdict: "changes_requested", overall: "Good start." });
  assert.deepEqual(request.action.review, { verdict: "changes_requested", overall: "Good start." });
  assert.ok(Object.isFrozen(request.action.review));
  assert.equal(captureAskAction(epoch, handle, answers, epoch, { verdict: "approved", overall: "   " }).action.review!.overall, null);
  for (const review of [{ verdict: "maybe" }, { verdict: "approved", overall: "x".repeat(16385) }])
    assert.throws(() => captureAskAction(epoch, handle, answers, epoch, review));
});
