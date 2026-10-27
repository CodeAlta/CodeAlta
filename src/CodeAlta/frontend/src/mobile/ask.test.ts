import assert from "node:assert/strict";
import test from "node:test";
import {
  askAnswer, askBadge, askChoice, askCommentsText, askGo, askLimits, askMarkKey, askMode, askPrimaryLabel, askReviewWhat, askSendable, askSettled, askStep, askTextRoom,
  askVerdict, askWaiting, newAskDraft, newAskLocal,
} from "./ask";
import { maximumReviewText } from "../review/ReviewThreadCard";
import type { Ask, AskQuestion, Live } from "./contract";

const question = (title: string, choices: string[] = [], freeform = false): AskQuestion => ({
  title, question: `${title}?`, description: null,
  choices: choices.map(choice => ({ title: choice, description: null })),
  freeform: freeform ? { title: null, placeholder: null } : null,
});
const ask = (questions: AskQuestion[], review: Ask["review"] = null): Ask => ({ id: "a1", revision: "1", state: "pending", questions, review });
const mark = { id: "a1", revision: "1", state: "pending" as const, immediate: false };
const live = (state: Live["live"], asking: Live["asking"] = mark): Live => ({ id: "s1", projectId: "p1", live: state, kind: null, line: null, at: null, activity: null, canSteer: false, asking });

test("the ask takes the place of the composer once the run that asked ended, as on the desktop", () => {
  const pending = ask([question("Database", ["Postgres", "SQLite"])]);
  assert.equal(askMode(pending, live("idle"), undefined), "form");
  assert.equal(askMode(pending, live("running"), undefined), null, "the run still works: its composer stays");
  assert.equal(askMode(null, live("idle"), undefined), null);
  assert.equal(askMode(ask([]), live("idle"), undefined), null, "nothing to answer");
});

test("a question of a script is shown while its turn runs, in the list too", () => {
  const scripted = { ...ask([question("Deploy", ["Yes"])]), immediate: true };
  assert.equal(askMode(scripted, live("running"), undefined), "form");
  assert.equal(askWaiting(live("running", { ...mark, immediate: true })), true);
  assert.equal(askWaiting(live("running")), false, "another ask waits for the turn to end");
  assert.equal(askWaiting(live("idle")), true);
  assert.equal(askWaiting(live("idle", { ...mark, state: "submitting" })), false, "an answer on its way waits for nothing");
  assert.equal(askWaiting(live("idle", null)), false);
});

test("an answer on its way, or whose outcome is unknown, shows read-only", () => {
  const pending = ask([question("Database", ["Postgres"])]);
  assert.equal(askMode({ ...pending, state: "submitting" }, live("idle"), undefined), "sending");
  assert.equal(askMode({ ...pending, state: "indeterminate" }, live("idle"), undefined), "unknown");
  assert.equal(askMode(pending, live("idle"), { ...newAskLocal(), sending: true }), "sending", "the request of this phone");
  assert.equal(askMode(pending, live("running"), { ...newAskLocal(), sending: true }), "sending", "even when the run starts meanwhile");
  const submitted = { ...newAskLocal(), submitted: { askId: "a1", revision: "1" } };
  assert.equal(askMode(pending, live("idle"), submitted), "sending", "until the ask read again says what became of it");
  assert.equal(askMode({ ...pending, revision: "2" }, live("idle"), submitted), "form", "another revision waits for an answer");
});

test("the ask read again ends the answer, or asks for it again in a new revision", () => {
  const sent = { askId: "a1", revision: "1" };
  assert.equal(askSettled(null, sent), "done", "the session took it");
  assert.equal(askSettled({ ...ask([]), id: "a2" }, sent), "done", "another ask waits");
  assert.equal(askSettled({ ...ask([]), revision: "2" }, sent), "again", "the session did not take it");
  assert.equal(askSettled({ ...ask([]), state: "submitting" }, sent), "keep");
  assert.equal(askSettled(ask([]), sent), "open", "refused: the form again, and the same answer says why");
  assert.equal(askSettled({ ...ask([]), state: "indeterminate" }, sent), "keep");
  assert.equal(askMarkKey(mark), askMarkKey({ ...mark }));
  assert.notEqual(askMarkKey(mark), askMarkKey({ ...mark, revision: "2" }), "a new revision is read again");
  assert.notEqual(askMarkKey(mark), askMarkKey({ ...mark, state: "submitting" }), "so is a new state");
  assert.equal(askMarkKey(null), "");
});

test("the text answers share the limit of the computer, and the overall comment has that of the review store", () => {
  const pending = ask([question("One", [], true), question("Two", [], true)]);
  const draft = newAskDraft();
  assert.equal(askTextRoom(pending, draft, 0), askLimits.answerText);
  draft.text.set(0, "x".repeat(8000));
  assert.equal(askTextRoom(pending, draft, 1), askLimits.answerText - 8000);
  assert.equal(askSendable(pending, draft), true);
  draft.text.set(1, "y".repeat(193));
  assert.equal(askSendable(pending, draft), false, "beyond the limit of all the answers together");
  assert.equal(askLimits.answerText, 8192);
  assert.equal(askLimits.overallComment, maximumReviewText);
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
