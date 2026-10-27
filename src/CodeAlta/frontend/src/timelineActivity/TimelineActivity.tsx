import { memo, useCallback, useContext, useEffect, useId, useMemo, useState } from "react";
import { ActivitySpinner } from "../ActivitySpinner";
import { AppIcon } from "../AppIcon";
import { BackgroundCallsContext } from "../BackgroundTaskViews";
import { useShellLanguage } from "../shellLanguage";
import { commandPreview } from "../TimelineMessage";
import { TimelineGroupView } from "../TimelineEntryGroup";
import { TimelineImages } from "../TimelineImageStrip";
import { combinedImageSource, type TimelineImagePart } from "../timelineImages";
import { sameTimelineGroup, type TimelineGroupProps } from "../timelineGroupView";
import type { TimelineEntryType } from "../timelineEmphasis/timelineEmphasis";
import type { TimelineGroup } from "../toolGroups";
import { McpAppSlot } from "../mcpApps/McpAppView";
import { WorkflowBlock } from "../workflows/WorkflowBlock";
import { activityCalls, activityDuration, activitySummary, activityViews, activityWorkflows, type ActivityStep, type ActivityStepState } from "./activityFold";

/** What every group of the timeline is rendered with, whatever block holds it. */
export type TimelineGroupContext = Omit<TimelineGroupProps, "group" | "entryType">;

type TimelineActivityProps = TimelineGroupContext & {
  groups: readonly TimelineGroup[];
  entryTypes: ReadonlyMap<string, TimelineEntryType>;
  expanded: boolean;
  /** The work of the turn that runs: its line moves and says what is under way. */
  live: boolean;
  /** Folded, the line shows the images the model read during the work under it, in a strip. */
  images?: boolean;
  onToggle: (groups: readonly TimelineGroup[], expanded: boolean) => void;
};

function sameActivity(previous: TimelineActivityProps, next: TimelineActivityProps): boolean {
  if (previous.expanded !== next.expanded || previous.live !== next.live || previous.images !== next.images || previous.onToggle !== next.onToggle
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

/**
 * The images of the rows of an activity, in the order they were read: what a tool gave the model (a screenshot, an
 * image file). The cards of plugins read their images otherwise, and are left out. An image read from a file is named
 * by that file, which the call before it names, rather than by the name the provider gave it.
 */
export function activityImageParts(groups: readonly TimelineGroup[], readImages: TimelineGroupContext["readImages"]): TimelineImagePart[] {
  // A call knows the record of its output, which is the row of the images it gave.
  const files = new Map<string, string>();
  for (const group of groups) {
    for (const row of group.rows) {
      const output = row.source === "history" ? row.item.toolCall?.outputOffset : null;
      const file = output && row.source === "history" ? readFileName(row.item.summary) : null;
      if (output && file) files.set(output, file);
    }
  }
  const parts: TimelineImagePart[] = [];
  for (const group of groups) {
    for (const row of group.rows) {
      if (row.source !== "history" || !row.item.images?.length || row.item.eventType === "plugin") continue;
      const { item } = row, file = files.get(item.key);
      parts.push({ source: readImages?.(item.key), images: file && item.images!.length === 1 ? [{ ...item.images![0], title: file }] : item.images! });
    }
  }
  return parts;
}

// The name of the file a call read, when what it says is a path: one word that ends with a file name.
function readFileName(summary: string | null | undefined): string | null {
  const path = summary?.trim();
  if (!path || /\s/u.test(path) || path.length > 1024) return null;
  const name = path.split(/[\\/]/u).pop() ?? "";
  return /^[^.]+.*\.[A-Za-z0-9]{2,5}$/u.test(name) ? name : null;
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
  const { groups, entryTypes, expanded, live, images = false, onToggle, ...context } = props;
  const { t } = useShellLanguage();
  const backgroundCalls = useContext(BackgroundCallsContext);
  const summary = activitySummary(groups, backgroundCalls);
  const now = useNow(live);
  const bodyId = useId();
  const duration = activityDuration((live ? Math.max(now, summary.endedAt) : summary.endedAt) - summary.startedAt);
  const state = live ? "running" : summary.failed > 0 ? "failed" : "done";
  // Without a thought of the model, the line says that tools are called, and which ones.
  // The strip stays under the folded line once the work is over; opened, the rows show each image where it was read.
  const strip = useMemo(() => !expanded && images ? combinedImageSource(activityImageParts(groups, context.readImages)) : null,
    [expanded, images, groups, context.readImages]);
  const views = useMemo(() => activityViews(groups), [groups]);
  const workflows = useMemo(() => activityWorkflows(groups), [groups]);
  const calls = useMemo(() => activityCalls(groups), [groups]);
  const { canInspect, onOpenTool } = context;
  const openCall = useCallback((rowKey: string, origin: HTMLButtonElement) => { if (canInspect()) onOpenTool(rowKey, origin); }, [canInspect, onOpenTool]);
  const headline = summary.headline ??(summary.tools + summary.jobs > 0 ? t(live ? "Calling tools…" : "Called tools") : t(live ? "Working…" : "Worked"));
  const openStep = (step: ActivityStep, origin: HTMLButtonElement) => {
    if (context.canInspect() && step.item.toolCall) context.onOpenTool(step.rowKey, origin);
    else onToggle(groups, true);
  };
  // Its calls, by which a task in the background goes to the place of the call that started it, even folded.
  return <div className="timeline-activity" role="group" data-state={state} data-expanded={expanded} data-tool-calls={calls || undefined} aria-label={t("Activity")}>
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
    {strip && strip.images.length > 0 && <div className="timeline-activity-images" role="group" aria-label={t("{count} image(s) read", { count: strip.images.length })}>
      <TimelineImages images={strip.images} source={strip.source} />
    </div>}
    {expanded && <div id={bodyId} className="timeline-activity-body">
      {groups.map(group => <TimelineGroupView key={`${context.sessionId}:${group.key}`} {...context} group={group} entryType={entryTypes.get(group.key) ?? "status"} />)}
    </div>}
    {/* The runs of its scripts that ran steps or started sessions, folded or not: each is a block of its own, the width of
        the conversation, which the tile of its call goes to. */}
    {workflows.length > 0 && <div className="timeline-activity-workflows">
      {workflows.map(workflow => <WorkflowBlock key={workflow.rowKey} run={workflow.run} title={workflow.title} rowKey={workflow.rowKey} onOpenCall={openCall} />)}
    </div>}
    {/* The views of its MCP calls, folded or not: a view is the result to see. They keep their place, so a fold that
        opens or closes leaves them as they are. */}
    {views.length > 0 && <div className="timeline-activity-views">
      {views.map(view => <McpAppSlot key={view.offset} offset={view.offset} server={view.server} timestamp={view.timestamp}
        onOpenCall={origin => { if (context.canInspect()) context.onOpenTool(view.rowKey, origin); }} />)}
    </div>}
  </div>;
}, sameActivity);
