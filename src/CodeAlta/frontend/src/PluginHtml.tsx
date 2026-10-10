import { useCallback, useContext, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent, type MouseEvent, type Ref } from "react";
import { createPortal } from "react-dom";
import { MarkdownContent, MarkdownLinksContext } from "./MarkdownContent";
import { markdownLinkActivation, safeMarkdownHref } from "./markdownLinks";
import { collectPluginFields, createPluginHtmlSanitizer, pluginActionAttribute, pluginChartClass, pluginChartLabelAttribute, pluginChartOptionAttribute,
  pluginCommandAttribute, pluginMarkdownClass, pluginMarkdownSource, pluginValueAttribute } from "./pluginHtmlSanitizer";
import { PluginChartBlock, readPluginChart, type PluginChartSource } from "./pluginScript/PluginChartBlock";
import { PluginUiContext, type PluginPane } from "./pluginUi";
import { AltaReactContext, ScriptBoundary, ScriptFailure, usePluginScript, usePluginScriptMount, useWhenShown, type PluginScriptProps } from "./pluginScript/PluginScript";

type MarkdownBlock = Readonly<{ element: HTMLElement; source: string }>;
type ChartBlock = Readonly<{ element: HTMLElement; source: PluginChartSource }>;

/**
 * Shows an HTML fragment given by a plugin. The fragment is sanitized first, so nothing in it runs: the
 * window acts for it. An element with `data-alta-command` runs the plugin command of that name for the
 * pane the fragment is shown in; one with `data-alta-action` reports the action with the values of the
 * fragment's named fields to `onAction` (a dialog). A link opens its page of the web in the system browser, and
 * never navigates the window.
 *
 * An element with the class `alta-markdown` holds Markdown as its text: it is shown by the Markdown
 * component of the window, the one of the timeline, so a fragment has highlighted code and diagrams without
 * a script of its own. A `div` with the class `alta-chart` and a `data-option` of JSON is drawn as a chart.
 *
 * A plugin that wants script of its own gives it next to the fragment (`script`), never inside it: the module is loaded from the
 * plugin, and either it is a component, which replaces the fragment in the tree of the window, or its `mount(root, alta)` fills the
 * element that holds the fragment.
 */
export function PluginHtml({ html, pluginKey = null, pane, className, script, onAction, onSubmit, ref }: {
  html: string; pluginKey?: string | null; pane?: Partial<PluginPane>; className?: string;
  /** The script of the content, when it has one (see {@link PluginScriptProps}). */
  script?: PluginScriptProps;
  onAction?: (action: string, value: string | null, values: Record<string, string>) => void;
  /** Enter in a single-line field, for a dialog that has a default button. */
  onSubmit?: () => void;
  ref?: Ref<HTMLDivElement>;
}) {
  const ui = useContext(PluginUiContext);
  const openLink = useContext(MarkdownLinksContext);
  const sanitize = useMemo(() => createPluginHtmlSanitizer(window), []);
  // React compares this prop by identity: equivalent refreshes must not replace fields the user is editing.
  const markup = useMemo(() => ({ __html: sanitize(html) }), [sanitize, html]);
  const root = useRef<HTMLDivElement | null>(null);
  const [blocks, setBlocks] = useState<readonly MarkdownBlock[]>([]);
  const sources = useRef(new WeakMap<HTMLElement, string>());
  const [charts, setCharts] = useState<readonly ChartBlock[]>([]);
  const chartSources = useRef(new WeakMap<HTMLElement, PluginChartSource>());
  // A script that filled the fragment is ended by putting the fragment back; the blocks are then found again.
  const [fragmentVersion, setFragmentVersion] = useState(0);
  const seen = useWhenShown(root, !!script?.whenShown && !!script.path);
  const { state: scripted, fail } = usePluginScript(script && script.whenShown && !seen ? { ...script, path: null, problem: null } : script, pluginKey, pane, sanitize);
  const restore = useCallback(() => {
    if (root.current) { root.current.innerHTML = markup.__html; setFragmentVersion(value => value + 1); }
  }, [markup]);
  usePluginScriptMount(scripted, root, restore, fail);
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
  }, [markup, fragmentVersion]);
  // Each `alta-chart` block gives its option once and becomes the place where the chart is drawn.
  useLayoutEffect(() => {
    const found: ChartBlock[] = [];
    for (const element of Array.from(root.current?.querySelectorAll<HTMLElement>(`.${pluginChartClass}`) ?? [])) {
      let source = chartSources.current.get(element);
      if (source === undefined) {
        source = readPluginChart(element.getAttribute(pluginChartOptionAttribute), element.getAttribute(pluginChartLabelAttribute));
        chartSources.current.set(element, source);
        element.removeAttribute(pluginChartOptionAttribute);
      }
      found.push({ element, source });
    }
    setCharts(previous => found.length === 0 && previous.length === 0 ? previous : found);
  }, [markup, fragmentVersion]);

  // A link of a fragment never navigates the window: a trusted click on one opens its page of the web in the
  // system browser, through the opener of the window.
  function followLink(event: MouseEvent<HTMLDivElement>) {
    const link = (event.target as Element).closest("a");
    if (!link || !event.currentTarget.contains(link)) return;
    const follow = !event.defaultPrevented && markdownLinkActivation(event.nativeEvent);
    event.preventDefault();
    const address = link.getAttribute("href");
    if (follow && openLink && address && safeMarkdownHref(address) && !link.closest(`[${pluginCommandAttribute}], [${pluginActionAttribute}]`)) openLink(address, null);
  }

  function activate(event: MouseEvent<HTMLDivElement>) {
    const target = event.target as Element;
    followLink(event);
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

  // A script that is a component takes the place of the fragment: the window draws it in its own tree, in an error boundary.
  if (scripted.phase === "failed" && scripted.failure) return <div className={`plugin-html plugin-script${className ? ` ${className}` : ""}`}><ScriptFailure error={scripted.failure} /></div>;
  if (scripted.phase === "ready" && scripted.module?.kind === "component" && scripted.handle) {
    const Content = scripted.module.component;
    return <div className={`plugin-html plugin-script${className ? ` ${className}` : ""}`} data-script="component">
      <AltaReactContext.Provider value={scripted.handle.alta}><ScriptBoundary key={script?.path ?? ""}><Content /></ScriptBoundary></AltaReactContext.Provider></div>;
  }

  const attach = (element: HTMLDivElement | null) => {
    root.current = element;
    if (typeof ref === "function") ref(element);
    else if (ref) ref.current = element;
  };
  return <><div ref={attach} className={`plugin-html${className ? ` ${className}` : ""}`} data-script={scripted.phase === "none" ? undefined : scripted.phase} onClick={activate} onAuxClick={followLink}
    onChange={event => {
      const target = event.target as unknown as HTMLElement;
      const action = target.tagName === "SELECT" ? target.getAttribute(pluginActionAttribute) : null;
      if (action && onAction) onAction(action, target.getAttribute(pluginValueAttribute), collectPluginFields(event.currentTarget));
    }}
    onKeyDown={keyDown} dangerouslySetInnerHTML={markup} />
    {blocks.map((block, index) => createPortal(<MarkdownContent source={block.source} timelineCodeBlocks />, block.element, String(index)))}
    {charts.map((block, index) => createPortal(<PluginChartBlock source={block.source} visible={script?.visible ?? true} />, block.element, `chart-${index}`))}</>;
}
