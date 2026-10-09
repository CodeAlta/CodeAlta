import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { Button, Tag } from "@blueprintjs/core";
import { ActivitySpinner } from "./ActivitySpinner";
import { AppIcon } from "./AppIcon";
import { AppWindowSurface } from "./AppWindow";
import { backgroundTaskElapsed, backgroundTaskEnd, type BackgroundTask } from "./backgroundTasks";
import type { BackgroundJobOutputProps } from "./BackgroundTaskViews";
import { isDialogBackdrop } from "./dialogBackdrop";
import { useShellLanguage } from "./shellLanguage";
import { formatSize } from "./toolCall";
import { useToolOutput, type ToolOutputs } from "./toolOutput";
import { ToolTerminal } from "./ToolTerminal";

function BackgroundTaskTag({ task }: { task: BackgroundTask }) {
  const { t } = useShellLanguage();
  const end = backgroundTaskEnd(task.state);
  return <Tag minimal round className="tool-state" data-state={task.state}
    intent={task.state === "running" ? "primary" : task.state === "completed" ? "success" : task.state === "failed" ? "danger" : "warning"}
    icon={task.state === "running" ? <ActivitySpinner size={11} /> : undefined}>{t(end ?? "Running")}</Tag>;
}

/**
 * What a background job writes, in a window: what it wrote so far, then what it writes, until its command ends.
 * A job that ended shows what the host still has of it.
 */
export function BackgroundJobDialog({ task, outputs, disabled, onStop, onClose }: BackgroundJobOutputProps & {
  /** The live output of the session of the job. */
  outputs?: ToolOutputs;
}) {
  const { t } = useShellLanguage();
  const id = useId();
  const dialog = useRef<HTMLDialogElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  useLayoutEffect(() => {
    const element = dialog.current!;
    element.showModal(); closeButton.current?.focus();
    return () => { if (element.open) element.close(); };
  }, []);
  const running = task.state === "running";
  const live = useToolOutput(outputs, task.id, true);
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!running) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [running]);
  const elapsed = backgroundTaskElapsed(task.startedAt, running || !task.endedAt ? now : Date.parse(task.endedAt));
  const text = live?.text ?? "";
  return <dialog ref={dialog} className="app-dialog tool-call-dialog background-job-dialog" aria-labelledby={id} data-tool-family="shell" data-job-state={task.state}
    onClick={event => { if (isDialogBackdrop(event)) onClose(); }}
    onClose={event => { if (!event.currentTarget.open) onClose(); }} onCancel={event => { event.preventDefault(); onClose(); }}
    onKeyDown={event => { event.stopPropagation(); if (event.key === "Escape" && !event.repeat) { event.preventDefault(); onClose(); } }}>
    <AppWindowSurface storageKey="codealta.desktop.window.background-job.v1" titleId={id} minimumSize={{ width: 420, height: 280 }}
      title={<span className="tool-title"><AppIcon name="terminal" size={15} /><span className="tool-title-name">{task.description ?? t("Command")}</span><BackgroundTaskTag task={task} /></span>}
      preferredSize={viewport => ({ width: Math.min(900, viewport.width - 40), height: Math.min(620, viewport.height - 40) })}
      onClose={onClose} closeLabel={t("Close")} closeRef={closeButton}>
      <div className="tool-summary">
        {elapsed && <span className="tool-fact" title={t("Duration")}><AppIcon name="reminder" size={13} />{elapsed}</span>}
        {task.exitCode !== null && <Tag minimal intent={task.exitCode === 0 ? "success" : "danger"} className="tool-exit">{t("Exit code {code}", { code: task.exitCode })}</Tag>}
        {live && live.total > 0 && <span className="tool-fact">{t("{count} lines", { count: live.lines })} · {formatSize(live.total)}</span>}
        {running && <Button size="small" variant="outlined" className="background-job-stop" icon={<AppIcon name="stop" size={13} />} disabled={disabled}
          onClick={() => onStop(task.id)}>{t("Stop this background task")}</Button>}
      </div>
      <div className="tool-panel" data-view="shell"><div className="tool-shell"><div className="tool-terminal-frame">
        {live?.ended && !text ? <p className="tool-empty">{t("No output")}</p> : <ToolTerminal stream={`job:${task.id}`} text={text} offset={live?.offset ?? 0} />}
        {!live?.ended && !text && <p className="tool-waiting">{t("Waiting for output…")}</p>}
      </div></div></div>
    </AppWindowSurface>
  </dialog>;
}
