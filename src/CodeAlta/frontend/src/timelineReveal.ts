import type { BackgroundTask } from "./backgroundTasks";

/** Brings an element of the timeline into view and draws the eye to it, with the focus when it takes one. */
export function flashTimelineTarget(element: HTMLElement): void {
  element.scrollIntoView({ block: "nearest", behavior: "smooth" });
  const focusable = element.matches("[tabindex], button, a[href]") ? element : element.querySelector<HTMLElement>("button");
  focusable?.focus({ preventScroll: true });
  element.animate?.([{ boxShadow: "0 0 0 2px var(--accent)" }, { boxShadow: "0 0 0 2px transparent" }], { duration: 900, easing: "ease-out" });
}

/** Whether the place of a task in the timeline can be looked for: a run of a script, or a task that names the call that started it. */
export function revealableTask(task: BackgroundTask): boolean {
  return !task.job && (task.kind === "workflow" || task.toolCallId !== null);
}

/**
 * Goes to the place of a background task in the timeline of a session: the block of its run, else the tile of the call
 * that started it, else the activity that holds that call, opened on it. False when none of them is in the timeline (a
 * call older than the history the timeline has read).
 */
export function revealBackgroundTask(workspace: ParentNode, task: BackgroundTask): boolean {
  const run = task.kind === "workflow" ? workspace.querySelector<HTMLElement>(`.workflow-block[data-run-id="${CSS.escape(task.id)}"]`) : null;
  if (run) { flashTimelineTarget(run); return true; }
  if (!task.toolCallId) return false;
  const call = CSS.escape(task.toolCallId);
  const tile = workspace.querySelector<HTMLElement>(`.timeline-message[data-tool-call="${call}"]`);
  if (tile) { flashTimelineTarget(tile); return true; }
  const activity = workspace.querySelector<HTMLElement>(`.timeline-activity[data-tool-calls~="${call}"]`);
  const line = activity?.querySelector<HTMLButtonElement>(":scope > .timeline-activity-line");
  if (!activity || !line) return false;
  if (activity.dataset.expanded === "true") { flashTimelineTarget(line); return true; }
  // A folded activity opens, then the tile of the call is shown once it is drawn.
  line.click();
  requestAnimationFrame(() => flashTimelineTarget(workspace.querySelector<HTMLElement>(`.timeline-message[data-tool-call="${call}"]`) ?? line));
  return true;
}
