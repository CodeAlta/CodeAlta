import { Component, createContext, useCallback, useContext, useEffect, useLayoutEffect, useMemo, useRef, useState, type ErrorInfo, type ReactNode, type RefObject } from "react";
import { Button, NonIdealState } from "@blueprintjs/core";
import { showToast } from "../appToaster";
import { readChartTokens } from "../charts/tokens";
import { MarkdownLinksContext } from "../MarkdownContent";
import { PluginUiContext, type PluginPane } from "../pluginUi";
import { SessionLinksContext } from "../SessionReference";
import { useShellLanguage } from "../shellLanguage";
import { createAlta, type Alta, type AltaContext, type AltaHandle, type AltaHostBridge, type AltaRpc, type AltaTheme } from "./alta";
import { PluginHostBridgeContext } from "./hostBridge";
import { loadScriptModule, scriptErrorMessage, ScriptError, type ScriptLoader, type ScriptModule } from "./scriptModule";

/** The tab that shows the content, for the members of `alta.host` that change it. */
export type PluginScriptTab = Readonly<{ setTitle(title: string | null): void; setStatus(status: string | null): void; setBadge(badge: string | null): void }>;

/** The script of a piece of HTML of a plugin: where its module is, and what it is about. */
export type PluginScriptProps = Readonly<{
  /** The path of the module on the origin of the application (`/plugin/...`), or null for content without script. */
  path: string | null;
  /** Why the host could not serve the script, or null. The content then shows the problem instead. */
  problem?: string | null;
  /** Whether the content is shown. Default true. */
  visible?: boolean;
  /** What the script is about beyond its plugin: the canvas and the instance. */
  context?: Partial<Pick<AltaContext, "canvasId" | "instanceId" | "spaceId" | "key" | "input">>;
  /** The tab that shows the content, when it is one. */
  tab?: PluginScriptTab;
  /** The script starts when the content is first on the screen, not when it is drawn: for a card of a timeline, since a session can have hundreds. */
  whenShown?: boolean;
  /** How the module is loaded: the window imports it; a test gives its own. */
  load?: ScriptLoader;
  /** The calls and streams of the plugin of this content, when the window carries them: scripts then reach them as `alta.rpc`. */
  rpc?: AltaRpc;
}>;

/** What a script gets from React: the `alta` object of the content the component is drawn in. */
export const AltaReactContext = createContext<Alta | null>(null);

/**
 * The `alta` object of the script a component is drawn for. A component of a plugin calls it for what the script reaches the window with.
 *
 * @throws Error When the component is not drawn by the window for a script.
 */
export function useAlta(): Alta {
  const alta = useContext(AltaReactContext);
  if (!alta) throw new Error("useAlta is called by a component that the window draws for the script of a plugin.");
  return alta;
}

/** The theme of the window as values, read when asked. */
const readTheme = (): AltaTheme => readChartTokens(document.body);

/** Calls the listener when the theme, the color scheme or the zoom of the window changes. */
function subscribeTheme(listener: () => void): () => void {
  let frame = 0;
  const observer = new MutationObserver(() => { cancelAnimationFrame(frame); frame = requestAnimationFrame(listener); });
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ["class", "data-theme", "data-palette", "style"] });
  return () => { observer.disconnect(); cancelAnimationFrame(frame); };
}

/** What `alta.host` can do, made of what the content already has: its links, its sessions, the commands of plugins, the toaster and the shell. */
function useHostBridge(pluginKey: string | null, pane: Partial<PluginPane> | undefined, tab: PluginScriptTab | undefined): AltaHostBridge {
  const openLink = useContext(MarkdownLinksContext);
  const sessions = useContext(SessionLinksContext);
  const ui = useContext(PluginUiContext);
  const shell = useContext(PluginHostBridgeContext);
  // The bridge is read when a script asks: a new function of the window does not make a new `alta` object.
  const latest = useRef({ openLink, sessions, ui, shell, tab, pluginKey, pane });
  latest.current = { openLink, sessions, ui, shell, tab, pluginKey, pane };
  return useMemo<AltaHostBridge>(() => ({
    openFile: (path, line, scope) => latest.current.openLink?.(line ? `${path}:${line}` : path, { projectId: scope.projectId ?? undefined, sessionId: scope.sessionId ?? undefined }),
    openLink: url => latest.current.openLink?.(url, null),
    openSession: sessionId => latest.current.sessions?.open(sessionId),
    openDiff: projectId => latest.current.shell?.showChanges(projectId),
    openCanvas: request => latest.current.shell?.openCanvas(request),
    notify: (message, tone) => showToast({ message, intent: tone === "info" ? "primary" : tone, timeout: 6000 }),
    runCommand: name => latest.current.ui.runNamed(name, latest.current.pluginKey, latest.current.pane),
    setTitle: title => latest.current.tab?.setTitle(title),
    setStatus: status => latest.current.tab?.setStatus(status),
    setBadge: badge => latest.current.tab?.setBadge(badge),
  }), []);
}

/** The state of the script of some content. */
export type PluginScriptState = Readonly<{
  /** `none` without script, `loading` while the module loads, `ready` once it runs or is drawn, `failed` when it could not. */
  phase: "none" | "loading" | "ready" | "failed";
  module: ScriptModule | null;
  handle: AltaHandle | null;
  failure: ScriptError | null;
}>;

/**
 * Loads the script of some content and gives it its `alta` object. The module is loaded when the path is set and again when it
 * changes (a reloaded plugin has a new path); the object lives as long as the content is drawn for that path, and its `closed`
 * signal aborts when either ends.
 */
export function usePluginScript(script: PluginScriptProps | undefined, pluginKey: string | null, pane: Partial<PluginPane> | undefined,
  sanitize: (html: string) => string): Readonly<{ state: PluginScriptState; fail: (error: unknown, stage: "mount" | "render") => void }> {
  const path = script?.path ?? null;
  const problem = script?.problem ?? null;
  const load = script?.load;
  const rpc = script?.rpc;
  const [loaded, setLoaded] = useState<Readonly<{ path: string; module: ScriptModule | null; failure: ScriptError | null }> | null>(null);
  const [handle, setHandle] = useState<AltaHandle | null>(null);
  const bridge = useHostBridge(pluginKey, pane, script?.tab);
  const instance = script?.context;
  const inputKey = useMemo(() => JSON.stringify(instance?.input ?? null), [instance?.input]);
  const visible = script?.visible ?? true;
  const initialVisible = useRef(visible);
  initialVisible.current = visible;
  const projectId = pane?.projectId ?? null, sessionId = pane?.sessionId ?? null;

  useEffect(() => {
    if (!path || problem) { setLoaded(null); return; }
    let current = true;
    setLoaded(null);
    loadScriptModule(path, load).then(
      module => { if (current) setLoaded({ path, module, failure: null }); },
      error => { if (current) setLoaded({ path, module: null, failure: error instanceof ScriptError ? error : new ScriptError("load", scriptErrorMessage(error)) }); });
    return () => { current = false; };
  }, [path, problem, load]);

  // The object lives for one load of one module: a reload of the plugin ends the old one (its `closed` aborts) before the new one starts.
  const ready = loaded?.module ?? null;
  useLayoutEffect(() => {
    if (!ready) { setHandle(null); return; }
    const made = createAlta({
      context: { pluginKey: pluginKey ?? "", canvasId: instance?.canvasId ?? null, instanceId: instance?.instanceId ?? null, spaceId: instance?.spaceId ?? null,
        projectId, sessionId, key: instance?.key ?? null, input: JSON.parse(inputKey) },
      visible: initialVisible.current, host: bridge, sanitize, readTheme, subscribeTheme, rpc,
    });
    setHandle(made);
    return () => made.dispose();
  }, [ready, pluginKey, instance?.canvasId, instance?.instanceId, instance?.spaceId, instance?.key, inputKey, projectId, sessionId, bridge, sanitize, rpc]);
  useEffect(() => { handle?.setVisible(visible); }, [handle, visible]);

  const [lateFailure, setLateFailure] = useState<Readonly<{ path: string; error: ScriptError }> | null>(null);
  const fail = useCallback((error: unknown, stage: "mount" | "render") => {
    setLateFailure({ path: path ?? "", error: error instanceof ScriptError ? error : new ScriptError(stage, scriptErrorMessage(error)) });
  }, [path]);
  useEffect(() => { setLateFailure(null); }, [path]);

  const state = useMemo<PluginScriptState>(() => {
    if (problem) return { phase: "failed", module: null, handle: null, failure: new ScriptError("load", problem) };
    if (!path) return { phase: "none", module: null, handle: null, failure: null };
    if (lateFailure && lateFailure.path === path) return { phase: "failed", module: null, handle: null, failure: lateFailure.error };
    if (!loaded || loaded.path !== path) return { phase: "loading", module: null, handle: null, failure: null };
    if (loaded.failure) return { phase: "failed", module: null, handle: null, failure: loaded.failure };
    return { phase: handle ? "ready" : "loading", module: loaded.module, handle, failure: null };
  }, [problem, path, loaded, handle, lateFailure]);
  return { state, fail };
}

/**
 * Calls `mount(root, alta)` of a script on the element that holds the fragment, and ends it: the `closed` signal of its `alta` aborts, the
 * function `mount` returned is called, and the element goes back to the fragment. React runs the effect twice for a new component
 * in development, so each run starts from the fragment and ends cleanly.
 */
export function usePluginScriptMount(state: PluginScriptState, root: RefObject<HTMLElement | null>, restore: () => void, fail: (error: unknown, stage: "mount") => void) {
  const { module, handle } = state;
  const restoreLatest = useRef(restore);
  restoreLatest.current = restore;
  useEffect(() => {
    const element = root.current;
    if (module?.kind !== "mount" || !handle || !element) return;
    let ended = false;
    let cleanup: unknown;
    const end = (value: unknown) => { if (typeof value === "function") { try { (value as () => void)(); } catch { /* A cleanup that throws has nothing left to end. */ } } };
    // After the cleanup of the first run of a development double run, nothing is mounted: the microtask finds the run ended.
    void Promise.resolve().then(() => ended ? undefined : module.mount(element, handle.alta)).then(
      value => { if (ended) end(value); else cleanup = value; },
      error => { if (!ended) fail(error, "mount"); });
    return () => {
      ended = true;
      handle.dispose();
      end(cleanup);
      restoreLatest.current();
    };
  }, [module, handle, root, fail]);
}

/** What the content shows in place of itself when its script failed. */
export function ScriptFailure({ error }: { error: ScriptError }) {
  const { t } = useShellLanguage();
  return <NonIdealState className="plugin-script-failure" icon="error" title={t("The script of this plugin failed.")}
    description={<code className="plugin-script-failure-detail">{error.message}</code>}
    action={<Button size="small" icon="duplicate" onClick={() => void navigator.clipboard?.writeText(`${error.stage}: ${error.message}`)}>{t("Copy the error")}</Button>} />;
}

/** Catches what a component of a plugin throws while it draws, so that the window around it goes on. */
export class ScriptBoundary extends Component<{ children: ReactNode; onError?: (error: unknown, stage: "render") => void }, { error: ScriptError | null }> {
  state = { error: null as ScriptError | null };

  static getDerivedStateFromError(error: unknown) {
    return { error: error instanceof ScriptError ? error : new ScriptError("render", scriptErrorMessage(error)) };
  }

  componentDidCatch(error: unknown, _info: ErrorInfo) {
    this.props.onError?.(error, "render");
  }

  render() {
    return this.state.error ? <ScriptFailure error={this.state.error} /> : this.props.children;
  }
}

/**
 * True once the element has been on the screen, or at once when the wait is not asked for: the script of a card of a timeline starts when
 * the card is first seen, not when the history draws it. A window without `IntersectionObserver` does not wait.
 */
export function useWhenShown(element: RefObject<HTMLElement | null>, wait: boolean): boolean {
  const [seen, setSeen] = useState(false);
  useEffect(() => {
    const target = element.current;
    if (!wait || seen || !target) return;
    if (typeof IntersectionObserver !== "function") { setSeen(true); return; }
    const observer = new IntersectionObserver(entries => { if (entries.some(entry => entry.isIntersecting)) { setSeen(true); observer.disconnect(); } });
    observer.observe(target);
    return () => observer.disconnect();
  }, [element, wait, seen]);
  return !wait || seen;
}
