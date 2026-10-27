import { lazy, memo, Suspense, useCallback, useContext, useEffect, useMemo, useRef, useState } from "react";
import { Button, Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon, type IconName } from "../AppIcon";
import { SaveAsScript, ScriptRunActionsContext } from "../scriptCard/SaveAsScript";
import { ScriptSessionBox } from "../scriptCard/ScriptSessionBox";
import { ScriptWaitLine } from "../scriptCard/ScriptWaitLine";
import { scriptRunOffers, wakeTime } from "../scriptCard/scriptRunOffers";
import { useShellLanguage } from "../shellLanguage";
import type { ScriptRun } from "../timeline";
import { formatDuration } from "../toolCall";
import { FlowActionsContext, graphStateLabel, runPhaseLabel, type FlowActions } from "./flowActions";
import { OpenRunContext } from "./openRun";
import { blockFit, blockPlan, compactTokens, estimateFlowSize, flowElements, runMermaid, runPhase, runTotals, tabCanvasHeight, workflowGraph, type FlowElements, type FlowMode } from "./runFlow";
import type { FlowLayout } from "./runFlowLayout";
import { graphState, type RunGraph } from "./runGraph";

// React Flow and dagre are a chunk of their own, loaded with the first graph shown.
const WorkflowFlow = lazy(() => import("./WorkflowFlow").then(module => ({ default: module.WorkflowFlow })));

type Rerun = Readonly<{ from?: string; all?: boolean }>;
const none: ReadonlySet<string> = new Set();

/** The width of an element, and whether it is near the view: a graph is drawn only there. */
function useCanvasView(target: HTMLElement | null): Readonly<{ near: boolean; width: number }> {
  const [view, setView] = useState({ near: false, width: 0 });
  useEffect(() => {
    if (!target) return;
    const resize = new ResizeObserver(entries => {
      const width = Math.floor(entries[0]?.contentRect.width ?? 0);
      setView(current => current.width === width ? current : { ...current, width });
    });
    resize.observe(target);
    if (typeof IntersectionObserver !== "function") { setView(current => current.near ? current : { ...current, near: true }); return () => resize.disconnect(); }
    const seen = new IntersectionObserver(entries => {
      const near = entries.some(entry => entry.isIntersecting);
      setView(current => current.near === near ? current : { ...current, near });
    }, { rootMargin: "480px 0px" });
    seen.observe(target);
    return () => { resize.disconnect(); seen.disconnect(); };
  }, [target]);
  return view;
}

/** The time, every second, while it is asked to. */
function useNow(ticking: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!ticking) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [ticking]);
  return now;
}

/** Which steps have their group of sessions open, and what opens or closes one. */
function useExpandedSteps() {
  const [expanded, setExpanded] = useState<ReadonlySet<string>>(none);
  const toggle = useCallback((stepId: string) => setExpanded(current => {
    const next = new Set(current);
    if (!next.delete(stepId)) next.add(stepId);
    return next;
  }), []);
  return [expanded, toggle] as const;
}

/**
 * The graph of a run, drawn by React Flow once the chunk is there and the place is near the view; before, and when it
 * leaves the view, an empty place of the same size, so the timeline never jumps. `block`: still, fitted to the width;
 * `tab`: pans and zooms.
 */
function RunCanvas({ graph, elements, mode, width, near, label }: { graph: RunGraph; elements: FlowElements; mode: FlowMode; width: number; near: boolean; label: string }) {
  const laid = useRef<{ elements: FlowElements; layout: FlowLayout } | null>(null);
  const onLayout = useCallback((layout: FlowLayout) => { laid.current = { elements, layout }; }, [elements]);
  const size = laid.current?.elements === elements ? laid.current.layout : estimateFlowSize(graph, elements, mode);
  const place = mode === "block" ? { width: blockFit(width, size).width, height: blockFit(width, size).height } : { width: undefined, height: tabCanvasHeight(size) };
  const placeholder = <div className="workflow-flow-placeholder" style={place} aria-hidden="true" />;
  if (!near || (mode === "block" && width <= 0)) return placeholder;
  return <Suspense fallback={placeholder}><WorkflowFlow elements={elements} mode={mode} available={width} onLayout={onLayout} label={label} /></Suspense>;
}

/**
 * A run of a script in the timeline, as its own block at the place of its call, outside the folded tool calls: a header
 * (its title and id, a pill of its state with the time it runs, its sessions, tokens and duration, and discreet actions),
 * what it waits for, and its graph left to right: a node per step with its state and the sessions it started, an edge per
 * `after`. The graph takes the width of the conversation; its height follows it.
 */
export const WorkflowBlock = memo(function WorkflowBlock({ run, title = null, rowKey, onOpenCall }: {
  run: ScriptRun; title?: string | null;
  /** The key of the row of the call that runs the script: the row goes to the block by it, and the block opens the call by it. */
  rowKey?: string;
  onOpenCall?: (rowKey: string, origin: HTMLButtonElement) => void;
}) {
  const { t } = useShellLanguage();
  const actions = useContext(ScriptRunActionsContext);
  const openRun = useContext(OpenRunContext);
  const offers = actions ? scriptRunOffers(run) : null;
  const graph = useMemo(() => workflowGraph(run), [run]);
  const [expanded, toggle] = useExpandedSteps();
  const [canvas, setCanvas] = useState<HTMLDivElement | null>(null);
  const view = useCanvasView(canvas);
  // Left to right while it fits at a readable scale, top to bottom otherwise.
  const plan = useMemo(() => graph ? blockPlan(graph, expanded, view.width) : null, [graph, expanded, view.width]);
  const stepWidth = plan?.stepWidth, direction = plan?.direction;
  const elements = useMemo(() => graph ? flowElements(graph, expanded, "block", stepWidth, direction) : null, [graph, expanded, stepWidth, direction]);
  const [saving, setSaving] = useState(false);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const phase = runPhase(run);
  const live = phase === "running" || phase === "waiting" || phase === "sleeping";
  const now = useNow(live && view.near);
  const totals = runTotals(run, now);
  const name = title ?? run.title ?? t("Script");

  const rerun = useCallback(async (how: Rerun) => {
    if (!actions) return;
    setBusy(true);
    setProblem(null);
    const failure = await actions.resume(run.runId, how);
    setBusy(false);
    if (failure !== null) setProblem(failure || t("The session could not be asked."));
  }, [actions, run.runId, t]);
  const flowActions = useMemo<FlowActions>(() => ({ toggle, busy, rerunFrom: offers?.rerunFromStep ? key => void rerun({ from: key }) : null }),
    [toggle, busy, offers?.rerunFromStep, rerun]);
  useEffect(() => {
    if (!copied) return;
    const timer = setTimeout(() => setCopied(false), 1500);
    return () => clearTimeout(timer);
  }, [copied]);
  async function copy() {
    if (!graph) return;
    try { await navigator.clipboard.writeText(runMermaid(graph)); setCopied(true); } catch { setProblem(t("Copy failed")); }
  }
  const resumes = offers?.resume ? wakeTime(run.resumesAt) : null;
  const sessionsLabel = t("{count} sessions", { count: totals.sessions });
  const tokensLabel = t("{count} tokens", { count: totals.tokens.toLocaleString() });
  // The actions, as icons on a wide block and as the items of its `…` menu on a narrow one: the head stays one line.
  const items: readonly Readonly<{ icon: IconName; label: string; busy?: boolean; act: (origin: HTMLButtonElement) => void }>[] = [
    ...offers?.resume ? [{ icon: "play" as const, label: t("Resume"), busy, act: () => void rerun({}) }] : [],
    ...offers?.rerun ? [{ icon: "repeat" as const, label: t("Rerun with recorded results"), busy, act: () => void rerun({ all: true }) }] : [],
    ...graph ? [{ icon: copied ? "check" as const : "copy" as const, label: t(copied ? "Copied" : "Copy as Mermaid"), act: () => void copy() }] : [],
    ...onOpenCall && rowKey ? [{ icon: "code" as const, label: t("Show the call"), act: (origin: HTMLButtonElement) => onOpenCall(rowKey, origin) }] : [],
    ...openRun && run.runId ? [{ icon: "expand" as const, label: t("Open run"), act: () => openRun({ runId: run.runId, title: name }) }] : [],
  ];
  const more = useRef<HTMLButtonElement>(null);
  const menu = <Menu>
    {offers?.saveAsScript && <MenuItem icon={<AppIcon name="save" size={14} />} text={t("Save as script…")} onClick={() => setSaving(true)} />}
    {items.map(item => <MenuItem key={item.label} icon={<AppIcon name={item.icon} size={14} />} text={item.label} disabled={item.busy}
      onClick={() => item.act(more.current!)} />)}
  </Menu>;
  const moreButton = <span className="workflow-block-more">
    <PopoverNext placement="bottom-end" content={menu}>
      <Button ref={more} size="small" variant="minimal" className="workflow-action" icon={<AppIcon name="ellipsis" size={14} />} title={t("Actions")} aria-label={t("Actions")} />
    </PopoverNext>
  </span>;

  return <section className="workflow-block" data-phase={phase} data-workflow-row={rowKey} tabIndex={-1} aria-label={name}>
    <header className="workflow-block-head">
      <span className="workflow-block-emblem" aria-hidden="true"><AppIcon name="workflow" size={15} /></span>
      <span className="workflow-block-title">
        <strong title={name}>{name}</strong>
        {run.runId && <code>{run.runId}</code>}
      </span>
      <span className="workflow-pill" data-phase={phase} role="status">
        {phase === "running" ? <ActivitySpinner size={9} /> : <i className="workflow-pill-dot" aria-hidden="true" />}
        {t(runPhaseLabel(phase))}
        {live && totals.durationMs !== null && <time>{formatDuration(totals.durationMs)}</time>}
      </span>
      <span className="workflow-block-facts">
        {totals.sessions > 0 && <span title={sessionsLabel} aria-label={sessionsLabel}><AppIcon name="subAgents" size={12} />{totals.sessions}</span>}
        {totals.tokens > 0 && <span title={tokensLabel} aria-label={tokensLabel}><AppIcon name="usage" size={12} />{compactTokens(totals.tokens)}</span>}
        {!live && totals.durationMs !== null && <span title={t("Duration")}><AppIcon name="timer" size={12} />{formatDuration(totals.durationMs)}</span>}
      </span>
      <span className="workflow-block-actions">
        {items.slice(0, offers?.resume || offers?.rerun ? 1 : 0).map(item => <IconAction key={item.label} icon={item.icon} label={item.label} busy={item.busy} onClick={item.act} />)}
        {offers?.saveAsScript && <SaveAsScript runId={run.runId} iconOnly />}
        {items.slice(offers?.resume || offers?.rerun ? 1 : 0).map(item => <IconAction key={item.label} icon={item.icon} label={item.label} busy={item.busy} onClick={item.act} />)}
      </span>
      {offers?.saveAsScript ? <SaveAsScript runId={run.runId} open={saving} onClose={() => setSaving(false)}>{moreButton}</SaveAsScript> : moreButton}
    </header>
    {run.waiting && <ScriptWaitLine wait={run.waiting} />}
    {resumes && <div className="script-run-waiting" role="status" data-kind="sleep"><AppIcon name="timer" size={13} />
      <span className="script-run-waiting-text"><strong>{t("Resumes by itself at {time}", { time: resumes })}</strong></span></div>}
    {graph && elements && graph.columns.length > 0 && <div ref={setCanvas} className="workflow-block-canvas">
      <FlowActionsContext.Provider value={flowActions}>
        <RunCanvas graph={graph} elements={elements} mode="block" width={view.width} near={view.near} label={t("Graph of the run")} />
      </FlowActionsContext.Provider>
    </div>}
    {graph && (graph.others.length > 0 || graph.loose.length > 0) && <div className="workflow-block-extra">
      {graph.others.length > 0 && <ul className="workflow-other-steps" aria-label={t("Other steps")}>
        {graph.others.map(step => <li key={step.id} data-state={graphState(step.status)} title={t(graphStateLabel(graphState(step.status)))}><i aria-hidden="true" />{step.id}</li>)}
      </ul>}
      {graph.loose.length > 0 && <ul className="script-run-sessions">{graph.loose.map(session => <li key={session.sessionId}><ScriptSessionBox session={session} /></li>)}</ul>}
    </div>}
    {problem && <p role="alert" className="script-run-problem">{problem}</p>}
  </section>;
});

function IconAction({ icon, label, busy = false, onClick }: { icon: IconName; label: string; busy?: boolean; onClick: (origin: HTMLButtonElement) => void }) {
  return <Button size="small" variant="minimal" className="workflow-action" icon={<AppIcon name={icon} size={14} />} title={label} aria-label={label}
    disabled={busy} onClick={event => { event.stopPropagation(); onClick(event.currentTarget as HTMLButtonElement); }} />;
}

/**
 * The graph of a run in its tab: the same graph, larger, that pans and zooms, with a minimap and controls. Its groups of
 * sessions open and close as in the timeline; a step is not rerun from there.
 */
export function RunTabGraph({ run, visible }: { run: ScriptRun; visible: boolean }) {
  const { t } = useShellLanguage();
  const graph = useMemo(() => workflowGraph(run), [run]);
  const [expanded, toggle] = useExpandedSteps();
  const elements = useMemo(() => graph ? flowElements(graph, expanded, "tab") : null, [graph, expanded]);
  const actions = useMemo<FlowActions>(() => ({ toggle, busy: false, rerunFrom: null }), [toggle]);
  if (!graph || !elements || graph.columns.length === 0) return null;
  return <section className="workflow-run-graph" aria-label={t("Graph of the run")}>
    <FlowActionsContext.Provider value={actions}>
      <RunCanvas graph={graph} elements={elements} mode="tab" width={0} near={visible} label={t("Graph of the run")} />
    </FlowActionsContext.Provider>
  </section>;
}
