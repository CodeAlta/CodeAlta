import type { SessionRuntimeStateEntry } from "#neoastra";
import type { IconName } from "./AppIcon";
import type { MessageKey } from "./localization";
import { formatThinkingElapsed } from "./thinkingElapsed";

/** How far a background task is: it goes on, or it ended. Only a background job is told when it ran to its end. */
export type BackgroundTaskState = "running" | "failed" | "stopped" | "completed";

/** What a tool call is shown with for the task it started: that task goes on, or ended without running to its end. */
export type BackgroundCallState = Exclude<BackgroundTaskState, "completed">;

/**
 * Something that goes on for a session after the call that started it returned, and possibly after the run
 * ended: a task of its provider (a command in the background, a watch, an agent of its own), or a background
 * job, a command CodeAlta runs for the session.
 */
export type BackgroundTask = Readonly<{
  /** The identity it is stopped by. */
  id: string;
  /** `command`, `agent`, `workflow`, or the name its provider gives it. */
  kind: string;
  description: string | null;
  /** The tool call that started it, as the timeline names that call. */
  toolCallId: string | null;
  startedAt: string | null;
  state: BackgroundTaskState;
  /** Whether it is a background job of CodeAlta: what it writes can be followed, while it runs and after. */
  job: boolean;
  /** The exit code of the command of a job that ended by itself. */
  exitCode: number | null;
  /** When a job ended. */
  endedAt: string | null;
}>;

/** The host's sixteen provider tasks and eight background jobs, without dropping jobs from a busy session. */
export const maximumBackgroundTasks = 24;

/** How long a job that ended stays in the list of its session, for its output to be read. */
export const endedJobShownMilliseconds = 10 * 60_000;

const none: readonly BackgroundTask[] = Object.freeze([]);
const text = (value: unknown, maximum: number): value is string => typeof value === "string" && value.length > 0 && value.length <= maximum;
const time = (value: unknown): value is string => text(value, 64) && Number.isFinite(Date.parse(value));

/**
 * The background tasks an attachment of a session reports: the ones that go on, then the last that ended. What
 * does not have the shape of a task is left out, and an attachment that is ending has none.
 */
export function backgroundTasks(entry: SessionRuntimeStateEntry | null | undefined): readonly BackgroundTask[] {
  if (!entry || entry.isRetiring || entry.isTerminated || !Array.isArray(entry.backgroundTasks) || entry.backgroundTasks.length === 0) return none;
  const tasks: BackgroundTask[] = [];
  const seen = new Set<string>();
  for (const task of entry.backgroundTasks.slice(0, maximumBackgroundTasks)) {
    if (!task || !text(task.taskId, 64) || seen.has(task.taskId) || !text(task.kind, 32)) continue;
    const job = task.isJob === true;
    // A task of a provider that ran to its end leaves nothing; a job that succeeded is still listed.
    if (task.state !== "running" && task.state !== "failed" && task.state !== "stopped" && !(job && task.state === "completed")) continue;
    seen.add(task.taskId);
    tasks.push({ id: task.taskId, kind: task.kind, description: text(task.description, 256) ? task.description : null,
      toolCallId: text(task.toolCallId, 256) ? task.toolCallId : null,
      startedAt: time(task.startedAt) ? task.startedAt : null, state: task.state, job,
      exitCode: job && Number.isInteger(task.exitCode) ? task.exitCode! : null, endedAt: job && time(task.endedAt) ? task.endedAt : null });
  }
  return tasks.length ? tasks : none;
}

/** The tasks that go on. */
export function runningBackgroundTasks(tasks: readonly BackgroundTask[]): readonly BackgroundTask[] {
  const running = tasks.filter(task => task.state === "running");
  return running.length === tasks.length ? tasks : running.length ? running : none;
}

/**
 * The tasks the list of a session shows: the ones that go on, then the jobs that ended a moment ago, whose
 * output can still be read.
 */
export function listedBackgroundTasks(tasks: readonly BackgroundTask[], now: number): readonly BackgroundTask[] {
  const listed = tasks.filter(task => task.state === "running"
    || task.job && task.endedAt !== null && now - Date.parse(task.endedAt) < endedJobShownMilliseconds);
  return listed.length === tasks.length ? tasks : listed.length ? listed : none;
}

/** Whether two readings list the same tasks in the same states: a reading that changes nothing is not shown again. */
export function sameBackgroundTasks(left: readonly BackgroundTask[], right: readonly BackgroundTask[]): boolean {
  return left === right || left.length === right.length && left.every((task, index) => {
    const other = right[index];
    return task.id === other.id && task.state === other.state && task.kind === other.kind && task.description === other.description
      && task.toolCallId === other.toolCallId && task.startedAt === other.startedAt && task.job === other.job
      && task.exitCode === other.exitCode && task.endedAt === other.endedAt;
  });
}

/**
 * What each tool call of the timeline is shown with beside its own state: its task goes on in the background,
 * or ended there without running to its end. A call that started a task again is shown as going on.
 */
export function backgroundCalls(tasks: readonly BackgroundTask[]): ReadonlyMap<string, BackgroundCallState> {
  const calls = new Map<string, BackgroundCallState>();
  for (const task of tasks) if (task.toolCallId && task.state !== "completed" && (task.state === "running" || !calls.has(task.toolCallId))) calls.set(task.toolCallId, task.state);
  return calls;
}

/** The name of a kind of task. */
export function backgroundTaskKind(kind: string): MessageKey {
  return kind === "command" ? "Command" : kind === "agent" ? "Agent" : kind === "workflow" ? "Workflow" : "Task";
}

/** The icon of a kind of task. */
export function backgroundTaskIcon(kind: string): IconName {
  return kind === "command" ? "terminal" : kind === "agent" ? "assistant" : "task";
}

/** How a task that ended did so, in a word; null for one that goes on. */
export function backgroundTaskEnd(state: BackgroundTaskState): MessageKey | null {
  return state === "completed" ? "Succeeded" : state === "failed" ? "Failed" : state === "stopped" ? "Stopped" : null;
}

/** How long a task has been going on, as the composer says how long a run thinks; null when its start is unknown. */
export function backgroundTaskElapsed(startedAt: string | null, now: number): string | null {
  const started = startedAt ? Date.parse(startedAt) : Number.NaN;
  return Number.isFinite(started) ? formatThinkingElapsed((now - started) / 1000) : null;
}
