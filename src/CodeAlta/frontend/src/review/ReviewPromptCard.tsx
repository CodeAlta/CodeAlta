import { useId, useState } from "react";
import { AppIcon, type IconName } from "../AppIcon";
import { MarkdownContent } from "../MarkdownContent";
import { useShellLanguage } from "../shellLanguage";
import type { MessageKey } from "../localization";
import type { ReviewVerdict } from "./reviewThreads";
import type { ReviewPromptSummary } from "./reviewPrompt";

const verdictLabels: Readonly<Record<ReviewVerdict, MessageKey>> = { approved: "Approved", changes_requested: "Changes requested", commented: "Comments" };
const verdictIcons: Readonly<Record<ReviewVerdict, IconName>> = { approved: "checked", changes_requested: "edit", commented: "chat" };

/**
 * A review round sent as a prompt, in the timeline: its verdict, what was reviewed and how many comments it sent,
 * one line instead of the text the agent receives, which opens below it on demand.
 */
export function ReviewPromptCard({ summary, text, onOpenLink }: { summary: ReviewPromptSummary; text: string; onOpenLink?: ((address: string) => void) | null }) {
  const { t } = useShellLanguage();
  const [open, setOpen] = useState(false);
  const id = useId();
  const what = summary.subject.kind === "file" ? summary.subject.path : t(summary.subject.kind === "branch" ? "The changes of the branch" : "The uncommitted changes");
  const comments = summary.comments + summary.earlier;
  return <div className="review-prompt-card" data-verdict={summary.verdict}>
    <button type="button" className="review-prompt-head" aria-expanded={open} aria-controls={id} onClick={() => setOpen(value => !value)}>
      <AppIcon name={verdictIcons[summary.verdict]} size={14} />
      <strong>{t(verdictLabels[summary.verdict])}</strong>
      <span>{t(comments === 1 ? "{count} comment" : "{count} comments", { count: comments })}</span>
      {summary.round !== null && <span>{t("Round {round}", { round: summary.round })}</span>}
      <span className="review-prompt-what" title={what}>{what}</span>
      <AppIcon name={open ? "chevronDown" : "chevronRight"} size={13} />
      <span className="sr-only">{t(open ? "Hide the review" : "Show the review")}</span>
    </button>
    {open && <div id={id} className="review-prompt-body"><MarkdownContent source={text} timelineCodeBlocks onOpenLink={onOpenLink} /></div>}
  </div>;
}
