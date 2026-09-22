import { useRef, useState } from "react";
import { AppIcon } from "./AppIcon";
import { MarkdownContent } from "./MarkdownContent";
import { writeMarkdown, type TimelineItem } from "./timeline";

export function TimelineMessage({ item }: { item: TimelineItem }) {
  const [copyState, setCopyState] = useState<"idle" | "copied" | "failed">("idle");
  const reset = useRef<number | undefined>(undefined);
  async function copy() {
    if (!item.copyMarkdown) return;
    const state = await writeMarkdown(text => navigator.clipboard.writeText(text), item.copyMarkdown);
    setCopyState(state);
    if (reset.current !== undefined) window.clearTimeout(reset.current);
    reset.current = window.setTimeout(() => setCopyState("idle"), 1600);
  }
  const hasDetails = !!(item.detailMarkdown || item.details || item.metadata.length);
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
      {item.markdown && <MarkdownContent source={item.markdown} />}
      {hasDetails && <details className="event-details"><summary><AppIcon name="chevronDown" size={14} />{item.detailsLabel}</summary>
        <div className="event-detail-body">
          {item.detailMarkdown && item.detailMarkdown !== item.markdown && <MarkdownContent source={item.detailMarkdown} />}
          {item.details && <pre>{item.details}</pre>}
          <ul className="event-meta-inline">{item.metadata.map(value => <li key={value}>{value}</li>)}</ul>
        </div>
      </details>}
      {!item.markdown && !item.summary && !hasDetails && item.bodyOmitted && <p className="muted-text">Additional diagnostic details were omitted.</p>}
      {item.truncated && <p className="muted-text">Some details were shortened to fit the desktop history window.</p>}
    </div>
  </article>;
}

function formatTimestamp(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? value : date.toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
}
