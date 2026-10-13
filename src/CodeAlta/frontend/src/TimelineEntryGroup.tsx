import { memo, type CSSProperties } from "react";
import { AppIcon } from "./AppIcon";
import { liveTextItem, liveToolItem } from "./liveTimeline";
import { useShellLanguage } from "./shellLanguage";
import { TimelineMessage } from "./TimelineMessage";
import { entryEmphasisProperties } from "./timelineEmphasis/timelineEmphasis";
import { groupHistorySource, sameTimelineGroup, type TimelineGroupProps } from "./timelineGroupView";

/** One row of the timeline on its own, or a run of tool calls under one heading. */
export const TimelineGroupView = memo(function TimelineGroupView(props: TimelineGroupProps) {
  const { sessionId, group, entryType, echoImages, readImages, toolOutputs, canInspect, onOpenSource, onOpenTool, promptKeys, onBranch } = props;
  const { t } = useShellLanguage();
  return <div className={group.tools ? "timeline-tool-group" : "timeline-single-row"} role={group.tools ? "group" : undefined}
    aria-label={group.tools ? t("Tools") : undefined} data-entry-type={entryType}
    style={entryEmphasisProperties(entryType) as CSSProperties}>
    {group.tools && <div className="timeline-tool-group-heading"><AppIcon name="tool" size={14} /><span>{t("Tool calls")}</span><span className="tool-group-counts">{t("{count} call(s)", { count: group.rows.length })}
      {(["completed", "failed", "started", "canceled"] as const).map(phase => {
        const count = group.rows.filter(row => (row.source === "history" ? row.item.toolPhase : row.source === "liveTool" ? row.row.phase.toLowerCase() : null) === phase).length;
        return count > 0 ? <span key={phase} data-phase={phase}> · {t(phase === "completed" ? "{count} done" : phase === "failed" ? "{count} failed" : phase === "started" ? "{count} running" : "{count} canceled", { count })}</span> : null;
      })}</span></div>}
    {group.rows.map(item => item.source === "history" ? <TimelineMessage key={`${sessionId}:${item.key}`} item={item.item} canInspect={canInspect} toolTile={group.tools}
    imageSource={!item.item.images ? undefined : echoImages.get(item.key) ?? (item.item.eventType !== "plugin" ? readImages?.(item.item.key) : undefined)}
    rowKey={item.key} onOpenTool={onOpenTool} toolOutputs={toolOutputs} onBranch={onBranch && promptKeys?.has(item.item.key) ? onBranch : undefined}
    onOpenSource={onOpenSource} historySource={groupHistorySource(props, item.item.key)} />
    : <TimelineMessage key={`${sessionId}:${item.key}`} item={item.source === "liveText" ? liveTextItem(item.row) : liveToolItem(item.row)} canInspect={canInspect} toolTile={group.tools}
      rowKey={item.key} onOpenTool={onOpenTool} toolOutputs={toolOutputs} />)}
  </div>;
}, sameTimelineGroup);
