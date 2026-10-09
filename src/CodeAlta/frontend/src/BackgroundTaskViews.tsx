import { createContext, useEffect, useState, type ReactNode } from "react";
import { Button, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "./AppIcon";
import { backgroundTaskElapsed, backgroundTaskEnd, backgroundTaskIcon, backgroundTaskKind, listedBackgroundTasks,
  type BackgroundCallState, type BackgroundTask } from "./backgroundTasks";
import { useShellLanguage } from "./shellLanguage";

/**
 * The tool calls of the session whose task goes on in the background or ended there, by the identity the
 * timeline gives a call. A tile reads it to say more than "Completed" of a call that only returned.
 */
export const BackgroundCallsContext = createContext<ReadonlyMap<string, BackgroundCallState>>(new Map());

/** A mark for a session that is not running a turn and still has tasks going on in the background. */
export function BackgroundMark({ count }: { count: number }) {
  const { t } = useShellLanguage();
  const label = t(count === 1 ? "1 background task" : "{count} background tasks", { count });
  return <span className="session-background" role="img" aria-label={label} title={label} />;
}

/** What the window of the output of a background job is given. */
export type BackgroundJobOutputProps = Readonly<{
  task: BackgroundTask;
  /** Whether the job can be stopped now. */
  disabled?: boolean;
  onStop: (taskId: string) => void;
  onClose: () => void;
}>;

/**
 * What the composer says of what goes on in the background, in the place of its status: how many tasks there
 * are, and on a click each of them with what it does, how long it has been going on and a button that stops it.
 * A background job also shows what it writes, and stays listed a moment after it ended.
 */
export function BackgroundTasksStatus({ tasks, disabled, onStop, renderOutput }: {
  /** The background tasks of the session: the ones that go on and the last that ended. */
  tasks: readonly BackgroundTask[];
  /** Whether a task can be stopped now. */
  disabled?: boolean;
  /** Asks the host to stop a task; settles when the host answered, whatever it answered. */
  onStop: (taskId: string) => Promise<void>;
  /** Draws the window that shows what a job writes; without it a job only has its line in the list. */
  renderOutput?: (props: BackgroundJobOutputProps) => ReactNode;
}) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const [stopping, setStopping] = useState<ReadonlySet<string>>(new Set());
  const [now, setNow] = useState(() => Date.now());
  // The job whose output is shown, as it was last listed: its window stays when the job leaves the list.
  const [shown, setShown] = useState<BackgroundTask | null>(null);
  const listed = listedBackgroundTasks(tasks, now);
  const ended = listed.some(task => task.state !== "running");
  // The clock runs while the list is shown, and slowly while an ended job waits to leave it.
  useEffect(() => {
    if (!open && !ended) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), open ? 1000 : 30_000);
    return () => clearInterval(timer);
  }, [open, ended]);
  // A task that is gone is no longer being stopped.
  useEffect(() => {
    setStopping(current => { const kept = new Set([...current].filter(id => tasks.some(task => task.id === id && task.state === "running"))); return kept.size === current.size ? current : kept; });
    setShown(current => current && (tasks.find(task => task.id === current.id) ?? current));
  }, [tasks]);
  const stop = (id: string) => {
    setStopping(current => new Set(current).add(id));
    void onStop(id).catch(() => {}).finally(() => setTimeout(() => setStopping(current => { const next = new Set(current); next.delete(id); return next; }), 4000));
  };
  const output = shown && renderOutput ? renderOutput({ task: shown, disabled: disabled || stopping.has(shown.id), onStop: stop, onClose: () => setShown(null) }) : null;
  if (listed.length === 0) return output;
  const running = listed.filter(task => task.state === "running").length;
  const label = running === 0 ? t("Background tasks") : t(running === 1 ? "1 background task" : "{count} background tasks", { count: running });
  const list = <div className="background-tasks" role="group" aria-label={t("Background tasks")}>
    <h6>{t("Background tasks")}</h6>
    <ul>{listed.map(task => {
      const kind = t(backgroundTaskKind(task.kind));
      const end = backgroundTaskEnd(task.state);
      const elapsed = end ? null : backgroundTaskElapsed(task.startedAt, now);
      const state = end ? task.exitCode !== null && task.state === "failed" ? `${t(end)} · ${t("Exit code {code}", { code: task.exitCode })}` : t(end) : elapsed;
      return <li key={task.id} data-state={task.state}>
        <span className="background-task-icon" title={kind}><AppIcon name={backgroundTaskIcon(task.kind)} size={14} /></span>
        <span className="background-task-text"><strong title={task.description ?? undefined}>{task.description ?? kind}</strong>
          <small>{kind}{state ? ` · ${state}` : ""}</small></span>
        {task.job && renderOutput && <Button variant="minimal" size="small" icon={<AppIcon name="openExternal" size={13} />}
          aria-label={t("Show the output of this job")} title={t("Show the output of this job")} onClick={() => { setShown(task); setOpen(false); }} />}
        {task.state === "running" && <Button variant="minimal" size="small" icon={<AppIcon name="stop" size={13} />} disabled={disabled || stopping.has(task.id)}
          aria-label={t("Stop this background task")} title={t("Stop this background task")} onClick={() => stop(task.id)} />}
      </li>;
    })}</ul>
  </div>;
  return <>
    <PopoverNext isOpen={open} onInteraction={next => setOpen(next)} placement="top-start" content={list} popoverClassName="background-tasks-popover">
      <Button variant="minimal" size="small" className="background-tasks-status" data-running={running > 0 ? "true" : undefined}
        icon={running > 0 ? <span className="session-background" aria-hidden="true" /> : <AppIcon name="terminal" size={13} />}>{label}</Button>
    </PopoverNext>
    {output}
  </>;
}
