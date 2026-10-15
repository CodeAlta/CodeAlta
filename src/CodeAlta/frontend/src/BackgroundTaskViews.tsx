import { createContext, useEffect, useRef, useState, type ReactNode } from "react";
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
  const trigger = useRef<HTMLButtonElement>(null);
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
  const output = shown && renderOutput ? renderOutput({ task: shown, disabled: disabled || stopping.has(shown.id), onStop: stop, onClose: () => {
    setShown(null);
    requestAnimationFrame(() => { if (trigger.current?.isConnected && !document.querySelector('dialog[open]')) trigger.current.focus(); });
  } }) : null;
  if (listed.length === 0) return output;
  const running = listed.filter(task => task.state === "running").length;
  const label = running === 0 ? t("No background tasks running") : t(running === 1 ? "1 background task running" : "{count} background tasks running", { count: running });
  const list = <div className="background-tasks" role="group" aria-label={t("Background tasks")}>
    <header><AppIcon name="terminal" size={16} /><strong>{t("Background tasks")}</strong><span>{listed.length}</span></header>
    <div className="background-tasks-list">{(["running", "recent"] as const).map(group => {
      const members = listed.filter(task => (task.state === "running") === (group === "running"));
      return members.length > 0 && <section className="background-tasks-section" key={group}>
        <h6>{t(group === "running" ? "Running" : "Recent")}</h6>
        <ul>{members.map(task => {
          const kind = t(backgroundTaskKind(task.kind));
          const end = backgroundTaskEnd(task.state);
          const elapsed = backgroundTaskElapsed(task.startedAt, task.endedAt ? Date.parse(task.endedAt) : now);
          const content = <><span className="background-task-icon" title={kind}><AppIcon name={backgroundTaskIcon(task.kind)} size={16} /></span>
            <span className="background-task-text"><strong>{task.description ?? kind}</strong>
              <span className="background-task-meta"><span className="background-task-state">{t(end ?? "Running")}</span>
                {task.exitCode !== null && <span>{t("Exit code {code}", { code: task.exitCode })}</span>}
                {!task.job && <span>{kind}</span>}{elapsed && <span className="background-task-elapsed">{elapsed}</span>}</span></span>
            {task.job && renderOutput && <AppIcon name="chevronRight" size={14} />}</>;
          return <li key={task.id} data-state={task.state}>
            {task.job && renderOutput ? <button type="button" className="background-task-open" title={task.description ?? kind}
              aria-label={`${t("Show the output of this job")} · ${task.description ?? kind}`} aria-haspopup="dialog"
              onClick={() => { setShown(task); setOpen(false); }}
              // Like timeline cards, open on release so Enter cannot also activate the window's Close button.
              onKeyDown={event => { if (event.key === "Enter" || event.key === " " && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
              onKeyUp={event => { if (event.key === "Enter" && !event.defaultPrevented && !event.repeat && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) event.currentTarget.click(); }}>{content}</button>
              : <div className="background-task-info" title={task.description ?? kind}>{content}</div>}
            {task.state === "running" && <Button variant="minimal" size="small" className="background-task-stop" icon={<AppIcon name="stop" size={13} />} disabled={disabled || stopping.has(task.id)}
              aria-label={t("Stop this background task")} title={t("Stop this background task")} onClick={() => stop(task.id)} />}
          </li>;
        })}</ul>
      </section>;
    })}</div>
  </div>;
  return <>
    <PopoverNext isOpen={open} onInteraction={next => setOpen(next)} placement="top-start" content={list} popoverClassName="background-tasks-popover" shouldReturnFocusOnClose={!shown}>
      <Button ref={trigger} variant="minimal" size="small" className="background-tasks-status" data-running={running > 0 ? "true" : undefined}
        aria-label={label} title={label}
        // The spinner says that tasks go on; where the label is too long for the composer, the glyph of the tasks
        // takes its place, so that the count never stands alone.
        icon={<span className="background-tasks-icon" data-running={running > 0 || undefined}>
          {running > 0 && <span className="background-tasks-spinner" aria-hidden="true" />}<AppIcon name="terminal" size={13} className="background-tasks-glyph" /></span>}>
        <span className="background-tasks-count">{running > 0 ? running : listed.length}</span>
        <span className="background-tasks-word">{t(running > 0 ? "in the background" : "ended")}</span></Button>
    </PopoverNext>
    {output}
  </>;
}
