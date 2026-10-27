import assert from "node:assert/strict";
import test from "node:test";
import {
  askAnswer, askBadge, askChoice, askCommentsText, askGo, askPrimaryLabel, askReviewWhat, askSendable, askShown, askStep, askVerdict, newAskDraft,
} from "./ask";
import type { Ask, AskQuestion, Live } from "./contract";

const question = (title: string, choices: string[] = [], freeform = false): AskQuestion => ({
  title, question: `${title}?`, description: null,
  choices: choices.map(choice => ({ title: choice, description: null })),
  freeform: freeform ? { title: null, placeholder: null } : null,
});
const ask = (questions: AskQuestion[], review: Ask["review"] = null): Ask => ({ id: "a1", revision: "1", state: "pending", questions, review });
const live = (state: Live["live"]): Live => ({ id: "s1", projectId: "p1", live: state, kind: null, line: null, at: null, activity: null, canSteer: false, asking: true });

test("the ask takes the place of the composer once the run that asked ended, as on the desktop", () => {
  const pending = ask([question("Database", ["Postgres", "SQLite"])]);
  assert.equal(askShown(pending, live("idle")), true);
  assert.equal(askShown(pending, live("running")), false, "the run still works: its composer stays");
  assert.equal(askShown({ ...pending, state: "submitting" }, live("idle")), false, "an answer on its way is not asked again");
  assert.equal(askShown(null, live("idle")), false);
});

test("the badge says what the agent asks for, and a review what it is of", () => {
  assert.equal(askBadge(ask([question("One")])), "A question for you");
  assert.equal(askBadge(ask([question("One"), question("Two")])), "Questions for you");
  const file = { kind: "file" as const, path: "docs/plan.md", title: null, message: null, comments: 0 };
  assert.equal(askBadge(ask([], file)), "Review requested");
  assert.equal(askReviewWhat(file), "plan.md");
  assert.equal(askReviewWhat({ ...file, title: "The plan" }), "The plan");
  assert.equal(askReviewWhat({ ...file, kind: "branch", path: null }), "The changes of the branch");
  assert.equal(askReviewWhat({ ...file, kind: "changes", path: null }), "The uncommitted changes");
});

test("a question answers with its first choice until another is picked, and its text when it has some", () => {
  const pending = ask([question("Database", ["Postgres", "SQLite"], true), question("Name", [], true)]);
  const draft = newAskDraft();
  assert.equal(askChoice(pending, draft, 0), 0);
  draft.choices.set(0, 1);
  draft.text.set(1, "parser");
  assert.deepEqual(askAnswer(pending, draft, "action").answers, [
    { question: 0, choices: [1], text: null },
    { question: 1, choices: [], text: "parser" },
  ]);
  assert.equal(askAnswer(pending, draft, "action").review, undefined, "questions carry no verdict");
});

test("Submit shows the questions not seen yet before it sends, as on the desktop", () => {
  const pending = ask([question("One", ["A"]), question("Two", ["B"]), question("Three", ["C"])]);
  const draft = newAskDraft();
  assert.equal(askPrimaryLabel(pending, draft), "Next");
  assert.deepEqual(askStep(pending, draft), { kind: "show", index: 1 });
  assert.equal(askGo(pending, draft, 2), true);
  assert.deepEqual(askStep(pending, draft), { kind: "show", index: 1 }, "the one skipped is shown before it sends");
  askGo(pending, draft, 1);
  assert.equal(askPrimaryLabel(pending, draft), "Submit");
  assert.equal(askGo(pending, draft, 3), false, "there is no fourth question");
});

test("a review defaults to changes asked when it has comments, and a comment-only one needs something to say", () => {
  const review = ask([], { kind: "changes", path: null, title: null, message: "Look at the parser.", comments: 2 });
  const draft = newAskDraft();
  assert.equal(askPrimaryLabel(review, draft), "Submit review");
  assert.equal(askVerdict(review, draft), "changes_requested");
  assert.equal(askVerdict({ ...review, review: { ...review.review!, comments: 0 } }, draft), "approved");
  draft.verdict = "commented";
  const silent = { ...review, review: { ...review.review!, comments: 0 } };
  assert.equal(askSendable(silent, draft), false);
  draft.overall = "Fine by me.";
  assert.equal(askSendable(silent, draft), true);
  assert.deepEqual(askAnswer(silent, draft, "action").review, { verdict: "commented", overall: "Fine by me." });
  assert.equal(askCommentsText(2), "Your review · 2 comments to send");
  assert.equal(askCommentsText(0), "Your review · No comment yet");
});
