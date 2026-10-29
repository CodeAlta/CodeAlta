// A run of a script of Code Mode (the `exec` tool), read from its records (`workflow.*` events) as the desktop's timeline
// reads them (timeline.ts scriptRunOf): the steps its scripts declare and their states, the sessions it started, what it
// waits for, and how it ended. The phone shows it as one card on the call that runs it, which opens on its steps and
// sessions; it draws no graph.
import type { TimelineEvent } from "./contract";

/** The state of a step, of a session of a run, or of the run itself. */
export type WorkflowState = "pending" | "running" | "waiting" | "completed" | "failed" | "canceled" | "interrupted";

/** A step of a run, as the card lists it: all its instances (a fan-out runs one per item) as one line. */
export type WorkflowStep = Readonly<{ id: string; title: string; state: WorkflowState; durationMs: number | null; count: number }>;

/** A session a run started, which a tap opens. */
export type WorkflowSession = Readonly<{ sessionId: string; title: string | null; stepId: string | null; state: WorkflowState; durationMs: number | null }>;

/** What a running script waits for: the answer of an `ask()`, the approval of one of its calls, or the end of a `sleep()`. */
export type WorkflowWait = Readonly<{ kind: "question" | "approval" | "sleep"; stepId: string | null; text: string | null; until: string | null }>;

/** A run of a script, from its records. `version` changes with each record, so its card is drawn again only then. */
export type WorkflowRun = Readonly<{
  runId: string;
  /** The tool call that runs the script: the card is shown on it. */
  callId: string | null;
  title: string | null;
  state: WorkflowState;
  waiting: WorkflowWait | null;
  steps: readonly WorkflowStep[];
  sessions: readonly WorkflowSession[];
  /** The steps that ended, and all the steps known (declared or run). */
  done: number;
  total: number;
  /** The title of the step that runs or waits now. */
  current: string | null;
  /** What went wrong, for a run that failed. */
  message: string | null;
  startedAt: string | null;
  endedAt: string | null;
  durationMs: number | null;
  /** What the script showed the user (`present()`), as Markdown, in its order. */
  presented: readonly string[];
  /** Whether the host started the run to run a script of the library for the user: its value is then for the user. */
  forUser: boolean;
  version: string;
}>;

type Instance = { id: string; state: WorkflowState; durationMs: number | null; startedAt: number; endedAt: number };

/** Whether an event is a record of a run of a script. */
export const isWorkflowEvent = (event: Pick<TimelineEvent, "kind">): boolean => event.kind.startsWith("workflow.");

const ended = (state: WorkflowState) => state === "completed" || state === "failed" || state === "canceled" || state === "interrupted";

/** The state a record names: `cancelled` and `stopped` are canceled; an unknown end is a failure. */
export function workflowState(status: string | null | undefined, fallback: WorkflowState = "completed"): WorkflowState {
  switch ((status ?? "").toLowerCase()) {
    case "": return fallback;
    case "completed": case "succeeded": case "ok": return "completed";
    case "cancelled": case "canceled": case "stopped": return "canceled";
    case "interrupted": return "interrupted";
    case "running": return "running";
    default: return "failed";
  }
}

/** Reads the runs of the records, in the order of their first record. */
export function foldWorkflows(events: readonly TimelineEvent[]): WorkflowRun[] {
  const records = new Map<string, TimelineEvent[]>();
  for (const event of events) {
    if (!isWorkflowEvent(event) || !event.workflow?.runId) continue;
    let list = records.get(event.workflow.runId);
    if (!list) records.set(event.workflow.runId, list = []);
    list.push(event);
  }
  return [...records.entries()].map(([runId, list]) => workflowRun(runId, list));
}

/** Reads one run from its records, in their order. */
export function workflowRun(runId: string, records: readonly TimelineEvent[]): WorkflowRun {
  let callId: string | null = null, title: string | null = null, declaredTitle: string | null = null, message: string | null = null;
  let state: WorkflowState = "running", waiting: WorkflowWait | null = null, endedAt: string | null = null, durationMs: number | null = null;
  let forUser = false;
  const presented: string[] = [];
  const declared: { id: string; title: string }[] = [];
  const instances: Instance[] = [];
  const sessions: WorkflowSession[] = [];
  const time = (event: TimelineEvent) => { const at = Date.parse(event.at ?? ""); return Number.isFinite(at) ? at : NaN; };
  for (const event of records) {
    const facts = event.workflow!;
    callId ??= facts.callId ?? null;
    const kind = event.kind.slice("workflow.".length);
    switch (kind) {
      case "runStarted": title ??= facts.title?.trim() || event.text?.trim() || null; forUser ||= facts.forUser === true; break;
      case "presented": if (event.text?.trim()) presented.push(event.text); break;
      case "stepsDeclared":
        declaredTitle ??= facts.title?.trim() || null;
        for (const step of facts.steps ?? []) if (step?.id && !declared.some(known => known.id === step.id)) declared.push({ id: step.id, title: step.title || step.id });
        break;
      case "stepStarted":
        if (facts.stepId) instances.push({ id: facts.stepId, state: "running", durationMs: null, startedAt: time(event), endedAt: NaN });
        break;
      case "stepEnded": {
        if (!facts.stepId) break;
        const open = last(instances, instance => instance.id === facts.stepId && instance.state === "running");
        const next = { state: workflowState(facts.status), durationMs: facts.durationMs ?? null, endedAt: time(event) };
        if (open) Object.assign(open, next); else instances.push({ id: facts.stepId, startedAt: NaN, ...next });
        break;
      }
      case "sessionStarted":
        if (facts.childSessionId) sessions.push({ sessionId: facts.childSessionId, title: facts.title ?? event.text ?? null, stepId: facts.stepId ?? null, state: "running", durationMs: null });
        break;
      case "sessionEnded": {
        const index = sessions.indexOf(last(sessions, session => session.sessionId === facts.childSessionId)!);
        if (index >= 0) sessions[index] = { ...sessions[index], state: workflowState(facts.status), durationMs: facts.durationMs ?? null };
        break;
      }
      case "waitStarted": {
        const status = (facts.status ?? "").toLowerCase();
        waiting = { kind: status === "sleep" ? "sleep" : status === "approval" ? "approval" : "question", stepId: facts.stepId ?? null,
          text: status === "sleep" ? null : facts.title ?? event.text ?? null, until: status === "sleep" ? facts.wakeAt ?? null : null };
        break;
      }
      case "waitEnded": waiting = null; break;
      case "runEnded":
        state = workflowState(facts.status);
        endedAt = event.at;
        durationMs = facts.durationMs ?? null;
        message = state === "failed" ? facts.message ?? event.text ?? null : null;
        break;
    }
  }
  if (ended(state)) waiting = null;
  // CodeAlta ended while the run ran: what still ran then stopped with it.
  if (state === "interrupted") for (const instance of instances) if (instance.state === "running") instance.state = "interrupted";
  if (state === "interrupted") for (let index = 0; index < sessions.length; index++) if (sessions[index].state === "running") sessions[index] = { ...sessions[index], state: "interrupted" };
  const ids = [...declared.map(step => step.id), ...instances.map(instance => instance.id).filter(id => !declared.some(step => step.id === id))];
  const steps = [...new Set(ids)].map(id => step(id, declared.find(known => known.id === id)?.title ?? id, instances.filter(instance => instance.id === id), waiting));
  const now = steps.find(item => item.state === "running" || item.state === "waiting");
  return {
    runId, callId, title: title ?? declaredTitle, state: !ended(state) && waiting ? "waiting" : state, waiting, steps, sessions,
    done: steps.filter(item => ended(item.state)).length, total: steps.length, current: now?.title ?? null, message,
    startedAt: records[0]?.at ?? null, endedAt, durationMs, presented, forUser, version: `${records.length}:${records.at(-1)?.seq ?? 0}`,
  };
}

function last<T>(items: readonly T[], match: (item: T) => boolean): T | undefined {
  for (let index = items.length - 1; index >= 0; index--) if (match(items[index])) return items[index];
  return undefined;
}

// One line of the steps: its instances as one, in the state that matters most.
function step(id: string, title: string, instances: readonly Instance[], waiting: WorkflowWait | null): WorkflowStep {
  const states = instances.map(instance => instance.state);
  const running = states.includes("running");
  const state: WorkflowState = instances.length === 0 ? "pending"
    : running && waiting?.stepId === id ? "waiting"
    : running ? "running"
    : states.includes("failed") ? "failed"
    : states.includes("interrupted") ? "interrupted"
    : states.every(item => item === "canceled") ? "canceled"
    : "completed";
  let durationMs: number | null = null;
  if (instances.length === 1) {
    const only = instances[0];
    durationMs = only.durationMs ?? (Number.isFinite(only.startedAt) && Number.isFinite(only.endedAt) ? only.endedAt - only.startedAt : null);
  } else if (instances.length > 1 && !running) {
    // Instances run at once: the step took from the first start to the last end.
    const start = Math.min(...instances.map(instance => instance.startedAt)), end = Math.max(...instances.map(instance => instance.endedAt));
    durationMs = Number.isFinite(start) && Number.isFinite(end) ? end - start : instances.reduce((sum, instance) => sum + (instance.durationMs ?? 0), 0) || null;
  }
  return { id, title, state, durationMs, count: instances.length };
}
