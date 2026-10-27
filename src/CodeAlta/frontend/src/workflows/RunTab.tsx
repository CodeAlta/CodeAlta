import { useContext, useEffect, useMemo, useRef, useState } from "react";
import { Button, HTMLTable } from "@blueprintjs/core";
import type { WorkflowRunDetailResponse } from "#neoastra";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { Chart, type ChartDatum } from "../charts";
import { SessionLinksContext } from "../SessionReference";
import { useShellLanguage } from "../shellLanguage";
import { scriptRunOf } from "../timeline";
import { formatDuration } from "../toolCall";
import { graphStateLabel, RunGraphView } from "./RunGraphView";
import { graphState, runGraph, timelineBars, timelineOption, timelineStates, type GraphState } from "./runGraph";
import { runDuration, runStateLabel, runTone } from "./workflows";
import { createWorkflowsHub, type WorkflowsApi } from "./workflowsHub";

/** How often the tab reads a run again while it runs. */
export const runTabRefreshMilliseconds = 2000;

/**
 * The tab of a run of a script: its graph large, the sessions it started (title, step, state, duration, tokens; each opens
 * its session), a timeline of its steps and sessions, its script and its result, and Copy as Mermaid, the text `alta
 * workflow show --format mermaid` gives. It reads the run again while it runs. A run the host no longer keeps closes its tab.
 */
export function RunTab({ api, epoch, runId, visible, onActivate, onGone, write = text => navigator.clipboard.writeText(text) }: {
  api: WorkflowsApi;
  /** The epoch of the host of the window; null in a window without a host of its own, undefined while it is not known. */
  epoch: string | null | undefined;
  runId: string;
  visible: boolean;
  onActivate: () => void;
  /** The host no longer keeps the run: the tab closes. */
  onGone: () => void;
  /** Writes the Mermaid text on the clipboard. */
  write?: (text: string) => Promise<void>;
}) {
  const { t } = useShellLanguage();
  const links = useContext(SessionLinksContext);
  const hub = useMemo(() => epoch ? createWorkflowsHub(api, epoch) : null, [api, epoch]);
  const [detail, setDetail] = useState<WorkflowRunDetailResponse | null>(null);
  const [copied, setCopied] = useState(false);
  const [now, setNow] = useState(() => Date.now());
  const running = detail?.run?.state === "running";
  const gone = useRef(onGone); gone.current = onGone;
  // Read when the tab shows, and again while the run runs.
  useEffect(() => {
    if (!hub || !visible) return;
    const abort = new AbortController();
    let timer: ReturnType<typeof setTimeout> | null = null;
    async function read() {
      const value = await hub!.runDetail(runId, abort.signal);
      if (abort.signal.aborted) return;
      if (value === "gone") { gone.current(); return; }
      if (value) { setDetail(value); setNow(Date.now()); }
      if (!value || value.run?.state === "running") timer = setTimeout(() => void read(), runTabRefreshMilliseconds);
    }
    void read();
    return () => { abort.abort(); if (timer !== null) clearTimeout(timer); };
  }, [hub, visible, runId]);
  useEffect(() => {
    if (!visible || !running) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [visible, running]);

  const run = useMemo(() => detail ? scriptRunOf(detail.records.map(record => ({ ...record, timestamp: record.timestamp })), runId) : null, [detail, runId]);
  const graph = useMemo(() => run ? runGraph(run) : null, [run]);
  const bars = useMemo(() => run ? timelineBars(run, now) : [], [run, now]);
  const states = useMemo(() => Object.fromEntries(timelineStates.map(state => [state, t(graphStateLabel(state))])) as Record<GraphState, string>, [t]);
  const option = useMemo(() => timelineOption(bars, states, t("Seconds")), [bars, states, t]);

  if (epoch === null) return <div className="workflow-run-tab" onPointerDown={onActivate}><p className="automation-empty">{t("Workflows are unavailable in this window.")}</p></div>;
  if (!detail?.run || !run) return <div className="workflow-run-tab" onPointerDown={onActivate}><p className="automation-empty"><ActivitySpinner size={16} /></p></div>;
  const info = detail.run;
  const session = links?.title(info.sessionId) ?? null;
  const tokens = run.sessions.reduce((sum, item) => sum + (item.inputTokens ?? 0) + (item.outputTokens ?? 0), 0);
  const facts = [t(runStateLabel(info.state)), formatDuration(runDuration(info, now)),
    run.sessions.length > 0 ? t("{count} sessions", { count: run.sessions.length }) : null, tokens > 0 ? t("{count} tokens", { count: tokens.toLocaleString() }) : null].filter(Boolean).join(" · ");
  async function copy() {
    if (!detail?.mermaid) return;
    await write(detail.mermaid);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  }
  const select = (datum: ChartDatum) => { const bar = bars[datum.dataIndex]; if (bar?.sessionId && links) links.open(bar.sessionId); };
  return <div className="workflow-run-tab" data-state={runTone(info.state)} onPointerDown={onActivate}>
    <header className="workflow-run-head">
      <span className="automations-emblem workflows-emblem">{running ? <ActivitySpinner size={18} /> : <AppIcon name="workflow" size={20} />}</span>
      <div className="workflow-run-title">
        <h2>{info.title ?? info.id}</h2>
        <p><code>{info.id}</code>{session && <> · <button type="button" className="workflow-run-session" onClick={() => links?.open(info.sessionId)}>{session}</button></>} · {facts}</p>
      </div>
      <Button variant="minimal" icon={<AppIcon name={copied ? "check" : "copy"} size={15} />} disabled={!detail.mermaid} onClick={() => void copy()}>{t(copied ? "Copied" : "Copy as Mermaid")}</Button>
    </header>
    {graph ? <section aria-label={t("Graph of the run")}><RunGraphView graph={graph} size="large" /></section>
      : run.steps.length > 0 && <section aria-label={t("Steps")}><ol className="script-run-steps">{run.steps.map((step, index) =>
        <li key={`${step.id}:${index}`} data-state={graphState(step.status)}><span>{step.id}</span></li>)}</ol></section>}
    {run.sessions.length > 0 && <section className="workflow-run-section">
      <h3>{t("Sessions")}<span className="count">{run.sessions.length}</span></h3>
      <HTMLTable compact className="workflow-run-sessions">
        <thead><tr><th>{t("Title")}</th><th>{t("Step")}</th><th>{t("State")}</th><th>{t("Duration")}</th><th>{t("Tokens")}</th></tr></thead>
        <tbody>{run.sessions.map(item => {
          const title = links?.title(item.sessionId) ?? item.title ?? item.sessionId.slice(0, 8);
          const used = item.inputTokens !== null || item.outputTokens !== null ? (item.inputTokens ?? 0) + (item.outputTokens ?? 0) : null;
          const state = graphState(item.status);
          return <tr key={item.sessionId} data-state={state}>
            <td>{links && links.title(item.sessionId) !== null
              ? <button type="button" className="workflow-run-session" onClick={() => links.open(item.sessionId)}>{title}</button> : title}</td>
            <td>{item.stepId ?? ""}</td>
            <td>{t(graphStateLabel(state))}</td>
            <td className="numeric">{item.durationMs !== null ? formatDuration(item.durationMs) : ""}</td>
            <td className="numeric">{used !== null ? used.toLocaleString() : ""}</td>
          </tr>;
        })}</tbody>
      </HTMLTable>
    </section>}
    {bars.length > 0 && <section className="workflow-run-section">
      <h3>{t("Timeline")}</h3>
      <Chart option={option} ariaLabel={t("Timeline of the steps and sessions of the run")} height={Math.min(560, 64 + bars.length * 24)} visible={visible} onSelect={select} />
    </section>}
    {detail.result && <section className="workflow-run-section">
      <h3>{t("Result")}</h3>
      <pre className="workflow-run-text">{detail.result}</pre>
    </section>}
    {detail.source && <section className="workflow-run-section">
      <h3>{t("Script")}</h3>
      <pre className="workflow-run-text workflow-run-source">{detail.source}</pre>
    </section>}
  </div>;
}
