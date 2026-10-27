import { thoughtHeadline } from "../timelineActivity/activityText";
import type { ActivityFacts, TimelineEvent } from "./contract";

/** A file of a message: the id of its saved copy is known once the journal holds the message. */
export type MessageAttachment = Readonly<{ title: string; mediaType: string; id?: string | null }>;
/** A prompt, an answer or an error: a message on its own line. */
export type MessageBlock = Readonly<{ kind: "message"; key: string; role: "user" | "assistant" | "error"; text: string; attachments?: readonly MessageAttachment[] }>;
/** A thought of the agent, inside an activity. */
export type ThoughtRow = Readonly<{ type: "thought"; key: string; text: string; at: string | null }>;
/** A tool call, inside an activity: its last phase and the times of its first and last events. */
export type ToolRow = Readonly<{ type: "tool"; key: string; title: string; phase: string; at: string | null; end: string | null }>;
/** The work between two messages, folded into one line that opens on its rows. */
export type ActivityBlock = Readonly<{ kind: "activity"; key: string; rows: readonly (ThoughtRow | ToolRow)[] }>;
export type TimelineBlock = MessageBlock | ActivityBlock;

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
 * it. The turns themselves (`activity.turn.*`) and the notices of the host are not rows.
 */
export function foldEvents(events: readonly TimelineEvent[]): TimelineBlock[] {
  const blocks: TimelineBlock[] = [];
  let run: { key: string; rows: (ThoughtRow | ToolRow)[]; tools: Map<string, number> } | null = null;
  const close = () => { if (run) blocks.push({ kind: "activity", key: run.key, rows: run.rows }); run = null; };
  for (const event of events) {
    const kind = event.kind ?? "";
    const role = kind === "user.message" ? "user" : kind === "assistant.message" ? "assistant" : kind === "error" || kind.endsWith(".error") ? "error" : null;
    if (role) {
      close();
      const files = role === "user" ? (event.attachments ?? []).filter(file => typeof file?.title === "string" && file.title.trim().length > 0)
        .map(file => ({ title: file.title, mediaType: typeof file.mediaType === "string" ? file.mediaType : "application/octet-stream", id: typeof file.id === "string" ? file.id : null })) : [];
      const text = event.text ?? "";
      if (text.trim() || files.length || role === "error")
        blocks.push({ kind: "message", key: `m${event.seq}`, role, text, ...(files.length ? { attachments: files } : {}) });
      continue;
    }
    const thought = isThought(kind);
    if (!thought && (!kind.startsWith("activity.") || kind.startsWith("activity.turn."))) continue;
    if (thought && !(event.text ?? "").trim()) continue;
    run ??= { key: `a${event.seq}`, rows: [], tools: new Map() };
    if (thought) { run.rows.push({ type: "thought", key: `t${event.seq}`, text: event.text ?? "", at: event.at }); continue; }
    const parts = kind.split(".");
    const phase = parts.at(-1) ?? "", id = event.activityId || `s${event.seq}`;
    const index = run.tools.get(id);
    if (index === undefined) {
      run.tools.set(id, run.rows.length);
      run.rows.push({ type: "tool", key: `c${id}`, title: event.text?.trim() || parts[1] || "tool", phase, at: event.at, end: event.at });
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
  return `${block.rows.map(row => row.type === "tool" ? `${row.key}:${row.phase}` : row.key).join(",")}|${live}|${open}`;
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
