// The points a session can be forked at or rewound to, and what each action does at one of them: what the
// window of a fork or a rewind lists and says, decided without the DOM.
import type { WorkspaceSessionTurn } from "#neoastra";
import { parseDelegatedMessage } from "../delegatedMessage";
import { parseJobResult } from "../jobResult";
import { promptProvenance, promptProvenanceIds } from "../promptProvenance";

export type BranchMode = "fork" | "rewind";
export type BranchTurn = WorkspaceSessionTurn;

/**
 * Who started a turn: the user, another agent session, the answer to a question an agent asked, a background job
 * that ended, the provider by itself (a background command of an agent CLI ended), or the host for another reason
 * its provenance names (a reminder, a review round, an automation...).
 */
export type TurnSender = "you" | "agent" | "answer" | "job" | "provider" | "host";

/** What the content id of the prompt of a turn the provider started by itself starts with, as the host writes it. */
export const providerPromptPrefix = "provider:";

const delegatedMarker = "[CodeAlta delegated-agent message]";
const jobMarker = "[CodeAlta background job]";

export function turnSender(turn: Pick<BranchTurn, "sourceSessionId" | "answersQuestion" | "prompt"> & Partial<Pick<BranchTurn, "startedByProvider" | "provenance">>): TurnSender {
  if (turn.startedByProvider) return "provider";
  // The provenance the host recorded the prompt with; the turns of a journal written before it was recorded have none.
  // It comes before the question a prompt answers: the review round that answers `alta review request` is a review.
  const provenance = promptProvenance(turn.provenance);
  if (turn.answersQuestion && (provenance === null || provenance === promptProvenanceIds.userAnswer)) return "answer";
  if (turn.prompt.startsWith(jobMarker)) return "job";
  if (turn.sourceSessionId || turn.prompt.startsWith(delegatedMarker)) return "agent";
  return provenance === null ? "you"
    : provenance === promptProvenanceIds.userAnswer ? "answer"
    : provenance === promptProvenanceIds.backgroundJob ? "job"
    : provenance === promptProvenanceIds.agentSession ? "agent"
    : provenance === promptProvenanceIds.provider ? "provider"
    : "host";
}

/** The text of a prompt as the timeline shows it: without the envelope of a message between agents or of a job result. */
export function turnText(turn: Pick<BranchTurn, "prompt">): string {
  const delegated = parseDelegatedMessage(turn.prompt);
  if (delegated) return delegated.body;
  const job = parseJobResult(turn.prompt);
  return job ? [job.title ?? job.command ?? job.jobId, job.result].join(" · ") : turn.prompt;
}

/**
 * The turns that are known after a page is read, in session order. A page read again replaces what it held
 * before: a turn that ran is then one that ended.
 */
export function mergeTurnPages(known: readonly BranchTurn[], page: readonly BranchTurn[]): BranchTurn[] {
  const byNumber = new Map(known.map(turn => [turn.number, turn]));
  for (const turn of page) byNumber.set(turn.number, turn);
  return [...byNumber.values()].sort((left, right) => left.number - right.number);
}

/**
 * The prompt a fork or a rewind hands back, as the composer takes it: the text the user wrote. The result of a
 * background job and a message of another agent are prompts of the host, not something to edit and send again.
 */
export function composerPrompt(prompt: string | null | undefined): string | null {
  return !prompt?.trim() || prompt.startsWith(jobMarker) || prompt.startsWith(delegatedMarker) ? null : prompt;
}

/** A row of the list: the end of the session, the start of a turn, or what loads the turns before the oldest one listed. */
export type BranchRow = Readonly<{ kind: "end" } | { kind: "turn"; turn: BranchTurn } | { kind: "more"; missing: number }>;

export const rowId = (row: BranchRow): string => row.kind === "turn" ? `turn:${row.turn.runId}` : row.kind;

/**
 * The rows of the list, the newest first: the end of the session, then the turns from the last to the first,
 * then the row that loads older turns when the session has more than were read. A filter keeps the turns whose
 * prompt holds every word of it, and leaves the end of the session out: it has no prompt.
 */
export function branchRows(turns: readonly BranchTurn[], total: number, options: Readonly<{ onlyMine: boolean; query: string }>): BranchRow[] {
  const words = options.query.toLowerCase().split(/\s+/u).filter(Boolean);
  const rows: BranchRow[] = words.length ? [] : [{ kind: "end" }];
  for (let index = turns.length - 1; index >= 0; index--) {
    const turn = turns[index];
    if (options.onlyMine && turnSender(turn) !== "you") continue;
    if (words.length) { const text = turnText(turn).toLowerCase(); if (!words.every(word => text.includes(word))) continue; }
    rows.push({ kind: "turn", turn });
  }
  if (turns.length < total) rows.push({ kind: "more", missing: total - turns.length });
  return rows;
}

/**
 * The turn a message of the timeline belongs to, by the journal offset of that message: the turn it started,
 * or, for a message sent while a turn ran, the turn that was running. Null while that turn is not among the
 * ones read.
 */
export function turnForOffset(turns: readonly BranchTurn[], offset: string): BranchTurn | null {
  if (!/^\d+$/u.test(offset)) return null;
  const at = BigInt(offset);
  let found: BranchTurn | null = null;
  for (const turn of turns) {
    if (turn.promptOffset === null || !/^\d+$/u.test(turn.promptOffset)) continue;
    if (BigInt(turn.promptOffset) <= at) found = turn; else break;
  }
  // Null when the oldest turn read starts after the message: its own turn is then on an older page.
  return found;
}

/** What a fork at a point starts with: the number of turns it copies, and the turn whose prompt goes to its composer. */
export type ForkConsequence = Readonly<{ kept: number; next: number | null }>;

/**
 * A fork before a turn copies the turns before it. A fork at the end copies every turn but the one that runs,
 * which no fork holds.
 */
export function forkConsequence(point: BranchTurn | null, total: number, runningTurn: number | null): ForkConsequence {
  if (point) return { kept: point.number - 1, next: point.number };
  return runningTurn === null ? { kept: total, next: null } : { kept: Math.max(0, runningTurn - 1), next: null };
}

/** What a rewind to a turn removes: that turn and every later one. */
export type RewindConsequence = Readonly<{ first: number; last: number; count: number; kept: number }>;

export function rewindConsequence(point: BranchTurn, total: number): RewindConsequence {
  const last = Math.max(total, point.number);
  return { first: point.number, last, count: last - point.number + 1, kept: point.number - 1 };
}

/** The number of the turn that runs, when it is among the ones read. */
export function runningTurnNumber(turns: readonly BranchTurn[], activeRunId: string | null): number | null {
  return turns.find(turn => turn.status === "running" || activeRunId !== null && turn.runId === activeRunId)?.number ?? null;
}

/**
 * Why a rewind cannot be made now, known before it is asked: no turn is chosen, the session works, a question
 * waits for its answer, something still runs in its background, or the session is read-only.
 */
export type RewindBlock = "end" | "read_only" | "running" | "question" | "background";

export function rewindBlock(point: BranchTurn | null, facts: Readonly<{ canMutate: boolean; running: boolean; question: boolean; background: number }>,
  activeRunId: string | null): RewindBlock | null {
  if (!facts.canMutate) return "read_only";
  if (!point) return "end";
  if (facts.running || activeRunId !== null || point.status === "running") return "running";
  if (facts.question) return "question";
  return facts.background > 0 ? "background" : null;
}

/** A row of the timeline as far as the actions of a fork or a rewind are concerned. */
export type BranchableItem = Readonly<{ key: string; category: string; eventType: string; delegated?: boolean; jobResult?: unknown; providerPrompt?: boolean }>;

/**
 * Whether a row of the timeline is a prompt the user wrote and the journal holds: the one a session can be
 * forked from or rewound to. A message of another agent, the result of a background job, the message of a turn
 * the provider started by itself, a prompt being sent and a live row are not.
 */
export function canBranchFrom(item: BranchableItem): boolean {
  return item.category === "user" && item.eventType === "contentCompleted" && !item.delegated && !item.jobResult && !item.providerPrompt
    && /^\d+$/u.test(item.key);
}

/**
 * The journal offsets of the user messages that start a turn, among the records of a history window: the
 * first user message of each run. A message sent while a turn ran (a steer) belongs to that turn and starts none.
 */
export function turnPromptKeys(entries: readonly Readonly<{ offset: string; eventType: string; kind: string | null; runId: string | null }>[]): ReadonlySet<string> {
  const runs = new Set<string>();
  const keys = new Set<string>();
  for (const entry of entries) {
    if (entry.eventType !== "contentCompleted" || entry.kind?.toLowerCase() !== "user" || !entry.runId || runs.has(entry.runId)) continue;
    runs.add(entry.runId);
    keys.add(entry.offset);
  }
  return keys;
}

/** The index of the row to select when the list changes: the row that was selected when it is still listed, else the nearest one above. */
export function keepSelection(rows: readonly BranchRow[], selected: string | null, fallback: number): number {
  const index = selected === null ? -1 : rows.findIndex(row => rowId(row) === selected);
  return index >= 0 ? index : Math.max(0, Math.min(fallback, rows.length - 1));
}

/** The row a window opens on: the turn it was asked for, else the end of the session for a fork and the last prompt for a rewind. */
export function initialSelection(rows: readonly BranchRow[], mode: BranchMode, runId: string | null): number {
  if (runId !== null) { const index = rows.findIndex(row => row.kind === "turn" && row.turn.runId === runId); if (index >= 0) return index; }
  if (mode === "fork") return 0;
  const first = rows.findIndex(row => row.kind === "turn");
  return first >= 0 ? first : 0;
}
