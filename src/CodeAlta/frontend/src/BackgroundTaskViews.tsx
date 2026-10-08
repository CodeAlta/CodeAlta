import { createContext, useEffect, useState } from "react";
import { Button, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { backgroundTaskElapsed, backgroundTaskIcon, backgroundTaskKind, type BackgroundTask, type BackgroundTaskState } from "./backgroundTasks";
import { useShellLanguage } from "./shellLanguage";

/**
 * The tool calls of the session whose task goes on in the background or ended there, by the identity the
 * timeline gives a call. A tile reads it to say more than "Completed" of a call that only returned.
 */
export const BackgroundCallsContext = createContext<ReadonlyMap<string, BackgroundTaskState>>(new Map());

/** A mark for a session that is not running a turn and still has tasks going on in the background. */
export function BackgroundMark({ count }: { count: number }) {
  const { t } = useShellLanguage();
  const label = t(count === 1 ? "1 background task" : "{count} background tasks", { count });
  return <span className="session-background" role="img" aria-label={label} title={label} />;
}

/**
 * What the composer says of the tasks that go on in the background, in the place of its status: how many there
 * are, and on a click each of them with what it does, how long it has been going on and a button that stops it.
 */
export function BackgroundTasksStatus({ tasks, disabled, onStop }: {
  tasks: readonly BackgroundTask[];
  /** Whether a task can be stopped now. */
  disabled?: boolean;
  /** Asks the host to stop a task; settles when the host answered, whatever it answered. */
  onStop: (taskId: string) => Promise<void>;
}) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const [stopping, setStopping] = useState<ReadonlySet<string>>(new Set());
  const [now, setNow] = useState(() => Date.now());
  // The clock runs while the list is shown.
  useEffect(() => {
    if (!open) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [open]);
  // A task that is gone is no longer being stopped.
  useEffect(() => {
    setStopping(current => { const kept = new Set([...current].filter(id => tasks.some(task => task.id === id))); return kept.size === current.size ? current : kept; });
  }, [tasks]);
  if (tasks.length === 0) return null;
  const stop = (id: string) => {
    setStopping(current => new Set(current).add(id));
    void onStop(id).catch(() => {}).finally(() => setTimeout(() => setStopping(current => { const next = new Set(current); next.delete(id); return next; }), 4000));
  };
  const label = t(tasks.length === 1 ? "1 background task" : "{count} background tasks", { count: tasks.length });
  const list = <div className="background-tasks" role="group" aria-label={t("Background tasks")}>
    <h6>{t("Background tasks")}</h6>
    <ul>{tasks.map(task => { const elapsed = backgroundTaskElapsed(task.startedAt, now); const kind = t(backgroundTaskKind(task.kind)); return <li key={task.id}>
      <span className="background-task-icon" title={kind}><AppIcon name={backgroundTaskIcon(task.kind)} size={14} /></span>
      <span className="background-task-text"><strong title={task.description ?? undefined}>{task.description ?? kind}</strong>
        <small>{kind}{elapsed ? ` · ${elapsed}` : ""}</small></span>
      <Button variant="minimal" size="small" icon={<AppIcon name="stop" size={13} />} disabled={disabled || stopping.has(task.id)}
        aria-label={t("Stop this background task")} title={t("Stop this background task")} onClick={() => stop(task.id)} />
    </li>; })}</ul>
  </div>;
  return <PopoverNext isOpen={open} onInteraction={next => setOpen(next)} placement="top-start" content={list} popoverClassName="background-tasks-popover">
    <Button variant="minimal" size="small" className="background-tasks-status" icon={<span className="session-background" aria-hidden="true" />}>{label}</Button>
  </PopoverNext>;
}
