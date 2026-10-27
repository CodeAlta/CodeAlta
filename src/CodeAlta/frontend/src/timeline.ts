import type { HistoryResponse } from "#neoastra";
import { parseDelegatedMessage } from "./delegatedMessage";
import { parseJobResult, type JobResult } from "./jobResult";
import { promptProvenance, promptProvenanceIcon, promptProvenanceIds, promptProvenanceTitle } from "./promptProvenance";
import type { IconName } from "./AppIcon";
import { compactionDetailsMarkdown, splitCheckpointSummary } from "./compactionDetails";
import { fileChangesMarkdown, projectFileChanges, type FileChanges } from "./fileChanges";
import { projectTimelineImages, type TimelineImage } from "./timelineImages";
import { projectTimelineAttachments, type TimelineAttachment } from "./timelineAttachments";
import { promptRow, promptTokens, type PromptRow } from "./systemPrompt";

export type HistoryEntry = HistoryResponse["entries"][number];

export type TimelineItem = Readonly<{
  key: string;
  eventType: string;
  category: "user" | "assistant" | "reasoning" | "tool" | "file" | "image" | "status" | "prompt" | "plan" | "notes" | "error" | "plugin";
  icon: IconName;
  title: string;
  subtitle: string | null;
  timestamp: string;
  markdown: string | null;
  summary: string | null;
  summaryIsCode: boolean;
  detailMarkdown: string | null;
  details: string | null;
  detailsLabel: string;
  metadata: ReadonlyArray<string>;
  truncated: boolean;
  bodyOmitted: boolean;
  copyMarkdown: string | null;
  /** An HTML fragment a plugin gave for its card: shown instead of the summary, after sanitizing. */
  html?: string | null;
  /** The module of the plugin that draws the HTML of a card, as a path of the application's origin, and why it is not served when it is not. */
  script?: string | null;
  scriptProblem?: string | null;
  /** The detail sections of a card that has an HTML section, in order: each is an HTML fragment or Markdown. */
  detailSections?: ReadonlyArray<Readonly<{ header: string; html: string | null; markdown: string | null }>>;
  /** The plugin a card comes from, for the commands its fragments name. */
  pluginKey?: string;
  fileChanges?: FileChanges;
  /** The images attached to a user message, or the ones a tool gave the model; their content is read by index. */
  images?: ReadonlyArray<TimelineImage>;
  /** The files attached to a user message, by name and media type. */
  attachments?: ReadonlyArray<TimelineAttachment>;
  /** The identity of the Send of a user message, by which the composer recognizes the prompt it sent. */
  clientRequestId?: string;
  /** What names a tool call: its row, its details and its live output are found by it. */
  toolCall?: ToolCallIdentity;
  toolPhase?: string;
  toolOutput?: string | null;
  toolOutputLines?: number;
  toolOutputBytes?: number | null;
  toolFields?: NonNullable<HistoryEntry["tool"]>["fields"];
  /** The lines an edit added to and removed from files, when the record of the call has its diff. */
  toolChanges?: Readonly<{ added: number; removed: number }>;
  /** The exit code of a command, when its record has one. */
  toolExitCode?: number | null;
  /** The note the sandbox of the session added after a command (`[sandbox: <profile>] …`), when it added one. */
  toolSandbox?: string | null;
  /** The MCP server whose view (an MCP App) the call has, when it has one: the row shows it. */
  toolApp?: Readonly<{ server: string }>;
  /** True for a prompt another agent session delivered, shown without its routing envelope. */
  delegated?: boolean;
  /** The session a delegated prompt comes from, when it is known: the row names it by its title. */
  sourceSessionId?: string;
  /** The recorded result of a background job, shown as a compact card with its facts and output in a window. */
  jobResult?: JobResult;
  /** True for the message of a turn the provider started by itself: nobody wrote it, and it is not a prompt to continue from. */
  providerPrompt?: boolean;
  /**
   * The provenance of a user message the host sent (a reminder, a review round, the answer to a question...), as the
   * journal records it: the row is titled by it instead of "You". Absent for a prompt the user typed, and for the
   * records written before it was recorded, which the envelope of a message or the session that sent it still tell.
   */
  provenance?: string;
  /** The provider of an error that says it has no Claude Code CLI to run: the card proposes its download. */
  missingCliProvider?: string;
  /** Where a prompt made outside CodeAlta was made (in VS Code, in a terminal), on the conversation its provider keeps. */
  madeElsewhere?: ExternalOrigin;
  /** True for a prompt its run never gave the provider: the conversation changed while the run waited for it. */
  notSent?: true;
  /** What a system prompt record says: its row is one line, and its texts are read by its window. */
  prompt?: PromptRow;
  /** The calls the script of a tool call made (Code Mode's `exec`), in their order: shown inside its row. */
  scriptCalls?: ReadonlyArray<ScriptCall>;
  /** The title of the script of a tool call, from the `// @exec: {"title": …}` line of its code. */
  scriptTitle?: string;
  /** The run of the script of a tool call, as its records in the journal tell it: its steps and the sessions it started. */
  scriptRun?: ScriptRun;
}>;

/**
 * A run of a script of Code Mode, from its records (`workflow` entries of the journal). `declared` lists the steps its
 * scripts declare (`meta.steps`), which its graph draws from the start; `startedAt` is the time of its first record.
 */
export type ScriptRun = Readonly<{ runId: string; status: string | null; steps: ReadonlyArray<ScriptStep>; sessions: ReadonlyArray<ScriptSession>;
  waiting?: ScriptWait | null; resumesAt?: string | null; declared?: ReadonlyArray<ScriptDeclaredStep>; startedAt?: string | null; endedAt?: string | null;
  /** The title its start gives it, when it has one. */
  title?: string | null }>;

/** A step a script of a run declares: `after` the steps it runs after (null: the one declared before it; empty: the start), `each` a fan-out. */
export type ScriptDeclaredStep = Readonly<{ id: string; title: string; after: readonly string[] | null; each: boolean }>;

/** A record of a run as the card reads it: a `workflow` row of the history, or a record the tab of the run reads from the host. */
export type ScriptRunRecord = Readonly<{ kind?: string | null; activityId?: string | null; phase?: string | null; text?: string | null; details?: string | null;
  name?: string | null; timestamp?: string | null }>;

/**
 * What a running script waits for: the answer of an `ask()`, the approval of one of its calls, or the end of a `sleep()`
 * (`until`, its wake time). A run CodeAlta ended during a sleep says in `resumesAt` when it resumes by itself.
 */
export type ScriptWait = Readonly<{ kind: "question" | "approval" | "sleep"; stepId: string | null; text: string | null; until?: string | null }>;

/**
 * A step of a run: its state, and how long it took once ended. `key` is what a rerun from it names (`merge#1`), once ended;
 * `replayed` says it gave the result a previous run recorded instead of running.
 */
export type ScriptStep = Readonly<{ id: string; status: string; durationMs: number | null; key: string | null; replayed: boolean;
  startedAt?: string | null; endedAt?: string | null }>;

/** A session a run started: its state, and what it spent once ended; the provider and the model it runs with, when its record says them. */
export type ScriptSession = Readonly<{ sessionId: string; title: string | null; stepId: string | null; status: string; durationMs: number | null;
  inputTokens: number | null; outputTokens: number | null; message: string | null; startedAt?: string | null; endedAt?: string | null;
  providerKey?: string | null; modelId?: string | null }>;

/** A call that the script of a tool call made: a line of the card of that call. */
export type ScriptCall = Readonly<{ key: string; activityId: string; name: string; phase: string | null; startedAt: string | null; endedAt: string | null;
  stepId: string | null; error: string | null }>;

/**
 * The identity of a tool call and where its records are. `offset` is the journal offset of its newest
 * activity record and `outputOffset` the one of its output record; both are null for a call the journal has
 * not shown yet. `startedAt` and `endedAt` are the times of its first record and of the record that ended it,
 * when the loaded window has them.
 */
export type ToolCallIdentity = Readonly<{ providerId: string; runId: string | null; activityId: string; kind: string; name: string | null;
  offset: string | null; outputOffset: string | null; startedAt: string | null; endedAt: string | null;
  /** The images of the output record: they are read at `outputOffset`. */
  images?: ReadonlyArray<TimelineImage> }>;

type JsonObject = Record<string, unknown>;

/** Where a turn made outside CodeAlta was made, as the content identifier of its prompt names it. */
export type ExternalOrigin = "vscode" | "terminal" | "other" | "unknown";

/** The subtitle of a prompt made outside CodeAlta, by where it was made: an English key of the translations. */
export const externalOriginSubtitles = Object.freeze({
  vscode: "in VS Code", terminal: "in a terminal", other: "in another application", unknown: "outside CodeAlta",
} as const satisfies Record<ExternalOrigin, string>);

/**
 * The kind of the error of a run that never gave its prompt to the provider: it waited for another program to release
 * the conversation (a Claude Code conversation open in VS Code), and that program added turns to it meanwhile.
 */
export const conversationChangedErrorKind = "ConversationChangedWhileWaiting";

/** The subtitle of the prompt of such a run: an English key of the translations. */
export const notSentSubtitle = "Not sent: the conversation changed while waiting";

// The runs of a window that never gave their prompt to the provider.
const unsentRuns = (entries: HistoryResponse["entries"]) => new Set(entries
  .filter(entry => entry.eventType === "error" && entry.kind === conversationChangedErrorKind && entry.runId)
  .map(entry => entry.runId!));

const notSentItems = new WeakMap<TimelineItem, TimelineItem>();
function notSentItem(item: TimelineItem): TimelineItem {
  let shown = notSentItems.get(item);
  if (!shown) notSentItems.set(item, shown = { ...item, notSent: true, subtitle: notSentSubtitle });
  return shown;
}

/** The prompt of a run that was never sent, to give back to the composer of its session. */
export type UnsentPrompt = Readonly<{ runId: string; offset: string; text: string }>;

/**
 * The prompt to give back to the composer of a session: the last prompt of the window, when its run ended without
 * giving it to the provider because the conversation changed while it waited, and nothing was sent after it. Null
 * otherwise, also when the window holds the prompt cut (it is never given back in part) or the host sent it.
 */
export function unsentPrompt(entries: HistoryResponse["entries"]): UnsentPrompt | null {
  const unsent = unsentRuns(entries);
  if (unsent.size === 0) return null;
  for (let index = entries.length - 1; index >= 0; index--) {
    const entry = entries[index];
    if (entry.eventType !== "contentCompleted" || entry.kind?.toLowerCase() !== "user") continue;
    if (!entry.runId || !unsent.has(entry.runId) || entry.textTruncated || entry.bodyOmitted || !entry.text?.trim()) return null;
    // A prompt the host sent (a reminder, a review round, a message of another session...) is not the user's to send again.
    if (promptProvenance(entry.provenance) !== null || entry.sourceSessionId) return null;
    return { runId: entry.runId, offset: entry.offset, text: entry.text };
  }
  return null;
}

/** Reads where a prompt was made outside CodeAlta from its content identifier (`external:<where>:<id>`), or null. */
export function externalOrigin(contentId: string | null | undefined): ExternalOrigin | null {
  const match = /^external:(vscode|terminal|other|unknown):/u.exec(contentId ?? "");
  return match ? match[1] as ExternalOrigin : null;
}

// A journal record always presents the same way, and a window keeps its record objects when it grows or is
// refreshed. Remembering the item of a record keeps a long timeline cheap to rebuild and lets its rows
// see that nothing changed.
const presented = new WeakMap<HistoryEntry, TimelineItem>();
const folded = new WeakMap<HistoryEntry, { first: HistoryEntry; output: HistoryEntry | undefined; scriptCalls: ReadonlyArray<ScriptCall> | undefined;
  scriptRun: ScriptRun | undefined; item: TimelineItem }>();
function presentedItem(entry: HistoryEntry): TimelineItem {
  let item = presented.get(entry);
  if (!item) presented.set(entry, item = toTimelineItem(entry, false));
  return item;
}

export function buildTimelineItems(entries: HistoryResponse["entries"]): TimelineItem[] {
  const unsent = unsentRuns(entries);
  const completed = new Set(entries
    .filter(entry => entry.eventType === "contentCompleted" && entry.contentId)
    .map(entry => contentKey(entry)));
  // The records of one call (requested, started, ended) make one row: its newest record, where its first one is.
  const calls = new Map<string, { first: HistoryEntry; last: HistoryEntry }>();
  for (const entry of entries) {
    if (entry.eventType !== "activity" || !entry.activityId) continue;
    const key = activityKey(entry), call = calls.get(key);
    if (!call) calls.set(key, { first: entry, last: entry });
    // A record that ended the call is not replaced by a late report of its start.
    else if (!isTerminalPhase(call.last.phase) || isTerminalPhase(entry.phase)) call.last = entry;
  }
  // The calls a script made are lines of the card of the call that ran it, not rows of their own.
  const children = scriptChildren(calls);
  const runRecords = scriptRunRecords(entries);
  const deltas = new Map<string, TimelineItem>();
  const outputs = new Map<string, HistoryEntry>();
  for (const entry of entries) {
    if (entry.eventType === "contentCompleted" && isToolOutput(entry.kind) && entry.parentActivityId && entry.tool)
      outputs.set(parentActivityKey(entry), entry);
  }
  const result: TimelineItem[] = [];

  for (const entry of entries) {
    // Raw provider records are persistence/runtime plumbing duplicated by typed content and
    // activity events. The TUI intentionally keeps them out of its visual timeline too.
    // The records of a run of a script describe the card of its exec call, not rows of their own.
    if (entry.eventType === "raw" || entry.eventType === "notes" || entry.eventType === "workflow") continue;
    // These update the TUI's status/usage surfaces, not its conversation timeline.
    if (entry.eventType === "sessionUpdate" && !["warning", "reconnecting", "modelchanged", "providerfallback", "compactionstarted", "compactioncompleted", "diffupdated"].includes(entry.kind?.toLowerCase() ?? "")) continue;
    if ((entry.eventType === "contentCompleted" || entry.eventType === "contentDelta") &&
        isToolOutput(entry.kind) && entry.parentActivityId && calls.has(parentActivityKey(entry))) {
      // The text of an output is shown on the tile of its call; the images the model was given get a card.
      if (entry.eventType === "contentCompleted" && entry.images) { const card = presentedItem(entry); if (card.category === "image") result.push(card); }
      continue;
    }

    if (entry.eventType === "contentDelta" && entry.contentId) {
      const key = contentKey(entry);
      if (completed.has(key)) continue;
      const existing = deltas.get(key);
      if (existing) {
        const markdown = `${existing.markdown ?? ""}${entry.text ?? ""}`;
        const updated = { ...existing, markdown, copyMarkdown: markdown, timestamp: entry.timestamp,
          truncated: existing.truncated || entry.textTruncated || entry.detailsTruncated };
        deltas.set(key, updated);
        const index = result.indexOf(existing);
        if (index >= 0) result[index] = updated;
      } else {
        const item = toTimelineItem(entry, true);
        deltas.set(key, item);
        result.push(item);
      }
      continue;
    }
    const call = entry.eventType === "activity" && entry.activityId ? calls.get(activityKey(entry)) : undefined;
    if (!call) {
      const item = presentedItem(entry);
      result.push(item.category === "user" && entry.runId && unsent.has(entry.runId) ? notSentItem(item) : item);
      continue;
    }
    if (children.nested.has(call.first)) continue;
    if (entry === call.first) result.push(callItem(call.first, call.last, outputs.get(activityKey(entry)), children.of.get(activityKey(entry)),
      runRecords.get(JSON.stringify([entry.sessionId, entry.activityId]))));
  }
  return result.filter(item => item.category !== "reasoning" || !!item.markdown?.trim());
}

// The row of a call: what its newest record says, at the time of its first one, with the output record of the
// call when there is one. A typed completed output belongs to this exact session/provider/run/activity; a
// bounded preview or retained streaming deltas are never counted as a total.
function callItem(first: HistoryEntry, last: HistoryEntry, output: HistoryEntry | undefined, scriptCalls?: ReadonlyArray<ScriptCall>,
  runRecords?: ReadonlyArray<HistoryEntry>): TimelineItem {
  const scriptRun = runRecords ? scriptRunOf(runRecords) : undefined;
  const known = folded.get(last);
  if (known && known.first === first && known.output === output && sameScriptCalls(known.scriptCalls, scriptCalls) && known.scriptRun === scriptRun) return known.item;
  const base = presentedItem(last);
  const attached = output?.tool ? output : undefined;
  const title = scriptTitleOf(first, last);
  // A script reads by its title, under the name of its tool.
  const item: TimelineItem = { ...base, timestamp: first.timestamp, ...scriptCalls ? { scriptCalls } : {}, ...scriptRun ? { scriptRun } : {},
    ...title ? { scriptTitle: title, summary: title, summaryIsCode: false } : {},
    toolCall: base.toolCall && { ...base.toolCall, outputOffset: attached?.offset ?? null, images: projectTimelineImages(attached?.images),
      startedAt: isTerminalPhase(first.phase) ? null : first.timestamp, endedAt: isTerminalPhase(last.phase) ? last.timestamp : null },
    ...attached ? { toolOutput: attached.tool!.output, toolOutputLines: attached.tool!.outputLines, toolOutputBytes: attached.tool!.outputBytes,
      toolExitCode: attached.tool!.exitCode ?? base.toolExitCode, toolSandbox: attached.tool!.sandbox ?? base.toolSandbox,
      toolFields: [...(base.toolFields ?? []), { path: "content", text: attached.text ?? "", truncated: attached.textTruncated || attached.bodyOmitted }] } : {} };
  folded.set(last, { first, output, scriptCalls, scriptRun, item });
  return item;
}

// The records of the runs of scripts, by the session and the tool call that ran each (its `parentActivityId`).
function scriptRunRecords(entries: readonly HistoryEntry[]): Map<string, HistoryEntry[]> {
  const runs = new Map<string, HistoryEntry[]>();
  for (const entry of entries) {
    if (entry.eventType !== "workflow" || !entry.parentActivityId) continue;
    const key = JSON.stringify([entry.sessionId, entry.parentActivityId]);
    let list = runs.get(key);
    if (!list) runs.set(key, list = []);
    list.push(entry);
  }
  return runs;
}

function lastIndex<T>(items: readonly T[], match: (item: T) => boolean): number {
  for (let index = items.length - 1; index >= 0; index--) if (match(items[index])) return index;
  return -1;
}

// A run read from its records, kept while its records stay the same so that the row keeps its identity.
const scriptRuns = new WeakMap<ScriptRunRecord, { count: number; run: ScriptRun }>();

/** Reads a run from its records, in their order: its steps, the sessions it started, what its scripts declare, and its times. */
export function scriptRunOf(records: ReadonlyArray<ScriptRunRecord>, runId?: string): ScriptRun {
  const last = records[records.length - 1];
  const known = last ? scriptRuns.get(last) : undefined;
  if (known && known.count === records.length) return known.run;
  let status: string | null = "running";
  let waiting: ScriptWait | null = null;
  let resumesAt: string | null = null;
  let endedAt: string | null = null;
  let title: string | null = null;
  const steps: ScriptStep[] = [];
  const sessions: ScriptSession[] = [];
  const declared: ScriptDeclaredStep[] = [];
  for (const record of records) {
    const details = parseDetails(record.details ?? null);
    const number = (name: string) => { const value = details?.[name]; return typeof value === "number" ? value : null; };
    const at = record.timestamp ?? null;
    switch (record.kind) {
      case "RunStarted":
        title ??= record.text?.trim() || null;
        break;
      case "StepsDeclared": {
        const listed = details?.steps;
        if (!Array.isArray(listed)) break;
        for (const item of listed) {
          if (!isObject(item) || typeof item.id !== "string" || declared.some(step => step.id === item.id)) continue;
          const after = Array.isArray(item.after) ? item.after.filter((id): id is string => typeof id === "string") : null;
          declared.push({ id: item.id, title: typeof item.title === "string" && item.title ? item.title : item.id, after, each: item.each === true });
        }
        break;
      }
      case "StepStarted":
        if (record.activityId) steps.push({ id: record.activityId, status: "running", durationMs: null, key: null, replayed: false, startedAt: at, endedAt: null });
        break;
      case "StepEnded": {
        const index = lastIndex(steps, step => step.id === record.activityId && step.status === "running");
        const ended: ScriptStep = { id: record.activityId ?? "", status: record.phase ?? "completed", durationMs: number("durationMs"),
          key: stringAt(details, "stepKey"), replayed: details?.replayed === true, startedAt: index >= 0 ? steps[index].startedAt ?? null : null, endedAt: at };
        if (index >= 0) steps[index] = ended; else steps.push(ended);
        break;
      }
      case "SessionStarted":
        if (record.activityId) sessions.push({ sessionId: record.activityId, title: record.text ?? null, stepId: stringAt(details, "stepId"), status: "running",
          durationMs: null, inputTokens: null, outputTokens: null, message: null, startedAt: at, endedAt: null,
          providerKey: stringAt(details, "providerKey"), modelId: stringAt(details, "modelId") });
        break;
      case "SessionEnded": {
        const index = lastIndex(sessions, session => session.sessionId === record.activityId);
        if (index >= 0) sessions[index] = { ...sessions[index], status: record.phase ?? "completed", durationMs: number("durationMs"),
          inputTokens: number("inputTokens"), outputTokens: number("outputTokens"), message: stringAt(details, "message"), endedAt: at };
        break;
      }
      case "RunEnded":
        status = record.phase ?? "completed";
        endedAt = at;
        resumesAt = status === "interrupted" ? stringAt(details, "wakeAt") : null;
        break;
      case "WaitStarted":
        waiting = record.phase === "sleep"
          ? { kind: "sleep", stepId: record.activityId ?? null, text: null, until: stringAt(details, "wakeAt") }
          : { kind: record.phase === "approval" ? "approval" : "question", stepId: record.activityId ?? null, text: record.text ?? null };
        break;
      case "WaitEnded":
        waiting = null;
        break;
    }
  }
  // A step that waits for the user says so while the run goes on.
  if (status !== "running") waiting = null;
  else if (waiting?.stepId) {
    const index = lastIndex(steps, step => step.id === waiting!.stepId && step.status === "running");
    if (index >= 0) steps[index] = { ...steps[index], status: "waiting" };
  }
  // CodeAlta ended while the run ran: what still ran then stopped with it.
  if (status === "interrupted") {
    for (let index = 0; index < steps.length; index++) if (steps[index].status === "running") steps[index] = { ...steps[index], status: "interrupted" };
    for (let index = 0; index < sessions.length; index++) if (sessions[index].status === "running") sessions[index] = { ...sessions[index], status: "interrupted" };
  }
  const startedAt = records[0]?.timestamp ?? null;
  const run: ScriptRun = { runId: runId ?? last?.name ?? "", status, steps, sessions, ...waiting ? { waiting } : {}, ...resumesAt ? { resumesAt } : {},
    ...declared.length > 0 ? { declared } : {}, ...startedAt ? { startedAt } : {}, ...endedAt ? { endedAt } : {}, ...title ? { title } : {} };
  if (last) scriptRuns.set(last, { count: records.length, run });
  return run;
}

// The calls of scripts, and the call each belongs to. A call names the call that ran its script as its parent; a call
// made after the run of its parent ended (the script went on in the background) has no run, so the parent is found by
// its session, its provider and its identifier.
function scriptChildren(calls: ReadonlyMap<string, { first: HistoryEntry; last: HistoryEntry }>) {
  const owners = new Map<string, string>();
  for (const [key, call] of calls) owners.set(JSON.stringify([call.first.sessionId, call.first.providerId, call.first.activityId]), key);
  const nested = new Set<HistoryEntry>();
  const of = new Map<string, ScriptCall[]>();
  for (const [key, call] of calls) {
    const parentId = call.first.parentActivityId;
    if (!parentId || parentId === call.first.activityId) continue;
    const owner = owners.get(JSON.stringify([call.first.sessionId, call.first.providerId, parentId]));
    if (!owner || owner === key) continue;
    nested.add(call.first);
    let list = of.get(owner);
    if (!list) of.set(owner, list = []);
    list.push(scriptCall(call.first, call.last));
  }
  return { nested, of };
}

const scriptCalls = new WeakMap<HistoryEntry, { first: HistoryEntry; call: ScriptCall }>();
function scriptCall(first: HistoryEntry, last: HistoryEntry): ScriptCall {
  const known = scriptCalls.get(last);
  if (known && known.first === first) return known.call;
  const details = parseDetails(last.details) ?? parseDetails(first.details);
  const phase = last.phase?.toLowerCase() ?? null;
  const call: ScriptCall = { key: last.offset, activityId: last.activityId!, name: last.name?.trim() || friendly(last.kind || "Tool"), phase,
    startedAt: isTerminalPhase(first.phase) ? null : first.timestamp, endedAt: isTerminalPhase(last.phase) ? last.timestamp : null,
    stepId: stringAt(details, "script", "stepId"), error: phase === "failed" ? last.text?.trim() || null : null };
  scriptCalls.set(last, { first, call });
  return call;
}

function sameScriptCalls(a: ReadonlyArray<ScriptCall> | undefined, b: ReadonlyArray<ScriptCall> | undefined): boolean {
  if (a === b) return true;
  if (!a || !b || a.length !== b.length) return false;
  return a.every((call, index) => call === b[index]);
}

/** The records of the calls scripts made, which are shown inside the row of the call that ran the script. */
export function scriptCallRecords(entries: readonly HistoryEntry[]): ReadonlySet<HistoryEntry> {
  const calls = new Map<string, { first: HistoryEntry; last: HistoryEntry }>();
  for (const entry of entries) {
    if (entry.eventType !== "activity" || !entry.activityId) continue;
    const key = activityKey(entry);
    if (!calls.has(key)) calls.set(key, { first: entry, last: entry });
  }
  const { nested } = scriptChildren(calls);
  const keys = new Set([...nested].map(activityKey));
  return new Set(entries.filter(entry => entry.eventType === "activity" && entry.activityId && keys.has(activityKey(entry))));
}

// The title of a script: the `title` of the `// @exec: {...}` line that starts its code, when the record has it.
function scriptTitleOf(first: HistoryEntry, last: HistoryEntry): string | null {
  if (last.name !== "exec" && first.name !== "exec") return null;
  for (const entry of [last, first]) {
    const code = stringAt(parseDetails(entry.details), "arguments", "code")
      ?? entry.tool?.fields?.find(field => field.path === "arguments")?.text ?? null;
    const title = code ? execTitle(code) : null;
    if (title) return title;
  }
  return null;
}

/** Reads the title of a script of Code Mode from its first line, `// @exec: {"title": "…"}`; null when it has none. */
export function execTitle(code: string): string | null {
  // In the arguments of a call the code is a JSON string: its first line ends at an escaped line break.
  const match = /\/\/\s*@exec:\s*(\{[^\n]*?\})\s*(?:\n|\\n|$)/u.exec(code);
  if (!match) return null;
  try {
    const options: unknown = JSON.parse(match[1].replaceAll('\\"', '"'));
    const title = isObject(options) && typeof options.title === "string" ? options.title.trim() : "";
    return title || null;
  } catch { return null; }
}

function contentKey(entry: HistoryEntry): string {
  return JSON.stringify([entry.sessionId, entry.providerId, entry.runId, entry.kind, entry.contentId]);
}

function activityKey(entry: HistoryEntry): string {
  return JSON.stringify([entry.sessionId, entry.providerId, entry.runId, entry.activityId]);
}

function parentActivityKey(entry: HistoryEntry): string {
  return JSON.stringify([entry.sessionId, entry.providerId, entry.runId, entry.parentActivityId]);
}

function isTerminalPhase(phase: string | null): boolean {
  return ["completed", "failed", "canceled"].includes(phase?.toLowerCase() ?? "");
}

function isToolOutput(kind: string | null): boolean {
  return ["commandoutput", "filechangeoutput", "tooloutput"].includes(kind?.toLowerCase() ?? "");
}

function toTimelineItem(entry: HistoryEntry, streaming: boolean): TimelineItem {
  const kind = entry.kind ?? "";
  const normalizedKind = kind.toLowerCase();
  const parsedDetails = parseDetails(entry.details);
  let category: TimelineItem["category"] = "status";
  let icon: IconName = "info";
  let title = friendly(entry.eventType);
  let subtitle: string | null = entry.phase ? friendly(entry.phase) : null;
  let markdown = entry.text;
  let summary: string | null = null;
  let summaryIsCode = false;
  let detailMarkdown: string | null = null;
  let details = formatDetails(entry.details);
  let detailsLabel = "Details";
  // A prompt of another session is one that has the envelope of a message between agents, or one the host
  // recorded with the session that sent it: the prompt a parent gives its sub-agent is sent as it is written.
  const fromAgent = !streaming && normalizedKind === "user" && entry.eventType === "contentCompleted";
  // What the host recorded a prompt it sent as; a journal written before says it by the envelope of the prompt.
  const provenance = fromAgent ? promptProvenance(entry.provenance) : null;
  const delegated = !fromAgent ? null : parseDelegatedMessage(entry.text)
    ?? (entry.sourceSessionId ? { sourceSessionId: entry.sourceSessionId, kind: "prompt", body: entry.text ?? "" } : null);
  // The end of a background job is a prompt of the host: it is shown as what it is, without its envelope.
  const job = !fromAgent || delegated ? null : parseJobResult(entry.text);
  const toolImages = !streaming && normalizedKind === "tooloutput" && entry.eventType === "contentCompleted" ? projectTimelineImages(entry.images) : undefined;
  const elsewhere = fromAgent && !delegated && !job && !provenance ? externalOrigin(entry.contentId) : null;
  // A prompt of the host that has no card of its own (a message of an agent, a job result) is titled by its provenance.
  const hostPrompt = provenance && !delegated && !job ? provenance : null;
  let prompt: PromptRow | undefined;

  if (entry.eventType === "contentCompleted" || entry.eventType === "contentDelta") {
    if (normalizedKind === "user") {
      category = "user"; icon = delegated ? "branch" : job ? "terminal" : hostPrompt ? promptProvenanceIcon(hostPrompt) : "user";
      title = delegated ? "Agent message" : job ? "Background job" : hostPrompt ? userTitle(hostPrompt) : "You";
      if (delegated) markdown = delegated.body;
      if (job) {
        markdown = job.body;
        summary = job.title || job.command || job.jobId;
        summaryIsCode = !job.title && !!job.command;
      }
    }
    else if (normalizedKind === "assistant") { category = "assistant"; icon = "assistant"; title = "Assistant"; }
    else if (normalizedKind.startsWith("reasoning")) { category = "reasoning"; icon = "brain"; title = normalizedKind === "reasoningsummary" ? "Reasoning summary" : "Reasoning"; }
    else if (normalizedKind === "plan") { category = "plan"; icon = "plan"; title = "Plan"; }
    else if (normalizedKind === "filechangeoutput") { category = "file"; icon = "file"; title = "File changes"; }
    else if (toolImages) { category = "image"; icon = "fileImage"; title = "Image"; markdown = null; details = null; }
    else if (normalizedKind.endsWith("output")) { category = "tool"; icon = "tool"; title = friendly(kind); }
    else { title = friendly(kind || entry.eventType); }
    subtitle = streaming ? "Streaming" : elsewhere ? externalOriginSubtitles[elsewhere] : delegated
      ? friendly(delegated.kind)
      : job ? [job.title, job.result].filter(part => part).join(" · ")
      : category === "image" ? toolImages!.map(image => image.title).join(", ") : null;
  } else if (entry.eventType === "activity") {
    category = normalizedKind === "filechange" ? "file" : "tool";
    icon = category === "file" ? "file" : "tool";
    const tool = toolPresentation(entry, parsedDetails);
    title = tool.name;
    subtitle = [friendly(entry.phase ?? ""), tool.kindLabel].filter(Boolean).join(" · ") || null;
    summary = entry.tool?.primary ?? tool.primary;
    summaryIsCode = entry.tool?.isCommand ?? tool.primaryIsCode;
    markdown = tool.status;
    // Retain the bounded supplied message without treating it as output or an outcome.
    if (normalizedKind === "toolcall" && !markdown && entry.text) detailMarkdown = entry.text;
    detailsLabel = normalizedKind === "filechange" ? "File change record details" : tool.detailsLabel;
  } else if (entry.eventType === "system_prompt") {
    category = "prompt"; icon = "prompt";
    prompt = promptRow(entry);
    title = prompt.changed ? "System prompt changed" : "System prompt recorded";
    subtitle = [prompt.name, prompt.tokens > 0 ? `${promptTokens(prompt.tokens)} tokens` : null].filter(Boolean).join(" · ") || null;
    // The row has no text of its own: the prompt is read whole by its window.
    markdown = null; details = null;
  } else if (entry.eventType === "planSnapshot") {
    category = "plan"; icon = "plan"; title = "Plan"; subtitle = friendly(kind);
  } else if (entry.eventType === "notes") {
    category = "notes"; icon = "notes"; title = "Notes"; subtitle = friendly(kind);
  } else if (entry.eventType === "error") {
    category = "error"; icon = "error"; title = "Error";
  } else if (entry.eventType.startsWith("permission")) {
    category = "status"; icon = "question"; title = entry.name || "Permission request"; subtitle = friendly(kind);
  } else if (entry.eventType === "userInputRequest") {
    category = "status"; icon = "question"; title = entry.name || "User input requested";
  } else if (entry.eventType === "sessionUpdate") {
    category = normalizedKind === "warning" ? "error" : "status";
    icon = normalizedKind === "usageupdated" ? "usage" : normalizedKind === "modelchanged" ? "model" : normalizedKind.includes("completed") ? "check" : "info";
    if (normalizedKind === "diffupdated") {
      category = "file"; icon = "file"; title = "File changes";
    } else if (normalizedKind === "usageupdated") {
      title = usageHeadline(entry.text);
      subtitle = "Usage";
      summary = usageSummary(entry.text);
      detailMarkdown = entry.text;
      markdown = null;
      detailsLabel = "Usage details";
    } else if (normalizedKind === "providerfallback") {
      // The session went on with the next model of its tier after a failure of its provider.
      icon = "model";
      title = "Model changed after a failure";
      const from = stringAt(parsedDetails, "from");
      const to = stringAt(parsedDetails, "to");
      subtitle = from && to ? `${from} → ${to}` : null;
      detailsLabel = "Fallback details";
    } else if (normalizedKind === "modelchanged") {
      const model = modelPresentation(entry.text, parsedDetails);
      title = model.title;
      subtitle = model.subtitle;
      summary = model.summary;
      detailMarkdown = model.keepMessage ? entry.text : null;
      markdown = null;
      detailsLabel = "Model details";
    } else {
      title = sessionUpdateTitle(kind);
      // A local compaction is detailed like in the terminal, in place of its raw record.
      const checkpoint = normalizedKind === "compactioncompleted" ? splitCheckpointSummary(entry.text) : null;
      const compaction = checkpoint ? compactionDetailsMarkdown(parsedDetails, checkpoint.summary) : null;
      if (compaction) { markdown = checkpoint!.message; detailMarkdown = compaction; details = null; detailsLabel = "Compaction details"; }
    }
  } else if (entry.eventType === "interaction") {
    category = "status"; icon = "check"; title = friendly(kind || "Interaction");
  }

  const metadata = [
    delegated?.sourceSessionId ? `From session: ${delegated.sourceSessionId}` : null,
    entry.providerId ? `Provider: ${entry.providerId}` : null,
    entry.runId ? `Run: ${entry.runId}` : null,
    entry.activityId ? `Activity: ${entry.activityId}` : null,
    entry.parentActivityId ? `Parent: ${entry.parentActivityId}` : null,
    entry.interactionId ? `Interaction: ${entry.interactionId}` : null,
    `Event: ${entry.eventType} · byte ${entry.offset}`,
  ].filter((value): value is string => value !== null);
  const fileChanges = projectFileChanges(entry);
  // What the card says, as Markdown. The record of the event (JSON) stays in its details: it is never copied.
  const copyMarkdown = [summaryIsCode && summary ? `\`\`\`\n${summary}\n\`\`\`` : summary, markdown, detailMarkdown, fileChangesMarkdown(fileChanges)]
    .filter((value): value is string => !!value).join("\n\n") || null;

  return {
    key: entry.offset,
    eventType: entry.eventType,
    category,
    icon,
    title,
    subtitle,
    timestamp: entry.timestamp,
    markdown,
    summary,
    summaryIsCode,
    detailMarkdown,
    details,
    detailsLabel,
    metadata,
    truncated: entry.textTruncated || entry.detailsTruncated,
    bodyOmitted: entry.bodyOmitted,
    copyMarkdown: job ? [job.title, job.result, job.body].filter(Boolean).join("\n\n") : copyMarkdown,
    fileChanges,
    images: category === "user" ? projectTimelineImages(entry.images) : category === "image" ? toolImages : undefined,
    attachments: category === "user" ? projectTimelineAttachments(entry.attachments) : undefined,
    clientRequestId: category === "user" && typeof entry.clientRequestId === "string" ? entry.clientRequestId : undefined,
    toolCall: entry.eventType === "activity" && entry.activityId && category === "tool" ? { providerId: entry.providerId, runId: entry.runId,
      activityId: entry.activityId, kind, name: entry.name, offset: /^\d+$/.test(entry.offset) ? entry.offset : null, outputOffset: null,
      startedAt: null, endedAt: null } : undefined,
    toolPhase: entry.eventType === "activity" ? entry.phase?.toLowerCase() : undefined,
    toolOutput: entry.tool?.output,
    toolOutputLines: entry.tool?.outputLines,
    toolOutputBytes: entry.tool?.outputBytes,
    toolFields: entry.tool?.fields,
    toolChanges: entry.tool?.added != null && entry.tool.removed != null ? { added: entry.tool.added, removed: entry.tool.removed } : undefined,
    toolExitCode: entry.tool?.exitCode,
    toolSandbox: entry.tool?.sandbox,
    toolApp: entry.tool?.app ? { server: entry.tool.app } : undefined,
    delegated: delegated ? true : undefined,
    jobResult: job ?? undefined,
    providerPrompt: category === "user" && (entry.contentId?.startsWith("provider:") || provenance === promptProvenanceIds.provider) ? true : undefined,
    provenance: category === "user" ? provenance ?? undefined : undefined,
    missingCliProvider: entry.eventType === "error" && entry.kind === "claude-code-not-found" && entry.providerId ? entry.providerId : undefined,
    madeElsewhere: elsewhere ?? undefined,
    sourceSessionId: delegated?.sourceSessionId ?? undefined,
    prompt,
  };
}

// The English title of a prompt of the host: the key of the translations of a known provenance, the words of another.
function userTitle(provenance: string): string {
  const title = promptProvenanceTitle(provenance);
  return "key" in title ? title.key : title.text;
}

function toolPresentation(entry: HistoryEntry, details: JsonObject | null) {
  const detailsName = stringAt(details, "toolName") ?? stringAt(details, "mcpToolName") ?? stringAt(details, "tool") ?? stringAt(details, "name");
  const name = entry.name?.trim() || detailsName || friendly(entry.kind || "Tool");
  const command = stringAt(details, "command") ?? stringAt(details, "arguments", "command") ?? stringAt(details, "input", "command");
  const query = stringAt(details, "query") ?? stringAt(details, "arguments", "query") ?? stringAt(details, "input", "query");
  const path = stringAt(details, "path") ?? stringAt(details, "arguments", "path") ?? stringAt(details, "input", "path");
  const prompt = stringAt(details, "prompt") ?? stringAt(details, "arguments", "prompt") ?? stringAt(details, "input", "prompt");
  const primary = firstUsefulLine(command ?? query ?? path ?? prompt);
  const phase = entry.phase?.toLowerCase();
  const status = entry.text && (!primary || phase === "failed") ? entry.text : null;
  return {
    name,
    kindLabel: entry.name || detailsName ? friendly(entry.kind || "Tool call") : "",
    primary,
    primaryIsCode: !!(command || path),
    status,
    detailsLabel: command ? "Command and result" : "Tool details",
  };
}

function firstUsefulLine(value: string | null): string | null {
  return value?.replaceAll("\r\n", "\n").split("\n").map(line => line.trim()).find(Boolean) ?? null;
}

function usageHeadline(text: string | null): string {
  const context = capture(text, /\*\*Context:\*\*\s*([^\n]+)/i);
  return context ? `Context ${context}` : "Usage updated";
}

function usageSummary(text: string | null): string | null {
  const model = capture(text, /\*\*Model:\*\*\s*([^\n]+)/i);
  const input = capture(text, /\*\*Input tokens:\*\*\s*([^\n]+)/i);
  const output = capture(text, /\*\*Output tokens:\*\*\s*([^\n]+)/i);
  const cached = capture(text, /\*\*Cached input tokens:\*\*\s*([^\n]+)/i);
  const cost = capture(text, /\*\*Cost:\*\*\s*([^\n]+)/i);
  const tokens = [input ? `${input} in${cached && cached !== "0" ? ` (${cached} cached)` : ""}` : null, output ? `${output} out` : null].filter(Boolean).join(" · ");
  // A cost that comes with its unit reads by itself: "0.0614 AI credits".
  return [model, tokens || null, cost ? /[a-z]/i.test(cost) ? cost : `${cost} cost` : null].filter(Boolean).join(" · ") || null;
}

function modelPresentation(text: string | null, details: JsonObject | null) {
  const provider = stringAt(details, "providerKey") ?? stringAt(details, "providerId");
  // A provider that answered with another model than the one requested (a reroute, a fallback on a refusal).
  const effective = stringAt(details, "effectiveModel");
  const requested = effective ? stringAt(details, "requestedModel") : null;
  const model = stringAt(details, "modelId") ?? effective ?? capture(text, /model\s+`([^`]+)`/i);
  const reasoning = stringAt(details, "reasoningEffort") ?? capture(text, /reasoning:\s*`([^`]+)`/i);
  return {
    title: model ? `Model · ${model}` : "Model changed",
    subtitle: provider ? `Provider ${provider}` : requested ? `Instead of ${requested}` : null,
    summary: reasoning ? `Reasoning: ${friendly(reasoning)}` : null,
    keepMessage: !provider && !model && !reasoning,
  };
}

function sessionUpdateTitle(kind: string): string {
  const normalized = kind.toLowerCase();
  if (normalized === "reconnecting") return "Reconnecting";
  if (normalized === "compactionstarted") return "Compaction started";
  if (normalized === "compactioncompleted") return "Compaction completed";
  if (normalized === "warning") return "Warning";
  return friendly(kind || "Session update");
}

function capture(value: string | null, pattern: RegExp): string | null {
  return value?.match(pattern)?.[1]?.trim() || null;
}

function parseDetails(details: string | null): JsonObject | null {
  if (!details) return null;
  try {
    const value: unknown = JSON.parse(details);
    return isObject(value) ? value : null;
  } catch { return null; }
}

function isObject(value: unknown): value is JsonObject {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function stringAt(root: JsonObject | null, ...path: string[]): string | null {
  let value: unknown = root;
  for (const segment of path) {
    if (!isObject(value)) return null;
    value = value[segment];
  }
  return typeof value === "string" && value.trim() ? value.trim() : null;
}

/** Text of the newest persisted usage record in the loaded window, or null. */
export function latestUsageText(entries: HistoryResponse["entries"]): string | null {
  for (let index = entries.length - 1; index >= 0; index--) {
    const entry = entries[index];
    if (entry.eventType === "sessionUpdate" && entry.text?.includes("**Context:**")) return entry.text;
  }
  return null;
}

export function latestNotes(entries: HistoryResponse["entries"]): string {
  let markdown = "";
  for (const entry of entries) {
    if (entry.eventType === "notes") markdown = entry.kind?.toLowerCase() === "cleared" ? "" : entry.text ?? "";
  }
  return markdown;
}

export function formatDetails(details: string | null): string | null {
  if (!details) return null;
  try { return JSON.stringify(JSON.parse(details), null, 2); }
  catch { return details; }
}

export function friendly(value: string): string {
  if (!value) return "";
  return value.replaceAll("_", " ").replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/^./, character => character.toUpperCase());
}

export async function writeMarkdown(writeText: (text: string) => Promise<void>, markdown: string): Promise<"copied" | "failed"> {
  try { await writeText(markdown); return "copied"; }
  catch { return "failed"; }
}
