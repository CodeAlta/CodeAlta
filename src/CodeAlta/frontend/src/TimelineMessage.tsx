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

// Display-only command identity, never a command parser or execution target.
export function commandPreview(source: string): string {
  const line = source.trim().split(/\r?\n/)[0];
  const identity = line.match(/^[\w./\\:-]+(?:\s+[\w.-]+)?/)?.[0];
  return (identity ?? line).slice(0, 48) + ((identity ?? line).length > 48 || identity && identity.length < line.length ? "…" : "");
}

export function TimelineMessage({ item, canInspect, historySource, onOpenSource }: { item: TimelineItem; canInspect?: () => boolean;
  historySource?: HistorySourceTarget; onOpenSource?: (target: HistorySourceTarget) => void }) {
  const { t, locale } = useShellLanguage();
  const timestamp = timelineTime(item.timestamp, locale);
  const [copyState, setCopyState] = useState<"idle" | "copied" | "failed">("idle");
  const [details, setDetails] = useState<{ item: TimelineItem; origin: HTMLButtonElement; current?: () => boolean } | null>(null);
  const latestItem = useRef(item); latestItem.current = item;
  const detailCurrent = () => !!details && latestItem.current === details.item && (details.current?.() ?? true) && (canInspect?.() ?? true);
  function closeDetails() {
    const origin = details?.origin;
    const restore = detailCurrent();
    setDetails(null);
    if (restore && origin) requestAnimationFrame(() => {
      if (origin.isConnected && !origin.closest('[inert], [hidden]') && (canInspect?.() ?? true)
        && latestItem.current === details?.item && (details.current?.() ?? true)
        && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) origin.focus();
    });
  }
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
  const hasDetails = !!(body || item.detailMarkdown || item.details || item.metadata.length);
  const compact = ["status", "reasoning", "tool", "file", "prompt"].includes(item.category);
  const excerpt = (item.summary || body || item.detailMarkdown || "").split(/\r?\n\s*\r?\n/)[0];
  const codePreview = item.category === "tool" && item.summaryIsCode
    ? commandPreview(excerpt) : null;
  // Categories below have fixed UI titles in toTimelineItem; tool/provider names do not.
  const title = item.category === "user" ? t("You") : item.category === "plan" ? t("Plan")
    : item.category === "notes" ? t("Alta notes") : item.category === "error" ? t("Error")
    : item.category === "reasoning" ? t(item.title === "Reasoning summary" ? "Reasoning summary" : "Reasoning") : item.title;
  const copyLabel = copyState === "copied" ? t("Copied") : copyState === "failed" ? t("Copy failed") : t("Copy {title} as Markdown", { title });
  return <article className={`message timeline-message message-${item.category}${compact ? " timeline-compact" : ""}`}
    data-persisted-message={item.category === "user" || item.category === "assistant" ? "true" : undefined}>
    <div className="avatar"><AppIcon name={item.icon} size={17} /></div>
    <div className="message-body">
      <div className="message-heading">
        <span><strong>{title}</strong>{item.subtitle && <small>{item.subtitle}</small>}</span>
        {compact && excerpt && <div className="timeline-inline-preview">{codePreview !== null ? <code>{codePreview}</code>
          : item.summary ? excerpt : <MarkdownContent source={excerpt} />}</div>}
        <span className="message-actions">
          {item.toolRecord && <ToolRecordInspection key={item.toolRecord.source} record={item.toolRecord} canInspect={canInspect} />}
          {hasDetails && <button type="button" className="timeline-detail-trigger" aria-label={t("Details")} title={t("Details")} aria-haspopup="dialog"
            onKeyDown={event => { if ((event.key === "Enter" || event.key === " ") && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
            onClick={event => { if (!event.defaultPrevented && current() && event.currentTarget.isConnected && !event.currentTarget.closest('[inert], [hidden]') && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) setDetails({ item, origin: event.currentTarget, current: canInspect }); }}><AppIcon name="info" size={15} /></button>}
          {historySource && <button type="button" className="timeline-source-trigger" aria-label={t("Read full raw record (paged)")} title={t("Read full raw record (paged)")} aria-haspopup="dialog"
            onKeyDown={event => { if ((event.key === "Enter" || event.key === " ") && (event.repeat || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229)) event.preventDefault(); }}
            onClick={event => { if (!event.defaultPrevented && current() && event.currentTarget.isConnected && !event.currentTarget.closest('[inert], [hidden]') && !document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) onOpenSource?.(historySource); }}><AppIcon name="file" size={15} /></button>}
          {item.copyMarkdown && <button type="button" className={`copy-markdown copy-${copyState}`} onClick={() => void copy()}
            aria-label={copyLabel} title={copyLabel}><AppIcon name={copyState === "copied" ? "checked" : copyState === "failed" ? "error" : "copy"} size={15} /><span className="sr-only" aria-live="polite">{copyState === "idle" ? "" : copyLabel}</span></button>}
          <time title={timestamp.title} dateTime={timestamp.dateTime}>{timestamp.label}</time>
        </span>
      </div>
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
      </> : body && <MarkdownContent source={body} timelineCodeBlocks />)}
      {item.fileChanges && <FileChangeInspection key={item.fileChanges.source} changes={item.fileChanges} canInspect={canInspect} />}
      {details && <TimelineDetails item={details.item} current={detailCurrent} onClose={closeDetails} />}
      {item.bodyOmitted && <p className="muted-text">{t(item.category === "user" || item.category === "assistant"
        ? "Additional message content was omitted from this history record." : "Additional diagnostic details were omitted.")}</p>}
      {item.truncated && <p className="muted-text">{t("Some details were shortened to fit the desktop history window.")}</p>}
    </div>
  </article>;
}

// Never parse a cut Markdown document: React escapes this inert excerpt as plain text. Avoid splitting a surrogate pair.
function plainTextPreview(source: string): string {
  const last = source.charCodeAt(previewLength - 1);
  const end = last >= 0xd800 && last <= 0xdbff ? previewLength - 1 : previewLength;
  return source.slice(0, end);
}
