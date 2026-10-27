import { Fragment, memo, useState } from "react";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { ScriptSessionBox } from "../scriptCard/ScriptSessionBox";
import { useShellLanguage } from "../shellLanguage";
import type { ScriptSession, ScriptStep } from "../timeline";
import { formatDuration } from "../toolCall";
import { foldSessions, graphState, type GraphNode, type GraphState, type RunGraph } from "./runGraph";

/** The label of a state, as a box says it. */
export const graphStateLabel = (state: GraphState) => state === "pending" ? "Pending" : state === "running" ? "Running" : state === "waiting" ? "Waiting"
  : state === "completed" ? "Done" : state === "failed" ? "Failed" : state === "interrupted" ? "Interrupted" : "Stopped";

function StateMark({ state }: { state: GraphState }) {
  return state === "running" ? <ActivitySpinner size={10} /> : state === "pending" ? <span className="run-graph-pending-dot" />
    : <AppIcon name={state === "completed" ? "check" : state === "failed" ? "error" : state === "waiting" ? "hand" : "minus"} size={11} />;
}

/**
 * The graph of a run: the steps its scripts declare, in columns from the start of the run (a step after every step it
 * runs after, steps that share their predecessors side by side), each with the state it reached and the sessions it
 * started; a step that holds more than six folds them into one group, which a click opens. `large` is the graph of the
 * tab of the run; the card draws it small. A narrow card stacks the columns: it never scrolls sideways.
 */
export const RunGraphView = memo(function RunGraphView({ graph, size = "small" }: { graph: RunGraph; size?: "small" | "large" }) {
  const { t } = useShellLanguage();
  return <div className="run-graph" data-size={size} role="group" aria-label={t("Graph of the run")}>
    <ol className="run-graph-columns">
      {graph.columns.map((column, index) => <Fragment key={column[0].id}>
        {index > 0 && <li className="run-graph-edge" aria-hidden="true"><AppIcon name="chevronRight" size={12} /></li>}
        <li className="run-graph-column"><ul>{column.map(node => <li key={node.id}><StepBox node={node} /></li>)}</ul></li>
      </Fragment>)}
    </ol>
    {graph.others.length > 0 && <div className="run-graph-others">
      <h5>{t("Other steps")}</h5>
      <ul>{graph.others.map(step => <OtherStep key={step.id} step={step} />)}</ul>
    </div>}
    {graph.loose.length > 0 && <ul className="script-run-sessions">{graph.loose.map(session => <li key={session.sessionId}><ScriptSessionBox session={session} /></li>)}</ul>}
  </div>;
});

function StepBox({ node }: { node: GraphNode }) {
  const { t } = useShellLanguage();
  const label = t(graphStateLabel(node.state));
  return <div className="run-graph-step" data-state={node.state} data-each={node.each || undefined} data-step={node.id}>
    <div className="run-graph-step-head" title={node.title === node.id ? `${node.id} · ${label}` : `${node.title} · ${node.id} · ${label}`}>
      <span className="run-graph-mark" role="img" aria-label={label}><StateMark state={node.state} /></span>
      <strong>{node.title}</strong>
      {node.each && <small className="run-graph-each" aria-label={t("For each item")}>×</small>}
      {node.durationMs !== null && node.state !== "running" && <small className="run-graph-duration">{formatDuration(node.durationMs)}</small>}
    </div>
    {node.sessions.length > 0 && <StepSessions sessions={node.sessions} />}
  </div>;
}

// The sessions of a step: one by one up to six, else one group that says how they are and lists them when opened.
function StepSessions({ sessions }: { sessions: readonly ScriptSession[] }) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const fold = foldSessions(sessions);
  if (!fold) return <ul className="run-graph-sessions">{sessions.map(session => <li key={session.sessionId}><ScriptSessionBox session={session} showStep={false} /></li>)}</ul>;
  const tone: GraphState = fold.running > 0 ? "running" : fold.failed > 0 ? "failed" : "completed";
  const summary = [t("{count} sessions", { count: fold.total }), t("{count} done", { count: fold.done }), fold.running > 0 ? t("{count} running", { count: fold.running }) : null,
    fold.failed > 0 ? t("{count} failed", { count: fold.failed }) : null].filter(Boolean).join(" · ");
  return <div className="run-graph-group" data-state={tone}>
    <button type="button" className="run-graph-group-head" aria-expanded={open} title={t(open ? "Hide the sessions" : "Show the sessions")}
      onClick={event => { event.stopPropagation(); setOpen(value => !value); }}>
      {fold.running > 0 ? <ActivitySpinner size={10} /> : <AppIcon name="childSession" size={12} />}
      <span className="run-graph-group-summary">{summary}</span>
      <AppIcon name={open ? "chevronDown" : "chevronRight"} size={12} />
    </button>
    {/* One dot per session, the order of the list: running and failed first. */}
    <span className="run-graph-dots" aria-hidden="true">{fold.ordered.map(session => <i key={session.sessionId} data-state={graphState(session.status)} />)}</span>
    {open && <ul className="run-graph-sessions">{fold.ordered.map(session => <li key={session.sessionId}><ScriptSessionBox session={session} showStep={false} /></li>)}</ul>}
  </div>;
}

function OtherStep({ step }: { step: ScriptStep }) {
  const { t } = useShellLanguage();
  const state = graphState(step.status);
  return <li className="run-graph-other" data-state={state} title={step.durationMs !== null ? formatDuration(step.durationMs) : undefined}>
    <span className="run-graph-mark" role="img" aria-label={t(graphStateLabel(state))}><StateMark state={state} /></span><span>{step.id}</span>
  </li>;
}
