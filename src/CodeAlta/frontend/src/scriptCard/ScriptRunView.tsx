import { memo, useContext, useState } from "react";
import { Button } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import type { ScriptRun } from "../timeline";
import { OpenRunContext } from "../workflows/openRun";
import { hasWorkflowBlock } from "../workflows/runFlow";
import { WorkflowBlock } from "../workflows/WorkflowBlock";
import { SaveAsScript, ScriptRunActionsContext } from "./SaveAsScript";
import { ScriptWaitLine } from "./ScriptWaitLine";
import { scriptRunOffers, wakeTime } from "./scriptRunOffers";

type Rerun = Readonly<{ from?: string; all?: boolean }>;

/**
 * The run of a script. A run that ran steps or started sessions is drawn as its workflow block (its graph, its state and its
 * actions); the timeline shows that block outside the tool calls, and the window of the call shows it too. Another run
 * keeps a compact card under the tile of its call: what it waits for, and, once it ended, where the session can be asked,
 * Resume (a run CodeAlta ended during) or Rerun with recorded results, and Save as script…. Open run opens its tab.
 */
export const ScriptRunView = memo(function ScriptRunView({ run, title = null }: { run: ScriptRun; title?: string | null }) {
  const { t } = useShellLanguage();
  const actions = useContext(ScriptRunActionsContext);
  const openRun = useContext(OpenRunContext);
  const offers = actions ? scriptRunOffers(run) : null;
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  if (hasWorkflowBlock(run)) return <WorkflowBlock run={run} title={title} />;
  if (!offers?.saveAsScript && !run.waiting && !(openRun && run.runId)) return null;
  async function rerun(how: Rerun) {
    setBusy(true);
    setProblem(null);
    const failure = await actions!.resume(run.runId, how);
    setBusy(false);
    if (failure !== null) setProblem(failure || t("The session could not be asked."));
  }
  return <div className="script-run" role="group" aria-label={t("Steps and sessions of the script")}>
    {run.waiting && <ScriptWaitLine wait={run.waiting} />}
    {openRun && run.runId && !offers?.saveAsScript && <div className="script-run-actions"><OpenRunButton run={run} title={title} /></div>}
    {offers?.saveAsScript && <div className="script-run-actions">
      {openRun && <OpenRunButton run={run} title={title} />}
      {offers.resume && wakeTime(run.resumesAt) && <span className="script-run-resumes" role="status">
        <AppIcon name="timer" size={12} />{t("Resumes by itself at {time}", { time: wakeTime(run.resumesAt)! })}</span>}
      {offers.resume && <Button size="small" variant="minimal" icon={<AppIcon name="play" size={13} />} text={t("Resume")} loading={busy}
        title={t("Run the script again: the steps it completed give their recorded results.")} onClick={event => { event.stopPropagation(); void rerun({}); }} />}
      {offers.rerun && <Button size="small" variant="minimal" icon={<AppIcon name="repeat" size={13} />} text={t("Rerun with recorded results")} loading={busy}
        title={t("Run the script again: every step it recorded gives its result, only the rest of the code runs.")} onClick={event => { event.stopPropagation(); void rerun({ all: true }); }} />}
      <SaveAsScript runId={run.runId} />
      {problem && <span role="alert" className="script-run-problem">{problem}</span>}
    </div>}
  </div>;
});

// Open run: the tab of the run, with its graph large, its sessions, its timeline, its script and its result.
function OpenRunButton({ run, title }: { run: ScriptRun; title: string | null }) {
  const { t } = useShellLanguage();
  const openRun = useContext(OpenRunContext);
  return <Button size="small" variant="minimal" icon={<AppIcon name="expand" size={13} />} text={t("Open run")} title={t("Open the run in a tab: its graph, its sessions, its timeline and its script.")}
    onClick={event => { event.stopPropagation(); openRun?.({ runId: run.runId, title }); }} />;
}
