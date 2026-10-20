import { useContext, useEffect, useMemo, useState } from "react";
import { Button } from "@blueprintjs/core";
import { projectGit, sessionOperations } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { changesWaitForReview } from "./changesReview";
import { ReviewOpenerContext, useCheckoutCounts, useReviewList, useReviewsHub } from "./useReviews";

type Api = Readonly<{ reviewBeforeCommit: typeof sessionOperations.reviewBeforeCommit; status: typeof projectGit.status }>;
const defaultApi: Api = { reviewBeforeCommit: request => sessionOperations.reviewBeforeCommit(request), status: (request, options) => projectGit.status(request, options) };
const changesTarget = { kind: "changes" } as const;
const statusRefreshMilliseconds = 10_000;

/**
 * What waits for the user's review in the status of a composer:
 * - "Review · 2 draft comments · Open" while the checkout of the session has comments that were not sent; **Open** goes
 *   to the first file or changes they are written on;
 * - "Changes waiting for your review · Open" when the session reviews before commit, its checkout has uncommitted
 *   changes, no agent asked for their review yet and no run is active: the user can review them unasked.
 */
export function ReviewComposerStatus({ epoch, projectId, checkout, sessionId = null, running = false, api = defaultApi }: {
  epoch?: string | null; projectId: string | null; checkout: string | null;
  /** The session of the composer; null for a draft, which has no changes yet. */
  sessionId?: string | null;
  /** A run of the session is active. */
  running?: boolean;
  api?: Api;
}) {
  const { t } = useShellLanguage();
  const hub = useReviewsHub();
  const open = useContext(ReviewOpenerContext);
  const scope = useMemo(() => projectId ? { projectId, checkout } : null, [projectId, checkout]);
  const counts = useCheckoutCounts(hub, scope);
  // Read when the composer shows, after each run, every ten seconds and when the window gets the focus back (the
  // switch may be flipped meanwhile, the agent or the user may change files): the switch, and whether the checkout has changes.
  const [facts, setFacts] = useState<Readonly<{ key: string; review: boolean | null; changed: number | null }> | null>(null);
  const key = JSON.stringify([epoch, sessionId, projectId, checkout]);
  useEffect(() => {
    if (!epoch || !sessionId || !projectId || running || !hub) return;
    const controller = new AbortController();
    let reading = false;
    const read = async () => {
      if (reading || document.visibilityState === "hidden") return;
      reading = true;
      try {
        const review = await api.reviewBeforeCommit({ expectedEpoch: epoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 8000 });
        const on = review.status === "ok" ? review.effective : null;
        const changed = on ? await api.status({ expectedEpoch: epoch, projectId, worktree: checkout }, { signal: controller.signal, timeoutMilliseconds: 15000 })
          .then(reply => reply.status === "ok" ? reply.changedFiles : null) : null;
        if (!controller.signal.aborted) setFacts(current => current?.key === key && current.review === on && current.changed === changed ? current : { key, review: on, changed });
      } catch { /* What cannot be read shows nothing. */ } finally { reading = false; }
    };
    void read();
    const timer = window.setInterval(() => void read(), statusRefreshMilliseconds);
    const focused = () => void read();
    window.addEventListener("focus", focused);
    return () => { controller.abort(); window.clearInterval(timer); window.removeEventListener("focus", focused); };
  }, [epoch, sessionId, projectId, checkout, running, hub, api, key]);
  const shown = facts?.key === key ? facts : null;
  // The request of an agent, read only while the changes may wait for a review.
  const watch = !!shown?.review && (shown.changed ?? 0) > 0 && !running;
  const changes = useReviewList(hub, watch ? scope : null, watch ? changesTarget : null);
  const waiting = !!scope && changes.loaded && changesWaitForReview({ reviewBeforeCommit: shown?.review ?? null, changedFiles: shown?.changed ?? null,
    requested: !!changes.request, running });
  const drafts = !!hub && !!scope && !!counts && counts.drafts > 0;
  if (!hub || !scope || !drafts && !waiting) return null;
  const draftText = drafts ? t(counts!.drafts === 1 ? "{count} draft comment" : "{count} draft comments", { count: counts!.drafts }) : "";
  return <>
    {drafts && <span className="review-composer-status" role="status">
      <AppIcon name="chat" size={13} /><strong>{t("Review")}</strong><span title={draftText}>{draftText}</span>
      {open && <Button size="small" variant="minimal" title={t("Open the review that has these comments")}
        onClick={() => void hub.drafts(scope).then(targets => { if (targets[0]) open(scope, targets[0]); })}>{t("Open")}</Button>}
    </span>}
    {waiting && <span className="review-composer-status" data-kind="waiting" role="status">
      <AppIcon name="changes" size={13} /><span title={t("Changes waiting for your review")}>{t("Changes waiting for your review")}</span>
      {open && <Button size="small" variant="minimal" title={t("Review the uncommitted changes of the session")} onClick={() => open(scope, changesTarget)}>{t("Open")}</Button>}
    </span>}
  </>;
}
