import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { AppIcon } from "./AppIcon";
import { MarkdownContent } from "./MarkdownContent";
import { writeMarkdown, type TimelineItem } from "./timeline";
import { useShellLanguage } from "./shellLanguage";
import { timelineTime } from "./sessionTime";
import { FileChangeInspection } from "./FileChangeInspection";
import { ToolRecordInspection } from "./ToolRecordInspection";
import type { HistorySourceTarget } from "./HistorySource";
import { TimelineDetails } from "./TimelineDetails";

const longBodyThreshold = 1200;
const previewLength = 240;
const commandPreviewLength = 80;

// Display-only command identity, never a command parser or execution target.
export function commandPreview(source: string): string {
  const line = source.trim().replace(/\s+/g, " ");
  if (line.length <= commandPreviewLength) return line;
  const last = line.charCodeAt(commandPreviewLength - 2);
  const end = last >= 0xd800 && last <= 0xdbff ? commandPreviewLength - 2 : commandPreviewLength - 1;
  return line.slice(0, end) + "…";
}

export function TimelineMessage({ item, canInspect, historySource, onOpenSource, toolTile = false }: { item: TimelineItem; canInspect?: () => boolean; toolTile?: boolean;
  historySource?: HistorySourceTarget; onOpenSource?: (target: HistorySourceTarget) => void }) {
  const { t, locale } = useShellLanguage();
  const timestamp = timelineTime(item.timestamp, locale);
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
  const hasDetails = !!(body || item.detailMarkdown || item.details || item.metadata.length);
  const compact = ["status", "reasoning", "tool", "file", "prompt"].includes(item.category);
  const excerpt = (item.summary || body || item.detailMarkdown || "").split(/\r?\n\s*\r?\n/)[0];
  const codePreview = item.category === "tool" && item.summaryIsCode
    ? commandPreview(excerpt) : null;
  const toolOutput = item.toolOutput ?? item.toolRecord?.fields.find(field => ["result.content", "result.detailedContent", "output.body", "error.message"].includes(field.path))?.text;
  const outcome = ({ completed: "Completed", failed: "Failed", canceled: "Canceled", requested: "Pending",
    started: "Running", progressed: "Running", selected: "Running", deselected: "Completed" } as Record<string, "Completed" | "Failed" | "Canceled" | "Pending" | "Running">)[item.toolPhase ?? ""];
  // Categories below have fixed UI titles in toTimelineItem; tool/provider names do not.
  const title = item.category === "file" ? t("Modified files") : item.category === "user" ? t("You") : item.category === "plan" ? t("Plan")
    : item.category === "assistant" ? t("Assistant") : item.category === "notes" ? t("Alta notes") : item.category === "error" ? t("Error")
    : item.category === "reasoning" ? t(item.title === "Reasoning summary" ? "Reasoning summary" : "Reasoning") : item.title;
  const copyLabel = copyState === "copied" ? t("Copied") : copyState === "failed" ? t("Copy failed") : t("Copy {title} as Markdown", { title });
  function openDetails(origin: HTMLButtonElement) {
    if (current() && origin.isConnected && !origin.closest('[inert], [hidden]')
      && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) setDetails({ item, origin, current: canInspect });
  }
  return <article className={`message timeline-message message-${item.category}${compact ? " timeline-compact" : ""}`}
    data-tool-phase={item.toolPhase} data-persisted-message={item.category === "user" || item.category === "assistant" ? "true" : undefined}>
    <div className="avatar"><AppIcon name={item.icon} size={17} /></div>
    <div className="message-body">
      <div className="message-heading">
        <span>{toolTile && hasDetails ? <button type="button" className="tool-tile-title" aria-haspopup="dialog"
          onKeyDown={event => { if ((event.key === "Enter" || event.key === " ") && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
          onClick={event => { if (!event.defaultPrevented) openDetails(event.currentTarget); }}><span className="tool-state-dot" aria-hidden="true">●</span> <strong>{title}</strong></button>
          : item.category !== "reasoning" && <strong>{title}</strong>}{!toolTile && item.subtitle && <small>{item.subtitle}</small>}</span>
        {compact && excerpt && !toolTile && item.category !== "file" && <div className="timeline-inline-preview">{codePreview !== null ? <code>{codePreview}</code>
          : item.summary ? excerpt : <MarkdownContent source={excerpt} />}</div>}
        <span className="message-actions">
          {item.toolRecord && <ToolRecordInspection key={item.toolRecord.source} record={item.toolRecord} canInspect={canInspect} />}
          {hasDetails && <button type="button" className="timeline-detail-trigger" aria-label={t("Details")} title={t("Details")} aria-haspopup="dialog"
            onKeyDown={event => { if ((event.key === "Enter" || event.key === " ") && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
            onClick={event => { if (!event.defaultPrevented) openDetails(event.currentTarget); }}><AppIcon name="info" size={15} /></button>}
          {item.copyMarkdown && <button type="button" className={`copy-markdown copy-${copyState}`} onClick={() => void copy()}
            aria-label={copyLabel} title={copyLabel}><AppIcon name={copyState === "copied" ? "checked" : copyState === "failed" ? "error" : "copy"} size={15} /><span className="sr-only" aria-live="polite">{copyState === "idle" ? "" : copyLabel}</span></button>}
          {!toolTile && <time title={timestamp.title} dateTime={timestamp.dateTime}>{timestamp.label}</time>}
        </span>
      </div>
      {toolTile && codePreview && <code className="tool-command-preview">{codePreview}</code>}
      {toolTile && <div className="tool-result-summary">
        {outcome && <span className="tool-outcome">{t(outcome)}</span>}
        {item.toolOutputBytes != null && item.toolOutputLines != null && <span className="tool-output-stats">{item.toolOutputLines}L · {(item.toolOutputBytes / 1024).toFixed(1)} KB</span>}
        {toolOutput && <span className="tool-result-preview" title={toolOutput.slice(0, 512)}>{commandPreview(toolOutput)}</span>}
      </div>}
      {!compact && item.summary && (item.summaryIsCode ? <code className="timeline-primary-code">{item.summary}</code> : <p className="timeline-summary">{item.summary}</p>)}
      {toolTile && !codePreview && item.summary && <div className="tool-argument-preview">{commandPreview(item.summary)}</div>}
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
      </> : body && <MarkdownContent source={body} timelineCodeBlocks />)}
      {item.fileChanges && <FileChangeInspection key={item.fileChanges.source} changes={item.fileChanges} canInspect={canInspect} />}
      {details && <TimelineDetails item={details.item} current={detailCurrent} onClose={closeDetails} />}
    </div>
  </article>;
}

// Never parse a cut Markdown document: React escapes this inert excerpt as plain text. Avoid splitting a surrogate pair.
function plainTextPreview(source: string): string {
  const last = source.charCodeAt(previewLength - 1);
  const end = last >= 0xd800 && last <= 0xdbff ? previewLength - 1 : previewLength;
  return source.slice(0, end);
}
