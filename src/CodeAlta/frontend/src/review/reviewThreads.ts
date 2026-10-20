// The comments of a review as the page holds them: what the host says of each thread, in the order the review
// shows them, grouped by file and filtered by state. Pure: the review surfaces (a file, the changes) share it.

/** What a review is about: a file of the checkout, its uncommitted changes, or the changes of its branch. */
export type ReviewTargetKind = "file" | "changes" | "branch";
/** The side of a diff a comment is on: `old` is `HEAD`, `new` the working tree. A file has only its new side. */
export type ReviewSide = "old" | "new";
export type ReviewThreadState = "draft" | "open" | "addressed" | "resolved";
export type ReviewVerdict = "approved" | "changes_requested" | "commented";
export type ReviewReply = Readonly<{ author: "user" | "agent"; sessionId: string | null; body: string; at: string }>;

/**
 * A comment of the user with its replies. Lines are 1-based and inclusive; 0 and 0 is the whole file. `anchor` is
 * the text of the lines when the comment was written; `outdated` says that text is gone from the file.
 */
export type ReviewThread = Readonly<{ id: string; target: ReviewTargetKind; path: string; side: ReviewSide; startLine: number; endLine: number;
  anchor: string; body: string; suggestion: string | null; state: ReviewThreadState; outdated: boolean; replies: readonly ReviewReply[];
  round: string | null; createdAt: string; updatedAt: string }>;

/** Which threads a list shows: the ones not resolved (`active`), one state, the outdated ones, or all. */
export type ReviewFilter = "active" | "all" | ReviewThreadState | "outdated";
export const reviewFilters: readonly ReviewFilter[] = ["active", "draft", "open", "addressed", "resolved", "outdated", "all"];

/** What the chip of a thread says. An open thread was sent to the agent. */
export type ReviewChip = "draft" | "sent" | "addressed" | "resolved" | "outdated";

const states: readonly string[] = ["draft", "open", "addressed", "resolved"];
const targets: readonly string[] = ["file", "changes", "branch"];

/** A thread of the host, or null when it is not one the page knows how to show. */
export function readThread(value: Readonly<{ id: string; target: string; path: string; side: string; startLine: number; endLine: number; anchor: string;
  body: string; suggestion: string | null; state: string; outdated: boolean; replies: readonly Readonly<{ author: string; sessionId: string | null; body: string; at: string }>[];
  round: string | null; createdAt: string; updatedAt: string }>): ReviewThread | null {
  if (!states.includes(value.state) || !targets.includes(value.target) || (value.side !== "old" && value.side !== "new")
    || !Number.isInteger(value.startLine) || !Number.isInteger(value.endLine) || value.startLine < 0 || value.endLine < value.startLine) return null;
  return Object.freeze({ id: value.id, target: value.target as ReviewTargetKind, path: value.path, side: value.side, startLine: value.startLine, endLine: value.endLine,
    anchor: value.anchor, body: value.body, suggestion: value.suggestion, state: value.state as ReviewThreadState, outdated: value.outdated,
    replies: Object.freeze(value.replies.map(reply => Object.freeze({ author: reply.author === "agent" ? "agent" as const : "user" as const,
      sessionId: reply.sessionId, body: reply.body, at: reply.at }))),
    round: value.round, createdAt: value.createdAt, updatedAt: value.updatedAt });
}

/** The number in a thread's id (`c12` is 12), for the order threads were written in. */
export function threadNumber(id: string): number {
  const match = /^c(\d+)$/.exec(id);
  return match ? Number(match[1]) : Number.MAX_SAFE_INTEGER;
}

/** Whether a thread is about the whole file rather than lines. */
export const wholeFile = (thread: Pick<ReviewThread, "startLine" | "endLine">) => thread.startLine === 0 && thread.endLine === 0;

/** The order of a review: by file (ordinal), then the whole file first, then by line, the old side first, then as written. */
export function compareThreads(left: ReviewThread, right: ReviewThread): number {
  if (left.path !== right.path) return left.path < right.path ? -1 : 1;
  return left.startLine - right.startLine || left.endLine - right.endLine || (left.side === right.side ? 0 : left.side === "old" ? -1 : 1)
    || threadNumber(left.id) - threadNumber(right.id) || (left.id < right.id ? -1 : left.id > right.id ? 1 : 0);
}

export function orderThreads(threads: readonly ReviewThread[]): ReviewThread[] {
  return [...threads].sort(compareThreads);
}

export function threadMatches(thread: ReviewThread, filter: ReviewFilter): boolean {
  switch (filter) {
    case "all": return true;
    case "active": return thread.state !== "resolved";
    case "outdated": return thread.outdated && thread.state !== "resolved";
    default: return thread.state === filter;
  }
}

export function filterThreads(threads: readonly ReviewThread[], filter: ReviewFilter): ReviewThread[] {
  return orderThreads(threads.filter(thread => threadMatches(thread, filter)));
}

/** The threads a surface draws on its lines or blocks: not resolved, not outdated, on lines of the side it shows. */
export function threadsOnLines(threads: readonly ReviewThread[], side: ReviewSide = "new", path: string | null = null): ReviewThread[] {
  return orderThreads(threads.filter(thread => thread.state !== "resolved" && !thread.outdated && !wholeFile(thread) && thread.side === side
    && (path === null || thread.path === path)));
}

export type ReviewFileGroup = Readonly<{ path: string; threads: readonly ReviewThread[] }>;

/**
 * The threads by file, in path order and line order. Outdated threads that are not resolved are apart: their
 * lines are no longer in the file, and a list shows them after the files.
 */
export function groupThreads(threads: readonly ReviewThread[]): Readonly<{ files: readonly ReviewFileGroup[]; outdated: readonly ReviewThread[] }> {
  const files: { path: string; threads: ReviewThread[] }[] = [];
  const outdated: ReviewThread[] = [];
  for (const thread of orderThreads(threads)) {
    if (thread.outdated && thread.state !== "resolved") { outdated.push(thread); continue; }
    const last = files[files.length - 1];
    if (last?.path === thread.path) last.threads.push(thread); else files.push({ path: thread.path, threads: [thread] });
  }
  return { files, outdated };
}

/** The order a list shows threads in, the one `n` and `p` follow: the files, then the outdated threads. */
export function listedThreads(threads: readonly ReviewThread[]): ReviewThread[] {
  const groups = groupThreads(threads);
  return [...groups.files.flatMap(group => group.threads), ...groups.outdated];
}

export type ReviewCounts = Readonly<{ draft: number; open: number; addressed: number; resolved: number; outdated: number; active: number; total: number }>;

export function threadCounts(threads: readonly ReviewThread[]): ReviewCounts {
  const counts = { draft: 0, open: 0, addressed: 0, resolved: 0, outdated: 0, active: 0, total: threads.length };
  for (const thread of threads) {
    counts[thread.state]++;
    if (thread.state !== "resolved") counts.active++;
    if (thread.outdated && thread.state !== "resolved") counts.outdated++;
  }
  return counts;
}

/** What the chip of a thread says: a resolved thread is resolved, an outdated one outdated, else its state. */
export function threadChip(thread: Pick<ReviewThread, "state" | "outdated">): ReviewChip {
  if (thread.state === "resolved") return "resolved";
  if (thread.outdated) return "outdated";
  return thread.state === "open" ? "sent" : thread.state;
}

/**
 * The thread after (or before) another one in a list, wrapping around; from a place without a thread (a line of
 * a file), the first one after it. Null without threads.
 */
export function neighbourThread(listed: readonly ReviewThread[], from: Readonly<{ id?: string | null; path?: string; line?: number }>, direction: 1 | -1): ReviewThread | null {
  if (listed.length === 0) return null;
  const current = from.id ? listed.findIndex(thread => thread.id === from.id) : -1;
  if (current >= 0) return listed[(current + direction + listed.length) % listed.length];
  if (from.path !== undefined && from.line !== undefined) {
    const after = (thread: ReviewThread) => thread.path > from.path! || thread.path === from.path && thread.startLine > from.line!;
    const before = (thread: ReviewThread) => thread.path < from.path! || thread.path === from.path && thread.startLine < from.line!;
    // An outdated thread is on no line: a place in a file finds the threads that are.
    const placed = listed.filter(thread => !thread.outdated || thread.state === "resolved");
    const found = direction === 1 ? placed.find(after) : [...placed].reverse().find(before);
    if (found) return found;
  }
  return direction === 1 ? listed[0] : listed[listed.length - 1];
}

/** The lines of a text, from 1, inclusive; the text of a file split as the host splits it (LF, CRLF or CR). */
export function textLines(text: string, start: number, end: number): string {
  if (start < 1 || end < start) return "";
  return text.split(/\r\n|\r|\n/u).slice(start - 1, end).join("\n");
}

/** One line of the small diff of a suggestion: kept, taken out or put in. */
export type SuggestionLine = Readonly<{ kind: "same" | "removed" | "added"; text: string }>;

/**
 * The small diff a suggestion is shown as: the lines it is about, then what replaces them. The lines both have at
 * their start and at their end are kept as context; an empty suggestion takes every line out.
 */
export function suggestionDiff(original: string, suggestion: string): SuggestionLine[] {
  const before = original === "" ? [] : original.split(/\r\n|\r|\n/u);
  const after = suggestion === "" ? [] : suggestion.split(/\r\n|\r|\n/u);
  let start = 0;
  while (start < before.length && start < after.length && before[start] === after[start]) start++;
  let endBefore = before.length, endAfter = after.length;
  while (endBefore > start && endAfter > start && before[endBefore - 1] === after[endAfter - 1]) { endBefore--; endAfter--; }
  return [
    ...before.slice(0, start).map(text => ({ kind: "same" as const, text })),
    ...before.slice(start, endBefore).map(text => ({ kind: "removed" as const, text })),
    ...after.slice(start, endAfter).map(text => ({ kind: "added" as const, text })),
    ...before.slice(endBefore).map(text => ({ kind: "same" as const, text })),
  ];
}

/** Whether a round can be sent: a comment-only verdict needs something to say. */
export function canSubmitReview(counts: Pick<ReviewCounts, "draft" | "open">, verdict: ReviewVerdict, overall: string): boolean {
  return verdict !== "commented" || counts.draft + counts.open > 0 || overall.trim().length > 0;
}

/** Whether a file is a plan of CodeAlta: a review of it starts a new session in Plan mode. */
export const isPlanPath = (path: string | null | undefined) => !!path && path.startsWith(".alta/plans/") && /\.md$/iu.test(path);

/** A block of a rendered document and the lines of the source it comes from. */
export type SourceBlock = Readonly<{ start: number; end: number }>;

/** The lines of a range of blocks: from the first line of either to the last line of either. */
export function blockRange(first: SourceBlock, second: SourceBlock): SourceBlock {
  return { start: Math.min(first.start, second.start), end: Math.max(first.end, second.end) };
}

/**
 * The block a thread's card goes after, by index, or -1 when no block holds its last line: among the blocks that
 * hold that line, the widest one that starts inside the thread (the whole block a comment is about, a list item with
 * its sub-items), else the narrowest (a comment on a line inside a block, written in the source).
 */
export function placementBlock(blocks: readonly SourceBlock[], lines: Readonly<{ startLine: number; endLine: number }>): number {
  let inside = -1, narrow = -1;
  blocks.forEach((block, index) => {
    if (block.start > lines.endLine || block.end < lines.endLine) return;
    if (block.start >= lines.startLine && (inside < 0 || block.end - block.start > blocks[inside].end - blocks[inside].start)) inside = index;
    if (narrow < 0 || block.end - block.start < blocks[narrow].end - blocks[narrow].start) narrow = index;
  });
  return inside >= 0 ? inside : narrow;
}

/** Whether a block shows lines a thread is about. */
export const blockCommented = (block: SourceBlock, thread: Pick<ReviewThread, "startLine" | "endLine">) =>
  block.start <= thread.endLine && block.end >= thread.startLine;
