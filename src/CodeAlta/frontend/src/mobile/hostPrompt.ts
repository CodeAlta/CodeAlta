// What a user message of the timeline is when nobody typed it: the host sent it. The desktop names such a message by the
// provenance the journal records and by its envelope (promptProvenance.ts, jobResult.ts, delegatedMessage.ts,
// workflows/scriptResult.ts); the phone shows each as a compact card of its own, never as "You".
import { parseDelegatedMessage } from "../delegatedMessage";
import { parseJobResult, type JobResult } from "../jobResult";
import { promptProvenance, promptProvenanceIds, promptProvenanceTitle } from "../promptProvenance";
import { parseWorkflowResult, type WorkflowResult } from "../workflows/scriptResult";
import type { TimelineEvent } from "./contract";
import type { TimelineIconName } from "./timelineIcons";
import type { WorkflowRun } from "./workflowRun";

/** How a background job ended, for the chip of its card: its outcome, its exit code and how long it ran. */
export type JobOutcome = Readonly<{ state: "succeeded" | "failed" | "stopped"; label: string; exitCode: number | null; duration: string | null }>;

/**
 * A message of the host. `job`: the end of a background job; `agent`: a message of another agent session (`sourceSessionId`,
 * when known, opens it); `workflow`: the result of a script that ended in the background, whose body is what the script
 * presented (its value is data for the agent, shown apart), with its run when its records are there; `host`: any other provenance (a
 * reminder, a review round, a model fallback, an automation...), titled by it.
 */
export type HostPrompt =
  | Readonly<{ kind: "job"; title: string; icon: TimelineIconName; job: JobResult; outcome: JobOutcome }>
  | Readonly<{ kind: "agent"; title: string; icon: TimelineIconName; sourceSessionId: string | null; label: string | null; body: string }>
  | Readonly<{ kind: "workflow"; title: string; icon: TimelineIconName; result: WorkflowResult; body: string; run?: WorkflowRun | null }>
  | Readonly<{ kind: "host"; title: string; icon: TimelineIconName; provenance: string; body: string }>;

// The icons of the provenances the phone knows; another one gets the icon of the host.
const icons: Readonly<Record<string, TimelineIconName>> = Object.freeze({
  [promptProvenanceIds.userAnswer]: "ask",
  [promptProvenanceIds.userReview]: "eye",
  [promptProvenanceIds.reminder]: "timer",
  [promptProvenanceIds.backgroundJob]: "terminal",
  [promptProvenanceIds.agentSession]: "branch",
  [promptProvenanceIds.automation]: "zap",
  [promptProvenanceIds.modelFallback]: "model",
  [promptProvenanceIds.plugin]: "plugin",
  [promptProvenanceIds.provider]: "terminal",
  [promptProvenanceIds.workflow]: "code",
});

/** The title of a provenance, in English as the desktop's source text: its words for one this version does not know. */
export function provenanceTitle(id: string): string {
  const title = promptProvenanceTitle(id);
  return "key" in title ? title.key : title.text;
}

/** The icon of a provenance. */
export const provenanceIcon = (id: string): TimelineIconName => Object.hasOwn(icons, id) ? icons[id] : "info";

/**
 * Reads what a user message is, as the desktop's timeline does: the envelope of a message between agents, or a message
 * the host recorded with the session that sent it; then the end of a background job; then the result of a script of
 * the background; then any other provenance. Null for a prompt the user typed.
 */
export function hostPromptOf(event: Pick<TimelineEvent, "kind" | "text" | "provenance" | "sourceSessionId">): HostPrompt | null {
  if (event.kind !== "user.message") return null;
  const text = event.text ?? "";
  const provenance = promptProvenance(event.provenance);
  const delegated = parseDelegatedMessage(text) ?? (event.sourceSessionId ? { sourceSessionId: event.sourceSessionId, kind: "prompt", body: text } : null);
  if (delegated) {
    const sourceSessionId = delegated.sourceSessionId ?? event.sourceSessionId ?? null;
    return { kind: "agent", title: "Agent message", icon: "branch", sourceSessionId, label: delegated.kind === "prompt" ? null : words(delegated.kind), body: delegated.body };
  }
  const job = parseJobResult(text);
  if (job) return { kind: "job", title: "Background job", icon: "terminal", job, outcome: jobOutcome(job) };
  const workflow = provenance === promptProvenanceIds.workflow ? parseWorkflowResult(text) : null;
  if (workflow) return { kind: "workflow", title: "Workflow", icon: "code", result: workflow, body: workflow.presented ?? "" };
  if (provenance) return { kind: "host", title: provenanceTitle(provenance), icon: provenanceIcon(provenance), provenance, body: text };
  return null;
}

/**
 * The outcome of a job from the words of the host: "succeeded (exit code 0) after 26 s", "failed (exit code 2) after
 * 1 min 3 s", "timed out after 10 min", "stopped after 4 s".
 */
export function jobOutcome(job: Pick<JobResult, "result" | "phase">): JobOutcome {
  const code = /\(exit code (-?\d+)\)/u.exec(job.result);
  const after = /\bafter (.+)$/u.exec(job.result)?.[1]?.trim() ?? null;
  const state = job.phase === "completed" ? "succeeded" : job.phase === "canceled" ? "stopped" : "failed";
  const label = job.result.startsWith("timed out") ? "timed out" : state;
  return { state, label, exitCode: code ? Number(code[1]) : null, duration: after };
}

/** The words of an identifier: `child-session-update` is `Child session update`. */
function words(id: string): string {
  const text = id.split(/[-_\s]+/u).filter(Boolean).join(" ");
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/** A line of plain text from Markdown, for the preview of a folded card: no marks, no blocks of code. */
export function previewLine(markdown: string, length = 160): string {
  let code = false;
  const kept: string[] = [];
  for (const raw of markdown.split(/\r?\n/u)) {
    const line = raw.trim();
    if (/^(```|~~~)/u.test(line)) { code = !code; continue; }
    if (code || !line) continue;
    kept.push(line.replace(/^(#{1,6}\s+|>\s*|[-*+]\s+|\d+[.)]\s+)/u, "").replace(/!?\[([^\]]*)\]\([^)]*\)/gu, "$1").replace(/[*_`~]+/gu, ""));
    if (kept.join(" ").length >= length) break;
  }
  const text = kept.join(" ").replace(/\s+/gu, " ").trim();
  return text.length > length ? `${text.slice(0, length - 1)}…` : text;
}
