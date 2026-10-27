import assert from "node:assert/strict";
import test from "node:test";
import { scriptRunOf, type ScriptRunRecord } from "../timeline";
import { blockFit, blockPlan, blockStepWidth, narrowestVerticalStep, compactTokens, estimateFlowSize, minimumBlockScale, tabCanvasHeight, widestBlockStep, flowElements, flowMetrics, hasWorkflowBlock, runMermaid, runPhase, runTotals, stepCounts, stepHeight, workflowGraph } from "./runFlow";
import { layoutFlow } from "./runFlowLayout";

const at = (second: number) => new Date(Date.UTC(2026, 9, 27, 10, 0, second)).toISOString();
const record = (kind: string, second: number, fields: Omit<Partial<ScriptRunRecord>, "details"> & { details?: object } = {}): ScriptRunRecord =>
  ({ kind, timestamp: at(second), name: "run-1", activityId: fields.activityId ?? null, phase: fields.phase ?? null, text: fields.text ?? null,
    details: JSON.stringify(fields.details ?? {}) });
const declared = (steps: object[]) => record("StepsDeclared", 0, { details: { steps } });

// The research sample: split, research each sub-question (two sessions), write.
function research(ended = false) {
  const list = [
    record("RunStarted", 0, { text: "Research a question" }),
    declared([{ id: "split", title: "Split the question" }, { id: "research", title: "Research each sub-question", each: true }, { id: "write", title: "Write the answer" }]),
    record("StepStarted", 1, { activityId: "split" }),
    record("StepEnded", 4, { activityId: "split", phase: "completed", details: { durationMs: 3000, stepKey: "split#0" } }),
    record("StepStarted", 4, { activityId: "research" }),
    record("SessionStarted", 5, { activityId: "child-1", text: "Angle A", details: { stepId: "research", providerKey: "codex", modelId: "gpt-6.1-sol" } }),
    record("SessionStarted", 5, { activityId: "child-2", text: "Angle B", details: { stepId: "research" } }),
    record("SessionEnded", 20, { activityId: "child-1", phase: "completed", details: { durationMs: 15_000, inputTokens: 9000, outputTokens: 1000 } }),
  ];
  if (ended) list.push(record("SessionEnded", 25, { activityId: "child-2", phase: "failed", details: { durationMs: 20_000, inputTokens: 500, outputTokens: 0 } }),
    record("StepEnded", 25, { activityId: "research", phase: "failed", details: { durationMs: 21_000, stepKey: "research#0" } }),
    record("RunEnded", 26, { phase: "failed" }));
  return scriptRunOf(list);
}

test("a run with steps or sessions has its block; a script that ran neither keeps its card", () => {
  assert.equal(hasWorkflowBlock(research()), true);
  assert.equal(hasWorkflowBlock(scriptRunOf([record("RunStarted", 0), record("RunEnded", 1, { phase: "completed" })])), false);
  assert.equal(hasWorkflowBlock(scriptRunOf([record("SessionStarted", 1, { activityId: "child-1", details: {} })])), true);
});

test("the nodes of the research run: a node per step, sized by its sessions, an edge per after, animated toward the step that runs", () => {
  const run = research();
  const graph = workflowGraph(run)!;
  const elements = flowElements(graph, new Set(), "block");
  assert.deepEqual(elements.nodes.map(node => `${node.id}:${node.data.node.state}:${node.data.counts.total}`), ["split:completed:0", "research:running:2", "write:pending:0"]);
  assert.deepEqual(elements.edges.map(edge => `${edge.id}:${edge.state}:${edge.animated}`), ["split->research:running:true", "research->write:pending:false"]);
  const metrics = flowMetrics.block;
  assert.equal(elements.nodes[0].height, metrics.head);
  assert.equal(elements.nodes[1].height, metrics.head + 2 * metrics.session + metrics.gap + metrics.padding);
  assert.equal(elements.nodes[1].data.progress, 0.5, "one of its two sessions ended");
  assert.equal(elements.nodes[0].data.progress, null);
  assert.equal(elements.nodes[0].data.node.key, "split#0", "a step that ended can be rerun from");
  // The provider and the model of a session, as its start says them.
  assert.equal(run.sessions[0].providerKey, "codex");
  assert.equal(run.sessions[0].modelId, "gpt-6.1-sol");
  assert.equal(run.sessions[1].providerKey, null);
});

test("dagre lays the steps out left to right, one rank per column, within the size the block keeps for it", () => {
  const graph = workflowGraph(scriptRunOf([
    declared([{ id: "plan", title: "Plan" }, { id: "a", title: "A", after: ["plan"] }, { id: "b", title: "B", after: ["plan"] }, { id: "merge", title: "Merge", after: ["a", "b"] }]),
  ]))!;
  const elements = flowElements(graph, new Set(), "block");
  const layout = layoutFlow(elements, "block");
  const x = (id: string) => layout.positions.get(id)!.x, y = (id: string) => layout.positions.get(id)!.y;
  assert.ok(x("plan") < x("a") && x("a") === x("b") && x("b") < x("merge"), "left to right");
  assert.notEqual(y("a"), y("b"), "the parallel steps are stacked in their column");
  const estimate = estimateFlowSize(graph, elements, "block");
  assert.equal(layout.width, estimate.width);
  assert.equal(layout.height, estimate.height);
});

test("a block fits its graph to its width, never above a scale of 1, never below the smallest readable one: beyond, it scrolls", () => {
  const margin = flowMetrics.block.margin;
  const size = { width: 1000 - 2 * margin, height: 200 - 2 * margin };
  const wide = blockFit(1600, size);
  assert.deepEqual([wide.scale, wide.width, wide.height, wide.x, wide.y], [1, 1600, 200, 300 + margin, margin], "a narrow graph is centered at its size");
  const fitted = blockFit(800, size);
  assert.deepEqual([fitted.scale, fitted.width, fitted.height], [0.8, 800, 160], "a wider graph scales down to the width, its height with it");
  const scrolled = blockFit(500, size);
  assert.equal(scrolled.scale, minimumBlockScale);
  assert.equal(scrolled.width, 720, "below the smallest scale, the canvas is wider than the block, which scrolls");
  assert.equal(blockFit(0, size).scale, 1, "an unmeasured block draws at a scale of 1");
  // The steps share a wide conversation, within bounds.
  const { stepWidth, ranksep } = flowMetrics.block;
  assert.equal(blockStepWidth(1000, 3), Math.floor((1000 - 2 * margin - 2 * ranksep) / 3));
  assert.equal(blockStepWidth(3000, 3), widestBlockStep);
  assert.equal(blockStepWidth(600, 3), stepWidth, "a narrow block keeps the usual width, and scales down");
  assert.equal(blockStepWidth(0, 3), stepWidth);
  const graph = workflowGraph(scriptRunOf([declared([{ id: "a", title: "A" }, { id: "b", title: "B" }])]))!;
  assert.deepEqual(estimateFlowSize(graph, flowElements(graph, new Set(), "block", 300), "block").width, 2 * 300 + ranksep);
  assert.equal(tabCanvasHeight({ height: 100 }), 300);
  assert.equal(tabCanvasHeight({ height: 5000 }), 640);
});

test("a block too narrow for its graph at the smallest readable scale lays it out top to bottom, as wide as it lets the steps", () => {
  const graph = workflowGraph(scriptRunOf([
    declared([{ id: "plan", title: "Plan" }, { id: "a", title: "A", after: ["plan"] }, { id: "b", title: "B", after: ["plan"] }, { id: "merge", title: "Merge", after: ["a", "b"] }]),
  ]))!;
  assert.deepEqual(blockPlan(graph, new Set(), 1200), { direction: "LR", stepWidth: blockStepWidth(1200, 3) });
  assert.equal(blockPlan(graph, new Set(), 0).direction, "LR", "an unmeasured block flows left to right");
  const narrow = blockPlan(graph, new Set(), 560);
  const { margin, nodesep } = flowMetrics.block;
  assert.deepEqual(narrow, { direction: "TB", stepWidth: Math.floor((560 - 2 * margin - nodesep) / 2) }, "two steps side by side share the width");
  assert.equal(blockPlan(graph, new Set(), 300).stepWidth, narrowestVerticalStep);
  const elements = flowElements(graph, new Set(), "block", narrow.stepWidth, narrow.direction);
  assert.equal(elements.direction, "TB");
  assert.equal(elements.nodes[0].data.direction, "TB", "a node knows where its edges come in and go out");
  const layout = layoutFlow(elements, "block");
  const x = (id: string) => layout.positions.get(id)!.x, y = (id: string) => layout.positions.get(id)!.y;
  assert.ok(y("plan") < y("a") && y("a") === y("b") && y("b") < y("merge"), "top to bottom");
  assert.notEqual(x("a"), x("b"), "the parallel steps side by side");
  const estimate = estimateFlowSize(graph, elements, "block");
  assert.deepEqual([layout.width, layout.height], [estimate.width, estimate.height]);
  assert.ok(blockFit(560, layout).scale === 1, "it fits without scaling down nor scrolling");
});

test("a fan-out of 50 sessions is one group: closed it takes one line, open it lists a few rows and scrolls", () => {
  const list = [declared([{ id: "fix", title: "Fix each test", each: true }]), record("StepStarted", 1, { activityId: "fix" })];
  for (let index = 0; index < 50; index++) {
    list.push(record("SessionStarted", 2, { activityId: `child-${index}`, details: { stepId: "fix" } }));
    if (index < 41) list.push(record("SessionEnded", 3, { activityId: `child-${index}`, phase: "completed" }));
    else if (index >= 47) list.push(record("SessionEnded", 3, { activityId: `child-${index}`, phase: "failed" }));
  }
  const graph = workflowGraph(scriptRunOf(list))!;
  const closed = flowElements(graph, new Set(), "block").nodes[0];
  const open = flowElements(graph, new Set(["fix"]), "block").nodes[0];
  const metrics = flowMetrics.block;
  assert.deepEqual(closed.data.counts, { total: 50, done: 41, running: 6, failed: 3 });
  assert.equal(closed.data.fold?.total, 50);
  assert.equal(closed.height, metrics.head + metrics.group + metrics.padding);
  assert.equal(open.height, stepHeight(50, true, true, "block"));
  assert.equal(open.height, metrics.head + metrics.group + metrics.gap + metrics.groupRows * metrics.session + (metrics.groupRows - 1) * metrics.gap + metrics.padding);
  assert.equal(closed.data.progress, 44 / 50);
});

test("a run that declares nothing draws the steps it ran: side by side while they ran together, after them otherwise", () => {
  const graph = workflowGraph(scriptRunOf([
    record("StepStarted", 1, { activityId: "fetch" }), record("StepEnded", 3, { activityId: "fetch", phase: "completed" }),
    record("StepStarted", 4, { activityId: "left" }), record("StepStarted", 5, { activityId: "right" }),
    record("StepEnded", 8, { activityId: "left", phase: "completed" }), record("StepEnded", 9, { activityId: "right", phase: "completed" }),
    record("StepStarted", 10, { activityId: "merge" }),
    record("SessionStarted", 11, { activityId: "child-1", details: { stepId: "merge" } }),
    record("SessionStarted", 11, { activityId: "child-2", details: {} }),
    // A step that runs again is still one box.
    record("StepEnded", 12, { activityId: "merge", phase: "completed" }), record("StepStarted", 13, { activityId: "merge" }),
  ]))!;
  assert.deepEqual(graph.columns.map(column => column.map(node => `${node.id}:${node.state}`)), [["fetch:completed"], ["left:completed", "right:completed"], ["merge:running"]]);
  assert.deepEqual(graph.columns[2][0].after, ["left", "right"]);
  assert.deepEqual(graph.columns[2][0].sessions.map(session => session.sessionId), ["child-1"]);
  assert.deepEqual(graph.loose.map(session => session.sessionId), ["child-2"]);
  assert.equal(workflowGraph(scriptRunOf([record("RunStarted", 0)])), null);
});

test("the phase of a run, its totals, and its graph as Mermaid", () => {
  const running = research();
  assert.equal(runPhase(running), "running");
  assert.deepEqual(runTotals(running, Date.parse(at(30))), { sessions: 2, tokens: 10_000, durationMs: 30_000 }, "a running run counts until now");
  const ended = research(true);
  assert.equal(runPhase(ended), "failed");
  assert.deepEqual(runTotals(ended, Date.parse(at(99))), { sessions: 2, tokens: 10_500, durationMs: 26_000 });
  assert.equal(runPhase(scriptRunOf([record("RunStarted", 0), record("WaitStarted", 1, { phase: "question", text: "Which?" })])), "waiting");
  assert.equal(runPhase(scriptRunOf([record("RunStarted", 0), record("WaitStarted", 1, { phase: "sleep", details: { wakeAt: at(50) } })])), "sleeping");
  assert.equal(runPhase(scriptRunOf([record("RunStarted", 0), record("RunEnded", 1, { phase: "interrupted" })])), "interrupted");
  assert.deepEqual(stepCounts([]), { total: 0, done: 0, running: 0, failed: 0 });
  assert.deepEqual([950, 12_345, 1_234_567, 4_200].map(compactTokens), ["950", "12k", "1.2M", "4.2k"]);

  assert.equal(runMermaid(workflowGraph(ended)!), [
    "flowchart LR",
    "  s0[\"Split the question · completed\"]:::completed",
    "  s1[\"Research each sub-question × · failed · 2 sessions\"]:::failed",
    "  s2[\"Write the answer · pending\"]:::pending",
    "  s0 --> s1",
    "  s1 --> s2",
    "  classDef pending fill:#f1f3f5,stroke:#adb5bd,color:#495057,stroke-dasharray:4 3",
    "  classDef completed fill:#ebfbee,stroke:#2f9e44,color:#2b8a3e",
    "  classDef failed fill:#fff5f5,stroke:#e03131,color:#c92a2a",
    "",
  ].join("\n"));
});
