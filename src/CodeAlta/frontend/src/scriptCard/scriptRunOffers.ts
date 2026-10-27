import type { ScriptRun } from "../timeline";

/** The state of a step or a session, as the marks of the card show it. */
export type ScriptMarkState = "running" | "waiting" | "completed" | "failed" | "interrupted";

export function scriptMarkState(status: string): ScriptMarkState {
  return status === "running" || status === "waiting" || status === "completed" || status === "interrupted" ? status : "failed";
}

/**
 * Where the user answers what a run waits for, in the pane of its session: the questions of `ask()` take the place of the
 * prompt (or wait in the ask panel), the approvals wait in the panel of permissions.
 */
export function scriptWaitTarget(kind: "question" | "approval"): string {
  return kind === "question" ? ".ask-form, .ask-panel" : ".command-permission-panel";
}

/**
 * What the card of a run offers, once it no longer runs: Resume for a run CodeAlta ended during, Rerun with recorded results
 * for one that ended, Rerun from this step on each step it recorded, and Save as script.
 */
export type ScriptRunOffers = Readonly<{ resume: boolean; rerun: boolean; rerunFromStep: boolean; saveAsScript: boolean }>;

const none: ScriptRunOffers = Object.freeze({ resume: false, rerun: false, rerunFromStep: false, saveAsScript: false });

export function scriptRunOffers(run: ScriptRun): ScriptRunOffers {
  if (run.runId === "" || run.status === null || run.status === "running") return none;
  const interrupted = run.status === "interrupted";
  return { resume: interrupted, rerun: !interrupted, rerunFromStep: run.steps.some(step => step.key !== null), saveAsScript: true };
}
