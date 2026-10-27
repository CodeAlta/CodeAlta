// The graph of a run of a script, as its card and its tab draw it: the steps its scripts declare (`meta.steps`), laid out
// by their `after` from the start of the run, each with the state it reached and the sessions it started; a step that
// holds many sessions folds them into one group; and the bars of the timeline of the run. Pure: no React, no DOM.
import type { ScriptDeclaredStep, ScriptRun, ScriptSession, ScriptStep } from "../timeline";

/** The state of a box: what the run did with it so far. */
export type GraphState = "pending" | "running" | "waiting" | "completed" | "failed" | "cancelled" | "interrupted";

/** A step of the graph: where it is drawn, how far it went, and the sessions it started. */
export type GraphNode = Readonly<{
  id: string; title: string; each: boolean; state: GraphState;
  /** The column of the graph, from the start: a step comes after every step it runs after. */
  column: number;
  /** The steps it runs after, resolved: empty at the start. */
  after: readonly string[];
  /** The last time it ran, when it did. */
  durationMs: number | null;
  /** What a rerun from it names (`merge#1`), once it ended; and whether it gave a recorded result instead of running. */
  key: string | null;
  replayed: boolean;
  sessions: readonly ScriptSession[];
}>;

export type RunGraph = Readonly<{
  /** The columns, left to right; the steps of a column run side by side. */
  columns: readonly (readonly GraphNode[])[];
  /** The steps that ran without being declared, under **Other steps**. */
  others: readonly ScriptStep[];
  /** The sessions started outside any step of the graph. */
  loose: readonly ScriptSession[];
}>;

/** How many sessions a step shows one by one; above, they fold into one group. */
export const foldLimit = 6;

/** The state of a step or a session as the boxes draw it. */
export function graphState(status: string | null | undefined): GraphState {
  switch (status) {
    case "running": case "waiting": case "completed": case "failed": case "cancelled": case "interrupted": return status;
    case "aborted": case "stopped": return "cancelled";
    case undefined: case null: case "": case "pending": return "pending";
    default: return "failed";
  }
}

/**
 * Lays out the steps a run declares: a step goes one column after the last of the steps it runs after (no `after`: the
 * step declared before it; an empty list: the start), so steps that share their predecessors sit side by side. Null for
 * a run whose scripts declare no step: its card keeps its line of marks.
 */
export function runGraph(run: ScriptRun): RunGraph | null {
  const declared = run.declared ?? [];
  if (declared.length === 0) return null;
  const column = new Map<string, number>();
  const nodes: GraphNode[] = [];
  declared.forEach((step: ScriptDeclaredStep, index) => {
    const after = step.after ?? (index > 0 ? [declared[index - 1].id] : []);
    const at = after.reduce((most, id) => Math.max(most, (column.get(id) ?? -1) + 1), 0);
    column.set(step.id, at);
    const ran = lastOf(run.steps, candidate => candidate.id === step.id);
    nodes.push({ id: step.id, title: step.title, each: step.each, column: at, after: after.filter(id => column.has(id)),
      state: ran ? graphState(ran.status) : "pending", durationMs: ran?.durationMs ?? null, key: ran?.key ?? null, replayed: ran?.replayed ?? false,
      sessions: run.sessions.filter(session => session.stepId === step.id) });
  });
  const columns: GraphNode[][] = [];
  for (const node of nodes) (columns[node.column] ??= []).push(node);
  const ids = new Set(declared.map(step => step.id));
  const others: ScriptStep[] = [];
  for (const step of run.steps) {
    if (ids.has(step.id)) continue;
    const index = others.findIndex(known => known.id === step.id);
    if (index >= 0) others[index] = step; else others.push(step);
  }
  return { columns: columns.filter(Boolean), others,
    loose: run.sessions.filter(session => !session.stepId || !ids.has(session.stepId)) };
}

/** What a group of folded sessions says, and the sessions it lists when it is opened: the running and failed ones first. */
export type SessionFold = Readonly<{ total: number; done: number; running: number; failed: number; ordered: readonly ScriptSession[] }>;

/** Folds the sessions of a step when there are more than {@link foldLimit}; null when they are shown one by one. */
export function foldSessions(sessions: readonly ScriptSession[], limit = foldLimit): SessionFold | null {
  if (sessions.length <= limit) return null;
  let done = 0, running = 0, failed = 0;
  for (const session of sessions) {
    const state = graphState(session.status);
    if (state === "completed") done++;
    else if (state === "running" || state === "waiting") running++;
    else if (state === "failed" || state === "cancelled" || state === "interrupted") failed++;
  }
  const rank = (session: ScriptSession) => { const state = graphState(session.status); return state === "running" || state === "waiting" ? 0 : state === "completed" ? 2 : 1; };
  const ordered = sessions.map((session, index) => ({ session, index })).sort((a, b) => rank(a.session) - rank(b.session) || a.index - b.index).map(item => item.session);
  return { total: sessions.length, done, running, failed, ordered };
}

/**
 * A bar of the timeline of a run: a step or a session, from its start to its end (or now, while it runs), in ms since the run
 * started; a session says the step it belongs to, under which it is drawn, and its tokens once it ended.
 */
export type TimelineBar = Readonly<{ label: string; kind: "step" | "session"; state: GraphState; start: number; end: number; sessionId: string | null;
  stepId: string | null; tokens: number | null }>;

/**
 * The bars of the timeline of a run, in groups: each step, then the sessions it started under it (under the first time it
 * ran); the sessions of no step that ran come last.
 */
export function timelineBars(run: ScriptRun, now: number): readonly TimelineBar[] {
  const origin = time(run.startedAt) ?? Math.min(...[...run.steps.map(step => time(step.startedAt)), ...run.sessions.map(session => time(session.startedAt))]
    .filter((value): value is number => value !== null));
  if (!Number.isFinite(origin)) return [];
  const finish = time(run.endedAt) ?? now;
  const bar = (label: string, kind: TimelineBar["kind"], state: GraphState, startedAt: string | null | undefined, endedAt: string | null | undefined,
    sessionId: string | null, stepId: string | null, tokens: number | null): TimelineBar | null => {
    const start = time(startedAt);
    if (start === null) return null;
    const end = time(endedAt) ?? (state === "running" || state === "waiting" ? finish : start);
    return { label, kind, state, start: Math.max(0, start - origin), end: Math.max(0, end - origin), sessionId, stepId, tokens };
  };
  const sessions = run.sessions.map(session => bar(session.title ?? session.sessionId.slice(0, 8), "session", graphState(session.status), session.startedAt, session.endedAt,
    session.sessionId, session.stepId, session.inputTokens !== null || session.outputTokens !== null ? (session.inputTokens ?? 0) + (session.outputTokens ?? 0) : null));
  const placed = new Set<number>();
  const bars: TimelineBar[] = [];
  for (const step of run.steps) {
    const shown = bar(step.id, "step", graphState(step.status), step.startedAt, step.endedAt, null, step.id, null);
    if (!shown) continue;
    bars.push(shown);
    sessions.forEach((session, index) => {
      if (session && !placed.has(index) && session.stepId === step.id) { placed.add(index); bars.push(session); }
    });
  }
  sessions.forEach((session, index) => { if (session && !placed.has(index)) bars.push(session); });
  return bars;
}

function time(value: string | null | undefined): number | null {
  if (!value) return null;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function lastOf<T>(items: readonly T[], match: (item: T) => boolean): T | undefined {
  for (let index = items.length - 1; index >= 0; index--) if (match(items[index])) return items[index];
  return undefined;
}

/** The order the states of the timeline take their series, and so their colors. */
export const timelineStates: readonly GraphState[] = ["completed", "running", "waiting", "failed", "cancelled", "interrupted", "pending"];

/**
 * The ECharts option of the timeline of a run, as plain data: a row per step, its sessions under it (marked, in a lighter
 * tone), time in seconds since the run started on the x axis; each bar from its start to its end, in the color of its state
 * (a series per state that is there, `colors` the plain colors of the graph's palette). An invisible series stacked below
 * carries each bar to its start. `describe` gives the tooltip of a bar, as text.
 */
export function timelineOption(bars: readonly TimelineBar[], stateNames: Readonly<Record<GraphState, string>>, axisName: string,
  colors: Readonly<Partial<Record<GraphState, string>>> = {}, describe?: (bar: TimelineBar) => string): Readonly<Record<string, unknown>> {
  const seconds = (ms: number) => Math.round(ms / 100) / 10;
  const states = timelineStates.filter(state => bars.some(bar => bar.state === state));
  return {
    grid: { left: 8, right: 16, top: 28, bottom: 28, containLabel: true },
    xAxis: { type: "value", name: axisName, nameLocation: "middle", nameGap: 22, min: 0 },
    yAxis: { type: "category", inverse: true, data: bars.map(bar => bar.kind === "session" && bar.stepId ? `↳ ${bar.label}` : bar.label), axisTick: { show: false } },
    tooltip: { trigger: "item" },
    series: [
      { name: "start", type: "bar", stack: "run", silent: true, itemStyle: { color: "transparent" }, tooltip: { show: false }, data: bars.map(bar => seconds(bar.start)) },
      ...states.map(state => ({
        name: stateNames[state], type: "bar", stack: "run", barMaxWidth: 14, ...colors[state] ? { itemStyle: { color: colors[state] } } : {},
        data: bars.map(bar => bar.state !== state ? "-" : {
          value: Math.max(0.1, seconds(bar.end - bar.start)),
          ...bar.kind === "session" ? { itemStyle: { opacity: 0.55 } } : {},
          ...describe ? { tooltip: { formatter: tooltipText(describe(bar)) } } : {},
        }),
      })),
    ],
  };
}

// A tooltip is a template ECharts puts in the page as markup: what it says is escaped, braces included (`{b}` is a field there).
function tooltipText(text: string): string {
  return text.replace(/[&<>"'{}]/gu, char => `&#${char.charCodeAt(0)};`);
}
