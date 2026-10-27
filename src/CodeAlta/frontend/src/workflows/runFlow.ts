// What the workflow block of the timeline and the tab of a run draw with React Flow, as plain data: the graph of a run (the
// steps it declares, else the steps it ran), a node per step with the size it takes, an edge per `after`, the phase of the
// run, its totals and its graph as Mermaid. Pure: no React, no DOM, no layout engine (runFlowLayout.ts places the nodes).
import type { ScriptRun, ScriptSession, ScriptStep } from "../timeline";
import { foldSessions, graphState, runGraph, type GraphNode, type GraphState, type RunGraph, type SessionFold } from "./runGraph";

/** Whether a run draws its block in the timeline: it ran steps or started sessions, or its scripts declare steps. Another keeps its compact card. */
export function hasWorkflowBlock(run: ScriptRun): boolean {
  return run.steps.length > 0 || run.sessions.length > 0 || (run.declared?.length ?? 0) > 0;
}

/**
 * The graph of a run: the steps its scripts declare ({@link runGraph}); else the steps it ran, in the order they started, a
 * step beside the ones it ran together with (it started before one of them ended), after them otherwise. Null for a run
 * that ran no step and started no session.
 */
export function workflowGraph(run: ScriptRun): RunGraph | null {
  return runGraph(run) ?? ranGraph(run);
}

function ranGraph(run: ScriptRun): RunGraph | null {
  if (run.steps.length === 0 && run.sessions.length === 0) return null;
  // A step that ran more than once (in a loop) is one box: where it first started, how it last went.
  const seen = new Map<string, { first: ScriptStep; last: ScriptStep; order: number }>();
  run.steps.forEach((step, order) => {
    const known = seen.get(step.id);
    if (known) known.last = step; else seen.set(step.id, { first: step, last: step, order });
  });
  const columns: GraphNode[][] = [];
  let ends: number[] = [];
  let column = -1, previous: string[] = [];
  for (const { first, last } of seen.values()) {
    const start = time(first.startedAt) ?? time(first.endedAt);
    const together = column >= 0 && start !== null && ends.length > 0 && start < Math.min(...ends);
    if (!together) {
      previous = column >= 0 ? columns[column].map(node => node.id) : [];
      column++;
      columns.push([]);
      ends = [];
    }
    const state = graphState(last.status);
    ends.push(state === "running" || state === "waiting" ? Number.POSITIVE_INFINITY : time(last.endedAt) ?? start ?? Number.POSITIVE_INFINITY);
    columns[column].push({ id: first.id, title: first.id, each: false, column, after: previous, state, durationMs: last.durationMs,
      key: last.key, replayed: last.replayed, sessions: run.sessions.filter(session => session.stepId === first.id) });
  }
  return { columns, others: [], loose: run.sessions.filter(session => !session.stepId || !seen.has(session.stepId)) };
}

/** How the sessions of a step are: how many, and how many ended well, still run or failed. */
export type StepCounts = Readonly<{ total: number; done: number; running: number; failed: number }>;

export function stepCounts(sessions: readonly ScriptSession[]): StepCounts {
  let done = 0, running = 0, failed = 0;
  for (const session of sessions) {
    const state = graphState(session.status);
    if (state === "completed") done++;
    else if (state === "running" || state === "waiting") running++;
    else if (state !== "pending") failed++;
  }
  return { total: sessions.length, done, running, failed };
}

/** Where a graph is drawn: in the timeline (`block`), or larger in the tab of its run (`tab`). */
export type FlowMode = "block" | "tab";

/** Which way a graph flows: left to right, or top to bottom in a block too narrow for it. */
export type FlowDirection = "LR" | "TB";

/**
 * The sizes of the boxes, in pixels at a zoom of 1: the components draw their parts at these heights, so the layout knows
 * the size of a node before it is in the page.
 */
export const flowMetrics = Object.freeze({
  block: Object.freeze({ stepWidth: 248, head: 46, padding: 8, gap: 4, session: 38, group: 44, groupRows: 6, nodesep: 18, ranksep: 64, verticalRanksep: 36, margin: 12 }),
  tab: Object.freeze({ stepWidth: 272, head: 50, padding: 10, gap: 5, session: 40, group: 46, groupRows: 10, nodesep: 24, ranksep: 80, verticalRanksep: 48, margin: 24 }),
} satisfies Record<FlowMode, Readonly<Record<string, number>>>);

/** A step as its node draws it: the step, how its sessions are, and the sessions it lists. */
export type StepFlowData = Readonly<{
  node: GraphNode;
  counts: StepCounts;
  /** The share of its sessions that ended, while it runs and has some; null otherwise. */
  progress: number | null;
  /** Its sessions folded into one group (more than six), null when it lists them one by one. */
  fold: SessionFold | null;
  /** Whether its folded group is open. */
  expanded: boolean;
  mode: FlowMode;
  direction: FlowDirection;
}>;

export type FlowNodeSpec = Readonly<{ id: string; width: number; height: number; data: StepFlowData }>;
export type FlowEdgeSpec = Readonly<{ id: string; source: string; target: string; state: GraphState; animated: boolean }>;
export type FlowElements = Readonly<{ nodes: readonly FlowNodeSpec[]; edges: readonly FlowEdgeSpec[]; direction: FlowDirection }>;

/** The height of the node of a step: its head, then a row per session, or its group (and the rows it shows once open). */
export function stepHeight(sessions: number, fold: boolean, expanded: boolean, mode: FlowMode): number {
  const metrics = flowMetrics[mode];
  if (sessions === 0) return metrics.head;
  if (!fold) return metrics.head + sessions * metrics.session + (sessions - 1) * metrics.gap + metrics.padding;
  const rows = expanded ? Math.min(sessions, metrics.groupRows) : 0;
  return metrics.head + metrics.group + (rows > 0 ? metrics.gap + rows * metrics.session + (rows - 1) * metrics.gap : 0) + metrics.padding;
}

/**
 * The nodes and the edges of a graph: a node per step, sized by what it shows (`expanded`: the steps whose group is open),
 * an edge per `after`, of the state of the step it leads to, animated while that step runs.
 */
export function flowElements(graph: RunGraph, expanded: ReadonlySet<string>, mode: FlowMode, stepWidth: number = flowMetrics[mode].stepWidth,
  direction: FlowDirection = "LR"): FlowElements {
  const nodes: FlowNodeSpec[] = [];
  const edges: FlowEdgeSpec[] = [];
  for (const column of graph.columns) for (const node of column) {
    const counts = stepCounts(node.sessions);
    const fold = foldSessions(node.sessions);
    const open = !!fold && expanded.has(node.id);
    const running = node.state === "running" || node.state === "waiting";
    nodes.push({ id: node.id, width: stepWidth, height: stepHeight(node.sessions.length, !!fold, open, mode),
      data: { node, counts, progress: running && counts.total > 0 ? (counts.done + counts.failed) / counts.total : null, fold, expanded: open, mode, direction } });
    for (const source of node.after) edges.push({ id: `${source}->${node.id}`, source, target: node.id, state: node.state, animated: running });
  }
  return { nodes, edges, direction };
}

/**
 * The size of a graph laid out in columns, before the layout engine is there: the columns one after the other (left to right,
 * or top to bottom), the steps of a column side by side across. The layout places the same nodes in the same ranks, so the
 * place a block keeps for its graph is the one it then takes.
 */
export function estimateFlowSize(graph: RunGraph, elements: FlowElements, mode: FlowMode): Readonly<{ width: number; height: number }> {
  const metrics = flowMetrics[mode];
  const nodes = new Map(elements.nodes.map(node => [node.id, node]));
  const vertical = elements.direction === "TB";
  // Along the flow, the largest node of each column; across it, the steps of a column with the space between them.
  let along = 0, across = 0;
  for (const column of graph.columns) {
    const sizes = column.map(node => nodes.get(node.id)).map(node => vertical ? { along: node?.height ?? 0, across: node?.width ?? 0 } : { along: node?.width ?? 0, across: node?.height ?? 0 });
    along += sizes.reduce((most, size) => Math.max(most, size.along), 0);
    across = Math.max(across, sizes.reduce((sum, size) => sum + size.across, 0) + Math.max(0, column.length - 1) * metrics.nodesep);
  }
  along += Math.max(0, graph.columns.length - 1) * (vertical ? metrics.verticalRanksep : metrics.ranksep);
  return vertical ? { width: across, height: along } : { width: along, height: across };
}

/** The widest a step of a block grows to fill the width of the conversation. */
export const widestBlockStep = 360;

/**
 * The width of the steps of a block: they share the width of the conversation, from their usual width up to
 * {@link widestBlockStep}. A graph too wide at the usual width keeps it, and scales down.
 */
export function blockStepWidth(available: number, columns: number): number {
  const metrics = flowMetrics.block;
  if (available <= 0 || columns <= 0) return metrics.stepWidth;
  const share = Math.floor((available - 2 * metrics.margin - (columns - 1) * metrics.ranksep) / columns);
  return Math.min(widestBlockStep, Math.max(metrics.stepWidth, share));
}

/** The smallest scale a block draws its graph at: narrower than that, the graph keeps it and scrolls sideways. */
export const minimumBlockScale = 0.72;

/** How a block lays its graph out in a width: the way it flows, and the width of its steps. */
export type BlockPlan = Readonly<{ direction: FlowDirection; stepWidth: number }>;

/**
 * Left to right while the graph fits the width at the smallest readable scale; top to bottom otherwise, the steps of a
 * column side by side and as wide as the width lets them, so the block never hides a step behind a sideways scroll.
 */
export function blockPlan(graph: RunGraph, expanded: ReadonlySet<string>, available: number): BlockPlan {
  const metrics = flowMetrics.block;
  const stepWidth = blockStepWidth(available, graph.columns.length);
  if (available <= 0) return { direction: "LR", stepWidth };
  const wide = estimateFlowSize(graph, flowElements(graph, expanded, "block", stepWidth), "block");
  if ((wide.width + 2 * metrics.margin) * minimumBlockScale <= available) return { direction: "LR", stepWidth };
  const across = graph.columns.reduce((most, column) => Math.max(most, column.length), 1);
  const share = Math.floor((available - 2 * metrics.margin - (across - 1) * metrics.nodesep) / across);
  return { direction: "TB", stepWidth: Math.min(widestBlockStep, Math.max(narrowestVerticalStep, share)) };
}

/** The narrowest a step gets in a graph that flows top to bottom: below, the graph scales down. */
export const narrowestVerticalStep = 200;

/** How a block draws a graph of a size in a width: its scale, the size of its canvas, and where the graph starts (centered). */
export type BlockFit = Readonly<{ scale: number; width: number; height: number; x: number; y: number }>;

export function blockFit(available: number, size: Readonly<{ width: number; height: number }>): BlockFit {
  const margin = flowMetrics.block.margin;
  const contentWidth = size.width + 2 * margin, contentHeight = size.height + 2 * margin;
  const scale = available > 0 ? Math.min(1, Math.max(minimumBlockScale, available / contentWidth)) : 1;
  const width = Math.max(Math.floor(available), Math.ceil(contentWidth * scale));
  return { scale, width, height: Math.ceil(contentHeight * scale), x: Math.max(0, (width - contentWidth * scale) / 2) + margin * scale, y: margin * scale };
}

/** The height of the canvas of the tab of a run: the graph at a zoom of 1, within bounds; it pans and zooms beyond. */
export function tabCanvasHeight(size: Readonly<{ height: number }>): number {
  return Math.round(Math.min(640, Math.max(300, size.height + 2 * flowMetrics.tab.margin + 48)));
}

/** Where a run is, as the pill of its block says it. */
export type RunPhase = "running" | "waiting" | "sleeping" | "completed" | "failed" | "interrupted" | "cancelled";

export function runPhase(run: ScriptRun): RunPhase {
  if (run.status === null || run.status === "running") return run.waiting ? run.waiting.kind === "sleep" ? "sleeping" : "waiting" : "running";
  const state = graphState(run.status);
  return state === "completed" || state === "failed" || state === "interrupted" || state === "cancelled" ? state : "failed";
}

/** What a run spent: the sessions it started, their tokens, and how long it ran (until `now` while it runs); null when unknown. */
export type RunTotals = Readonly<{ sessions: number; tokens: number; durationMs: number | null }>;

export function runTotals(run: ScriptRun, now: number): RunTotals {
  const tokens = run.sessions.reduce((sum, session) => sum + (session.inputTokens ?? 0) + (session.outputTokens ?? 0), 0);
  const start = time(run.startedAt);
  const end = run.status === "running" || run.status === null ? now : time(run.endedAt);
  return { sessions: run.sessions.length, tokens, durationMs: start !== null && end !== null ? Math.max(0, end - start) : null };
}

/** A count of tokens in few characters: `950`, `12.3k`, `1.2M`. */
export function compactTokens(count: number): string {
  if (count < 1000) return String(count);
  if (count < 1_000_000) return `${(count / 1000).toFixed(count < 10_000 ? 1 : 0)}k`;
  return `${(count / 1_000_000).toFixed(1)}M`;
}

// The look of each state in Mermaid, readable on a light and a dark page: those `alta workflow show --format mermaid` writes.
const mermaidStyles: readonly (readonly [GraphState, string])[] = [
  ["pending", "fill:#f1f3f5,stroke:#adb5bd,color:#495057,stroke-dasharray:4 3"],
  ["running", "fill:#e7f5ff,stroke:#1c7ed6,color:#1864ab"],
  ["waiting", "fill:#fff4e6,stroke:#f08c00,color:#a85d00"],
  ["completed", "fill:#ebfbee,stroke:#2f9e44,color:#2b8a3e"],
  ["failed", "fill:#fff5f5,stroke:#e03131,color:#c92a2a"],
  ["cancelled", "fill:#f8f9fa,stroke:#868e96,color:#495057"],
  ["interrupted", "fill:#fff9db,stroke:#f59f00,color:#a87100"],
];

/**
 * The graph as a Mermaid `flowchart`, as `alta workflow show --format mermaid` writes it: a box per step with its state and its
 * sessions, an arrow per `after`; the steps that ran without being declared follow, one after the other.
 */
export function runMermaid(graph: RunGraph): string {
  type Box = { id: string; title: string; each: boolean; state: GraphState; sessions: number; after: readonly string[] };
  const boxes: Box[] = graph.columns.flat().map(node => ({ id: node.id, title: node.title, each: node.each, state: node.state, sessions: node.sessions.length, after: node.after }));
  let previous = boxes.at(-1)?.id ?? null;
  for (const step of graph.others) {
    if (boxes.some(box => box.id === step.id)) continue;
    boxes.push({ id: step.id, title: step.id, each: false, state: graphState(step.status), sessions: 0, after: previous ? [previous] : [] });
    previous = step.id;
  }
  const ids = new Map(boxes.map((box, index) => [box.id, `s${index}`]));
  const escape = (text: string) => text.replace(/\r\n|\r|\n/gu, " ").replace(/&/gu, "#amp;").replace(/"/gu, "#quot;").replace(/</gu, "#lt;").replace(/>/gu, "#gt;");
  let text = "flowchart LR\n";
  for (const box of boxes) {
    const label = `${box.title}${box.each ? " ×" : ""} · ${box.state}${box.sessions > 0 ? ` · ${box.sessions} ${box.sessions === 1 ? "session" : "sessions"}` : ""}`;
    text += `  ${ids.get(box.id)}["${escape(label)}"]:::${box.state}\n`;
  }
  for (const box of boxes) for (const after of box.after) {
    const from = ids.get(after);
    if (from) text += `  ${from} --> ${ids.get(box.id)}\n`;
  }
  for (const [state, style] of mermaidStyles) if (boxes.some(box => box.state === state)) text += `  classDef ${state} ${style}\n`;
  return text;
}

function time(value: string | null | undefined): number | null {
  if (!value) return null;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}
