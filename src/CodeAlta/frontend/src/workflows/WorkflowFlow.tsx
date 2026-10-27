// The graph of a run drawn with React Flow: a node per step, its sessions inside it, an edge per `after`. This module is a
// chunk of its own, with React Flow and dagre: the application loads it when the first graph is shown (WorkflowBlock, RunTab).
// Its stylesheet is imported by main.tsx with the others: a component imports no CSS, so that tests can bundle it.
import { memo, useContext, useEffect, useMemo, useRef } from "react";
import { Background, BackgroundVariant, Controls, getBezierPath, Handle, MiniMap, Position, ReactFlow, useReactFlow, useStore,
  type Edge, type EdgeProps, type Node, type NodeProps } from "@xyflow/react";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { ProviderIcon } from "../ProviderIcon";
import { SessionLinksContext } from "../SessionReference";
import { useShellLanguage } from "../shellLanguage";
import type { ScriptSession } from "../timeline";
import { formatDuration } from "../toolCall";
import { FlowActionsContext, graphStateLabel } from "./flowActions";
import { blockFit, compactTokens, flowMetrics, tabCanvasHeight, type FlowElements, type FlowMode, type StepFlowData } from "./runFlow";
import { layoutFlow, type FlowLayout } from "./runFlowLayout";
import { graphState, type GraphState, type SessionFold } from "./runGraph";

type StepNodeType = Node<StepFlowData, "step">;
type EdgeData = { state: GraphState; animated: boolean };
type FlowEdgeType = Edge<EdgeData, "flow">;

/** The properties of {@link WorkflowFlow}. */
export type WorkflowFlowProps = Readonly<{
  elements: FlowElements;
  mode: FlowMode;
  /** In a block: the width the graph may take. It fits it, down to a smallest readable scale, and scrolls sideways beyond. */
  available?: number;
  /** Told the size of the laid-out graph: what a block keeps in its place when the graph leaves the view. */
  onLayout?: (layout: FlowLayout) => void;
  label: string;
}>;

const nodeTypes = { step: memo(StepNode) };
const edgeTypes = { flow: memo(FlowEdge) };
const ignore = () => { };

/**
 * The graph of a run, left to right. In a block of the timeline it is read-only and still (no drag, no pan, no zoom): it
 * fits the width it is given and its height follows. In the tab of the run it pans and zooms, with a minimap and controls.
 */
export function WorkflowFlow({ elements, mode, available = 0, onLayout, label }: WorkflowFlowProps) {
  const layout = useMemo(() => layoutFlow(elements, mode), [elements, mode]);
  const nodes = useMemo<StepNodeType[]>(() => elements.nodes.map(node => ({ id: node.id, type: "step", position: layout.positions.get(node.id)!,
    width: node.width, height: node.height, data: node.data, draggable: false, selectable: false, connectable: false, focusable: false })), [elements, layout]);
  const edges = useMemo<FlowEdgeType[]>(() => elements.edges.map(edge => ({ id: edge.id, source: edge.source, target: edge.target, type: "flow",
    data: { state: edge.state, animated: edge.animated }, selectable: false, focusable: false })), [elements]);
  const told = useRef(onLayout); told.current = onLayout;
  useEffect(() => { told.current?.(layout); }, [layout]);
  // No key of React Flow: each one it has listens on the whole window and cancels its key outside the fields it knows, which
  // the editor of the composer is not one of (Space would no longer type there while a graph is shown). A drag pans the tab.
  const common = { nodes, edges, nodeTypes, edgeTypes, nodesDraggable: false, nodesConnectable: false, elementsSelectable: false, nodesFocusable: false,
    edgesFocusable: false, zoomOnDoubleClick: false, zoomOnScroll: false, panOnScroll: false, preventScrolling: false,
    panActivationKeyCode: null, selectionKeyCode: null, multiSelectionKeyCode: null, deleteKeyCode: null, zoomActivationKeyCode: null, "aria-label": label } as const;
  if (mode === "block") {
    const fit = blockFit(available, layout);
    return <div className="workflow-flow" data-mode="block" style={{ width: fit.width, height: fit.height }}>
      <ReactFlow {...common} viewport={{ x: fit.x, y: fit.y, zoom: fit.scale }} onViewportChange={ignore} minZoom={0.1} maxZoom={2}
        panOnDrag={false} zoomOnPinch={false} disableKeyboardA11y>
        <Background variant={BackgroundVariant.Dots} gap={16} size={1} />
      </ReactFlow>
    </div>;
  }
  return <div className="workflow-flow" data-mode="tab" style={{ height: tabCanvasHeight(layout) }}>
    <ReactFlow {...common} fitView fitViewOptions={{ padding: 0.12, maxZoom: 1 }} minZoom={0.15} maxZoom={1.75} panOnDrag zoomOnPinch>
      <Background variant={BackgroundVariant.Dots} gap={18} size={1} />
      <MiniMapWhenLarger layout={layout} label={label} />
      <Controls showInteractive={false} />
      <FitWhenStepsChange steps={elements.nodes.map(node => `${node.id}:${node.height}`).join("\n")} />
    </ReactFlow>
  </div>;
}

// The tab fits the graph when it is drawn, and again when its steps change or one changes its size (a group of sessions that
// opens): not when what they say does, which the user reads where they panned to.
function FitWhenStepsChange({ steps }: { steps: string }) {
  const flow = useReactFlow();
  const first = useRef(true);
  useEffect(() => {
    if (first.current) { first.current = false; return; }
    const frame = requestAnimationFrame(() => void flow.fitView({ padding: 0.12, maxZoom: 1, duration: 200 }));
    return () => cancelAnimationFrame(frame);
  }, [flow, steps]);
  return null;
}

// The minimap, only while the graph is larger than the view (zoomed in, or too large to fit): fitted, the graph is whole
// on the screen and a minimap would only cover a part of it.
function MiniMapWhenLarger({ layout, label }: { layout: FlowLayout; label: string }) {
  const larger = useStore(state => layout.width * state.transform[2] > state.width + 1 || layout.height * state.transform[2] > state.height + 1);
  if (!larger) return null;
  return <MiniMap pannable zoomable ariaLabel={label} style={{ width: 160, height: 96 }}
    nodeClassName={node => `workflow-minimap-node workflow-minimap-${(node.data as StepFlowData).node.state}`} nodeBorderRadius={6} />;
}

/** The mark of a state: a ring that fills while a step runs and its sessions end, a spinner, or the icon of how it ended. */
function StateGlyph({ state, progress = null, size, label }: { state: GraphState; progress?: number | null; size: number; label?: string }) {
  const role = { ...label ? { role: "img", "aria-label": label, title: label } : { "aria-hidden": true }, style: { width: size, height: size } };
  if (state === "running" && progress !== null) {
    const radius = size / 2 - 2, length = 2 * Math.PI * radius;
    return <span className="workflow-glyph" data-state={state} {...role}>
      <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} className="workflow-ring">
        <circle cx={size / 2} cy={size / 2} r={radius} className="workflow-ring-track" />
        <circle cx={size / 2} cy={size / 2} r={radius} className="workflow-ring-value" strokeDasharray={`${length * progress} ${length}`} />
      </svg>
    </span>;
  }
  if (state === "running") return <span className="workflow-glyph" data-state={state} {...role}><ActivitySpinner size={Math.round(size * 0.6)} /></span>;
  return <span className="workflow-glyph" data-state={state} {...role}>
    {state === "pending" ? <i className="workflow-pending-dot" /> : <AppIcon name={state === "completed" ? "check" : state === "failed" ? "close" : state === "waiting" ? "hand" : "minus"}
      size={Math.round(size * 0.62)} />}
  </span>;
}

function StepNode({ data }: NodeProps<StepNodeType>) {
  const { node, counts, progress, fold, expanded, mode, direction } = data;
  const metrics = flowMetrics[mode];
  const { t } = useShellLanguage();
  const actions = useContext(FlowActionsContext);
  const label = t(graphStateLabel(node.state));
  const facts = [node.title !== node.id ? node.id : null, node.replayed ? t("Recorded result") : null,
    node.durationMs !== null && node.state !== "running" && node.state !== "waiting" ? formatDuration(node.durationMs) : null,
    counts.total > 0 ? `${node.each ? "× " : ""}${t("{done}/{total} sessions", { done: counts.done, total: counts.total })}` : node.each ? t("For each item") : null]
    .filter(Boolean).join(" · ");
  const [into, out] = direction === "TB" ? [Position.Top, Position.Bottom] : [Position.Left, Position.Right];
  return <div className="workflow-step" data-state={node.state} data-each={node.each || undefined} data-step={node.id} data-replayed={node.replayed || undefined}>
    <Handle type="target" position={into} isConnectable={false} className="workflow-handle" />
    <div className="workflow-step-head" style={{ height: metrics.head }}>
      <StateGlyph state={node.state} progress={progress} size={mode === "tab" ? 22 : 20} label={label} />
      <span className="workflow-step-titles">
        <strong title={node.title}>{node.title}</strong>
        <small>{facts || label}</small>
      </span>
      {actions?.rerunFrom && node.key && <button type="button" className="workflow-step-action nodrag nopan" disabled={actions.busy}
        title={t("Rerun from this step")} aria-label={t("Rerun from this step")}
        onClick={event => { event.stopPropagation(); actions.rerunFrom?.(node.key!); }}><AppIcon name="rewind" size={13} /></button>}
    </div>
    {node.sessions.length > 0 && (fold
      ? <SessionGroup stepId={node.id} fold={fold} expanded={expanded} mode={mode} />
      : <div className="workflow-sessions" style={{ gap: metrics.gap }}>
        {node.sessions.map(session => <SessionRow key={session.sessionId} session={session} height={metrics.session} />)}
      </div>)}
    <Handle type="source" position={out} isConnectable={false} className="workflow-handle" />
  </div>;
}

// More than six sessions: their counts and a bar of how they are, which a click opens on the list, the running and the
// failed ones first. The list shows a few rows and scrolls.
function SessionGroup({ stepId, fold, expanded, mode }: { stepId: string; fold: SessionFold; expanded: boolean; mode: FlowMode }) {
  const { t } = useShellLanguage();
  const actions = useContext(FlowActionsContext);
  const metrics = flowMetrics[mode];
  const pending = fold.total - fold.done - fold.running - fold.failed;
  const rows = Math.min(fold.total, metrics.groupRows);
  return <div className="workflow-group" data-open={expanded || undefined}>
    <button type="button" className="workflow-group-head nodrag nopan" style={{ height: metrics.group }} aria-expanded={expanded}
      title={t(expanded ? "Hide the sessions" : "Show the sessions")} onClick={event => { event.stopPropagation(); actions?.toggle(stepId); }}>
      <span className="workflow-group-line">
        <strong>{t("{count} sessions", { count: fold.total })}</strong>
        <span className="workflow-group-counts">
          <span data-state="completed" title={t("{count} done", { count: fold.done })}><AppIcon name="check" size={11} />{fold.done}</span>
          {fold.running > 0 && <span data-state="running" title={t("{count} running", { count: fold.running })}><ActivitySpinner size={8} />{fold.running}</span>}
          {fold.failed > 0 && <span data-state="failed" title={t("{count} failed", { count: fold.failed })}><AppIcon name="close" size={11} />{fold.failed}</span>}
        </span>
        <AppIcon name={expanded ? "chevronDown" : "chevronRight"} size={12} />
      </span>
      <span className="workflow-statusbar" aria-hidden="true">
        {fold.done > 0 && <i data-state="completed" style={{ flexGrow: fold.done }} />}
        {fold.running > 0 && <i data-state="running" style={{ flexGrow: fold.running }} />}
        {fold.failed > 0 && <i data-state="failed" style={{ flexGrow: fold.failed }} />}
        {pending > 0 && <i data-state="pending" style={{ flexGrow: pending }} />}
      </span>
    </button>
    {expanded && <div className="workflow-group-list nowheel nodrag nopan" style={{ gap: metrics.gap, maxHeight: rows * metrics.session + (rows - 1) * metrics.gap }}>
      {fold.ordered.map(session => <SessionRow key={session.sessionId} session={session} height={metrics.session} />)}
    </div>}
  </div>;
}

/** A session a step started: the logo of its provider, its title, its model, its duration and its tokens, its state. A click opens it. */
const SessionRow = memo(function SessionRow({ session, height }: { session: ScriptSession; height: number }) {
  const { t } = useShellLanguage();
  const links = useContext(SessionLinksContext);
  const known = links?.title(session.sessionId) ?? null;
  const title = known ?? session.title ?? session.sessionId.slice(0, 8);
  const state = graphState(session.status);
  const tokens = session.inputTokens !== null || session.outputTokens !== null ? (session.inputTokens ?? 0) + (session.outputTokens ?? 0) : null;
  const facts = [session.modelId, session.durationMs !== null ? formatDuration(session.durationMs) : null,
    tokens !== null ? t("{count} tokens", { count: compactTokens(tokens) }) : null].filter(Boolean).join(" · ");
  const content = <>
    <span className="workflow-session-logo"><ProviderIcon providerKey={session.providerKey} size={14} fallback="childSession" /></span>
    <span className="workflow-session-text"><strong>{title}</strong>{facts && <small>{facts}</small>}</span>
    <StateGlyph state={state} size={14} label={t(graphStateLabel(state))} />
  </>;
  const label = t("Open the session {title}", { title });
  return links && known !== null
    ? <button type="button" className="workflow-session nodrag nopan" data-state={state} data-session={session.sessionId} style={{ height }}
      title={session.message ?? label} aria-label={label} onClick={event => { event.stopPropagation(); links.open(session.sessionId); }}>{content}</button>
    : <div className="workflow-session" data-state={state} data-session={session.sessionId} style={{ height }} title={session.message ?? undefined}>{content}</div>;
});

// An `after`: a smooth curve of the state of the step it leads to; while that step runs, a glow and a current along it.
function FlowEdge({ sourceX, sourceY, targetX, targetY, sourcePosition, targetPosition, data }: EdgeProps<FlowEdgeType>) {
  const [path] = getBezierPath({ sourceX, sourceY, sourcePosition, targetX, targetY, targetPosition, curvature: 0.4 });
  return <g className="workflow-edge" data-state={data?.state} data-animated={data?.animated || undefined}>
    {data?.animated && <path className="workflow-edge-glow" d={path} />}
    <path className="workflow-edge-line" d={path} />
    {data?.animated && <path className="workflow-edge-current" d={path} />}
  </g>;
}
