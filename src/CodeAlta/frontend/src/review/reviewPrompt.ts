// What the timeline reads of a prompt that carries a review round (written by `ReviewPromptComposer` on the host),
// to show it as a compact card instead of its raw text.
import type { ReviewVerdict } from "./reviewThreads";

/** The start of every review round the host composes. */
export const reviewPromptPrefix = "# Review of ";

/** A review round, as its prompt says it: what was reviewed, the verdict, its round and how many comments it sent. */
export type ReviewPromptSummary = Readonly<{
  /** What was reviewed: `changes`, `branch`, or a file and its path. */
  subject: Readonly<{ kind: "changes" | "branch" } | { kind: "file"; path: string }>;
  verdict: ReviewVerdict;
  round: number | null;
  comments: number;
  /** The comments still open from earlier rounds that the round sends again. */
  earlier: number;
}>;

/**
 * The summary of a review round from the text of a prompt, or null when the text is not one. Only the first two lines
 * are read: the heading (`# Review of <what>: <verdict>`) and the round line (`Round 2 · 3 comments, 1 still open…`).
 */
export function reviewPromptSummary(text: string): ReviewPromptSummary | null {
  if (!text.startsWith(reviewPromptPrefix)) return null;
  const lines = text.split(/\r\n|\r|\n/u);
  const heading = /^# Review of (.+): (approved|changes requested|comments)$/u.exec(lines[0]);
  if (!heading) return null;
  const what = heading[1];
  const subject = what === "your changes" ? { kind: "changes" as const } : what === "the changes of your branch" ? { kind: "branch" as const }
    : /^`[^`]+`$/u.test(what) ? { kind: "file" as const, path: what.slice(1, -1) } : null;
  if (!subject) return null;
  const verdict: ReviewVerdict = heading[2] === "approved" ? "approved" : heading[2] === "changes requested" ? "changes_requested" : "commented";
  const roundLine = lines.slice(1, 4).find(line => line.startsWith("Round ")) ?? "";
  const round = /^Round (\d+)/u.exec(roundLine);
  const comments = / · (\d+) comments?\b/u.exec(roundLine);
  const earlier = /, (\d+) still open from earlier rounds/u.exec(roundLine);
  return { subject, verdict, round: round ? Number(round[1]) : null, comments: comments ? Number(comments[1]) : 0, earlier: earlier ? Number(earlier[1]) : 0 };
}
