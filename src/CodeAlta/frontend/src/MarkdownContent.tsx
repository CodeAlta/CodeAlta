import { useEffect, useMemo, useState, useSyncExternalStore, type KeyboardEvent } from "react";
import { diagramAppearance, diagrams } from "./diagrams";
import { translate } from "./localization";
import { createMarkdownRenderer } from "./markdownBoundary";
import { markdownLinkActivation, safeMarkdownHref } from "./markdownLinks";
import { appearanceKey, subscribeAppearance } from "./shellColors";
import { useShellLanguage } from "./shellLanguage";

/**
 * A text of Markdown, rendered through the sanitizing boundary. A message breaks its lines where its text does;
 * a `document` (a file, the instructions of a skill) is wrapped in its source, and its lines follow each other.
 */
export function MarkdownContent({ source, timelineCodeBlocks = false, document: asDocument = false, onOpenLink }: {
  source: string; timelineCodeBlocks?: boolean; document?: boolean;
  /** Explicit trusted-container grant; without it every link remains inert. */
  onOpenLink?: (address: string) => void;
}) {
  // The renderer is made once for a language: the titles of the alerts are in it.
  const { locale } = useShellLanguage();
  const render = useMemo(() => createMarkdownRenderer(window, { note: translate(locale, "Note"), tip: translate(locale, "Tip"),
    important: translate(locale, "Important"), warning: translate(locale, "Warning"), caution: translate(locale, "Caution"), copy: translate(locale, "Copy") }), [locale]);
  // Diagrams are drawn on the side, for the window's theme and color scheme, then found by the next render.
  const appearance = useSyncExternalStore(subscribeAppearance, appearanceKey);
  const [drawn, setDrawn] = useState(0);
  const rendered = useMemo(() => {
    const undrawn: string[] = [];
    return { html: render(source, timelineCodeBlocks, undrawn, { document: asDocument }), undrawn };
  }, [render, source, timelineCodeBlocks, asDocument, appearance, drawn]);
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

  function activateLink(event: { target: EventTarget; currentTarget: HTMLDivElement; nativeEvent: MouseEvent | globalThis.KeyboardEvent;
    preventDefault(): void; defaultPrevented: boolean }) {
    const target = event.target instanceof Element ? event.target : null;
    // Enter on the renderer's Copy button must still produce its native click, even inside an authored anchor.
    if (event.nativeEvent.type === "keydown" && target?.closest("button.markdown-copy")) return;
    const link = target?.closest("a");
    if (!link || !event.currentTarget.contains(link)) return;
    const activate = !event.defaultPrevented && markdownLinkActivation(event.nativeEvent);
    event.preventDefault(); // Never navigate the WebView, including ungranted and unsupported gestures.
    if (!activate || !onOpenLink || !link.isConnected || link.closest('[inert], [hidden]')
      || target?.closest("button") || document.querySelector('dialog[open], [role="dialog"][aria-modal="true"]')) return;
    const address = link.getAttribute("href"); // Read the sanitized literal, not the browser-resolved property.
    if (address && safeMarkdownHref(address)) onOpenLink(address);
  }
  // The button of a code block copies the text of that block, as it is written, and says so for a moment.
  function copyCode(event: { target: EventTarget }) {
    const button = (event.target as Element).closest<HTMLElement>("button.markdown-copy");
    const code = button?.parentElement?.querySelector(":scope > code");
    if (!button || !code) return;
    void navigator.clipboard?.writeText((code.textContent ?? "").replace(/\n$/, "")).then(() => {
      button.setAttribute("data-copied", "true");
      window.setTimeout(() => button.removeAttribute("data-copied"), 1400);
    }, () => { /* The clipboard is unavailable: nothing was copied and nothing changes. */ });
  }
  return <div className="markdown-content" onClick={event => {
    const handled = event.defaultPrevented;
    activateLink(event);
    if (!handled) copyCode(event); // Our navigation suppression must not swallow Copy inside an anchor.
  }} onAuxClick={activateLink}
    onKeyDown={event => {
      if (event.key === "Enter") activateLink(event);
      if (timelineCodeBlocks) scrollCode(event);
    }} dangerouslySetInnerHTML={codeMarkup} />;
}
