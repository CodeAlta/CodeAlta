import { useId, useLayoutEffect, useRef, useState } from "react";
import { AppIcon } from "./AppIcon";
import { MarkdownContent } from "./MarkdownContent";
import { writeMarkdown, type TimelineItem } from "./timeline";

const longBodyThreshold = 1200;
const previewLength = 240;

export function TimelineMessage({ item }: { item: TimelineItem }) {
  const [copyState, setCopyState] = useState<"idle" | "copied" | "failed">("idle");
  const [wrapDetails, setWrapDetails] = useState(true);
  const [disclosure, setDisclosure] = useState<{ source: string; expanded: boolean } | null>(null);
  const bodyId = useId();
  // A changed record at the same offset must not inherit the previous record's disclosure.
  const body = item.markdown;
  const longBody = (item.category === "user" || item.category === "assistant") && (body?.length ?? 0) > longBodyThreshold;
  const expanded = longBody && disclosure?.source === body && disclosure.expanded;
  // Invalidate the committed source lifetime, including A -> B -> A. Compare metadata
  // values, not arrays allocated by each History refresh; retained records keep expansion.
  const sourceMetadata = JSON.stringify(item.metadata);
  useLayoutEffect(() => {
    setDisclosure(null);
  }, [item.key, item.eventType, item.category, item.title, item.subtitle, item.timestamp,
    body, sourceMetadata, item.truncated, item.bodyOmitted]);
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
  const copyLabel = copyState === "copied" ? "Copied" : copyState === "failed" ? "Copy failed" : `Copy ${item.title} as Markdown`;
  return <article className={`message timeline-message message-${item.category}`}>
    <div className="avatar"><AppIcon name={item.icon} size={17} /></div>
    <div className="message-body">
      <div className="message-heading">
        <span><strong>{item.title}</strong>{item.subtitle && <small>{item.subtitle}</small>}</span>
        <span className="message-actions">
          {item.copyMarkdown && <button type="button" className={`copy-markdown copy-${copyState}`} onClick={() => void copy()}
            aria-label={copyLabel} title={copyLabel}><AppIcon name={copyState === "copied" ? "checked" : copyState === "failed" ? "error" : "copy"} size={15} /><span className="sr-only" aria-live="polite">{copyState === "idle" ? "" : copyLabel}</span></button>}
          <time>{formatTimestamp(item.timestamp)}</time>
        </span>
      </div>
      {item.summary && (item.summaryIsCode ? <code className="timeline-primary-code">{item.summary}</code> : <p className="timeline-summary">{item.summary}</p>)}
      {body && longBody ? <>
        <button type="button" className="quiet-button long-message-toggle" aria-controls={bodyId}
          aria-expanded={expanded} onClick={() => setDisclosure({ source: body, expanded: !expanded })}>
          <AppIcon name="chevronDown" size={14} />{expanded ? "Collapse message" : "Show full message"}
        </button>
        <div id={bodyId}>{expanded ? <MarkdownContent source={body} timelineCodeBlocks />
          : <p className="long-message-preview">Preview (plain text): {plainTextPreview(body)}…</p>}</div>
      </> : body && <MarkdownContent source={body} timelineCodeBlocks />}
      {hasDetails && <details className="event-details"><summary><AppIcon name="chevronDown" size={14} />{item.detailsLabel}</summary>
        <div className="event-detail-body">
          {item.detailMarkdown && item.detailMarkdown !== item.markdown && <>
            {item.eventType === "activity" && item.category === "tool" && <p className="muted-text">Supplied activity message</p>}
            <MarkdownContent source={item.detailMarkdown} timelineCodeBlocks />
          </>}
          {item.details && <pre className={hasToolDetails && wrapDetails ? "tool-detail-pre-wrap" : undefined}>{item.details}</pre>}
          {hasToolDetails && <label className="tool-detail-wrap"><input type="checkbox" checked={wrapDetails}
            onChange={event => setWrapDetails(event.target.checked)} />Wrap lines</label>}
          <ul className="event-meta-inline">{item.metadata.map(value => <li key={value}>{value}</li>)}</ul>
        </div>
      </details>}
      {item.bodyOmitted && <p className="muted-text">{item.category === "user" || item.category === "assistant"
        ? "Additional message content was omitted from this history record." : "Additional diagnostic details were omitted."}</p>}
      {item.truncated && <p className="muted-text">Some details were shortened to fit the desktop history window.</p>}
    </div>
  </article>;
}

// Never parse a cut Markdown document: React escapes this inert excerpt as plain text. Avoid splitting a surrogate pair.
function plainTextPreview(source: string): string {
  const last = source.charCodeAt(previewLength - 1);
  const end = last >= 0xd800 && last <= 0xdbff ? previewLength - 1 : previewLength;
  return source.slice(0, end);
}

function formatTimestamp(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? value : date.toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
}
