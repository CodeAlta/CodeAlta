import { memo, useContext, useEffect, useId, useState } from "react";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { BackgroundCallsContext } from "../BackgroundTaskViews";
import { useShellLanguage } from "../shellLanguage";
import { commandPreview } from "../TimelineMessage";
import { TimelineGroupView } from "../TimelineEntryGroup";
import { sameTimelineGroup, type TimelineGroupProps } from "../timelineGroupView";
import type { TimelineEntryType } from "../timelineEmphasis/timelineEmphasis";
import type { TimelineGroup } from "../toolGroups";
import { activityDuration, activitySummary, type ActivityStep, type ActivityStepState } from "./activityFold";

/** What every group of the timeline is rendered with, whatever block holds it. */
export type TimelineGroupContext = Omit<TimelineGroupProps, "group" | "entryType">;

type TimelineActivityProps = TimelineGroupContext & {
  groups: readonly TimelineGroup[];
  entryTypes: ReadonlyMap<string, TimelineEntryType>;
  expanded: boolean;
  /** The work of the turn that runs: its line moves and says what is under way. */
  live: boolean;
  onToggle: (groups: readonly TimelineGroup[], expanded: boolean) => void;
};

function sameActivity(previous: TimelineActivityProps, next: TimelineActivityProps): boolean {
  if (previous.expanded !== next.expanded || previous.live !== next.live || previous.onToggle !== next.onToggle
    || previous.groups.length !== next.groups.length) return false;
  for (let index = 0; index < next.groups.length; index++) {
    const before = previous.groups[index], after = next.groups[index];
    if (previous.entryTypes.get(before.key) !== next.entryTypes.get(after.key)) return false;
    if (!sameTimelineGroup({ ...previous, group: before, entryType: "status" }, { ...next, group: after, entryType: "status" })) return false;
  }
  return true;
}

/** Follows the time since a moment, every second, while it is asked to. */
function useNow(running: boolean): number {
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (!running) return;
    setNow(Date.now());
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [running]);
  return now;
}

function StepMark({ state }: { state: ActivityStepState }) {
  return state === "running" ? <ActivitySpinner size={10} className="timeline-activity-step-spinner" />
    : <span className="timeline-activity-step-dot" aria-hidden="true" />;
}

/**
 * The work between two messages on one line: what the model thinks last, what it did and for how long. The line
 * opens on the rows it holds, as the timeline shows them; while the work goes on, its last steps are under it.
 */
export const TimelineActivity = memo(function TimelineActivity(props: TimelineActivityProps) {
  const { groups, entryTypes, expanded, live, onToggle, ...context } = props;
  const { t } = useShellLanguage();
  const backgroundCalls = useContext(BackgroundCallsContext);
  const summary = activitySummary(groups, backgroundCalls);
  const now = useNow(live);
  const bodyId = useId();
  const duration = activityDuration((live ? Math.max(now, summary.endedAt) : summary.endedAt) - summary.startedAt);
  const state = live ? "running" : summary.failed > 0 ? "failed" : "done";
  // Without a thought of the model, the line says that tools are called, and which ones.
  const headline = summary.headline ?? (summary.tools + summary.jobs > 0 ? t(live ? "Calling tools…" : "Called tools") : t(live ? "Working…" : "Worked"));
  const openStep = (step: ActivityStep, origin: HTMLButtonElement) => {
    if (context.canInspect() && step.item.toolCall) context.onOpenTool(step.rowKey, origin);
    else onToggle(groups, true);
  };
  return <div className="timeline-activity" role="group" data-state={state} data-expanded={expanded} aria-label={t("Activity")}>
    <button type="button" className="timeline-activity-line" aria-expanded={expanded} aria-controls={expanded ? bodyId : undefined}
      onClick={() => onToggle(groups, !expanded)}>
      <AppIcon name="chevronRight" size={14} className="timeline-activity-chevron" />
      <span className="timeline-activity-mark">{live ? <ActivitySpinner size={12} label={t("Working…")} />
        : <AppIcon name={summary.failed > 0 ? "warning" : "check"} size={13} />}</span>
      <span className="timeline-activity-headline" data-thought={summary.headline ? true : undefined} title={summary.headline ?? undefined}>{headline}
        {!summary.headline && summary.names.length > 0 && <small>{summary.names.join(", ")}</small>}</span>
      <span className="timeline-activity-facts">
        {summary.thoughts > 0 && <span title={t("{count} thought(s)", { count: summary.thoughts })}><AppIcon name="brain" size={12} />{summary.thoughts}</span>}
        {summary.tools > 0 && <span title={t("{count} tool call(s)", { count: summary.tools })}><AppIcon name="tool" size={12} />{summary.tools}</span>}
        {summary.jobs > 0 && <span title={t("{count} background job(s)", { count: summary.jobs })}><AppIcon name="terminal" size={12} />{summary.jobs}</span>}
        {/* While the work goes on, how its steps end; once it is over, only what failed. */}
        {live && summary.succeeded > 0 && <span className="timeline-activity-succeeded" title={t("{count} done", { count: summary.succeeded })}><AppIcon name="check" size={12} />{summary.succeeded}</span>}
        {summary.failed > 0 && <span className="timeline-activity-failed" title={t("{count} failed", { count: summary.failed })}><AppIcon name="close" size={12} />{summary.failed}</span>}
        {live && summary.running > 0 && <span className="timeline-activity-running" title={t("{count} running", { count: summary.running })}><ActivitySpinner size={9} />{summary.running}</span>}
        {(summary.added > 0 || summary.removed > 0) && <span className="file-counts" title={t("Lines added and removed")}><b>+{summary.added}</b> <em>−{summary.removed}</em></span>}
        {duration && <span className="timeline-activity-duration"><AppIcon name="timer" size={12} />{duration}</span>}
      </span>
    </button>
    {!expanded && live && summary.latest.length > 0 && <ol className="timeline-activity-steps" aria-label={t("Latest steps")}>
      {summary.latest.map(step => <li key={step.rowKey} data-state={step.state ?? undefined}>
        <button type="button" onClick={event => openStep(step, event.currentTarget)} aria-haspopup={step.item.toolCall ? "dialog" : undefined}>
          <StepMark state={step.state} />
          <strong>{step.title}</strong>
          {step.detail && (step.detailIsCode ? <code>{commandPreview(step.detail)}</code> : <span>{commandPreview(step.detail)}</span>)}
        </button>
      </li>)}
    </ol>}
    {expanded && <div id={bodyId} className="timeline-activity-body">
      {groups.map(group => <TimelineGroupView key={`${context.sessionId}:${group.key}`} {...context} group={group} entryType={entryTypes.get(group.key) ?? "status"} />)}
    </div>}
  </div>;
}, sameActivity);
