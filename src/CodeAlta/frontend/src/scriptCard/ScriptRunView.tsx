import { memo, useContext, useState } from "react";
import { Button, Menu, MenuItem, PopoverNext } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { SessionLinksContext } from "../SessionReference";
import { useShellLanguage } from "../shellLanguage";
import type { ScriptRun, ScriptSession, ScriptStep, ScriptWait } from "../timeline";
import { formatDuration } from "../toolCall";
import { SaveAsScript, ScriptRunActionsContext } from "./SaveAsScript";
import { scriptMarkState as state, scriptRunOffers, scriptWaitTarget } from "./scriptRunOffers";

type Rerun = Readonly<{ from?: string; all?: boolean }>;

/**
 * The run of a script, under its tile: its steps in order, then a box for each session it started, which opens the
 * session. What the boxes say comes from the records of the run in the journal of the session. A run that ended offers,
 * where the session can be asked, Rerun with recorded results (Resume for a run CodeAlta ended during), Rerun from a step
 * on the menu of its mark, and Save as script….
 */
export const ScriptRunView = memo(function ScriptRunView({ run }: { run: ScriptRun }) {
  const { t } = useShellLanguage();
  const actions = useContext(ScriptRunActionsContext);
  const offers = actions ? scriptRunOffers(run) : null;
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  if (run.steps.length === 0 && run.sessions.length === 0 && !offers?.saveAsScript && !run.waiting) return null;
  async function rerun(how: Rerun) {
    setBusy(true);
    setProblem(null);
    const failure = await actions!.resume(run.runId, how);
    setBusy(false);
    if (failure !== null) setProblem(failure || t("The session could not be asked."));
  }
  return <div className="script-run" role="group" aria-label={t("Steps and sessions of the script")}>
    {run.waiting && <ScriptWaitLine wait={run.waiting} />}
    {run.steps.length > 0 && <ol className="script-run-steps">
      {run.steps.map((step, index) => <li key={`${step.id}:${index}`} data-state={state(step.status)} data-replayed={step.replayed || undefined}
        title={stepTitle(step, t("Recorded result"))}>
        {offers?.rerunFromStep && step.key
          // What is done in the menu stays there: the tile of the call it is under does not react to it.
          ? <PopoverNext placement="bottom-start" content={<Menu onClick={event => event.stopPropagation()} onPointerDown={event => event.stopPropagation()}>
            <MenuItem icon={<AppIcon name="rewind" size={14} />} text={t("Rerun from this step")} disabled={busy} onClick={() => void rerun({ from: step.key! })} />
          </Menu>}>
            <button type="button" className="script-step-button" aria-haspopup="menu" onClick={event => event.stopPropagation()}><StepMark step={step} /></button>
          </PopoverNext>
          : <StepMark step={step} />}
      </li>)}
    </ol>}
    {run.sessions.length > 0 && <ul className="script-run-sessions">
      {run.sessions.map(session => <li key={session.sessionId}><ScriptSessionBox session={session} /></li>)}
    </ul>}
    {offers?.saveAsScript && <div className="script-run-actions">
      {offers.resume && <Button size="small" variant="minimal" icon={<AppIcon name="play" size={13} />} text={t("Resume")} loading={busy}
        title={t("Run the script again: the steps it completed give their recorded results.")} onClick={event => { event.stopPropagation(); void rerun({}); }} />}
      {offers.rerun && <Button size="small" variant="minimal" icon={<AppIcon name="repeat" size={13} />} text={t("Rerun with recorded results")} loading={busy}
        title={t("Run the script again: every step it recorded gives its result, only the rest of the code runs.")} onClick={event => { event.stopPropagation(); void rerun({ all: true }); }} />}
      <SaveAsScript runId={run.runId} />
      {problem && <span role="alert" className="script-run-problem">{problem}</span>}
    </div>}
  </div>;
});

function StepMark({ step }: { step: ScriptStep }) {
  const mark = state(step.status);
  return <>{mark === "running" ? <ActivitySpinner size={9} /> : mark === "waiting" ? <AppIcon name="hand" size={11} /> : <span className="script-call-dot" aria-hidden="true" />}
    <span>{step.id}</span></>;
}

// The run waits for the user: the line says for what, and Open shows where it is answered in the pane of the session.
function ScriptWaitLine({ wait }: { wait: ScriptWait }) {
  const { t } = useShellLanguage();
  const label = wait.kind === "question" ? t("Waits for your answer") : t("Waits for your approval");
  return <div className="script-run-waiting" role="status">
    <AppIcon name={wait.kind === "question" ? "ask" : "shield"} size={13} />
    <span className="script-run-waiting-text"><strong>{label}</strong>{wait.kind === "question" && wait.text && <span>{wait.text}</span>}</span>
    <Button size="small" variant="minimal" text={t("Open")} onClick={event => {
      event.stopPropagation();
      const target = event.currentTarget.closest(".session-workspace")?.querySelector<HTMLElement>(scriptWaitTarget(wait.kind));
      target?.scrollIntoView({ block: "nearest" });
      target?.querySelector<HTMLElement>("textarea, input, button")?.focus();
    }} />
  </div>;
}

// A replayed step says so; another one says how long it took.
function stepTitle(step: ScriptStep, recorded: string): string | undefined {
  const parts = [step.replayed ? recorded : null, step.durationMs !== null && !step.replayed ? formatDuration(step.durationMs) : null].filter(Boolean);
  return parts.length > 0 ? parts.join(" · ") : undefined;
}

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
