import type { SessionRuntimeStateEntry } from "#neoastra";
import type { IconName } from "./AppIcon";
import type { MessageKey } from "./localization";
import { formatThinkingElapsed } from "./thinkingElapsed";

/** How far a background task is: it goes on, or it ended without running to its end. */
export type BackgroundTaskState = "running" | "failed" | "stopped";

/**
 * Something the provider of a session goes on doing after the call that started it returned, and possibly after
 * the run ended: a command in the background, a watch, an agent of its own.
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
}>;

/** How many tasks a session is shown with at most; the host sends no more. */
export const maximumBackgroundTasks = 16;

const none: readonly BackgroundTask[] = Object.freeze([]);
const text = (value: unknown, maximum: number): value is string => typeof value === "string" && value.length > 0 && value.length <= maximum;

/**
 * The background tasks an attachment of a session reports: the ones that go on, then the last that failed or
 * were stopped. What does not have the shape of a task is left out, and an attachment that is ending has none.
 */
export function backgroundTasks(entry: SessionRuntimeStateEntry | null | undefined): readonly BackgroundTask[] {
  if (!entry || entry.isRetiring || entry.isTerminated || !Array.isArray(entry.backgroundTasks) || entry.backgroundTasks.length === 0) return none;
  const tasks: BackgroundTask[] = [];
  const seen = new Set<string>();
  for (const task of entry.backgroundTasks.slice(0, maximumBackgroundTasks)) {
    if (!task || !text(task.taskId, 64) || seen.has(task.taskId) || !text(task.kind, 32)) continue;
    if (task.state !== "running" && task.state !== "failed" && task.state !== "stopped") continue;
    seen.add(task.taskId);
    tasks.push({ id: task.taskId, kind: task.kind, description: text(task.description, 256) ? task.description : null,
      toolCallId: text(task.toolCallId, 256) ? task.toolCallId : null,
      startedAt: text(task.startedAt, 64) && Number.isFinite(Date.parse(task.startedAt)) ? task.startedAt : null, state: task.state });
  }
  return tasks.length ? tasks : none;
}

/** The tasks that go on. */
export function runningBackgroundTasks(tasks: readonly BackgroundTask[]): readonly BackgroundTask[] {
  const running = tasks.filter(task => task.state === "running");
  return running.length === tasks.length ? tasks : running.length ? running : none;
}

/** Whether two readings list the same tasks in the same states: a reading that changes nothing is not shown again. */
export function sameBackgroundTasks(left: readonly BackgroundTask[], right: readonly BackgroundTask[]): boolean {
  return left === right || left.length === right.length && left.every((task, index) => {
    const other = right[index];
    return task.id === other.id && task.state === other.state && task.kind === other.kind && task.description === other.description
      && task.toolCallId === other.toolCallId && task.startedAt === other.startedAt;
  });
}

/**
 * What each tool call of the timeline is shown with beside its own state: its task goes on in the background,
 * or ended there without running to its end. A call that started a task again is shown as going on.
 */
export function backgroundCalls(tasks: readonly BackgroundTask[]): ReadonlyMap<string, BackgroundTaskState> {
  const calls = new Map<string, BackgroundTaskState>();
  for (const task of tasks) if (task.toolCallId && (task.state === "running" || !calls.has(task.toolCallId))) calls.set(task.toolCallId, task.state);
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

/** How long a task has been going on, as the composer says how long a run thinks; null when its start is unknown. */
export function backgroundTaskElapsed(startedAt: string | null, now: number): string | null {
  const started = startedAt ? Date.parse(startedAt) : Number.NaN;
  return Number.isFinite(started) ? formatThinkingElapsed((now - started) / 1000) : null;
}
