import { maximumFileCommentLength, maximumFileComments, maximumFileCommentsLength, type AskFileReview, type AskQuestion } from "./sessionAsks";

/**
 * A comment the user attaches to one line of the file an ask gives for review. `done` is set when the user
 * leaves the comment (Esc); a comment that is still being written is submitted as well.
 */
export type ReviewComment = Readonly<{ id: number; line: number; text: string; done: boolean }>;

/** The comments in line order, which is the order they are shown, cycled through and submitted in. */
export function orderedComments(comments: readonly ReviewComment[]): ReviewComment[] {
  return [...comments].sort((left, right) => left.line - right.line || left.id - right.id);
}

/**
 * Adds a comment on a line, or names the one already there: a line has at most one comment. Null when the
 * review holds as many comments as an answer can carry.
 */
export function addComment(comments: readonly ReviewComment[], line: number, id: number): { comments: readonly ReviewComment[]; id: number } | null {
  const existing = comments.find(comment => comment.line === line);
  if (existing) return { comments, id: existing.id };
  if (comments.length >= maximumFileComments || !Number.isInteger(line) || line < 1) return null;
  return { comments: [...comments, { id, line, text: "", done: false }], id };
}

/** Changing the text of a finished comment makes it unfinished again. */
export function editComment(comments: readonly ReviewComment[], id: number, text: string): readonly ReviewComment[] {
  const next = text.slice(0, maximumFileCommentLength);
  return comments.map(comment => comment.id === id && comment.text !== next ? { ...comment, text: next, done: false } : comment);
}

/** Leaving a comment marks it finished when it says something. */
export function finishComment(comments: readonly ReviewComment[], id: number): readonly ReviewComment[] {
  return comments.map(comment => comment.id === id ? { ...comment, done: comment.text.trim().length > 0 } : comment);
}

/**
 * Moves the comments to the lines they are on after an edit of the file. Two comments that end on one
 * line (the lines between them were deleted) both stay, in their previous order.
 */
export function moveComments(comments: readonly ReviewComment[], lineOf: (id: number) => number | null): readonly ReviewComment[] {
  let changed = false;
  const next = comments.map(comment => {
    const line = lineOf(comment.id);
    if (line === null || line === comment.line) return comment;
    changed = true;
    return { ...comment, line };
  });
  return changed ? next : comments;
}

/** The comment after (or before) a line, wrapping around; null without comments. */
export function neighbourComment(comments: readonly ReviewComment[], from: { line: number; id?: number }, direction: 1 | -1): ReviewComment | null {
  const ordered = orderedComments(comments);
  if (ordered.length === 0) return null;
  const current = from.id === undefined ? -1 : ordered.findIndex(comment => comment.id === from.id);
  if (current >= 0) return ordered[(current + direction + ordered.length) % ordered.length];
  const next = direction === 1 ? ordered.find(comment => comment.line > from.line) : [...ordered].reverse().find(comment => comment.line < from.line);
  return next ?? (direction === 1 ? ordered[0] : ordered[ordered.length - 1]);
}

/** Whether the comments still fit in one answer; the review cannot be submitted otherwise. */
export function commentsFit(comments: readonly ReviewComment[]): boolean {
  return comments.reduce((total, comment) => total + (comment.text.trim() ? comment.text.length : 0), 0) <= maximumFileCommentsLength;
}

/** What is sent with the answer: every comment that says something, finished or not, in line order. */
export function reviewSnapshot(comments: readonly ReviewComment[], fileModifiedAndSaved: boolean): AskFileReview {
  return { fileModifiedAndSaved, comments: orderedComments(comments).filter(comment => comment.text.trim().length > 0)
    .map(comment => ({ line: comment.line, text: comment.text })) };
}

/** A question's tab: an arrow while more questions follow, a check on the last one. */
export function questionTabTitle(title: string, index: number, count: number): string {
  return `${title} ${index === count - 1 ? "✓" : "→"}`;
}

/**
 * The choice an answer carries: the one the user picked, or the first when the question has choices and
 * none was picked yet (a choice question always answers with a choice).
 */
export function selectedChoice(question: Pick<AskQuestion, "choices">, chosen: readonly number[] | undefined): number | null {
  if (question.choices.length === 0) return null;
  const picked = chosen?.find(index => Number.isInteger(index) && index >= 0 && index < question.choices.length);
  return picked ?? 0;
}

/**
 * Submit advances before it submits: with questions the user has not looked at yet it goes to the first
 * of them, and only once every question was shown does it send the answer.
 */
export function submitStep(visited: ReadonlySet<number>, current: number, count: number): { kind: "submit" } | { kind: "show"; index: number } {
  for (let index = 0; index < count; index++) if (index !== current && !visited.has(index)) return { kind: "show", index };
  return { kind: "submit" };
}
