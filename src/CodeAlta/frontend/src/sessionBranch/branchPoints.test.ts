import assert from "node:assert/strict";
import test from "node:test";
import { branchRows, canBranchFrom, composerPrompt, forkConsequence, initialSelection, keepSelection, mergeTurnPages, rewindBlock, rewindConsequence,
  rowId, runningTurnNumber, turnForOffset, turnPromptKeys, turnSender, turnText, type BranchTurn } from "./branchPoints";

const turn = (number: number, values: Partial<BranchTurn> = {}): BranchTurn => ({ number, runId: `run-${number}`, status: "completed",
  startedAt: `2026-10-12T10:${String(number).padStart(2, "0")}:00Z`, endedAt: `2026-10-12T10:${String(number).padStart(2, "0")}:30Z`,
  promptOffset: String(number * 1000), prompt: `prompt ${number}`, promptTruncated: false, sourceSessionId: null, answersQuestion: false,
  steers: 0, assistantMessages: 1, toolCalls: 0, compactions: 0, ...values });
const job = "[CodeAlta background job]\nJob: job-1\nTitle: Build\nCommand: dotnet build\nResult: succeeded (exit code 0) after 12 s\n\nok";
const delegated = "[CodeAlta delegated-agent message]\nSource session: child-1\nKind: result\n\nThe review is done.";
const ids = (rows: ReturnType<typeof branchRows>) => rows.map(rowId);

test("who started a turn: the user, an agent, the answer to a question or a background job", () => {
  assert.equal(turnSender(turn(1)), "you");
  assert.equal(turnSender(turn(1, { sourceSessionId: "parent" })), "agent");
  assert.equal(turnSender(turn(1, { prompt: delegated })), "agent");
  assert.equal(turnSender(turn(1, { answersQuestion: true })), "answer");
  assert.equal(turnSender(turn(1, { prompt: job })), "job");
  // What the list shows of such a prompt is what the timeline shows: no envelope.
  assert.equal(turnText(turn(1, { prompt: delegated })), "The review is done.");
  assert.equal(turnText(turn(1, { prompt: job })), "Build · succeeded (exit code 0) after 12 s");
  assert.equal(turnText(turn(1)), "prompt 1");
});

test("only a prompt the user wrote goes back to a composer", () => {
  assert.equal(composerPrompt("fix the test"), "fix the test");
  assert.equal(composerPrompt(job), null);
  assert.equal(composerPrompt(delegated), null);
  assert.equal(composerPrompt("  \n"), null);
  assert.equal(composerPrompt(null), null);
});

test("the list starts with the end of the session, then the turns from the last to the first", () => {
  const turns = [turn(1), turn(2), turn(3)];
  assert.deepEqual(ids(branchRows(turns, 3, { onlyMine: true, query: "" })), ["end", "turn:run-3", "turn:run-2", "turn:run-1"]);
  // More turns than were read: a row loads the ones before.
  const rows = branchRows(turns.slice(1), 3, { onlyMine: true, query: "" });
  assert.deepEqual(ids(rows), ["end", "turn:run-3", "turn:run-2", "more"]);
  assert.deepEqual(rows.at(-1), { kind: "more", missing: 1 });
  assert.deepEqual(ids(branchRows([], 0, { onlyMine: true, query: "" })), ["end"]);
});

test("only my prompts hides the turns an agent, a job or an answer started", () => {
  const turns = [turn(1), turn(2, { sourceSessionId: "parent" }), turn(3, { prompt: job }), turn(4, { answersQuestion: true }), turn(5)];
  assert.deepEqual(ids(branchRows(turns, 5, { onlyMine: true, query: "" })), ["end", "turn:run-5", "turn:run-1"]);
  assert.deepEqual(ids(branchRows(turns, 5, { onlyMine: false, query: "" })), ["end", "turn:run-5", "turn:run-4", "turn:run-3", "turn:run-2", "turn:run-1"]);
});

test("the filter keeps the turns whose prompt holds every word, whatever their case, and leaves the end out", () => {
  const turns = [turn(1, { prompt: "Add the Fork command" }), turn(2, { prompt: "fix the rewind test" }), turn(3, { prompt: "rename fork to branch" })];
  assert.deepEqual(ids(branchRows(turns, 3, { onlyMine: true, query: "fork" })), ["turn:run-3", "turn:run-1"]);
  assert.deepEqual(ids(branchRows(turns, 3, { onlyMine: true, query: " FORK  command " })), ["turn:run-1"]);
  assert.deepEqual(ids(branchRows(turns, 3, { onlyMine: true, query: "nothing" })), []);
  // The filter reads the text that is shown, not the envelope of a message between agents.
  assert.deepEqual(ids(branchRows([turn(1, { prompt: delegated })], 1, { onlyMine: false, query: "delegated-agent" })), []);
  assert.deepEqual(ids(branchRows([turn(1, { prompt: delegated })], 1, { onlyMine: false, query: "review" })), ["turn:run-1"]);
  // Older turns can still be loaded while a filter is typed.
  assert.deepEqual(ids(branchRows(turns.slice(1), 3, { onlyMine: true, query: "fork" })), ["turn:run-3", "more"]);
});

test("pages of turns merge in session order, and a turn read again replaces what it was", () => {
  const newest = [turn(101), turn(102, { status: "running", endedAt: null })];
  const merged = mergeTurnPages(newest, [turn(99), turn(100)]);
  assert.deepEqual(merged.map(value => value.number), [99, 100, 101, 102]);
  const again = mergeTurnPages(merged, [turn(102), turn(103)]);
  assert.deepEqual(again.map(value => value.number), [99, 100, 101, 102, 103]);
  assert.equal(again[3].status, "completed");
  assert.equal(mergeTurnPages([], []).length, 0);
});

test("a message of the timeline names its turn by its offset: the one it starts, or the one it was sent in", () => {
  const turns = [turn(5), turn(6), turn(7)];
  assert.equal(turnForOffset(turns, "6000")?.number, 6);
  // A message sent while turn 6 ran lies between the prompts of turns 6 and 7.
  assert.equal(turnForOffset(turns, "6400")?.number, 6);
  assert.equal(turnForOffset(turns, "99000")?.number, 7);
  // Before the oldest turn read: its turn is on an older page.
  assert.equal(turnForOffset(turns, "4999"), null);
  assert.equal(turnForOffset(turns, "outgoing:1"), null);
  assert.equal(turnForOffset([turn(1, { promptOffset: null })], "10"), null);
  // An offset beyond what a number holds exactly is still compared as it is.
  assert.equal(turnForOffset([turn(1, { promptOffset: "9007199254740993" }), turn(2, { promptOffset: "9007199254740995" })], "9007199254740994")?.number, 1);
});

test("a fork before turn N starts with turns 1 to N-1; at the end, with every turn but the one that runs", () => {
  assert.deepEqual(forkConsequence(turn(4), 9, null), { kept: 3, next: 4 });
  assert.deepEqual(forkConsequence(turn(1), 9, null), { kept: 0, next: 1 });
  assert.deepEqual(forkConsequence(null, 9, null), { kept: 9, next: null });
  assert.deepEqual(forkConsequence(null, 9, 9), { kept: 8, next: null });
  assert.deepEqual(forkConsequence(null, 0, null), { kept: 0, next: null });
  // A fork before the turn that runs is allowed: it copies what came before.
  assert.deepEqual(forkConsequence(turn(9, { status: "running" }), 9, 9), { kept: 8, next: 9 });
});

test("a rewind to turn N removes turns N to the last one", () => {
  assert.deepEqual(rewindConsequence(turn(4), 9), { first: 4, last: 9, count: 6, kept: 3 });
  assert.deepEqual(rewindConsequence(turn(9), 9), { first: 9, last: 9, count: 1, kept: 8 });
  assert.deepEqual(rewindConsequence(turn(1), 3), { first: 1, last: 3, count: 3, kept: 0 });
});

test("the turn that runs is known by its state or by the run of the session", () => {
  assert.equal(runningTurnNumber([turn(1), turn(2, { status: "running" })], null), 2);
  assert.equal(runningTurnNumber([turn(1), turn(2)], "run-2"), 2);
  assert.equal(runningTurnNumber([turn(1), turn(2)], null), null);
  assert.equal(runningTurnNumber([turn(1)], "run-on-another-page"), null);
});

test("a rewind says beforehand why it cannot be made", () => {
  const idle = { canMutate: true, running: false, question: false, background: 0 };
  assert.equal(rewindBlock(turn(3), idle, null), null);
  assert.equal(rewindBlock(null, idle, null), "end");
  assert.equal(rewindBlock(turn(3), { ...idle, canMutate: false }, null), "read_only");
  assert.equal(rewindBlock(turn(3), { ...idle, running: true }, null), "running");
  // The pane may not know yet that the session works: the turns say it.
  assert.equal(rewindBlock(turn(3), idle, "run-9"), "running");
  assert.equal(rewindBlock(turn(3, { status: "running" }), idle, null), "running");
  assert.equal(rewindBlock(turn(3), { ...idle, question: true }, null), "question");
  assert.equal(rewindBlock(turn(3), { ...idle, background: 2 }, null), "background");
  // What comes first is what must be dealt with first.
  assert.equal(rewindBlock(null, { canMutate: false, running: true, question: true, background: 1 }, "run"), "read_only");
  assert.equal(rewindBlock(turn(3), { canMutate: true, running: true, question: true, background: 1 }, null), "running");
});

test("only a prompt the user wrote, kept in the journal, gets the actions of a fork and a rewind", () => {
  const user = { key: "1200", category: "user", eventType: "contentCompleted" };
  assert.equal(canBranchFrom(user), true);
  assert.equal(canBranchFrom({ ...user, delegated: true }), false);
  assert.equal(canBranchFrom({ ...user, jobResult: { jobId: "job-1" } }), false);
  assert.equal(canBranchFrom({ ...user, category: "assistant" }), false);
  // A prompt being sent and a live row have no journal offset yet.
  assert.equal(canBranchFrom({ ...user, key: "outgoing:abc" }), false);
  assert.equal(canBranchFrom({ ...user, key: '["run","content","User"]' }), false);
  assert.equal(canBranchFrom({ ...user, eventType: "contentDelta" }), false);
});

test("the prompts that start a turn are the first user message of each run: a message sent while it ran starts none", () => {
  const entry = (offset: string, kind: string, runId: string | null, eventType = "contentCompleted") => ({ offset, kind, runId, eventType });
  const keys = turnPromptKeys([entry("10", "User", "a"), entry("20", "Assistant", "a"), entry("30", "User", "a"), entry("40", "user", "b"),
    entry("50", "User", null), entry("60", "User", "c", "contentDelta"), entry("70", "User", "c")]);
  assert.deepEqual([...keys], ["10", "40", "70"]);
  assert.equal(turnPromptKeys([]).size, 0);
});

test("the selection stays on its row while it is listed, and the window opens on what was asked for", () => {
  const turns = [turn(1), turn(2), turn(3)];
  const rows = branchRows(turns, 3, { onlyMine: true, query: "" });
  assert.equal(keepSelection(rows, "turn:run-2", 0), 2);
  assert.equal(keepSelection(rows, "turn:gone", 0), 0);
  assert.equal(keepSelection(rows, null, 9), 3);
  assert.equal(keepSelection([], "end", 0), 0);
  // A fork starts from the end of the session; a rewind from the last prompt; a message from its own turn.
  assert.equal(initialSelection(rows, "fork", null), 0);
  assert.equal(initialSelection(rows, "rewind", null), 1);
  assert.equal(initialSelection(rows, "fork", "run-1"), 3);
  assert.equal(initialSelection(rows, "rewind", "run-2"), 2);
  assert.equal(initialSelection(rows, "rewind", "run-unknown"), 1);
  assert.equal(initialSelection(branchRows([], 0, { onlyMine: true, query: "" }), "rewind", null), 0);
});
