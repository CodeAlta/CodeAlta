import { useContext, useMemo, type KeyboardEvent, type MouseEvent, type Ref } from "react";
import { collectPluginFields, createPluginHtmlSanitizer, pluginActionAttribute, pluginCommandAttribute, pluginValueAttribute } from "./pluginHtmlSanitizer";
import { PluginUiContext, type PluginPane } from "./pluginUi";

/**
 * Shows an HTML fragment given by a plugin. The fragment is sanitized first, so nothing in it runs: the
 * window acts for it. An element with `data-alta-command` runs the plugin command of that name for the
 * pane the fragment is shown in; one with `data-alta-action` reports the action with the values of the
 * fragment's named fields to `onAction` (a dialog). Links are shown and not followed.
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

  return <div ref={ref} className={`plugin-html${className ? ` ${className}` : ""}`} onClick={activate} onAuxClick={event => { if ((event.target as Element).closest("a")) event.preventDefault(); }}
    onChange={event => {
      const target = event.target as unknown as HTMLElement;
      const action = target.tagName === "SELECT" ? target.getAttribute(pluginActionAttribute) : null;
      if (action && onAction) onAction(action, target.getAttribute(pluginValueAttribute), collectPluginFields(event.currentTarget));
    }}
    onKeyDown={keyDown} dangerouslySetInnerHTML={markup} />;
}
