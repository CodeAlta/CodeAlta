import { useMemo, type CSSProperties } from "react";
import { BackgroundCallsContext } from "../BackgroundTaskViews";
import { TimelineBookmarksContext } from "../bookmarks/timelineBookmarks";
import { SystemPromptReaderContext } from "../SystemPromptDialog";
import { MarkdownLinkScopeContext, MarkdownLinksContext } from "../MarkdownContent";
import { PluginPaneContext, PluginUiContext, noPluginContributions } from "../pluginUi";
import { SessionLinksContext } from "../SessionReference";
import { useShellLanguage } from "../shellLanguage";
import type { TimelineItem } from "../timeline";
import { TimelineGroupView } from "../TimelineEntryGroup";
import type { TimelineGroup } from "../toolGroups";
import { emphasisProperties, timelineEntryTypes, type TimelineEmphasis } from "./timelineEmphasis";

type PreviewType = typeof timelineEntryTypes[number];
type Translate = ReturnType<typeof useShellLanguage>["t"];

/** Synthetic presentation data only: no journal identity, reader, script or host action. */
export function timelineAppearanceSample(type: PreviewType, t: Translate): TimelineItem {
  const base: TimelineItem = { key: `appearance-preview-${type}`, eventType: "contentCompleted", category: "status", icon: "info",
    title: t("Preview"), subtitle: null, timestamp: "2026-10-13T12:00:00Z", markdown: null, summary: null,
    summaryIsCode: false, detailMarkdown: null, details: null, detailsLabel: t("Details"), metadata: [],
    truncated: false, bodyOmitted: false, copyMarkdown: null };
  switch (type) {
    case "prompt": return { ...base, category: "user", icon: "user", title: t("You"), markdown: t("Explain how this code works.") };
    case "agentMessage": return { ...base, category: "user", icon: "branch", title: t("Agent message"), delegated: true,
      markdown: t("The agent has finished reviewing the code.") };
    case "answer": return { ...base, category: "assistant", icon: "assistant", title: t("Assistant"), markdown: t("The requested change is complete.") };
    case "step": return { ...base, category: "assistant", icon: "assistant", title: t("Assistant"), markdown: t("I will inspect the relevant code first.") };
    case "reasoning": return { ...base, category: "reasoning", icon: "brain", title: t("Reasoning"), summary: t("Checking the existing behavior.") };
    case "tool": return { ...base, category: "tool", icon: "tool", eventType: "activity", title: "read_file", summary: "src/example.ts",
      toolPhase: "completed", metadata: [t("Completed")], toolOutput: "export const example = true;", toolOutputLines: 12, toolOutputBytes: 512 };
    case "file": return { ...base, category: "file", icon: "file", eventType: "activity", title: t("Modified files"),
      fileChanges: { source: "appearance-preview-file", partial: false, rows: [
        { index: 0, path: "src/example.ts", kind: "update", diff: null, counts: { added: 3, removed: 1 } },
      ] } };
    case "plan": return { ...base, category: "plan", icon: "plan", eventType: "planSnapshot", title: t("Plan"),
      markdown: `- ${t("Review the code, make the change, and verify it.")}` };
    case "systemPrompt": return { ...base, category: "prompt", icon: "prompt", eventType: "system_prompt", title: t("System prompt"),
      prompt: { changed: false, name: "Default", sourcePath: null, hash: "appearance-preview", tokens: 1200,
        systemTokens: 800, developerTokens: 400, applied: true, lossy: false, change: null } };
    case "status": return { ...base, icon: "model", eventType: "sessionUpdate", title: t("Model changed"), summary: t("Completed") };
    case "plugin": return { ...base, category: "plugin", eventType: "plugin", title: t("Turn statistics"), summary: t("Turn complete") };
  }
}

/** Shows the real timeline group in forced resting and hover states, isolated from all host actions. */
export function TimelineAppearancePreview({ type, emphasis }: { type: PreviewType; emphasis: TimelineEmphasis }) {
  const { t } = useShellLanguage();
  // Maps and action handlers belong to this preview, never shared mutable runtime state.
  const isolation = useMemo(() => ({ sources: new Map(), echoImages: new Map(), background: new Map(),
    canInspect: () => false, noop: () => {}, pluginUi: { epoch: null, projectId: null, contributions: noPluginContributions,
      run: () => {}, runNamed: () => {} } }), []);
  const item = timelineAppearanceSample(type, t);
  const group: TimelineGroup = { key: item.key, tools: type === "tool", rows: [{ source: "history", key: item.key, item }] };
  if (type === "tool") group.rows.push({ source: "history", key: `${item.key}-second`,
    item: { ...item, key: `${item.key}-second`, timestamp: "2026-10-13T12:00:01Z", summary: "src/other.ts", toolOutput: "export function readValue() { return example; }" } });
  const style = emphasisProperties({ ...emphasis, enabled: true }) as CSSProperties;
  return <TimelineBookmarksContext.Provider value={null}><SystemPromptReaderContext.Provider value={null}>
    <MarkdownLinksContext.Provider value={null}><MarkdownLinkScopeContext.Provider value={null}>
    <SessionLinksContext.Provider value={null}><PluginPaneContext.Provider value={undefined}>
      <PluginUiContext.Provider value={isolation.pluginUi}><BackgroundCallsContext.Provider value={isolation.background}>
        <div className="timeline-appearance-preview">
          <div className="timeline-appearance-preview-columns">
            {(["normal", "hover"] as const).map(state => <section className="timeline-appearance-preview-column" key={state}>
              <h4>{t(state === "normal" ? "Normal" : "Hover / keyboard focus")}</h4>
              <div inert className="messages timeline-appearance-enabled" data-preview-state={state} style={style}>
                <TimelineGroupView sessionId="appearance-preview" group={group} entryType={type} revision={null}
                  sources={isolation.sources} echoImages={isolation.echoImages} canInspect={isolation.canInspect}
                  onOpenSource={isolation.noop} onOpenTool={isolation.noop} />
              </div>
            </section>)}
          </div>
        </div>
      </BackgroundCallsContext.Provider></PluginUiContext.Provider>
    </PluginPaneContext.Provider></SessionLinksContext.Provider>
  </MarkdownLinkScopeContext.Provider></MarkdownLinksContext.Provider>
  </SystemPromptReaderContext.Provider></TimelineBookmarksContext.Provider>;
}
