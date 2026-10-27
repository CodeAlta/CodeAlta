import assert from "node:assert/strict";
import test from "node:test";
import type { TimelineEvent, WorkflowFacts } from "./contract";
import { foldWorkflows } from "./workflowRun";

let seq = 0;
const at = (second: number) => new Date(Date.UTC(2026, 9, 27, 10, 0, second)).toISOString();
const record = (kind: string, second: number, facts: Partial<WorkflowFacts>, text: string | null = null): TimelineEvent =>
  ({ seq: ++seq, at: at(second), kind: `workflow.${kind}`, text, activityId: null, workflow: { runId: "run-1", callId: "call-1", ...facts } });

const running = [
  record("runStarted", 0, { title: "Compare caches" }),
  record("stepsDeclared", 0, { steps: [{ id: "research", title: "Research" }, { id: "compare", title: "Compare" }, { id: "report", title: "Report" }] }),
  record("stepStarted", 1, { stepId: "research" }),
  record("stepStarted", 1, { stepId: "research" }),
  record("sessionStarted", 1, { stepId: "research", childSessionId: "s-redis", title: "Redis" }),
  record("stepEnded", 5, { stepId: "research", status: "completed", durationMs: 4000 }),
  record("sessionEnded", 6, { childSessionId: "s-redis", status: "completed", durationMs: 5000 }),
  record("stepEnded", 9, { stepId: "research", status: "completed", durationMs: 8000 }),
  record("stepStarted", 10, { stepId: "compare" }),
  record("waitStarted", 11, { stepId: "compare", status: "question", title: "Which one?" }),
];

test("a running run: its steps as declared, a fan-out as one line, the step that waits, its progress", () => {
  const [run] = foldWorkflows(running);
  assert.equal(run.title, "Compare caches");
  assert.equal(run.callId, "call-1");
  assert.equal(run.state, "waiting");
  assert.deepEqual(run.waiting, { kind: "question", stepId: "compare", text: "Which one?", until: null });
  assert.deepEqual(run.steps.map(step => [step.id, step.state, step.count, step.durationMs]),
    [["research", "completed", 2, 8000], ["compare", "waiting", 1, null], ["report", "pending", 0, null]]);
  assert.deepEqual([run.done, run.total, run.current], [1, 3, "Compare"]);
  assert.deepEqual(run.sessions, [{ sessionId: "s-redis", title: "Redis", stepId: "research", state: "completed", durationMs: 5000 }]);
});

test("a run that ended: its state, its duration, no wait; a version that changes with each record", () => {
  const ended = [...running, record("waitEnded", 12, {}), record("stepEnded", 13, { stepId: "compare", status: "completed", durationMs: 3000 }),
    record("stepStarted", 13, { stepId: "report" }), record("stepEnded", 14, { stepId: "report", status: "failed", durationMs: 500 }),
    record("runEnded", 14, { status: "failed", message: "The report failed.", durationMs: 14000 })];
  const [run] = foldWorkflows(ended);
  assert.deepEqual([run.state, run.waiting, run.done, run.total, run.current, run.message, run.durationMs], ["failed", null, 3, 3, null, "The report failed.", 14000]);
  assert.notEqual(run.version, foldWorkflows(running)[0].version);
});

test("a run CodeAlta ended while it ran: what still ran was interrupted; runs are kept apart", () => {
  const other = { ...record("runStarted", 0, { title: "Other" }), workflow: { runId: "run-2", callId: "call-9", title: "Other" } };
  const [run, second] = foldWorkflows([...running.slice(0, 5), other, record("runEnded", 20, { status: "interrupted" })]);
  assert.deepEqual([run.state, run.steps[0].state, run.sessions[0].state], ["interrupted", "interrupted", "interrupted"]);
  assert.deepEqual([second.runId, second.title, second.callId, second.state], ["run-2", "Other", "call-9", "running"]);
});
