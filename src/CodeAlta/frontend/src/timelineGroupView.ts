import type { HistoryTimeline } from "./history";
import type { HistorySourceTarget } from "./HistorySource";
import type { ReconciledRow } from "./reconcileTimeline";
import type { TimelineImageReader, TimelineImageSource } from "./timelineImages";
import type { TimelineGroup } from "./toolGroups";
import type { ToolOutputs } from "./toolOutput";

export type HistorySources = ReadonlyMap<string, NonNullable<HistoryTimeline["sources"]>[number]>;

/** What one group of the timeline is rendered from: a row on its own, or a run of tool calls under one heading. */
export type TimelineGroupProps = {
  sessionId: string; group: TimelineGroup;
  revision: HistoryTimeline["revision"] | null; sources: HistorySources;
  /** The images of the prompts being sent, by the key of their row. */
  echoImages: ReadonlyMap<string, TimelineImageSource>;
  readImages?: TimelineImageReader; toolOutputs?: ToolOutputs; canInspect: () => boolean;
  onOpenSource: (target: HistorySourceTarget) => void; onOpenTool: (key: string, origin: HTMLButtonElement) => void;
};

const subject = (row: ReconciledRow) => row.source === "history" ? row.item : row.row;

/** The place of a record in the journal, for the row that offers to show its source. */
export function groupHistorySource(props: Pick<TimelineGroupProps, "revision" | "sources">, key: string) {
  const range = props.revision ? props.sources.get(key) : undefined;
  return range ? { revision: props.revision!, ...range } : undefined;
}

function sameSource(before: ReturnType<typeof groupHistorySource>, after: ReturnType<typeof groupHistorySource>): boolean {
  return before === after || !!before && !!after && before.start === after.start && before.end === after.end
    && before.revision.sessionId === after.revision.sessionId && before.revision.length === after.revision.length
    && before.revision.lastWriteUtcTicks === after.revision.lastWriteUtcTicks;
}

/**
 * Whether a group has nothing to render again. A live update of a running session rebuilds the rows of the
 * whole timeline, and all but the last few are what they were: a group whose rows show the same items is
 * left alone, however long the timeline is. The rows themselves are new objects on each rebuild; what they
 * show is compared.
 */
export function sameTimelineGroup(previous: TimelineGroupProps, next: TimelineGroupProps): boolean {
  const before = previous.group, after = next.group;
  if (previous.sessionId !== next.sessionId || before.key !== after.key || before.tools !== after.tools || before.rows.length !== after.rows.length
    || previous.readImages !== next.readImages || previous.toolOutputs !== next.toolOutputs || previous.canInspect !== next.canInspect
    || previous.onOpenSource !== next.onOpenSource || previous.onOpenTool !== next.onOpenTool) return false;
  const sourcesChanged = previous.sources !== next.sources || previous.revision !== next.revision;
  const echoes = previous.echoImages.size > 0 || next.echoImages.size > 0;
  for (let index = 0; index < after.rows.length; index++) {
    const was = before.rows[index], now = after.rows[index];
    if (was.source !== now.source || was.key !== now.key || subject(was) !== subject(now)) return false;
    if (echoes && previous.echoImages.get(now.key)?.key !== next.echoImages.get(now.key)?.key) return false;
    // A refresh of the history gives a new revision and new ranges: the row is the same while its own did not move.
    if (sourcesChanged && now.source === "history" && !sameSource(groupHistorySource(previous, now.item.key), groupHistorySource(next, now.item.key))) return false;
  }
  return true;
}
