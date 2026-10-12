import { lazy, Suspense, useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { Button, Tab, Tabs, Tag } from "@blueprintjs/core";
import type { BackgroundJobDetails, BackgroundJobDetailsResponse } from "#neoastra";
import { AppIcon } from "./AppIcon";
import { AppWindowSurface } from "./AppWindow";
import { backgroundTaskElapsed, backgroundTaskEnd } from "./backgroundTasks";
import type { BackgroundJobOutputProps } from "./BackgroundTaskViews";
import { isDialogBackdrop } from "./dialogBackdrop";
import { MarkdownContent } from "./MarkdownContent";
import { timelineTime } from "./sessionTime";
import { useShellLanguage } from "./shellLanguage";
import { formatThinkingElapsed } from "./thinkingElapsed";
import type { TimelineItem } from "./timeline";
import { formatSize } from "./toolCall";
import { CommandBlock, CopyButton, ToolStateTag } from "./ToolCallChrome";
import { useToolOutput, type ToolOutputs } from "./toolOutput";

// Recorded timeline cards need no browser terminal; load it only for a job's live output.
const ToolTerminal = lazy(() => import("./ToolTerminal").then(module => ({ default: module.ToolTerminal })));

type LiveJobProps = BackgroundJobOutputProps & {
  item?: never;
  current?: () => boolean;
  outputs?: ToolOutputs;
  readJob?: (jobId: string, signal: AbortSignal) => Promise<BackgroundJobDetailsResponse>;
};
type RecordedJobProps = {
  item: TimelineItem;
  current: () => boolean;
  onClose: () => void;
  task?: never; outputs?: never; readJob?: never; disabled?: never; onStop?: never;
};

/**
 * The same job window from a timeline card or the task list: command, Output and Details. Live jobs follow
 * their output and may be stopped; historical cards show only their recorded facts, including tail notices.
 */
export function BackgroundJobDialog({ item, task, outputs, readJob, disabled, onStop, current, onClose }: LiveJobProps | RecordedJobProps) {
  const { t, locale } = useShellLanguage();
  const id = useId();
  const dialog = useRef<HTMLDialogElement>(null);
  const closeButton = useRef<HTMLButtonElement>(null);
  const composing = useRef(false);
  const [tab, setTab] = useState("output");
  const [record, setRecord] = useState<BackgroundJobDetails | null>(null);
  const [pending, setPending] = useState(!!task && !!readJob);
  useEffect(() => {
    setRecord(null);
    if (!task || !readJob) { setPending(false); return; }
    const controller = new AbortController();
    setPending(true);
    void readJob(task.id, controller.signal).then(value => {
      if (!controller.signal.aborted && value.status === "ok" && value.job?.jobId === task.id) setRecord(value.job);
    }, () => { /* The window still has the observed state and output; unavailable details are explicit. */ })
      .finally(() => { if (!controller.signal.aborted) setPending(false); });
    return () => controller.abort();
  }, [task?.id, readJob]);
  useLayoutEffect(() => {
    const element = dialog.current!;
    if (current && !current()) { onClose(); return; }
    element.showModal(); closeButton.current?.focus();
    const retire = (event: Event) => { if (event.target instanceof HTMLDialogElement && event.target !== element) onClose(); };
    document.addEventListener("beforetoggle", retire, true);
    return () => { document.removeEventListener("beforetoggle", retire, true); if (element.open) element.close(); };
  }, []);
  useLayoutEffect(() => { if (current && !current()) onClose(); });
  const job = item?.jobResult;
  const running = task?.state === "running";
  const live = useToolOutput(outputs, task?.id, !!task);
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!running) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [running]);
  const elapsed = task && backgroundTaskElapsed(task.startedAt, running || !task.endedAt ? now : Date.parse(task.endedAt));
  const text = live?.text ?? "";
  const title = job?.title || record?.title || task?.description || t("Background job");
  const command = job?.command ?? record?.command;
  const phase = job?.phase ?? (task?.state === "stopped" ? "canceled" : task?.state);
  const result = job?.result ?? (task ? t(backgroundTaskEnd(task.state) ?? "Running") : "");
  const commandView = command ? <CommandBlock command={command} /> : task && <p className="tool-partial">{t("Details")}: {t(pending ? "Loading…" : "Unavailable")}</p>;
  return <dialog ref={dialog} className={`app-dialog tool-call-dialog job-result-dialog${task ? " background-job-dialog" : ""}`}
    aria-label={`${t("Details")} · ${t("Background job")}`} data-tool-family="shell" data-job-state={task?.state}
    onClick={event => { if (!composing.current && isDialogBackdrop(event)) onClose(); }}
    onClose={event => { if (!event.currentTarget.open) onClose(); }} onCancel={event => { event.preventDefault(); if (!composing.current) onClose(); }}
    onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
    onKeyDown={event => { event.stopPropagation(); if (event.key === "Escape") { event.preventDefault(); if (!event.repeat && !composing.current && !event.nativeEvent.isComposing && event.nativeEvent.keyCode !== 229) onClose(); } }}>
    <AppWindowSurface storageKey="codealta.desktop.window.job-result.v1" titleId={id} minimumSize={{ width: 420, height: 280 }}
      title={<span className="tool-title"><AppIcon name="terminal" size={15} /><span className="tool-title-name">{title}</span>{phase && <ToolStateTag state={phase} />}</span>}
      preferredSize={viewport => ({ width: Math.min(900, viewport.width - 40), height: Math.min(640, viewport.height - 40) })}
      onClose={onClose} closeLabel={t("Close")} closeRef={closeButton}>
      <div className="tool-summary">
        <span className="tool-fact">{result}</span>
        {elapsed && <span className="tool-fact" title={t("Duration")}><AppIcon name="reminder" size={13} />{elapsed}</span>}
        {task?.exitCode != null && <Tag minimal intent={task.exitCode === 0 ? "success" : "danger"} className="tool-exit">{t("Exit code {code}", { code: task.exitCode })}</Tag>}
        {live && live.total > 0 && <span className="tool-fact">{t("{count} lines", { count: live.lines })} · {formatSize(live.total)}</span>}
        {task && text && <CopyButton text={text} label={t("Copy output")} />}
        {running && onStop && <Button size="small" variant="outlined" className="background-job-stop" icon={<AppIcon name="stop" size={13} />} disabled={disabled}
          aria-label={t("Stop this background task")} onClick={() => onStop(task.id)}>{t("Stop this background task")}</Button>}
      </div>
      <Tabs id={id} className="tool-tabs" selectedTabId={tab} onChange={value => setTab(String(value))} renderActiveTabPanelOnly>
        <Tab id="output" disabled={false} title={t("Output")} panel={task ? <div className="tool-panel" data-view="shell"><div className="tool-shell">
          {commandView}
          {live && live.offset > 0 && <p className="tool-partial">{t("Only the latest output is retained.")}</p>}
          <div className="tool-terminal-frame">
            {live?.ended && !text ? <p className="tool-empty">{t("No output")}</p> : <Suspense fallback={null}>
              <ToolTerminal stream={`job:${task.id}`} text={text} offset={live?.offset ?? 0} />
            </Suspense>}
            {!live?.ended && !text && <p className="tool-waiting">{t("Waiting for output…")}</p>}
          </div>
        </div></div> : <div className="tool-panel job-result-output">
          {commandView}
          {job?.output && <MarkdownContent source={job.output} timelineCodeBlocks />}
          {(item?.truncated || item?.bodyOmitted) && <p className="tool-partial">{t("The rest is too long to show.")}</p>}
        </div>} />
        <Tab id="details" disabled={false} title={t("Details")} panel={<div className="tool-panel tool-details">
          <dl className="tool-facts">
            <div><dt>{t("Background job")}</dt><dd>{job?.jobId ?? task?.id}</dd></div>
            <div><dt>{t("Title")}</dt><dd>{title}</dd></div>
            <div><dt>{t("Result")}</dt><dd>{result}{task?.exitCode != null && ` · ${t("Exit code {code}", { code: task.exitCode })}`}</dd></div>
            {item && <div><dt>{t("Time")}</dt><dd>{timelineTime(item.timestamp, locale).title}</dd></div>}
            {task?.startedAt && <div><dt>{t("Started")}</dt><dd>{timelineTime(task.startedAt, locale).title}</dd></div>}
            {task?.endedAt && <div><dt>{t("Ended")}</dt><dd>{timelineTime(task.endedAt, locale).title}</dd></div>}
            {elapsed && <div><dt>{t("Duration")}</dt><dd>{elapsed}</dd></div>}
            {record && <div><dt>{t("Working directory")}</dt><dd>{record.workingDirectory}</dd></div>}
          </dl>
          {record?.timeoutSeconds != null && <p className="tool-fact">{t("Timeout {duration}", { duration: formatThinkingElapsed(record.timeoutSeconds) })}</p>}
          {commandView}
          {item && <section className="tool-section"><header><h3>{t("Details")}</h3>
            {item.copyMarkdown && <CopyButton text={item.copyMarkdown} label={t("Copy {title} as Markdown", { title: t("Background job") })} />}</header>
            <pre className="tool-code">{item.metadata.join("\n")}</pre>
          </section>}
        </div>} />
      </Tabs>
    </AppWindowSurface>
  </dialog>;
}
