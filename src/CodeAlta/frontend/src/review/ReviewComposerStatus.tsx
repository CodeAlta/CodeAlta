import { Button } from "@blueprintjs/core";
import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import { ReviewOpenerContext, useCheckoutCounts, useReviewsHub } from "./useReviews";
import { useContext } from "react";

/**
 * "Review · 2 draft comments · Open" in the status of a composer, while the checkout of its session has comments
 * that were not sent. **Open** goes to the first file or changes they are written on.
 */
export function ReviewComposerStatus({ projectId, checkout }: { projectId: string | null; checkout: string | null }) {
  const { t } = useShellLanguage();
  const hub = useReviewsHub();
  const open = useContext(ReviewOpenerContext);
  const scope = projectId ? { projectId, checkout } : null;
  const counts = useCheckoutCounts(hub, scope);
  if (!hub || !scope || !counts || counts.drafts === 0) return null;
  const label = t(counts.drafts === 1 ? "{count} draft comment" : "{count} draft comments", { count: counts.drafts });
  return <span className="review-composer-status" role="status">
    <AppIcon name="chat" size={13} /><strong>{t("Review")}</strong><span>{label}</span>
    {open && <Button size="small" variant="minimal" title={t("Open the review that has these comments")}
      onClick={() => void hub.drafts(scope).then(targets => { if (targets[0]) open(scope, targets[0]); })}>{t("Open")}</Button>}
  </span>;
}
