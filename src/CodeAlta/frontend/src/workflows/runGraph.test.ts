import assert from "node:assert/strict";
import test from "node:test";
import { persistFileTabs, restoreFileTabs, sameFileTab, workflowRunTab, emptyFileTabs, openFileTab, fileTabKey } from "../fileTabs";
import { scriptRunOf, type ScriptRunRecord, type ScriptSession } from "../timeline";
import { foldLimit, foldSessions, graphState, runGraph, timelineBars, timelineOption, timelineStates, type GraphState } from "./runGraph";

const at = (second: number) => new Date(Date.UTC(2026, 9, 27, 10, 0, second)).toISOString();
const record = (kind: string, second: number, fields: Omit<Partial<ScriptRunRecord>, "details"> & { details?: object } = {}): ScriptRunRecord =>
  ({ kind, timestamp: at(second), name: "run-1", activityId: fields.activityId ?? null, phase: fields.phase ?? null, text: fields.text ?? null,
    details: JSON.stringify(fields.details ?? {}) });
const declared = (steps: object[]) => record("StepsDeclared", 0, { details: { steps } });
const session = (id: string, status: string, stepId = "fan"): ScriptSession =>
  ({ sessionId: id, title: id, stepId, status, durationMs: null, inputTokens: null, outputTokens: null, message: null });

test("the graph of a run is drawn from the start: the steps it declares, by their after, the ones not reached pending", () => {
  const run = scriptRunOf([
    record("RunStarted", 0),
    declared([{ id: "plan", title: "Split" }, { id: "explore", title: "Research", after: ["plan"], each: true }, { id: "check", title: "Check", after: ["plan"] },
      { id: "write", title: "Write", after: ["explore", "check"] }]),
    record("StepStarted", 1, { activityId: "plan" }),
    record("StepEnded", 3, { activityId: "plan", phase: "completed", details: { durationMs: 2000, stepKey: "plan#0" } }),
    record("StepStarted", 3, { activityId: "explore" }),
    record("SessionStarted", 4, { activityId: "child-1", text: "Research A", details: { stepId: "explore" } }),
    record("SessionStarted", 4, { activityId: "child-2", text: "Research B", details: { stepId: "explore" } }),
    record("SessionEnded", 9, { activityId: "child-1", phase: "completed", details: { durationMs: 5000, inputTokens: 10, outputTokens: 5 } }),
  ]);
  const graph = runGraph(run)!;

  assert.deepEqual(graph.columns.map(column => column.map(node => `${node.id}:${node.state}:${node.sessions.length}`)),
    [["plan:completed:0"], ["explore:running:2", "check:pending:0"], ["write:pending:0"]]);
  assert.deepEqual(graph.columns[2][0].after, ["explore", "check"]);
  assert.equal(graph.columns[1][0].each, true);
  assert.equal(graph.columns[0][0].durationMs, 2000);
  assert.deepEqual(graph.others, []);
  assert.deepEqual(graph.loose, []);
  // A run whose scripts declare nothing keeps its line of marks.
  assert.equal(runGraph(scriptRunOf([record("StepStarted", 1, { activityId: "a" })])), null);
});

test("a step that ran without being declared, and a session of no step, are drawn beside the graph", () => {
  const graph = runGraph(scriptRunOf([
    declared([{ id: "a", title: "A" }]),
    record("StepStarted", 1, { activityId: "a" }), record("StepEnded", 2, { activityId: "a", phase: "failed" }),
    record("StepStarted", 2, { activityId: "extra" }),
    record("SessionStarted", 3, { activityId: "child-9", details: {} }),
  ]))!;
  assert.equal(graph.columns[0][0].state, "failed");
  assert.deepEqual(graph.others.map(step => `${step.id}:${step.status}`), ["extra:running"]);
  assert.deepEqual(graph.loose.map(item => item.sessionId), ["child-9"]);
});

test("more than six sessions of a step fold into one group: its counts, the running and failed ones first", () => {
  const states = [...Array(41).fill("completed"), ...Array(6).fill("running"), ...Array(3).fill("failed")];
  // Mixed, as they end: the order of the list puts what needs a look first, the others in their order.
  const sessions = states.map((status, index) => session(`s${index}`, status)).sort((a, b) => Number(a.sessionId.slice(1)) % 7 - Number(b.sessionId.slice(1)) % 7);
  const fold = foldSessions(sessions)!;

  assert.deepEqual({ total: fold.total, done: fold.done, running: fold.running, failed: fold.failed }, { total: 50, done: 41, running: 6, failed: 3 });
  assert.equal(fold.ordered.length, 50);
  assert.deepEqual(fold.ordered.slice(0, 6).map(item => item.status), Array(6).fill("running"));
  assert.deepEqual(fold.ordered.slice(6, 9).map(item => item.status), Array(3).fill("failed"));
  assert.ok(fold.ordered.slice(9).every(item => item.status === "completed"));
  assert.equal(foldSessions(sessions.slice(0, foldLimit)), null, "six are shown one by one");
  assert.notEqual(foldSessions(sessions.slice(0, foldLimit + 1)), null);
});

test("the states of the boxes", () => {
  assert.deepEqual(["running", "completed", "failed", "aborted", "interrupted", "waiting", null, "refused"].map(graphState),
    ["running", "completed", "failed", "cancelled", "interrupted", "waiting", "pending", "failed"] satisfies GraphState[]);
});

test("the timeline of a run: each step, its sessions under it, a running bar until now, in seconds", () => {
  const run = scriptRunOf([
    record("RunStarted", 0),
    record("StepStarted", 1, { activityId: "plan" }), record("StepEnded", 3, { activityId: "plan", phase: "completed" }),
    record("StepStarted", 3, { activityId: "explore" }),
    record("SessionStarted", 4, { activityId: "child-1", text: "Research A", details: { stepId: "explore" } }),
    record("SessionStarted", 5, { activityId: "child-9", text: "Loose" }),
    record("SessionStarted", 5, { activityId: "child-2", text: "Research B", details: { stepId: "explore" } }),
    record("SessionEnded", 8, { activityId: "child-2", phase: "failed", details: { inputTokens: 900, outputTokens: 100 } }),
  ]);
  const now = Date.parse(at(10));
  const bars = timelineBars(run, now);

  assert.deepEqual(bars.map(bar => [bar.label, bar.kind, bar.state, bar.start, bar.end, bar.stepId, bar.tokens]), [
    ["plan", "step", "completed", 1000, 3000, "plan", null],
    ["explore", "step", "running", 3000, 10_000, "explore", null],
    ["Research A", "session", "running", 4000, 10_000, "explore", null],
    ["Research B", "session", "failed", 5000, 8000, "explore", 1000],
    ["Loose", "session", "running", 5000, 10_000, null, null],
  ], "the sessions of a step are under it, the ones of no step at the end");
  assert.deepEqual(timelineBars(scriptRunOf([]), now), []);
});

test("the option of the timeline: the colors of the graph by state, sessions marked and lighter, an escaped tooltip per bar", () => {
  const bar = (label: string, kind: "step" | "session", state: GraphState, start: number, end: number, stepId: string | null) =>
    ({ label, kind, state, start, end, sessionId: kind === "session" ? label : null, stepId, tokens: null });
  const bars = [bar("explore", "step", "completed", 1000, 3000, "explore"), bar("<b>A</b> {b}", "session", "failed", 1000, 2000, "explore"), bar("x", "session", "running", 0, 4000, null)];
  const names = Object.fromEntries(timelineStates.map(state => [state, state.toUpperCase()])) as Record<GraphState, string>;
  const option = timelineOption(bars, names, "Seconds", { completed: "rgb(0, 128, 0)", failed: "rgb(200, 0, 0)" }, item => `${item.label} · ${names[item.state]}`) as {
    yAxis: { data: string[] }; series: { name: string; itemStyle?: { color: string }; data: unknown[] }[] };
  assert.deepEqual(option.yAxis.data, ["explore", "↳ <b>A</b> {b}", "x"], "a session of a step is marked under it");
  assert.deepEqual(option.series.map(series => series.name), ["start", "COMPLETED", "RUNNING", "FAILED"]);
  assert.deepEqual(option.series.map(series => series.itemStyle?.color), ["transparent", "rgb(0, 128, 0)", undefined, "rgb(200, 0, 0)"]);
  assert.deepEqual(option.series[0].data, [1, 1, 0], "the invisible series carries each bar to its start");
  assert.deepEqual(option.series[1].data, [{ value: 2, tooltip: { formatter: "explore · COMPLETED" } }, "-", "-"]);
  assert.deepEqual(option.series[3].data[1], { value: 1, itemStyle: { opacity: 0.55 },
    tooltip: { formatter: "&#60;b&#62;A&#60;/b&#62; &#123;b&#125; · FAILED" } }, "a session is lighter, and its tooltip says its text, not markup nor fields");
  // The option is plain data: what a chart from JSON may hold.
  assert.equal(JSON.stringify(JSON.parse(JSON.stringify(option))), JSON.stringify(option));
});

test("the tab of a run is stored with the others, and restored while its id reads", () => {
  const tab = workflowRunTab({ runId: "run-7f3c2a", title: "Research a question" });
  const state = openFileTab(emptyFileTabs(), tab);
  let stored = "";
  assert.equal(persistFileTabs(value => { stored = value; }, state), true);
  const restored = restoreFileTabs(() => stored)!;
  assert.ok(sameFileTab(restored.active, tab));
  assert.equal(restored.open[0].name, "Research a question");
  assert.notEqual(fileTabKey(tab), fileTabKey(workflowRunTab({ runId: "run-000000" })), "a tab for each run");
  assert.deepEqual(restoreFileTabs(() => stored.replace("run-7f3c2a", "../etc"))?.open ?? [], [], "an id that does not read is not restored");
});
