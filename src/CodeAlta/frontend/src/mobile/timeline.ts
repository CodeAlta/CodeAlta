import { thoughtHeadline } from "../timelineActivity/activityText";
import type { ActivityFacts, EventImage, TimelineEvent } from "./contract";
import { hostPromptOf, type HostPrompt } from "./hostPrompt";
import { foldWorkflows, type WorkflowRun } from "./workflowRun";

/** A file of a message: the id of its saved copy is known once the journal holds the message. */
export type MessageAttachment = Readonly<{ title: string; mediaType: string; id?: string | null }>;
/**
 * A prompt, an answer or an error: a message on its own line. A user message the host sent has `host`, what it is (the
 * end of a background job, a message of another agent...); an answer lists the pictures of the disk it shows in `images`.
 */
export type MessageBlock = Readonly<{
  kind: "message"; key: string; role: "user" | "assistant" | "error"; text: string; attachments?: readonly MessageAttachment[];
  seq?: number; host?: HostPrompt; images?: readonly EventImage[];
}>;
/** A thought of the agent, inside an activity. */
export type ThoughtRow = Readonly<{ type: "thought"; key: string; text: string; at: string | null }>;
/** A tool call, inside an activity: its last phase and the times of its first and last events. */
export type ToolRow = Readonly<{ type: "tool"; key: string; title: string; phase: string; at: string | null; end: string | null; activityId?: string }>;
/** An image a tool gave the model during an activity: a saved copy of the session. */
export type ToolImage = Readonly<{ key: string; title: string; id: string }>;
/**
 * The work between two messages, folded into one line that opens on its rows. The images its tools gave the model and the
 * runs of the scripts its calls ran (Code Mode's `exec`) show under that line, folded or not; the calls a script made are
 * the script's, not rows of their own.
 */
export type ActivityBlock = Readonly<{ kind: "activity"; key: string; rows: readonly (ThoughtRow | ToolRow)[]; images?: readonly ToolImage[]; workflows?: readonly WorkflowRun[] }>;
/** A run of a script whose call the timeline does not hold: a card of its own. */
export type WorkflowBlock = Readonly<{ kind: "workflow"; key: string; run: WorkflowRun }>;
export type TimelineBlock = MessageBlock | ActivityBlock | WorkflowBlock;

/** Whether an event is a thought: `assistant.reasoning`, `assistant.reasoningSummary` (or `reasoning.*`). */
export const isThought = (kind: string): boolean => kind.startsWith("assistant.reasoning") || kind.startsWith("reasoning.");

/** Adds the events of a read after the ones already held, without the ones held already. */
export function appendEvents(held: readonly TimelineEvent[], read: readonly TimelineEvent[]): TimelineEvent[] {
  const last = held.at(-1)?.seq ?? -Infinity;
  const added = read.filter(event => event.seq > last).sort((a, b) => a.seq - b.seq);
  return added.length ? [...held, ...added] : [...held];
}

/**
 * Folds the events as the desktop's timeline does: prompts, answers and errors stand alone; the thoughts and the tool
 * calls between them are one activity, keyed by its first event, so a block keeps its key while events are added after
 * it. The turns themselves (`activity.turn.*`) and the notices of the host are not rows. A run of a script goes with the
 * activity of the call that runs it, whenever its records come (a script goes on after its turn).
 */
export function foldEvents(events: readonly TimelineEvent[]): TimelineBlock[] {
  const blocks: TimelineBlock[] = [];
  type Run = { key: string; rows: (ThoughtRow | ToolRow)[]; tools: Map<string, number>; images: ToolImage[]; workflows: WorkflowRun[] };
  let run: Run | null = null;
  const runs = foldWorkflows(events);
  const scriptCalls = new Set(runs.map(item => item.callId).filter((id): id is string => !!id));
  // The activity of each call that runs a script, and the calls the timeline holds (a call of a script names one as its parent).
  const owners = new Map<string, Run>();
  const placed = new Set<string>();
  const close = () => {
    if (run) blocks.push({ kind: "activity", key: run.key, rows: run.rows, ...(run.images.length ? { images: run.images } : {}), ...(run.workflows.length ? { workflows: run.workflows } : {}) });
    run = null;
  };
  for (const event of events) {
    const kind = event.kind ?? "";
    const role = kind === "user.message" ? "user" : kind === "assistant.message" ? "assistant" : kind === "error" || kind.endsWith(".error") ? "error" : null;
    if (role) {
      close();
      const files = role === "user" ? (event.attachments ?? []).filter(file => typeof file?.title === "string" && file.title.trim().length > 0)
        .map(file => ({ title: file.title, mediaType: typeof file.mediaType === "string" ? file.mediaType : "application/octet-stream", id: typeof file.id === "string" ? file.id : null })) : [];
      const text = event.text ?? "";
      const host = role === "user" ? hostPromptOf(event) : null;
      const images = role === "assistant" ? (event.images ?? []).filter(image => typeof image?.id === "string") : [];
      if (text.trim() || files.length || role === "error" || host)
        blocks.push({ kind: "message", key: `m${event.seq}`, role, text, seq: event.seq, ...(files.length ? { attachments: files } : {}), ...(host ? { host } : {}), ...(images.length ? { images } : {}) });
      continue;
    }
    if (kind.startsWith("workflow.")) {
      // A run whose call is not held gets a card of its own, where its first record is.
      const runId = event.workflow?.runId;
      if (runId && !placed.has(runId) && !(event.workflow?.callId && owners.has(event.workflow.callId))) {
        const found = runs.find(item => item.runId === runId);
        if (found) { close(); blocks.push({ kind: "workflow", key: `w${runId}`, run: found }); placed.add(runId); }
      }
      continue;
    }
    if (kind.startsWith("tool.")) {
      const images = (event.images ?? []).filter(image => typeof image?.id === "string" && image.id);
      if (!images.length) continue;
      run ??= { key: `a${event.seq}`, rows: [], tools: new Map(), images: [], workflows: [] };
      images.forEach((image, index) => run!.images.push({ key: `i${event.seq}-${index}`, title: image.title || image.id, id: image.id }));
      continue;
    }
    const thought = isThought(kind);
    if (!thought && (!kind.startsWith("activity.") || kind.startsWith("activity.turn."))) continue;
    if (thought && !(event.text ?? "").trim()) continue;
    // A call a script made is the script's: its card says what the script does.
    if (!thought && event.parentId && (owners.has(event.parentId) || scriptCalls.has(event.parentId))) continue;
    run ??= { key: `a${event.seq}`, rows: [], tools: new Map(), images: [], workflows: [] };
    if (thought) { run.rows.push({ type: "thought", key: `t${event.seq}`, text: event.text ?? "", at: event.at }); continue; }
    const parts = kind.split(".");
    const phase = parts.at(-1) ?? "", id = event.activityId || `s${event.seq}`;
    const index = run.tools.get(id);
    if (index === undefined) {
      run.tools.set(id, run.rows.length);
      run.rows.push({ type: "tool", key: `c${id}`, title: event.text?.trim() || parts[1] || "tool", phase, at: event.at, end: event.at, ...(event.activityId ? { activityId: event.activityId } : {}) });
      if (event.activityId) {
        owners.set(event.activityId, run);
        if (scriptCalls.has(event.activityId)) for (const item of runs) if (item.callId === event.activityId && !placed.has(item.runId)) { run.workflows.push(item); placed.add(item.runId); }
      }
    } else {
      const tool = run.rows[index] as ToolRow;
      run.rows[index] = { ...tool, phase, end: event.at ?? tool.end, title: tool.title === (parts[1] || "tool") && event.text?.trim() ? event.text.trim() : tool.title };
    }
  }
  close();
  return blocks;
}

/** Sums up an activity for its line, in the shape the server gives a card. A tool still running in a turn that is over was stopped under it. */
export function activityFacts(block: ActivityBlock, live: boolean): ActivityFacts & { state: "running" | "done" | "failed" } {
  let headline: string | null = null, thoughts = 0, tools = 0, succeeded = 0, failed = 0, running = 0, start = NaN, end = NaN;
  const names: string[] = [];
  const note = (iso: string | null) => {
    const at = Date.parse(iso ?? "");
    if (!Number.isFinite(at)) return;
    if (!(at >= start)) start = at;
    if (!(at <= end)) end = at;
  };
  for (const row of block.rows) {
    if (row.type === "thought") { thoughts++; note(row.at); headline = thoughtHeadline(row.text) ?? headline; continue; }
    tools++; note(row.at); note(row.end);
    if (row.phase === "completed") succeeded++;
    else if (row.phase === "failed") failed++;
    else if (row.phase !== "canceled") running++;
    if (names.length < 3 && !names.includes(row.title)) names.push(row.title);
  }
  if (!live) { failed += running; running = 0; }
  return {
    state: live ? "running" : failed > 0 ? "failed" : "done",
    headline: headline ?? (tools > 0 ? (live ? "Calling tools…" : "Called tools") : (live ? "Working…" : "Worked")),
    names: headline === null && names.length ? names.join(", ") : null,
    thoughts, tools, succeeded, failed, running,
    startedAt: Number.isFinite(start) ? new Date(start).toISOString() : null,
    endedAt: Number.isFinite(end) ? new Date(end).toISOString() : null,
  };
}

/**
 * What changes in a block when it is drawn again: a message never changes once written, an activity changes with its
 * rows, their phases, whether it is the running one and whether it is open.
 */
export function blockVersion(block: TimelineBlock, live: boolean, open: boolean): string {
  if (block.kind === "message") return block.key;
  if (block.kind === "workflow") return `${block.key}|${block.run.version}`;
  const runs = (block.workflows ?? []).map(run => `${run.runId}:${run.version}`).join(",");
  return `${block.rows.map(row => row.type === "tool" ? `${row.key}:${row.phase}` : row.key).join(",")}|${block.images?.length ?? 0}|${runs}|${live}|${open}`;
}

/** What drawing the timeline again does to each block: keep its element, draw it again in place, or add it. */
export type TimelineStep = Readonly<{ key: string; action: "keep" | "update" | "create" }>;

/**
 * Plans the drawing of the timeline from the versions drawn before: blocks are only added after the others or changed
 * in place (the last activity, while it runs), so the elements of the others stay, with the selection and the scroll
 * of their code. Returns the steps in order and the keys whose elements go.
 */
export function planTimeline(drawn: ReadonlyMap<string, string>, blocks: readonly Readonly<{ key: string; version: string }>[]): { steps: TimelineStep[]; removed: string[] } {
  const keys = new Set(blocks.map(block => block.key));
  return {
    steps: blocks.map(block => ({ key: block.key, action: !drawn.has(block.key) ? "create" : drawn.get(block.key) === block.version ? "keep" : "update" })),
    removed: [...drawn.keys()].filter(key => !keys.has(key)),
  };
}

/** Whether an activity is the running one: the last block, while its session works. */
export const liveBlock = (blocks: readonly TimelineBlock[], index: number, busy: boolean): boolean =>
  busy && index === blocks.length - 1 && blocks[index].kind === "activity";

/** A prompt the session accepted that its timeline does not hold yet: a steered one only reaches the journal later. */
export type PendingPrompt = Readonly<{ key: string; text: string; mode: "send" | "steer" | "queue"; sentAt: number; after: number; attachments?: readonly MessageAttachment[] }>;

/** How long a prompt waits for its message once the session is idle: then the journal had time to write it. */
export const pendingGrace = 30_000;

/**
 * The prompts still to show as pending: each goes once a user message of the same text comes after the last event held when it was
 * sent (one message for one prompt), or once the session is idle and it waited `pendingGrace`.
 */
export function stillPending(pending: readonly PendingPrompt[], events: readonly TimelineEvent[], busy: boolean, now: number): PendingPrompt[] {
  const used = new Set<number>();
  return pending.filter(prompt => {
    const match = events.find(event => event.kind === "user.message" && event.seq > prompt.after && !used.has(event.seq) && (event.text ?? "").trim() === prompt.text.trim());
    if (match) { used.add(match.seq); return false; }
    return busy || now - prompt.sentAt < pendingGrace;
  });
}

/** The message a pending prompt shows as, after the blocks of the timeline. */
export const pendingBlock = (prompt: PendingPrompt): MessageBlock => ({
  kind: "message", key: prompt.key, role: "user", text: prompt.text, ...(prompt.attachments?.length ? { attachments: prompt.attachments } : {}),
});
