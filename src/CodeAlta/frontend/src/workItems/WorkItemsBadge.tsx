import { AppIcon } from "../AppIcon";
import { useShellLanguage } from "../shellLanguage";
import type { WorkCounts } from "./workItems";

/**
 * Explorer marker of a project that has work items: how many wait for a decision, and a dot while one is
 * being carried out. It renders nothing for a project that has none. A click opens the Work items tab on
 * the project; the row around it is a button, so the marker is not one itself.
 */
export function WorkItemsBadge({ counts, onOpen }: { counts: WorkCounts | undefined; onOpen?: () => void }) {
  const { t } = useShellLanguage();
  if (!counts || counts.open + counts.running === 0) return null;
  const waiting = counts.open > 0 ? t(counts.open === 1 ? "{count} work item to do" : "{count} work items to do", { count: counts.open }) : null;
  const running = counts.running > 0 ? t(counts.running === 1 ? "{count} work item in progress" : "{count} work items in progress", { count: counts.running }) : null;
  const label = [waiting, running].filter(Boolean).join(" · ");
  return <span className="work-badge" role="img" aria-label={label} title={label} data-running={counts.running > 0 || undefined}
    onClick={onOpen ? event => { event.stopPropagation(); event.preventDefault(); onOpen(); } : undefined}>
    {counts.running > 0 && <span className="work-badge-pulse" aria-hidden="true" />}
    {counts.open > 0 ? <><AppIcon name="task" size={11} /><span aria-hidden="true">{counts.open}</span></> : <AppIcon name="task" size={11} />}
  </span>;
}
