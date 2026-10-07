import { useContext, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent, type MouseEvent, type Ref } from "react";
import { createPortal } from "react-dom";
import { MarkdownContent } from "./MarkdownContent";
import { collectPluginFields, createPluginHtmlSanitizer, pluginActionAttribute, pluginCommandAttribute, pluginMarkdownClass, pluginMarkdownSource,
  pluginValueAttribute } from "./pluginHtmlSanitizer";
import { PluginUiContext, type PluginPane } from "./pluginUi";

type MarkdownBlock = Readonly<{ element: HTMLElement; source: string }>;

/**
 * Shows an HTML fragment given by a plugin. The fragment is sanitized first, so nothing in it runs: the
 * window acts for it. An element with `data-alta-command` runs the plugin command of that name for the
 * pane the fragment is shown in; one with `data-alta-action` reports the action with the values of the
 * fragment's named fields to `onAction` (a dialog). Links are shown and not followed.
 *
 * An element with the class `alta-markdown` holds Markdown as its text: it is shown by the Markdown
 * component of the window, the one of the timeline, so a fragment has highlighted code and diagrams without
 * a script of its own.
 */
export function PluginHtml({ html, pluginKey = null, pane, className, onAction, onSubmit, ref }: {
  html: string; pluginKey?: string | null; pane?: Partial<PluginPane>; className?: string;
  onAction?: (action: string, value: string | null, values: Record<string, string>) => void;
  /** Enter in a single-line field, for a dialog that has a default button. */
  onSubmit?: () => void;
  ref?: Ref<HTMLDivElement>;
}) {
  const ui = useContext(PluginUiContext);
  const sanitize = useMemo(() => createPluginHtmlSanitizer(window), []);
  // React compares this prop by identity: equivalent refreshes must not replace fields the user is editing.
  const markup = useMemo(() => ({ __html: sanitize(html) }), [sanitize, html]);
  const root = useRef<HTMLDivElement | null>(null);
  const [blocks, setBlocks] = useState<readonly MarkdownBlock[]>([]);
  const sources = useRef(new WeakMap<HTMLElement, string>());
  // React writes the markup once for a given fragment and then leaves it alone: each Markdown element gives its
  // text and becomes the place where the component is drawn, before the fragment is painted.
  useLayoutEffect(() => {
    const found: MarkdownBlock[] = [];
    for (const element of Array.from(root.current?.querySelectorAll<HTMLElement>(`.${pluginMarkdownClass}`) ?? [])) {
      if (element.parentElement?.closest(`.${pluginMarkdownClass}`)) continue;
      // The text is taken once: an element that gave it holds what the component draws, and React runs an
      // effect twice for a new component under StrictMode.
      let source = sources.current.get(element);
      if (source === undefined) {
        source = pluginMarkdownSource(element.textContent ?? "");
        sources.current.set(element, source);
        element.textContent = "";
      }
      found.push({ element, source });
    }
    setBlocks(previous => found.length === 0 && previous.length === 0 ? previous : found);
  }, [markup]);

  function activate(event: MouseEvent<HTMLDivElement>) {
    const target = event.target as Element;
    if (target.closest("a")) event.preventDefault();
    const element = target.closest<HTMLElement>(`[${pluginCommandAttribute}], [${pluginActionAttribute}]`);
    if (!element || !event.currentTarget.contains(element) || element.matches(":disabled")) return;
    // A field that carries an action raises it when it changes, not when it is clicked into.
    if (["INPUT", "SELECT", "TEXTAREA"].includes(element.tagName) && !["checkbox", "radio"].includes((element as HTMLInputElement).type)) return;
    const command = element.getAttribute(pluginCommandAttribute);
    const action = element.getAttribute(pluginActionAttribute);
    if (command) { event.preventDefault(); ui.runNamed(command, pluginKey, pane); }
    else if (action && onAction) { event.preventDefault(); onAction(action, element.getAttribute(pluginValueAttribute), collectPluginFields(event.currentTarget)); }
  }

  function keyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key !== "Enter" || event.nativeEvent.isComposing || event.nativeEvent.keyCode === 229 || event.shiftKey || event.ctrlKey || event.altKey || event.metaKey) return;
    const target = event.target as HTMLElement;
    if (target.tagName !== "INPUT" || ["checkbox", "radio", "button"].includes((target as HTMLInputElement).type)) return;
    const action = target.getAttribute(pluginActionAttribute);
    if (action && onAction) { event.preventDefault(); onAction(action, target.getAttribute(pluginValueAttribute), collectPluginFields(event.currentTarget)); }
    else if (onSubmit) { event.preventDefault(); onSubmit(); }
  }

  const attach = (element: HTMLDivElement | null) => {
    root.current = element;
    if (typeof ref === "function") ref(element);
    else if (ref) ref.current = element;
  };
  return <><div ref={attach} className={`plugin-html${className ? ` ${className}` : ""}`} onClick={activate} onAuxClick={event => { if ((event.target as Element).closest("a")) event.preventDefault(); }}
    onChange={event => {
      const target = event.target as unknown as HTMLElement;
      const action = target.tagName === "SELECT" ? target.getAttribute(pluginActionAttribute) : null;
      if (action && onAction) onAction(action, target.getAttribute(pluginValueAttribute), collectPluginFields(event.currentTarget));
    }}
    onKeyDown={keyDown} dangerouslySetInnerHTML={markup} />
    {blocks.map((block, index) => createPortal(<MarkdownContent source={block.source} timelineCodeBlocks />, block.element, String(index)))}</>;
}
