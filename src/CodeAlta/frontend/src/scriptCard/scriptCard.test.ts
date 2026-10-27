import assert from "node:assert/strict";
import test from "node:test";
import { reconcileTimeline } from "../reconcileTimeline";
import { buildTimelineItems, execTitle, type HistoryEntry } from "../timeline";
import { toolFamily, toolCallFromItem } from "../toolCall";
import { groupTimelineTools } from "../toolGroups";
import { scriptCallCounts, shownScriptCalls, shownScriptCallsOf } from "./ScriptCalls";
import { scriptMarkState, scriptRunOffers, scriptWaitTarget, wakeTime } from "./scriptRunOffers";

const entry = (offset: string, changes: Partial<HistoryEntry> = {}): HistoryEntry => ({ offset,
  eventType: "activity", kind: "ToolCall", providerId: "provider", runId: "run", sessionId: "session",
  activityId: `tool-${offset}`, parentActivityId: null, phase: "Completed", contentId: null, interactionId: null, sourceSessionId: null, prompt: null,
  timestamp: "2026-10-26T00:00:00Z", name: "read_file", text: "Done", details: null,
  tool: null, files: null, images: null, textTruncated: false, detailsTruncated: false, bodyOmitted: false, ...changes });

const execDetails = JSON.stringify({ arguments: { code: '// @exec: {"title": "Review PR 231", "yield_ms": 1000}\nreturn 1;' } });

// An exec call that made two calls, one of them after its run ended (the script went on in the background).
function scriptEntries(): HistoryEntry[] {
  return [
    entry("1", { activityId: "call-1", name: "exec", phase: "Started", details: execDetails }),
    entry("2", { activityId: "call-1/1", parentActivityId: "call-1", phase: "Started", timestamp: "2026-10-26T00:00:01Z" }),
    entry("3", { activityId: "call-1/1", parentActivityId: "call-1", phase: "Completed", timestamp: "2026-10-26T00:00:03Z",
      details: JSON.stringify({ script: { stepId: "read" } }) }),
    entry("4", { activityId: "call-1", name: "exec", phase: "Completed", details: execDetails }),
    entry("5", { activityId: "call-1/2", parentActivityId: "call-1", runId: null, phase: "Failed", name: "shell_command", text: "exit 3" }),
    entry("6", { activityId: "other" }),
  ];
}

test("the calls of a script are lines of the card of its call, also those made after its run ended", () => {
  const items = buildTimelineItems(scriptEntries());

  assert.deepEqual(items.map(item => item.toolCall?.activityId), ["call-1", "other"]);
  const card = items[0];
  assert.equal(card.scriptTitle, "Review PR 231");
  assert.equal(card.summary, "Review PR 231");
  assert.deepEqual(card.scriptCalls?.map(call => [call.activityId, call.name, call.phase, call.stepId, call.error]),
    [["call-1/1", "read_file", "completed", "read", null], ["call-1/2", "shell_command", "failed", null, "exit 3"]]);
  assert.equal(card.scriptCalls?.[0].startedAt, "2026-10-26T00:00:01Z");
  assert.equal(card.scriptCalls?.[0].endedAt, "2026-10-26T00:00:03Z");
  assert.deepEqual(scriptCallCounts(card.scriptCalls!), { total: 2, running: 0, failed: 1 });
  assert.equal(toolFamily(toolCallFromItem(card)), "script");
});

test("a thousand calls of a script give the card its counts and a few lines, the running and failed ones first", () => {
  const nested = Array.from({ length: 1000 }, (_, index) => {
    const phase = index === 500 ? "Started" : index % 300 === 7 ? "Failed" : "Completed";
    return entry(`n${index}`, { activityId: `call-1/${index + 1}`, parentActivityId: "call-1", phase, name: index % 2 ? "read_file" : "grep" });
  });
  const items = buildTimelineItems([scriptEntries()[0], ...nested]);
  const calls = items[0].scriptCalls!;
  const shown = shownScriptCallsOf(calls);

  assert.equal(items.length, 1, "the calls are no rows of their own");
  assert.equal(calls.length, 1000);
  assert.deepEqual(scriptCallCounts(calls), { total: 1000, running: 1, failed: 4 });
  assert.equal(shown.length, shownScriptCalls);
  assert.deepEqual(shown.map(call => call.activityId), ["call-1/8", "call-1/308", "call-1/501", "call-1/608", "call-1/908", "call-1/1000"],
    "the running and the failed calls, then the last one, in the order the script made them");
});

test("a card keeps its identity while its records do not change", () => {
  const entries = scriptEntries();
  assert.equal(buildTimelineItems(entries)[0], buildTimelineItems(entries)[0]);
  const more = [...entries, entry("7", { activityId: "call-1/3", parentActivityId: "call-1", runId: null, phase: "Started" })];
  const grown = buildTimelineItems(more)[0];
  assert.notEqual(grown, buildTimelineItems(entries)[0]);
  assert.equal(grown.scriptCalls?.length, 3);
  // The lines that did not change are the same objects.
  assert.equal(grown.scriptCalls?.[0], buildTimelineItems(entries)[0].scriptCalls?.[0]);
});

test("a call whose parent is not in the window stays a row of its own", () => {
  const items = buildTimelineItems([entry("1", { activityId: "call-9/1", parentActivityId: "call-9" })]);
  assert.equal(items.length, 1);
  assert.equal(items[0].scriptCalls, undefined);
});

test("the calls of a script neither join nor split the group of the calls around it", () => {
  const entries = [entry("0", { activityId: "before" }), ...scriptEntries()];
  const groups = groupTimelineTools(reconcileTimeline(entries, null), entries);
  assert.deepEqual(groups.map(group => group.rows.map(row => row.source === "history" ? row.item.toolCall?.activityId : null)), [["before", "call-1", "other"]]);
});

test("the title of a script is read from its first line, in its code or in the JSON of its arguments", () => {
  assert.equal(execTitle('// @exec: {"title": "Bench"}\nreturn 1;'), "Bench");
  assert.equal(execTitle('{"code":"// @exec: {\\"title\\": \\"Escaped\\", \\"yield_ms\\": 5}\\nreturn 1;"}'), "Escaped");
  assert.equal(execTitle("return 1;"), null);
  assert.equal(execTitle('// @exec: {"yield_ms": 5}\nreturn 1;'), null);
});

// A record of a run as the history gives it: the call that runs the script is its parent.
const record = (offset: string, kind: string, changes: Partial<HistoryEntry> = {}, details: Record<string, unknown> = {}): HistoryEntry =>
  entry(offset, { eventType: "workflow", kind, runId: null, name: "run-1", parentActivityId: "call-1", activityId: null, phase: null, text: null,
    details: JSON.stringify(details), ...changes });

test("the records of a run give its card its steps and the sessions it started, each with its state", () => {
  const entries = [
    ...scriptEntries().slice(0, 1),
    record("r1", "RunStarted", { text: "Review PR 231" }),
    record("r2", "StepStarted", { activityId: "review" }),
    record("r3", "SessionStarted", { activityId: "child-a", text: "Security" }, { stepId: "review" }),
    record("r4", "SessionStarted", { activityId: "child-b", text: "Tests" }, { stepId: "review" }),
    record("r5", "SessionEnded", { activityId: "child-a", phase: "completed" }, { durationMs: 1200, inputTokens: 900, outputTokens: 100 }),
    record("r6", "SessionEnded", { activityId: "child-b", phase: "failed" }, { message: "It broke." }),
    record("r7", "StepEnded", { activityId: "review", phase: "completed" }, { durationMs: 1500 }),
  ];

  const run = buildTimelineItems(entries)[0].scriptRun!;

  assert.equal(run.runId, "run-1");
  assert.equal(run.status, "running", "no RunEnded record yet");
  assert.deepEqual(run.steps, [{ id: "review", status: "completed", durationMs: 1500, key: null, replayed: false }]);
  assert.deepEqual(run.sessions.map(session => [session.sessionId, session.title, session.stepId, session.status, session.inputTokens, session.message]),
    [["child-a", "Security", "review", "completed", 900, null], ["child-b", "Tests", "review", "failed", null, "It broke."]]);
  assert.equal(buildTimelineItems(entries)[0], buildTimelineItems(entries)[0], "the card keeps its identity while its records stay the same");
  const ended = buildTimelineItems([...entries, record("r8", "RunEnded", { phase: "completed" })])[0];
  assert.equal(ended.scriptRun?.status, "completed");
  assert.equal(buildTimelineItems(entries).length, 1, "the records are no rows of their own");
});

test("a step says its key and whether it was replayed; what ran when CodeAlta ended is interrupted", () => {
  const entries = [
    ...scriptEntries().slice(0, 1),
    record("r1", "RunStarted"),
    record("r2", "StepStarted", { activityId: "fetch" }),
    record("r3", "StepEnded", { activityId: "fetch", phase: "completed" }, { stepKey: "count-steps#0/fetch#0", replayed: true, durationMs: 0 }),
    record("r4", "StepStarted", { activityId: "merge" }),
    record("r5", "SessionStarted", { activityId: "child-a", text: "Merge" }, { stepId: "merge" }),
  ];
  const running = buildTimelineItems(entries)[0].scriptRun!;
  const interrupted = buildTimelineItems([...entries, record("r6", "RunEnded", { phase: "interrupted" })])[0].scriptRun!;

  assert.deepEqual(running.steps.map(step => [step.id, step.status, step.key, step.replayed]), [["fetch", "completed", "count-steps#0/fetch#0", true], ["merge", "running", null, false]]);
  assert.deepEqual(scriptRunOffers(running), { resume: false, rerun: false, rerunFromStep: false, saveAsScript: false }, "a run that runs offers nothing");
  assert.equal(interrupted.status, "interrupted");
  assert.deepEqual(interrupted.steps.map(step => step.status), ["completed", "interrupted"]);
  assert.equal(interrupted.sessions[0].status, "interrupted");
  assert.equal(scriptMarkState("interrupted"), "interrupted");
  assert.equal(scriptMarkState("cancelled"), "failed");
  assert.deepEqual(scriptRunOffers(interrupted), { resume: true, rerun: false, rerunFromStep: true, saveAsScript: true });
});

test("a run that waits for the user says for what, and its step waits", () => {
  const entries = [
    ...scriptEntries().slice(0, 1),
    record("r1", "RunStarted"),
    record("r2", "StepStarted", { activityId: "deploy" }),
    record("r3", "WaitStarted", { activityId: "deploy", phase: "question", text: "Deploy to production?" }),
  ];
  const asking = buildTimelineItems(entries)[0].scriptRun!;
  const answered = buildTimelineItems([...entries, record("r4", "WaitEnded", { activityId: "deploy", phase: "question" })])[0].scriptRun!;
  const approving = buildTimelineItems([...entries, record("r4", "WaitEnded", { phase: "question" }), record("r5", "WaitStarted", { phase: "approval" })])[0].scriptRun!;
  const ended = buildTimelineItems([...entries, record("r4", "RunEnded", { phase: "cancelled" })])[0].scriptRun!;

  assert.deepEqual(asking.waiting, { kind: "question", stepId: "deploy", text: "Deploy to production?" });
  assert.equal(asking.steps[0].status, "waiting");
  assert.equal(scriptMarkState(asking.steps[0].status), "waiting");
  assert.equal(answered.waiting, undefined);
  assert.equal(answered.steps[0].status, "running");
  assert.deepEqual(approving.waiting, { kind: "approval", stepId: null, text: null });
  assert.equal(ended.waiting, undefined, "a run that ended waits for nothing");
  assert.equal(scriptWaitTarget("question"), ".ask-form, .ask-panel");
  assert.equal(scriptWaitTarget("approval"), ".command-permission-panel");
});

test("a sleeping run says until when, and one CodeAlta ended during a sleep says when it resumes by itself", () => {
  const entries = [
    ...scriptEntries().slice(0, 1),
    record("r1", "RunStarted"),
    record("r2", "StepStarted", { activityId: "poll" }),
    record("r3", "WaitStarted", { activityId: "poll", phase: "sleep" }, { wakeAt: "2026-10-27T14:05:00+00:00" }),
  ];
  const sleeping = buildTimelineItems(entries)[0].scriptRun!;
  const awake = buildTimelineItems([...entries, record("r4", "WaitEnded", { activityId: "poll", phase: "sleep" })])[0].scriptRun!;
  const interrupted = buildTimelineItems([...entries, record("r4", "RunEnded", { phase: "interrupted" }, { wakeAt: "2026-10-27T14:05:00+00:00" })])[0].scriptRun!;
  const plain = buildTimelineItems([...entries, record("r4", "RunEnded", { phase: "interrupted" })])[0].scriptRun!;

  assert.deepEqual(sleeping.waiting, { kind: "sleep", stepId: "poll", text: null, until: "2026-10-27T14:05:00+00:00" });
  assert.equal(sleeping.steps[0].status, "waiting");
  assert.equal(awake.waiting, undefined);
  assert.equal(interrupted.waiting, undefined);
  assert.equal(interrupted.resumesAt, "2026-10-27T14:05:00+00:00");
  assert.equal(plain.resumesAt, undefined, "an interrupted run that does not resume by itself waits for Resume");
  assert.deepEqual(scriptRunOffers(interrupted), { resume: true, rerun: false, rerunFromStep: false, saveAsScript: true });

  const now = new Date(2026, 9, 27, 9, 0);
  const today = wakeTime(new Date(2026, 9, 27, 14, 5).toISOString(), now)!;
  const later = wakeTime(new Date(2026, 9, 29, 14, 5).toISOString(), now)!;
  assert.match(today, /14|2/u);
  assert.ok(later.length > today.length, "another day gives its date too");
  assert.equal(wakeTime("not a time", now), null);
  assert.equal(wakeTime(null, now), null);
});

test("a run that ended offers a rerun with its recorded results, and a rerun from a step it recorded", () => {
  const steps = [{ id: "fetch", status: "completed", durationMs: 4, key: "fetch#0", replayed: false }];
  assert.deepEqual(scriptRunOffers({ runId: "run-1", status: "failed", steps, sessions: [] }), { resume: false, rerun: true, rerunFromStep: true, saveAsScript: true });
  assert.deepEqual(scriptRunOffers({ runId: "run-1", status: "completed", steps: [], sessions: [] }), { resume: false, rerun: true, rerunFromStep: false, saveAsScript: true },
    "no step was recorded: no rerun from a step");
  assert.equal(scriptRunOffers({ runId: "", status: "completed", steps, sessions: [] }).rerun, false, "a run without its id offers nothing");
});
