import { useMemo, type KeyboardEvent } from "react";
import { createMarkdownRenderer } from "./markdownBoundary";

export function MarkdownContent({ source, timelineCodeBlocks = false }: { source: string; timelineCodeBlocks?: boolean }) {
  const render = useMemo(() => createMarkdownRenderer(window), []);
  const html = useMemo(() => render(source, timelineCodeBlocks), [render, source, timelineCodeBlocks]);
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

  function suppressLink(event: { target: EventTarget; preventDefault(): void }) {
    if ((event.target as Element).closest("a")) event.preventDefault();
  }
  return <div className="markdown-content" onClick={suppressLink} onAuxClick={suppressLink}
    onKeyDown={event => {
      if (event.key === "Enter") suppressLink(event);
      if (timelineCodeBlocks) scrollCode(event);
    }} dangerouslySetInnerHTML={codeMarkup} />;
}
