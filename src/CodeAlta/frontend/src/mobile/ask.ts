import { selectedChoice, submitStep } from "../askReview";
import { canSubmitReview, defaultVerdict } from "../review/reviewThreads";
import { isBusy, type Ask, type AskAnswerRequest, type AskMark, type AskReview, type AskVerdict, type Live } from "./contract";

/**
 * The limits of an answer, those the computer checks (`PluginAskLimits`): the text answers of all the questions together,
 * and the overall comment of a review (the limit of the review store, `maximumReviewText` on the desktop).
 */
export const askLimits = { answerText: 8192, overallComment: 16384 } as const;

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

/** What this phone knows of the answer it sent to an ask: on its way, and the revision of the ask it answered. */
export type AskSending = Readonly<{ askId: string; revision: string }>;

/** What the phone shows of the ask of a session, beside the timeline, as the state of the answer says it. */
export type AskLocal = {
  /** A request is on its way: the card waits for its answer. */
  sending: boolean;
  /** Why the last answer did not reach the session; empty when it did, or before. */
  note: string;
  /** The answer the computer took and still sends to the session; null when there is none. */
  submitted: AskSending | null;
};

export const newAskLocal = (): AskLocal => ({ sending: false, note: "", submitted: null });

/**
 * How the ask takes the place of the composer: `form` while it waits for the user, `sending` while an answer goes to the
 * session, `unknown` when an answer went and the computer cannot tell whether the session got it; null leaves the composer.
 * An ask is shown once the run that asked has ended, as the desktop presents it, except a question of a script (`immediate`),
 * which waits for the answer while its turn runs.
 */
export function askMode(ask: Ask | null | undefined, live: Live | null | undefined, local: AskLocal | undefined): "form" | "sending" | "unknown" | null {
  if (!ask || (ask.questions.length === 0 && !ask.review)) return null;
  if (local?.sending) return "sending";
  const visible = ask.immediate === true || !isBusy(live?.live);
  if (!visible) return null;
  if (ask.state === "indeterminate") return "unknown";
  // The answer this phone has on its way, until the ask read again says what became of it.
  const submitted = local?.submitted?.askId === ask.id && local.submitted.revision === ask.revision;
  return ask.state === "submitting" || submitted ? "sending" : "form";
}

/** Whether the card of a session in the list says it waits for an answer: its ask waits, and is shown now. */
export const askWaiting = (live: Live | null | undefined): boolean =>
  !!live?.asking && live.asking.state === "pending" && (live.asking.immediate || !isBusy(live.live));

/** What tells the page to read the ask of a session again: it came, went, or changed its revision or its state. */
export const askMarkKey = (mark: AskMark | null | undefined): string => mark ? `${mark.id}\n${mark.revision}\n${mark.state}\n${mark.immediate}` : "";

/**
 * What the ask read again means for the answer this phone sent: `done` when the ask is gone (or another one waits), so its
 * draft goes; `again` when it waits in another revision, so the session did not take the answer, which can go again with a
 * new id; `open` when it waits in the same revision, so the answer was refused, and the same answer sent again says why;
 * `keep` while it goes, or its outcome is unknown.
 */
export function askSettled(read: Ask | null, submitted: AskSending): "done" | "again" | "open" | "keep" {
  if (!read || read.id !== submitted.askId) return "done";
  if (read.state !== "pending") return "keep";
  return read.revision !== submitted.revision ? "again" : "open";
}

/** The characters the text answer of a question can still have: the answers of all the questions share one limit. */
export function askTextRoom(ask: Ask, draft: AskDraft, index: number): number {
  let others = 0;
  for (const [question, text] of draft.text) if (question !== index && question < ask.questions.length) others += text.length;
  return Math.max(0, askLimits.answerText - others);
}

/** Whether the text answers fit the limit of the computer, all together. */
export const askTextFits = (ask: Ask, draft: AskDraft): boolean =>
  [...draft.text].reduce((sum, [question, text]) => sum + (question < ask.questions.length ? text.length : 0), 0) <= askLimits.answerText;

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

/** Whether the answer can go: its text fits, and a comment-only review needs something to say. */
export const askSendable = (ask: Ask, draft: AskDraft): boolean => askTextFits(ask, draft) && draft.overall.length <= askLimits.overallComment
  && (!ask.review || canSubmitReview({ draft: ask.review.comments ?? 0, open: 0 }, askVerdict(ask, draft), draft.overall));

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
