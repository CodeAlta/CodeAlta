import { useRef, useState } from "react";
import { MarkdownContent } from "./MarkdownContent";
import { writeMarkdown, type TimelineItem } from "./timeline";

export function TimelineMessage({ item }: { item: TimelineItem }) {
  const [copyState, setCopyState] = useState<"idle" | "copied" | "failed">("idle");
  const reset = useRef<number | undefined>(undefined);
  async function copy() {
    const markdown = item.markdown ?? item.details;
    if (!markdown) return;
    const state = await writeMarkdown(text => navigator.clipboard.writeText(text), markdown);
    setCopyState(state);
    if (reset.current !== undefined) window.clearTimeout(reset.current);
    reset.current = window.setTimeout(() => setCopyState("idle"), 1600);
  }
  return <article className={`message timeline-message message-${item.category}`}>
    <div className="avatar">{item.icon}</div>
    <div className="message-body">
      <div className="message-heading">
        <span><strong>{item.title}</strong>{item.subtitle && <small>{item.subtitle}</small>}</span>
        <span className="message-actions">
          {(item.markdown || item.details) && <button type="button" className="copy-markdown" onClick={() => void copy()}
            aria-label={`Copy ${item.title} as Markdown`} title="Copy Markdown">{copyState === "copied" ? "Copied" : copyState === "failed" ? "Copy failed" : "Copy Markdown"}</button>}
          <time>{formatTimestamp(item.timestamp)}</time>
        </span>
      </div>
      {item.markdown && <MarkdownContent source={item.markdown} />}
      {item.details && <details className="event-details"><summary>Structured details</summary><pre>{item.details}</pre></details>}
      {!item.markdown && !item.details && item.bodyOmitted && <p className="muted-text">This provider-specific payload is not exposed by the bounded desktop history view.</p>}
      {item.truncated && <p className="muted-text">Some content was shortened to fit the bounded desktop response.</p>}
      <details className="event-meta"><summary>Event metadata</summary><ul>{item.metadata.map(value => <li key={value}>{value}</li>)}</ul><code>{item.eventType} · byte {item.key}</code></details>
    </div>
  </article>;
}

function formatTimestamp(value: string) {
  const date = new Date(value);
  return Number.isNaN(date.valueOf()) ? value : date.toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
}
