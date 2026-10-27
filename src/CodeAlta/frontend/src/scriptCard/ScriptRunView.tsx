import { memo, useContext } from "react";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { SessionLinksContext } from "../SessionReference";
import { useShellLanguage } from "../shellLanguage";
import type { ScriptRun, ScriptSession } from "../timeline";
import { formatDuration } from "../toolCall";
import { SaveAsScript, ScriptRunActionsContext } from "./SaveAsScript";

// The state of a step or a session, as the marks of the card show it.
function state(status: string): "running" | "completed" | "failed" {
  return status === "running" ? "running" : status === "completed" ? "completed" : "failed";
}

/**
 * The run of a script, under its tile: its steps in order, then a box for each session it started, which opens the
 * session. What the boxes say comes from the records of the run in the journal of the session. A run that ended offers
 * Save as script…, where the session can be asked.
 */
export const ScriptRunView = memo(function ScriptRunView({ run }: { run: ScriptRun }) {
  const { t } = useShellLanguage();
  const actions = useContext(ScriptRunActionsContext);
  const ended = run.status !== null && run.status !== "running" && run.runId !== "";
  if (run.steps.length === 0 && run.sessions.length === 0 && !(ended && actions)) return null;
  return <div className="script-run" role="group" aria-label={t("Steps and sessions of the script")}>
    {run.steps.length > 0 && <ol className="script-run-steps">
      {run.steps.map((step, index) => <li key={`${step.id}:${index}`} data-state={state(step.status)} title={step.durationMs !== null ? formatDuration(step.durationMs) : undefined}>
        {state(step.status) === "running" ? <ActivitySpinner size={9} /> : <span className="script-call-dot" aria-hidden="true" />}<span>{step.id}</span>
      </li>)}
    </ol>}
    {run.sessions.length > 0 && <ul className="script-run-sessions">
      {run.sessions.map(session => <li key={session.sessionId}><ScriptSessionBox session={session} /></li>)}
    </ul>}
    {ended && <div className="script-run-actions"><SaveAsScript runId={run.runId} /></div>}
  </div>;
});

function ScriptSessionBox({ session }: { session: ScriptSession }) {
  const { t } = useShellLanguage();
  const links = useContext(SessionLinksContext);
  const title = links?.title(session.sessionId) ?? session.title ?? session.sessionId.slice(0, 8);
  const tokens = session.inputTokens !== null || session.outputTokens !== null ? (session.inputTokens ?? 0) + (session.outputTokens ?? 0) : null;
  const facts = [session.stepId, session.durationMs !== null ? formatDuration(session.durationMs) : null,
    tokens !== null ? t("{count} tokens", { count: tokens.toLocaleString() }) : null].filter(Boolean).join(" · ");
  const content = <>
    <span className="script-session-mark">{state(session.status) === "running" ? <ActivitySpinner size={10} /> : <AppIcon name="childSession" size={12} />}</span>
    <span className="script-session-text"><strong>{title}</strong>{facts && <small>{facts}</small>}</span>
  </>;
  const label = t("Open the session {title}", { title });
  return links && links.title(session.sessionId) !== null
    ? <button type="button" className="script-session" data-state={state(session.status)} title={session.message ?? label} aria-label={label}
      onClick={event => { event.stopPropagation(); links.open(session.sessionId); }}>{content}</button>
    : <span className="script-session" data-state={state(session.status)} title={session.message ?? undefined}>{content}</span>;
}
