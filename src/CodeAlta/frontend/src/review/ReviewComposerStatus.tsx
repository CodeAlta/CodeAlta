import { useContext, useEffect, useMemo, useRef, useState } from "react";
import { Button } from "@blueprintjs/core";
import { projectGit, reviews, sessionOperations } from "#neoastra";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { changesWaitForReview } from "./changesReview";
import { ReviewOpenerContext, useCheckoutCounts, useReviewList, useReviewsHub } from "./useReviews";

type Api = Readonly<{ reviewBeforeCommit: typeof sessionOperations.reviewBeforeCommit; status: typeof projectGit.status; approval: typeof reviews.approval }>;
const defaultApi: Api = { reviewBeforeCommit: request => sessionOperations.reviewBeforeCommit(request), status: (request, options) => projectGit.status(request, options),
  approval: (request, options) => reviews.approval(request, options) };
const changesTarget = { kind: "changes" } as const;
const statusRefreshMilliseconds = 10_000;

/**
 * What waits for the user's review in the status of a composer:
 * - "Review · 2 draft comments · Open" while the checkout of the session has comments that were not sent; **Open** goes
 *   to the first file or changes they are written on;
 * - "Changes waiting for your review · Open" when the session reviews before commit, its checkout has uncommitted
 *   changes, no agent asked for their review yet, no run is active and the user did not approve the work tree as it is:
 *   the user can review them unasked. Once approved, what the agent leaves uncommitted waits for nothing.
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
  // Read when the composer shows, after each run, every ten seconds, when the window gets the focus back (the switch may
  // be flipped meanwhile, the agent or the user may change files) and after each round of the changes: the switch,
  // whether the checkout has changes, and whether they are the ones the user approved.
  const [facts, setFacts] = useState<Readonly<{ key: string; review: boolean | null; changed: number | null; approved: boolean; round: string | null }> | null>(null);
  const key = JSON.stringify([epoch, sessionId, projectId, checkout]);
  const readNow = useRef<(() => void) | null>(null);
  // The last round of the changes the hub knows; a read says which one it saw.
  const latestRound = useRef<string | null>(null);
  useEffect(() => {
    if (!epoch || !sessionId || !projectId || running || !hub) return;
    const controller = new AbortController();
    let reading = false, again = false;
    const read = async () => {
      if (document.visibilityState === "hidden") return;
      // Asked while a read runs (a round came): read once more after it, which sees the round.
      if (reading) { again = true; return; }
      reading = true;
      const round = latestRound.current;
      try {
        const review = await api.reviewBeforeCommit({ expectedEpoch: epoch, sessionId }, { signal: controller.signal, timeoutMilliseconds: 8000 });
        const on = review.status === "ok" ? review.effective : null;
        const changed = on ? await api.status({ expectedEpoch: epoch, projectId, worktree: checkout }, { signal: controller.signal, timeoutMilliseconds: 15000 })
          .then(reply => reply.status === "ok" ? reply.changedFiles : null) : null;
        // An approval is looked at only while there are changes; one that cannot be read approves nothing.
        const approved = on && (changed ?? 0) > 0 ? await api.approval({ expectedEpoch: epoch, projectId, checkout }, { signal: controller.signal, timeoutMilliseconds: 60_000 })
          .then(reply => reply.status === "ok" && !!reply.round && reply.current, () => false) : false;
        if (!controller.signal.aborted) setFacts(current => current?.key === key && current.review === on && current.changed === changed && current.approved === approved
          && current.round === round ? current : { key, review: on, changed, approved, round });
      } catch { /* What cannot be read shows nothing. */ } finally {
        reading = false;
        if (again && !controller.signal.aborted) { again = false; void read(); }
      }
    };
    void read();
    readNow.current = () => void read();
    const timer = window.setInterval(() => void read(), statusRefreshMilliseconds);
    const focused = () => void read();
    window.addEventListener("focus", focused);
    return () => { controller.abort(); readNow.current = null; window.clearInterval(timer); window.removeEventListener("focus", focused); };
  }, [epoch, sessionId, projectId, checkout, running, hub, api, key]);
  const shown = facts?.key === key ? facts : null;
  // The request of an agent, read only while the changes may wait for a review.
  const watch = !!shown?.review && (shown.changed ?? 0) > 0 && !running;
  const changes = useReviewList(hub, watch ? scope : null, watch ? changesTarget : null);
  // A round of the changes (an approval, here or in the Changes tab) is read at once, not at the next tick.
  // Until then, the facts read before it say nothing: the changes may have just been approved.
  const lastRound = changes.rounds.at(-1)?.id ?? null;
  latestRound.current = lastRound;
  useEffect(() => { if (lastRound) readNow.current?.(); }, [lastRound]);
  const waiting = !!scope && changes.loaded && shown?.round === lastRound && changesWaitForReview({ reviewBeforeCommit: shown?.review ?? null, changedFiles: shown?.changed ?? null,
    requested: !!changes.request, running, approved: !!shown?.approved });
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
