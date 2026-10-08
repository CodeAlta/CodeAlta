import { createContext, memo, useContext, useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { AppIcon } from "./AppIcon";
import { MarkdownContent } from "./MarkdownContent";
import { PluginHtml } from "./PluginHtml";
import { writeMarkdown, type TimelineItem } from "./timeline";
import { SessionReference } from "./SessionReference";
import { useShellLanguage } from "./shellLanguage";
import { timelineTime } from "./sessionTime";
import { FileChangeInspection } from "./FileChangeInspection";
import type { HistorySourceTarget } from "./HistorySource";
import { TimelineDetails } from "./TimelineDetails";
import { TimelineImages } from "./TimelineImageStrip";
import type { TimelineImageSource } from "./timelineImages";
import { ActivitySpinner } from "./ActivitySpinner";
import { formatSize, toolState } from "./toolCall";
import { useToolOutput, type ToolOutputs } from "./toolOutput";

const longBodyThreshold = 1200;
const previewLength = 240;
const commandPreviewLength = 80;

/** A host-owned message-link grant. Detached fixtures and other Markdown previews have no opener. */
export const MessageLinksContext = createContext<((address: string) => void) | null>(null);

// Display-only command identity, never a command parser or execution target.
export function commandPreview(source: string): string {
  const line = source.trim().replace(/\s+/g, " ");
  if (line.length <= commandPreviewLength) return line;
  const last = line.charCodeAt(commandPreviewLength - 2);
  const end = last >= 0xd800 && last <= 0xdbff ? commandPreviewLength - 2 : commandPreviewLength - 1;
  return line.slice(0, end) + "…";
}

type TimelineMessageProps = { item: TimelineItem; canInspect?: () => boolean; toolTile?: boolean;
  historySource?: HistorySourceTarget; onOpenSource?: (target: HistorySourceTarget) => void;
  /** Reads the images of the item, when it has some. */
  imageSource?: TimelineImageSource;
  /** The key of the row, and what opens the window of a tool call: given the key and the button that asked. */
  rowKey?: string; onOpenTool?: (rowKey: string, origin: HTMLButtonElement) => void;
  /** The live output of the tool calls: the tile of a running call says what it writes. */
  toolOutputs?: ToolOutputs };

// A long timeline is rendered again on every page of history and every live update: a row whose item,
// source range and image reader are the same has nothing to redo.
function sameMessage(previous: TimelineMessageProps, next: TimelineMessageProps): boolean {
  const before = previous.historySource, after = next.historySource;
  return previous.item === next.item && previous.toolTile === next.toolTile && previous.canInspect === next.canInspect
    && previous.onOpenSource === next.onOpenSource && previous.imageSource?.key === next.imageSource?.key
    && previous.rowKey === next.rowKey && previous.onOpenTool === next.onOpenTool && previous.toolOutputs === next.toolOutputs
    && (before === after || !!before && !!after && before.start === after.start && before.end === after.end
      && before.revision.sessionId === after.revision.sessionId && before.revision.length === after.revision.length
      && before.revision.lastWriteUtcTicks === after.revision.lastWriteUtcTicks);
}

export const TimelineMessage = memo(function TimelineMessage({ item, canInspect, historySource, onOpenSource, toolTile = false, imageSource,
  rowKey, onOpenTool, toolOutputs }: TimelineMessageProps) {
  const { t, locale } = useShellLanguage();
  const openLink = useContext(MessageLinksContext);
  const messageLink = openLink && item.category === "assistant"
    ? (address: string) => { if (canInspect?.() ?? true) openLink(address); } : undefined;
  const state = item.category === "tool" ? toolState(item.toolPhase) : null;
  // A call that runs is followed: its tile says how much it wrote and its last line.
  const live = useToolOutput(toolOutputs, item.toolCall?.activityId, toolTile && (state === "running" || state === "pending"));
  const timestamp = timelineTime(item.timestamp, locale);
  const toolTrigger = useRef<HTMLButtonElement>(null);
  const [copyState, setCopyState] = useState<"idle" | "copied" | "failed">("idle");
  const [details, setDetails] = useState<{ item: TimelineItem; origin: HTMLButtonElement; current?: () => boolean } | null>(null);
  const latestItem = useRef(item); latestItem.current = item;
  // Reconciliation may allocate an identical presentation on an unrelated App render.
  // Revision-key remounts and changed presentation values still retire the dialog.
  const sameDetailItem = () => !!details && (latestItem.current === details.item || JSON.stringify(latestItem.current) === JSON.stringify(details.item));
  const detailCurrent = () => sameDetailItem() && (details?.current?.() ?? true) && (canInspect?.() ?? true);
  function closeDetails() {
    const origin = details?.origin;
    const restore = detailCurrent();
    setDetails(null);
    if (restore && origin) requestAnimationFrame(() => {
      if (origin.isConnected && !origin.closest('[inert], [hidden]') && (canInspect?.() ?? true)
        && sameDetailItem() && (details?.current?.() ?? true)
        && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) origin.focus();
    });
  }
  const [disclosure, setDisclosure] = useState<{ source: string; expanded: boolean; current?: () => boolean } | null>(null);
  const bodyId = useId();
  // A changed record at the same offset must not inherit the previous record's disclosure.
  const body = item.markdown;
  // Conversation messages always render their supplied Markdown. Optional
  // disclosure is reserved for long supporting diagnostics, never the conversation.
  const longBody = item.category !== "user" && item.category !== "assistant" && (body?.length ?? 0) > longBodyThreshold;
  // Value identity of the bounded presentation, not original journal bytes. Do
  // not serialize terse/detail-only records that have no inline disclosure.
  const source = longBody ? JSON.stringify(item) : "";
  const latest = useRef({ source, canInspect }); latest.current = { source, canInspect };
  const current = () => latest.current.source === source && (canInspect?.() ?? true) && (latest.current.canInspect?.() ?? true);
  const expanded = longBody && disclosure?.source === source && disclosure.expanded && (disclosure.current?.() ?? true) && current();
  // Invalidate the committed source lifetime, including A -> B -> A. Compare metadata
  // values, not arrays allocated by each History refresh; retained records keep expansion.
  useLayoutEffect(() => {
    setDisclosure(null);
  }, [source]);
  useLayoutEffect(() => {
    if (disclosure && (!(disclosure.current?.() ?? true) || !current())) setDisclosure(null);
  });
  useEffect(() => {
    if (!longBody) return;
    const retire = (event: Event) => { if (event.target instanceof HTMLDialogElement) setDisclosure(null); };
    document.addEventListener("beforetoggle", retire, true);
    return () => document.removeEventListener("beforetoggle", retire, true);
  }, [longBody]);
  const reset = useRef<number | undefined>(undefined);
  const active = useRef(false);
  const copySequence = useRef(0);
  useLayoutEffect(() => {
    active.current = true;
    setCopyState("idle");
    return () => {
      active.current = false;
      copySequence.current++;
      if (reset.current !== undefined) window.clearTimeout(reset.current);
      reset.current = undefined;
    };
  }, [item.key, item.eventType, item.category, item.title, item.timestamp, item.copyMarkdown, item.truncated, item.bodyOmitted]);
  async function copy() {
    const text = item.copyMarkdown;
    if (!text || !active.current) return;
    const sequence = ++copySequence.current;
    if (reset.current !== undefined) window.clearTimeout(reset.current);
    reset.current = undefined;
    setCopyState("idle");
    const state = await writeMarkdown(value => navigator.clipboard.writeText(value), text);
    if (!active.current || sequence !== copySequence.current) return;
    setCopyState(state);
    reset.current = window.setTimeout(() => {
      if (!active.current || sequence !== copySequence.current) return;
      reset.current = undefined;
      setCopyState("idle");
    }, 1600);
  }
  const hasDetails = !!(body || item.detailMarkdown || item.details || item.metadata.length || item.detailSections?.length);
  const compact = ["status", "reasoning", "tool", "file", "prompt", "plugin"].includes(item.category);
  const excerpt = (item.summary || body || item.detailMarkdown || "").split(/\r?\n\s*\r?\n/)[0];
  const codePreview = item.category === "tool" && item.summaryIsCode
    ? commandPreview(excerpt) : null;
  const toolOutput = live?.last || item.toolOutput;
  const outcome = ({ completed: "Completed", failed: "Failed", canceled: "Canceled", requested: "Pending",
    started: "Running", progressed: "Running", selected: "Running", deselected: "Completed" } as Record<string, "Completed" | "Failed" | "Canceled" | "Pending" | "Running">)[item.toolPhase ?? ""];
  // Categories below have fixed UI titles in toTimelineItem; tool/provider names do not.
  const title = item.category === "file" ? t("Modified files") : item.category === "image" ? t("Image read") : item.category === "user" ? t(item.delegated ? "Agent message" : "You") : item.category === "plan" ? t("Plan")
    : item.category === "assistant" ? t("Assistant") : item.category === "notes" ? t("Notes") : item.category === "error" ? t("Error")
    : item.category === "reasoning" ? t(item.title === "Reasoning summary" ? "Reasoning summary" : "Reasoning") : item.title;
  const copyLabel = copyState === "copied" ? t("Copied") : copyState === "failed" ? t("Copy failed") : t("Copy {title} as Markdown", { title });
  // A tool call has its own window, which the timeline holds: it follows the call while it runs. A row that is
  // no call (an output whose call is not in the window) keeps the details of its record.
  const callWindow = item.category === "tool" && !!item.toolCall && !!onOpenTool && !!rowKey;
  function openDetails(origin: HTMLButtonElement) {
    if (!current() || !origin.isConnected || origin.closest('[inert], [hidden]')
      || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
    if (callWindow) onOpenTool!(rowKey!, origin);
    else setDetails({ item, origin, current: canInspect });
  }
  return <article className={`message timeline-message message-${item.category}${compact ? " timeline-compact" : ""}`}
    onClick={event => {
      if (item.category === "tool" && hasDetails && toolTrigger.current && !event.defaultPrevented
        && !(event.target as HTMLElement).closest("button, a, dialog, input, textarea") && !window.getSelection()?.toString()) openDetails(toolTrigger.current);
    }}
    data-tool-phase={item.toolPhase} data-delegated={item.delegated ? "true" : undefined} data-persisted-message={item.category === "user" || item.category === "assistant" ? "true" : undefined}>
    <div className="avatar"><AppIcon name={item.icon} size={17} /></div>
    <div className="message-body">
      <div className="message-heading">
        <span>{item.category === "tool" && hasDetails ? <button ref={toolTrigger} type="button" className="tool-tile-title" aria-haspopup="dialog"
          onKeyDown={event => { if ((event.key === "Enter" || event.key === " ") && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
          onClick={event => { if (!event.defaultPrevented) openDetails(event.currentTarget); }}>{state === "running"
            ? <ActivitySpinner size={10} className="tool-state-spinner" /> : <span className="tool-state-dot" aria-hidden="true">●</span>} <strong>{title}</strong></button>
          : item.category !== "reasoning" && <strong>{title}</strong>}{!toolTile && item.subtitle && <small>{item.subtitle === "Sending…" || item.subtitle === "Pending" || item.subtitle === "Failed" || item.subtitle === "Streaming" ? t(item.subtitle) : item.subtitle}</small>}
          {item.delegated && item.sourceSessionId && <SessionReference sessionId={item.sourceSessionId} />}</span>
        {compact && item.html ? <div className="timeline-inline-preview timeline-plugin-html"><PluginHtml html={item.html} pluginKey={item.pluginKey} /></div>
          : compact && excerpt && !toolTile && item.category !== "file" && <div className="timeline-inline-preview">{codePreview !== null ? <code>{codePreview}</code>
          : item.summary ? excerpt : <MarkdownContent source={excerpt} />}</div>}
        {!toolTile && item.toolChanges && <span className="file-counts tool-changes" title={t("Lines added and removed by this call")}><b>+{item.toolChanges.added}</b> <em>−{item.toolChanges.removed}</em></span>}
        <span className="message-actions">
          {hasDetails && !callWindow && <button type="button" className="timeline-detail-trigger" aria-label={t("Details")} title={t("Details")} aria-haspopup="dialog"
            onKeyDown={event => { if ((event.key === "Enter" || event.key === " ") && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
            onClick={event => { if (!event.defaultPrevented) openDetails(event.currentTarget); }}><AppIcon name="info" size={15} /></button>}
          {item.copyMarkdown && <button type="button" className={`copy-markdown copy-${copyState}`} onClick={() => void copy()}
            aria-label={copyLabel} title={copyLabel}><AppIcon name={copyState === "copied" ? "checked" : copyState === "failed" ? "error" : "copy"} size={15} /><span className="sr-only" aria-live="polite">{copyState === "idle" ? "" : copyLabel}</span></button>}
          <time title={timestamp.title} dateTime={timestamp.dateTime}>{timestamp.label}</time>
        </span>
      </div>
      {toolTile && codePreview && <code className="tool-command-preview">{codePreview}</code>}
      {/* A path too long for the tile loses its start: its last names say which file it is. */}
      {toolTile && !codePreview && item.summary && (/[\\/]/.test(item.summary) && !/\s/.test(item.summary)
        ? <div className="tool-argument-preview" data-path title={item.summary}><bdi>{item.summary}</bdi></div>
        : <div className="tool-argument-preview">{commandPreview(item.summary)}</div>)}
      {toolTile && <div className="tool-result-summary">
        {outcome && <span className="tool-outcome">{t(outcome)}</span>}
        {!!item.toolExitCode && <span className="tool-exit-code">{t("Exit code {code}", { code: item.toolExitCode })}</span>}
        {item.toolChanges && <span className="file-counts tool-changes" title={t("Lines added and removed by this call")}><b>+{item.toolChanges.added}</b> <em>−{item.toolChanges.removed}</em></span>}
        {live && live.total > 0 ? <span className="tool-output-stats">{live.lines}L · {formatSize(live.total)}</span>
          : item.toolOutputBytes != null && item.toolOutputLines != null && <span className="tool-output-stats">{item.toolOutputLines}L · {(item.toolOutputBytes / 1024).toFixed(1)} KB</span>}
        {toolOutput && <span className="tool-result-preview" title={toolOutput.slice(0, 512)}>{commandPreview(toolOutput)}</span>}
      </div>}
      {!compact && item.summary && (item.summaryIsCode ? <code className="timeline-primary-code">{item.summary}</code> : <p className="timeline-summary">{item.summary}</p>)}
      {!compact && (body && longBody ? <>
        <button type="button" className="quiet-button long-message-toggle" aria-controls={bodyId}
          aria-expanded={expanded} disabled={!current()}
          onKeyDown={event => { if ((event.key === "Enter" || event.key === " ") && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
          onClick={event => {
            if (event.defaultPrevented || !active.current || !current() || !event.currentTarget.isConnected || event.currentTarget.closest("[inert]")
              || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
            setDisclosure({ source, expanded: !expanded, current: canInspect });
          }}>
          <AppIcon name="chevronDown" size={14} />{t(expanded ? "Collapse message" : "Show full message")}
        </button>
        <div id={bodyId}>{expanded ? <MarkdownContent source={body} timelineCodeBlocks />
          : <p className="long-message-preview">{t("Preview (plain text):")} {plainTextPreview(body)}…</p>}</div>
      </> : body && <MarkdownContent source={body} timelineCodeBlocks onOpenLink={messageLink} />)}
      {item.images && <TimelineImages images={item.images} source={imageSource} />}
      {item.fileChanges && <FileChangeInspection key={item.fileChanges.source} changes={item.fileChanges} canInspect={canInspect} />}
      {details && <TimelineDetails item={details.item} current={detailCurrent} onClose={closeDetails} />}
    </div>
  </article>;
}, sameMessage);

// Never parse a cut Markdown document: React escapes this inert excerpt as plain text. Avoid splitting a surrogate pair.
function plainTextPreview(source: string): string {
  const last = source.charCodeAt(previewLength - 1);
  const end = last >= 0xd800 && last <= 0xdbff ? previewLength - 1 : previewLength;
  return source.slice(0, end);
}
