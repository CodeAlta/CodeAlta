import { useEffect, useMemo, useState, useSyncExternalStore, type KeyboardEvent } from "react";
import { diagramAppearance, diagrams } from "./diagrams";
import { createMarkdownRenderer } from "./markdownBoundary";
import { appearanceKey, subscribeAppearance } from "./shellColors";

export function MarkdownContent({ source, timelineCodeBlocks = false }: { source: string; timelineCodeBlocks?: boolean }) {
  const render = useMemo(() => createMarkdownRenderer(window), []);
  // Diagrams are drawn on the side, for the window's theme and color scheme, then found by the next render.
  const appearance = useSyncExternalStore(subscribeAppearance, appearanceKey);
  const [drawn, setDrawn] = useState(0);
  const rendered = useMemo(() => {
    const undrawn: string[] = [];
    return { html: render(source, timelineCodeBlocks, undrawn), undrawn };
  }, [render, source, timelineCodeBlocks, appearance, drawn]);
  const html = rendered.html;
  useEffect(() => {
    if (!rendered.undrawn.length) return;
    let current = true;
    // Text still being written changes before this fires, so only settled text is drawn.
    const timer = setTimeout(() => {
      const look = diagramAppearance();
      void Promise.all(rendered.undrawn.map(text => diagrams.draw(text, look))).then(() => { if (current) setDrawn(value => value + 1); });
    }, 200);
    return () => { current = false; clearTimeout(timer); };
  }, [rendered]);
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
