import DOMPurify from "dompurify";
import { useMemo, type KeyboardEvent } from "react";
import { renderMarkdownHtml } from "./markdown";

export function MarkdownContent({ source, timelineCodeBlocks = false }: { source: string; timelineCodeBlocks?: boolean }) {
  const html = useMemo(() => DOMPurify.sanitize(renderMarkdownHtml(source, timelineCodeBlocks), {
    FORBID_ATTR: ["style"],
    FORBID_TAGS: ["button", "form", "input", "option", "select", "style", "textarea"],
  }), [source, timelineCodeBlocks]);
  // React compares this prop by identity. Equivalent persisted refreshes must not
  // replace focused code DOM, its selection or its inner scroll position.
  const codeMarkup = useMemo(() => ({ __html: html }), [html]);

  function scrollCode(event: KeyboardEvent<HTMLDivElement>) {
    if (event.defaultPrevented || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey) return;
    const code = (event.target as Element).closest<HTMLElement>("pre.timeline-code");
    if (!code || !event.currentTarget.contains(code)) return;
    const line = parseFloat(getComputedStyle(code).lineHeight);
    const page = Math.max(line, code.clientHeight - line);
    const maximum = code.scrollHeight - code.clientHeight;
    let top = code.scrollTop;
    switch (event.key) {
      case "ArrowUp": top -= line; break;
      case "ArrowDown": top += line; break;
      case "ArrowLeft": case "ArrowRight": break; // Wrapped code has no horizontal viewport to move.
      case "PageUp": top -= page; break;
      case "PageDown": case " ": top += page; break;
      case "Home": top = 0; break;
      case "End": top = maximum; break;
      default: return;
    }
    // Keyboard navigation belongs to the focused code region even at its boundary.
    // Tab and selection/Copy modifiers retain their native behavior.
    event.preventDefault();
    code.scrollTop = Math.max(0, Math.min(maximum, top));
  }

  return <div className="markdown-content" onKeyDown={timelineCodeBlocks ? scrollCode : undefined}
    dangerouslySetInnerHTML={timelineCodeBlocks ? codeMarkup : { __html: html }} />;
}
