import { useEffect, useId, useLayoutEffect, useRef, useState } from "react";
import { AppIcon } from "./AppIcon";
import { MarkdownContent } from "./MarkdownContent";
import { writeMarkdown, type TimelineItem } from "./timeline";
import { useShellLanguage } from "./shellLanguage";
import { timelineTime } from "./sessionTime";
import { FileChangeInspection } from "./FileChangeInspection";
import { ToolRecordInspection } from "./ToolRecordInspection";
import type { HistorySourceTarget } from "./HistorySource";

const longBodyThreshold = 1200;
const previewLength = 240;
// These labels are generated UI chrome in timeline.ts, never provider content.
const detailLabels = Object.freeze(["Details", "File change record details", "Prompt details", "Usage details", "Model details", "Command and result", "Tool details"] as const);

export function TimelineMessage({ item, canInspect, historySource, onOpenSource }: { item: TimelineItem; canInspect?: () => boolean;
  historySource?: HistorySourceTarget; onOpenSource?: (target: HistorySourceTarget) => void }) {
  const { t, locale } = useShellLanguage();
  const timestamp = timelineTime(item.timestamp, locale);
  const [copyState, setCopyState] = useState<"idle" | "copied" | "failed">("idle");
  const [wrapDetails, setWrapDetails] = useState(true);
  const [disclosure, setDisclosure] = useState<{ source: string; expanded: boolean; current?: () => boolean } | null>(null);
  const bodyId = useId();
  // A changed record at the same offset must not inherit the previous record's disclosure.
  const body = item.markdown;
  // Only inline supplied prose gets this control. Detail-only messages keep the
  // existing Details route; terse outcomes and all summaries/notices stay visible.
  const longBody = (body?.length ?? 0) > longBodyThreshold;
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
  const hasDetails = !!(item.detailMarkdown || item.details || item.metadata.length);
  const hasToolDetails = (item.category === "tool" || item.category === "file") && !!item.details;
  const detailLabel = detailLabels.find(label => label === item.detailsLabel);
  // Categories below have fixed UI titles in toTimelineItem; tool/provider names do not.
  const title = item.category === "user" ? t("You") : item.category === "plan" ? t("Plan")
    : item.category === "notes" ? t("Alta notes") : item.category === "error" ? t("Error")
    : item.category === "reasoning" ? t(item.title === "Reasoning summary" ? "Reasoning summary" : "Reasoning") : item.title;
  const copyLabel = copyState === "copied" ? t("Copied") : copyState === "failed" ? t("Copy failed") : t("Copy {title} as Markdown", { title });
  return <article className={`message timeline-message message-${item.category}`}
    data-persisted-message={item.category === "user" || item.category === "assistant" ? "true" : undefined}>
    <div className="avatar"><AppIcon name={item.icon} size={17} /></div>
    <div className="message-body">
      <div className="message-heading">
        <span><strong>{title}</strong>{item.subtitle && <small>{item.subtitle}</small>}</span>
        <span className="message-actions">
          {item.copyMarkdown && <button type="button" className={`copy-markdown copy-${copyState}`} onClick={() => void copy()}
            aria-label={copyLabel} title={copyLabel}><AppIcon name={copyState === "copied" ? "checked" : copyState === "failed" ? "error" : "copy"} size={15} /><span className="sr-only" aria-live="polite">{copyState === "idle" ? "" : copyLabel}</span></button>}
          <time title={timestamp.title} dateTime={timestamp.dateTime}>{timestamp.label}</time>
        </span>
      </div>
      {item.summary && (item.summaryIsCode ? <code className="timeline-primary-code">{item.summary}</code> : <p className="timeline-summary">{item.summary}</p>)}
      {body && longBody ? <>
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
      </> : body && <MarkdownContent source={body} timelineCodeBlocks />}
      {item.fileChanges && <FileChangeInspection key={item.fileChanges.source} changes={item.fileChanges} canInspect={canInspect} />}
      {item.toolRecord && <ToolRecordInspection key={item.toolRecord.source} record={item.toolRecord} canInspect={canInspect} />}
      {hasDetails && <details className="event-details"><summary><AppIcon name="chevronDown" size={14} />{detailLabel ? t(detailLabel) : item.detailsLabel}</summary>
        <div className="event-detail-body">
          {item.detailMarkdown && item.detailMarkdown !== item.markdown && <>
            {item.eventType === "activity" && item.category === "tool" && <p className="muted-text">{t("Supplied activity message")}</p>}
            <MarkdownContent source={item.detailMarkdown} timelineCodeBlocks />
          </>}
          {item.details && <pre className={hasToolDetails && wrapDetails ? "tool-detail-pre-wrap" : undefined}>{item.details}</pre>}
          {hasToolDetails && <label className="tool-detail-wrap"><input type="checkbox" checked={wrapDetails}
            onChange={event => setWrapDetails(event.target.checked)} />{t("Wrap lines")}</label>}
          <ul className="event-meta-inline">{item.metadata.map(value => <li key={value}>{value}</li>)}</ul>
        </div>
      </details>}
      {item.bodyOmitted && <p className="muted-text">{t(item.category === "user" || item.category === "assistant"
        ? "Additional message content was omitted from this history record." : "Additional diagnostic details were omitted.")}</p>}
      {item.truncated && <p className="muted-text">{t("Some details were shortened to fit the desktop history window.")}</p>}
      {historySource && <button type="button" className="quiet-button"
        onKeyDown={event => { if ((event.key === "Enter" || event.key === " ") && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
        onClick={event => {
          if (!event.defaultPrevented && event.currentTarget.isConnected && !event.currentTarget.closest("[inert]")
            && (canInspect?.() ?? true) && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) onOpenSource?.(historySource);
        }}>{t("Read full raw record (paged)")}</button>}
    </div>
  </article>;
}

// Never parse a cut Markdown document: React escapes this inert excerpt as plain text. Avoid splitting a surrogate pair.
function plainTextPreview(source: string): string {
  const last = source.charCodeAt(previewLength - 1);
  const end = last >= 0xd800 && last <= 0xdbff ? previewLength - 1 : previewLength;
  return source.slice(0, end);
}
