// What the review of the changes of a checkout decides without the DOM: what a scope of the Changes tab reviews, the
// counts of the file rows, which files are viewed, and what a key does in review mode.
import type { ReviewViewedDto } from "#neoastra";
import { listedThreads, neighbourThread, type ReviewThread } from "./reviewThreads";
import type { ReviewTarget } from "./reviewsHub";

/** The review a scope of the Changes tab is part of: the uncommitted changes, the branch since its base; a commit has none. */
export function reviewTargetOfScope(scope: Readonly<{ kind: "head" | "branch" | "commit" }>): ReviewTarget | null {
  return scope.kind === "head" ? { kind: "changes" } : scope.kind === "branch" ? { kind: "branch" } : null;
}

/** What a file row says of its threads: those not resolved, and how many of them are drafts. */
export type FileThreadCounts = Readonly<{ active: number; drafts: number }>;

/** The threads of each file that are not resolved, by path. */
export function threadCountsByFile(threads: readonly ReviewThread[]): ReadonlyMap<string, FileThreadCounts> {
  const counts = new Map<string, { active: number; drafts: number }>();
  for (const thread of threads) {
    if (thread.state === "resolved") continue;
    const entry = counts.get(thread.path) ?? { active: 0, drafts: 0 };
    entry.active++;
    if (thread.state === "draft") entry.drafts++;
    counts.set(thread.path, entry);
  }
  return counts;
}

/**
 * The files viewed at the revision they have now: a mark of another revision (the agent changed the file since) does
 * not count, so that "3/7 files viewed" stays true.
 */
export function viewedFiles(viewed: readonly ReviewViewedDto[], target: ReviewTarget | null, files: readonly Readonly<{ path: string; revision: string }>[]): ReadonlySet<string> {
  if (!target || target.kind === "file") return new Set();
  const marks = new Map(viewed.filter(entry => entry.target === target.kind).map(entry => [entry.path, entry.revision]));
  return new Set(files.filter(file => marks.get(file.path) === file.revision).map(file => file.path));
}

/**
 * The threads of a review, in the order `n` and `p` go through them: by file as the list shows the files, then by line;
 * then the threads of files the list does not show, and the outdated ones last.
 */
export function reviewOrder(threads: readonly ReviewThread[], files: readonly Readonly<{ path: string }>[]): ReviewThread[] {
  const place = new Map(files.map((file, index) => [file.path, index]));
  const active = threads.filter(thread => thread.state !== "resolved");
  const listed = listedThreads(active);
  // Files of the list first, in its order; a thread on a file the list no longer has keeps its place among the outdated.
  const known = (thread: ReviewThread) => place.has(thread.path) && !thread.outdated;
  return [...listed.filter(known).sort((a, b) => place.get(a.path)! - place.get(b.path)! || listed.indexOf(a) - listed.indexOf(b)), ...listed.filter(thread => !known(thread))];
}

/** The thread after or before a place of the review: a thread, or a file (its first thread is the next one). */
export function nextReviewThread(ordered: readonly ReviewThread[], from: Readonly<{ id?: string | null; path?: string | null }>, direction: 1 | -1): ReviewThread | null {
  if (!ordered.length) return null;
  if (from.id && ordered.some(thread => thread.id === from.id)) return neighbourThread(ordered, { id: from.id }, direction);
  if (from.path) {
    const first = ordered.findIndex(thread => thread.path === from.path);
    if (first >= 0) return direction === 1 ? ordered[first] : ordered[(first - 1 + ordered.length) % ordered.length];
  }
  return direction === 1 ? ordered[0] : ordered[ordered.length - 1];
}

/** What a key does in the review of the changes. */
export type ReviewKeyAction = "comment" | "nextThread" | "previousThread" | "nextFile" | "previousFile" | "viewed";

/**
 * The action of a key pressed in review mode, or null: `c` comments, `n`/`p` go from thread to thread, `]`/`[` from
 * file to file, `v` marks the file viewed. The key is read as it is typed (a layout that needs Shift for `]` still gives
 * `]`), without Ctrl, Cmd or Alt, and never where text is typed: `typing` says the key goes to a field.
 */
export function reviewKeyAction(event: Readonly<{ key: string; ctrlKey: boolean; metaKey: boolean; altKey: boolean; repeat?: boolean }>, typing: boolean): ReviewKeyAction | null {
  if (typing || event.ctrlKey || event.metaKey || event.altKey) return null;
  switch (event.key) {
    case "c": return event.repeat ? null : "comment";
    case "n": return "nextThread";
    case "p": return "previousThread";
    case "]": return "nextFile";
    case "[": return "previousFile";
    case "v": return event.repeat ? null : "viewed";
    default: return null;
  }
}

/**
 * Whether a key pressed on an element goes to a field: an input, a text area, a select or editable content, but not
 * the read-only Monaco editor of a diff (its own text area takes the keys of the review), unless it is in a comment.
 */
export function typingTarget(element: Readonly<{ closest: (selector: string) => unknown }> | null): boolean {
  if (!element) return false;
  if (element.closest(".review-zone, .review-block-host, .review-thread, .review-editor, .review-notes")) return !!element.closest("input, textarea, select, [contenteditable='true'], [contenteditable='']");
  if (element.closest(".monaco-editor")) return false;
  return !!element.closest("input, textarea, select, [contenteditable='true'], [contenteditable='']");
}

/** The room the threads of a file take in its diff before it is drawn: a card is about this high. */
export const reviewCardHeight = 96;
export function reviewHeightEstimate(threads: number): number {
  return Math.max(0, threads) * reviewCardHeight;
}

/**
 * Whether the composer says "Changes waiting for your review": the session reviews before commit, its checkout has
 * uncommitted changes, no agent waits for a review of them already, no run is active (the agent may still be at work),
 * and the work tree is not the one the user approved last. An approval holds until a file changes, whatever is
 * committed meanwhile: what the agent left uncommitted after it was approved does not wait for another review.
 */
export function changesWaitForReview(state: Readonly<{ reviewBeforeCommit: boolean | null; changedFiles: number | null; requested: boolean; running: boolean;
  approved: boolean }>): boolean {
  return state.reviewBeforeCommit === true && (state.changedFiles ?? 0) > 0 && !state.requested && !state.running && !state.approved;
}

/**
 * The review mode the Changes tab keeps once the request of an agent is answered (`before` the id of the request it
 * showed, `now` the one it shows): an answer, given here or in the session, gives the choice back to what waits, so
 * that a review that is done does not stay open with its **Submit review** button.
 */
export function reviewChoiceAfterRequest(choice: boolean | null, before: string | null, now: string | null): boolean | null {
  return before !== null && now !== before ? null : choice;
}
