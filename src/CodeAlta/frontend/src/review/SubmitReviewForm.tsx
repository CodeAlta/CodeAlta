import { useEffect, useId, useRef, useState } from "react";
import { Button, HTMLSelect, PopoverNext, TextArea } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import { canSubmitReview, defaultVerdict, type ReviewVerdict } from "./reviewThreads";
import type { ReviewDestination, ReviewScope, ReviewsHub, ReviewTarget } from "./reviewsHub";
import { showToast } from "../appToaster";
import type { ReviewSessionChoice } from "./reviewSessions";
import { maximumReviewText } from "./ReviewThreadCard";

const verdicts: readonly Readonly<{ value: ReviewVerdict; label: MessageKey; detail: MessageKey }>[] = [
  { value: "approved", label: "Approve", detail: "the agent goes on, or commits" },
  { value: "changes_requested", label: "Request changes", detail: "the agent revises" },
  { value: "commented", label: "Comment", detail: "the agent answers, no verdict" },
];

/** The three verdicts of a review, as radio buttons; `1` to `3` choose one while the group has the keyboard. */
export function ReviewVerdictPicker({ value, onChange, disabled = false, name }: { value: ReviewVerdict; onChange: (verdict: ReviewVerdict) => void; disabled?: boolean; name?: string }) {
  const { t } = useShellLanguage();
  const own = useId();
  return <div className="review-verdicts" role="radiogroup" aria-label={t("Verdict")}>
    {verdicts.map(verdict => <label key={verdict.value} className="review-verdict" data-verdict={verdict.value} data-selected={value === verdict.value || undefined}>
      <input type="radio" name={name ?? own} value={verdict.value} checked={value === verdict.value} disabled={disabled} onChange={() => onChange(verdict.value)} />
      <strong>{t(verdict.label)}</strong><small>{t(verdict.detail)}</small>
    </label>)}
  </div>;
}

/**
 * The form that sends a round: an overall comment, the verdict, and where it goes. A review an agent asked for
 * goes back to that agent (`fixed`); otherwise the user picks the session among those of the checkout, or a new
 * session (in Plan mode for a plan).
 */
export function SubmitReviewForm({ comments, sessions, fixed = null, defaultSessionId = null, plan = false, onSubmit, onDone }: {
  /** The drafts and the open comments the round sends. */
  comments: Readonly<{ draft: number; open: number }>;
  sessions: readonly ReviewSessionChoice[];
  /** The session the round goes to whatever is chosen (the one that asked), by its title. */
  fixed?: Readonly<{ sessionId: string; title: string }> | null;
  defaultSessionId?: string | null;
  /** The file is a plan: a new session starts in Plan mode. */
  plan?: boolean;
  /** Sends the round; resolves to null once sent, or what to tell the user. */
  onSubmit: (verdict: ReviewVerdict, overall: string, to: ReviewDestination) => Promise<string | null>;
  onDone?: () => void;
}) {
  const { t } = useShellLanguage();
  const count = comments.draft + comments.open;
  const [verdict, setVerdict] = useState<ReviewVerdict>(() => defaultVerdict(count));
  const [overall, setOverall] = useState("");
  const [to, setTo] = useState<string>(() => fixed?.sessionId ?? (defaultSessionId && sessions.some(session => session.id === defaultSessionId) ? defaultSessionId : sessions[0]?.id ?? "new"));
  const [sending, setSending] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  const alive = useRef(true);
  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  const target = fixed?.sessionId ?? to;
  const ready = canSubmitReview(comments, verdict, overall) && !sending;
  async function send() {
    if (!ready) return;
    setSending(true); setProblem(null);
    const failed = await onSubmit(verdict, overall, target === "new" ? { newSession: true, like: sessions[0]?.id ?? null } : { sessionId: target }).catch(() => t("The review could not be sent."));
    if (!alive.current) return;
    setSending(false);
    if (failed) setProblem(failed); else onDone?.();
  }
  return <form className="review-submit" aria-label={t("Submit review")} onSubmit={event => { event.preventDefault(); void send(); }}
    onKeyDown={event => { if (event.key === "Enter" && (event.ctrlKey || event.metaKey)) { event.preventDefault(); void send(); } }}>
    <label className="review-submit-overall"><span>{t("Overall comment (optional)")}</span>
      <TextArea fill autoResize autoFocus rows={2} maxLength={maximumReviewText} value={overall} disabled={sending} onChange={event => setOverall(event.target.value)} /></label>
    <ReviewVerdictPicker value={verdict} onChange={setVerdict} disabled={sending} />
    <label className="review-submit-to"><span>{t("To")}</span>
      {fixed ? <strong className="review-submit-fixed" title={fixed.title}>{t("{title} (asked for this review)", { title: fixed.title })}</strong>
        : <HTMLSelect value={to} disabled={sending} aria-label={t("Session")} onChange={event => setTo(event.target.value)}>
          {sessions.map(session => <option key={session.id} value={session.id}>{session.title}</option>)}
          <option value="new">{t(plan ? "New session (Plan mode)" : "New session")}</option>
        </HTMLSelect>}
    </label>
    {problem && <p className="review-card-error" role="alert">{problem}</p>}
    {!canSubmitReview(comments, verdict, overall) && <p className="review-submit-hint">{t("Write a comment or an overall comment to send a comment-only review.")}</p>}
    <footer className="review-editor-footer"><span className="review-editor-spacer" />
      <Button type="submit" intent="primary" size="small" loading={sending} disabled={!ready} icon={<AppIcon name="send" size={13} />}>
        {count > 0 ? t(count === 1 ? "Submit {count} comment" : "Submit {count} comments", { count }) : t("Submit review")}</Button>
    </footer>
  </form>;
}

/**
 * **Submit review ▾**: the submit form in a popover. The popover keeps no hold on the keyboard: the form is in the
 * tab of the review, which a focus in the popover would show again over the tab the user went to.
 */
export function SubmitReviewButton(props: Omit<Parameters<typeof SubmitReviewForm>[0], "onDone"> & { disabled?: boolean }) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const { disabled = false, ...form } = props;
  return <PopoverNext isOpen={open} onInteraction={next => setOpen(next)} placement="bottom-end" disabled={disabled}
    autoFocus={false} enforceFocus={false} shouldReturnFocusOnClose={false}
    content={<div className="review-submit-popover">{open && <SubmitReviewForm {...form} onDone={() => setOpen(false)} />}</div>}>
    <Button size="small" intent="primary" disabled={disabled} endIcon={<AppIcon name="chevronDown" size={13} />}>{t("Submit review")}</Button>
  </PopoverNext>;
}

/**
 * What sends a round of a review from a surface: the call of the hub, a toast once the round is in its session
 * (with **Open** to go there), and the text of a refusal for the form to show.
 */
export function reviewSubmitter(hub: ReviewsHub, scope: ReviewScope, target: ReviewTarget, t: ReturnType<typeof useShellLanguage>["t"],
  openSession?: ((sessionId: string) => void) | null) {
  return async (verdict: ReviewVerdict, overall: string, to: ReviewDestination): Promise<string | null> => {
    const sent = await hub.submit(scope, target, verdict, overall, to);
    const open = openSession && sent.sessionId ? { text: t("Open"), onClick: () => openSession(sent.sessionId!) } : undefined;
    if (sent.status === "answer_in_session") {
      showToast({ message: t("The agent also asked questions: answer them in the session."), intent: "primary", icon: "info-sign", timeout: 8000, action: open });
      return t("The agent also asked questions: answer them in the session.");
    }
    if (!sent.ok) return sent.status === "nothing_to_submit" ? t("Write a comment or an overall comment to send a comment-only review.")
      : sent.message ?? t("The review could not be sent.");
    showToast({ message: t("The review was sent."), intent: "success", icon: "tick", timeout: 6000, action: open });
    return null;
  };
}

/** What deletes a review from a surface: the call of the hub, and a toast that says how it went. */
export function reviewDeleter(hub: ReviewsHub, scope: ReviewScope, target: ReviewTarget, t: ReturnType<typeof useShellLanguage>["t"]) {
  return async (): Promise<boolean> => {
    const done = await hub.deleteReview(scope, target);
    showToast(done.ok ? { message: t("The review was deleted."), intent: "success", icon: "tick", timeout: 4000 }
      : { message: done.message ?? t("The review could not be deleted."), intent: "warning", icon: "warning-sign", timeout: 6000 });
    return done.ok;
  };
}
