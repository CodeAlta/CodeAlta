import assert from "node:assert/strict";
import test from "node:test";
import { reconcileTimeline } from "../reconcileTimeline";
import { buildTimelineItems, execTitle, type HistoryEntry } from "../timeline";
import { toolFamily, toolCallFromItem } from "../toolCall";
import { groupTimelineTools } from "../toolGroups";
import { scriptCallCounts } from "./ScriptCalls";
import { scriptMarkState, scriptRunOffers } from "./scriptRunOffers";

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

test("a run that ended offers a rerun with its recorded results, and a rerun from a step it recorded", () => {
  const steps = [{ id: "fetch", status: "completed", durationMs: 4, key: "fetch#0", replayed: false }];
  assert.deepEqual(scriptRunOffers({ runId: "run-1", status: "failed", steps, sessions: [] }), { resume: false, rerun: true, rerunFromStep: true, saveAsScript: true });
  assert.deepEqual(scriptRunOffers({ runId: "run-1", status: "completed", steps: [], sessions: [] }), { resume: false, rerun: true, rerunFromStep: false, saveAsScript: true },
    "no step was recorded: no rerun from a step");
  assert.equal(scriptRunOffers({ runId: "", status: "completed", steps, sessions: [] }).rerun, false, "a run without its id offers nothing");
});
