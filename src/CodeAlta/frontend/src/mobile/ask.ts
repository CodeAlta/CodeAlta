import { selectedChoice, submitStep } from "../askReview";
import { canSubmitReview, defaultVerdict } from "../review/reviewThreads";
import { isBusy, type Ask, type AskAnswerRequest, type AskReview, type AskVerdict, type Live } from "./contract";

/** What the user answered so far of an ask, on this phone: kept while the page lives, across sessions and revisions. */
export type AskDraft = {
  /** The question shown. */
  index: number;
  /** The questions shown once: Submit shows those not seen before it sends. */
  seen: Set<number>;
  choices: Map<number, number>;
  text: Map<number, string>;
  /** The verdict chosen; null keeps the one the comments give. */
  verdict: AskVerdict | null;
  overall: string;
  /** The id of the answer or the cancel on its way: sent again with it, it is never sent twice. */
  actionId: string | null;
};

export const newAskDraft = (): AskDraft => ({ index: 0, seen: new Set([0]), choices: new Map(), text: new Map(), verdict: null, overall: "", actionId: null });

/** The key of the draft of an ask of a session. */
export const askDraftKey = (sessionId: string, ask: Pick<Ask, "id">) => `${sessionId}\n${ask.id}`;

/**
 * Whether the ask takes the place of the composer: it waits for the user, and the run that asked has ended, as the desktop
 * presents it.
 */
export const askShown = (ask: Ask | null | undefined, live: Live | null | undefined): ask is Ask =>
  !!ask && ask.state === "pending" && (ask.questions.length > 0 || !!ask.review) && !isBusy(live?.live);

/** The badge of the card: what the agent asks for. */
export const askBadge = (ask: Ask): string => ask.review ? "Review requested" : ask.questions.length === 1 ? "A question for you" : "Questions for you";

/** What a review request is of: its title, the name of its file, or the changes. */
export function askReviewWhat(review: AskReview): string {
  if (review.title) return review.title;
  if (review.kind === "file" && review.path) return review.path.slice(review.path.lastIndexOf("/") + 1);
  return review.kind === "branch" ? "The changes of the branch" : "The uncommitted changes";
}

/** Where the comments on the lines are written: on the computer, and those written there go with the review. */
export const askReviewWhere = (review: AskReview): string => review.kind === "file"
  ? "Comments on the lines of the file are written in CodeAlta on the computer; those written there go with this review."
  : "Comments on the changes are written in the Changes tab on the computer; those written there go with this review.";

/** The verdicts of a review, as the desktop names them. */
export const askVerdicts: readonly Readonly<{ value: AskVerdict; label: string; detail: string }>[] = [
  { value: "approved", label: "Approve", detail: "the agent goes on, or commits" },
  { value: "changes_requested", label: "Request changes", detail: "the agent revises" },
  { value: "commented", label: "Comment", detail: "the agent answers, no verdict" },
];

/** The verdict the answer carries: the one chosen, else changes asked when there are comments to send, approval otherwise. */
export const askVerdict = (ask: Ask, draft: AskDraft): AskVerdict => draft.verdict ?? defaultVerdict(ask.review?.comments ?? 0);

/** Whether the answer can go: a comment-only review needs something to say. */
export const askSendable = (ask: Ask, draft: AskDraft): boolean =>
  !ask.review || canSubmitReview({ draft: ask.review.comments ?? 0, open: 0 }, askVerdict(ask, draft), draft.overall);

/** What the review says of its comments, as the desktop: "Your review · 2 comments to send". */
export function askCommentsText(comments: number | null): string {
  if (comments === null) return "Your review";
  return `Your review · ${comments === 0 ? "No comment yet" : comments === 1 ? "1 comment to send" : `${comments} comments to send`}`;
}

/** The choice a question answers with: the one picked, else the first. */
export const askChoice = (ask: Ask, draft: AskDraft, index: number): number | null => {
  const question = ask.questions[index];
  if (!question) return null;
  const chosen = draft.choices.get(index);
  return selectedChoice(question, chosen === undefined ? undefined : [chosen]);
};

/** What the main button does: show the first question not seen yet, else send. */
export const askStep = (ask: Ask, draft: AskDraft) => submitStep(draft.seen, draft.index, ask.questions.length);

/** The label of the main button. */
export const askPrimaryLabel = (ask: Ask, draft: AskDraft): "Next" | "Submit" | "Submit review" =>
  askStep(ask, draft).kind === "show" ? "Next" : ask.review ? "Submit review" : "Submit";

/** Shows a question: it is seen from then on. */
export function askGo(ask: Ask, draft: AskDraft, index: number): boolean {
  if (index < 0 || index >= ask.questions.length || index === draft.index) return false;
  draft.index = index;
  draft.seen.add(index);
  return true;
}

/** The answer of every question, as the desktop sends it, with the verdict of a review. */
export function askAnswer(ask: Ask, draft: AskDraft, actionId: string): AskAnswerRequest {
  const answers = ask.questions.map((_, index) => {
    const choice = askChoice(ask, draft, index);
    return { question: index, choices: choice === null ? [] : [choice], text: draft.text.get(index) || null };
  });
  return {
    actionId, askId: ask.id, revision: ask.revision, answers,
    ...(ask.review ? { review: { verdict: askVerdict(ask, draft), overall: draft.overall || null } } : {}),
  };
}

/** What the cancel confirmation says, as the desktop's. */
export const askCancelText = {
  title: "Cancel Ask",
  question: "Exit ask mode without sending a response?",
  detail: "The queued ask will be canceled and the session will return to the normal prompt editor.",
  keep: "Keep answering",
  exit: "Exit without responding",
} as const;
