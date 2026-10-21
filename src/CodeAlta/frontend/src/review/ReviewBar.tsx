import { useContext, useState, type ReactNode } from "react";
import { Alert, Button, PopoverNext } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { threadCounts, type ReviewFilter, type ReviewThread } from "./reviewThreads";
import { ReviewCommentsList } from "./ReviewCommentsList";
import { ReviewsListOpenerContext } from "./useReviews";

/**
 * The bar at the top of a review: what is reviewed, how many comments wait (drafts, sent, addressed), the list of
 * all of them, **Resolve addressed** while the agent answered some, the submit button the surface gives, and
 * **Delete review**, which ends the review whatever its state once the user confirms.
 */
export function ReviewBar({ title, detail, threads, readOnly = null, notice = null, onResolveAddressed, onReveal, onDelete = null, submit, showPaths = false, projectId = null }: {
  /** What is reviewed: a file, the changes of a checkout. */
  title: ReactNode;
  /** Where, or the round: shown after the title. */
  detail?: ReactNode;
  threads: readonly ReviewThread[];
  /** Why the review cannot be changed (a newer or broken store), or null. */
  readOnly?: string | null;
  /** Why no comment can be added now ("Save the file to comment on it"), or null. */
  notice?: string | null;
  /** Resolves every addressed thread; resolves to whether it was done. Null hides the button. */
  onResolveAddressed?: (() => Promise<boolean>) | null;
  /** Shows a thread where it is drawn; null leaves the list out. */
  onReveal?: ((thread: ReviewThread) => void) | null;
  /**
   * Deletes the review (its comments, their replies, its rounds) after the user confirms; resolves to whether it was
   * done. Null hides the button.
   */
  onDelete?: (() => Promise<boolean>) | null;
  /** **Submit review ▾**, or nothing where the review is submitted elsewhere (the form of a request). */
  submit?: ReactNode;
  showPaths?: boolean;
  /** The project of the review: **All reviews** opens the reviews of this project in Work items. */
  projectId?: string | null;
}) {
  const { t } = useShellLanguage();
  const openAll = useContext(ReviewsListOpenerContext);
  const counts = threadCounts(threads);
  const [filter, setFilter] = useState<ReviewFilter>("active");
  const [listOpen, setListOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const parts = [
    counts.draft && t(counts.draft === 1 ? "{count} draft" : "{count} drafts", { count: counts.draft }),
    counts.open && t("{count} sent", { count: counts.open }),
    counts.addressed && t("{count} addressed", { count: counts.addressed }),
    counts.outdated && t("{count} outdated", { count: counts.outdated }),
    counts.resolved && t("{count} resolved", { count: counts.resolved }),
  ].filter(Boolean);
  return <div className="review-bar" role="region" aria-label={t("Review")}>
    <span className="review-bar-title"><AppIcon name="chat" size={14} /><strong>{t("Review")}</strong>{title}{detail && <span className="review-bar-detail">{detail}</span>}</span>
    <span className="review-bar-counts" role="status">{parts.length ? parts.join(" · ") : t("No comment yet")}</span>
    {readOnly && <span className="review-bar-notice" data-tone="warning" title={readOnly}><AppIcon name="warning" size={13} />{t("Read-only")}</span>}
    {!readOnly && notice && <span className="review-bar-notice" title={notice}><AppIcon name="info" size={13} />{notice}</span>}
    <span className="review-bar-actions">
      {onReveal && <PopoverNext isOpen={listOpen} onInteraction={setListOpen} placement="bottom-end"
        content={<div className="review-list-popover"><ReviewCommentsList threads={threads} filter={filter} onFilter={setFilter} showPaths={showPaths}
          onReveal={thread => { setListOpen(false); onReveal(thread); }} /></div>}>
        <Button size="small" variant="minimal" icon={<AppIcon name="list" size={13} />} endIcon={<AppIcon name="chevronDown" size={12} />}>
          {t("Comments {count}", { count: counts.active })}</Button>
      </PopoverNext>}
      {onResolveAddressed && counts.addressed > 0 && !readOnly && <Button size="small" variant="minimal" icon={<AppIcon name="checked" size={13} />} loading={busy}
        onClick={() => { setBusy(true); void onResolveAddressed().finally(() => setBusy(false)); }}>{t("Resolve addressed")}</Button>}
      {openAll && <Button size="small" variant="minimal" icon={<AppIcon name="eye" size={13} />} title={t("Every review in flight, in Work items")}
        aria-label={t("All reviews")} onClick={() => openAll(projectId)} />}
      {onDelete && !readOnly && threads.length > 0 && <Button size="small" variant="minimal" icon={<AppIcon name="trash" size={13} />} loading={deleting}
        aria-label={t("Delete review")} title={t("Delete review: its comments, their replies and its rounds")} onClick={() => setConfirming(true)} />}
      {submit}
    </span>
    {onDelete && <Alert isOpen={confirming} intent="danger" icon={null} cancelButtonText={t("Cancel")} confirmButtonText={t("Delete review")} canEscapeKeyCancel canOutsideClickCancel
      onCancel={() => setConfirming(false)}
      onConfirm={() => { setConfirming(false); setDeleting(true); void onDelete().finally(() => setDeleting(false)); }}>
      <p>{t("Delete this review? Its comments, their replies and its rounds are deleted. An agent that waits for it is told the review was cancelled.")}</p>
    </Alert>}
  </div>;
}
