import { useCallback, useEffect, useMemo, useReducer, useRef, useState, type ReactNode } from "react";
import { Button, NonIdealState } from "@blueprintjs/core";
import { ActivitySpinner } from "../ActivitySpinner";
import { showToast } from "../appToaster";
import type { FileTab } from "../fileTabs";
import { PluginHtml } from "../PluginHtml";
import type { PluginScriptTab } from "../pluginScript/PluginScript";
import type { ScriptLoader } from "../pluginScript/scriptModule";
import { useShellLanguage } from "../shellLanguage";
import type { CanvasHub, CanvasInstanceEvent } from "./canvasHub";
import type { CanvasPluginControl, CanvasPluginProbe } from "./canvasPlugin";
import { CanvasIcon } from "./CanvasIcon";
import { useCanvasRpc } from "./useCanvasRpc";

/** What a canvas tab shows: its fragment, or why it has none. */
type Phase = "loading" | "ready" | "stopped" | "missing" | "failed" | "unavailable";

type View = Readonly<{
  phase: Phase; instanceId: string | null; revision: number; html: string; title: string | null; statusText: string | null; actions: boolean;
  /** The module of the plugin that draws the tab, its path on the origin of the application, or null for a fragment alone. */
  script: string | null; scriptProblem: string | null; input: string | null;
}>;

type Change =
  | Readonly<{ kind: "opening" }>
  | Readonly<{ kind: "opened"; instanceId: string; revision: number; html: string; title: string | null; statusText: string | null; actions: boolean;
    script: string | null; scriptProblem: string | null; input: string | null }>
  | Readonly<{ kind: "refused"; phase: Exclude<Phase, "loading" | "ready"> }>
  | Readonly<{ kind: "event"; event: CanvasInstanceEvent }>
  | Readonly<{ kind: "html"; html: string }>;

const initial: View = { phase: "loading", instanceId: null, revision: 0, html: "", title: null, statusText: null, actions: false, script: null, scriptProblem: null, input: null };

/** The phase that a refusal of the host leaves a tab in. */
export function refusedPhase(status: string): Exclude<Phase, "loading" | "ready"> {
  return status === "plugin_stopped" ? "stopped" : status === "unknown_canvas" ? "missing" : status === "failed" || status === "limit" ? "failed" : "unavailable";
}

/** What the state of a tab becomes with a change. An event older than what the tab has is dropped, and a state event of the host moves the tab between its content and its placeholder. */
export function canvasView(view: View, change: Change): View {
  switch (change.kind) {
    case "opening": return view.phase === "ready" ? view : { ...initial, phase: "loading", title: view.title };
    case "opened": return { phase: "ready", instanceId: change.instanceId, revision: change.revision, html: change.html, title: change.title, statusText: change.statusText, actions: change.actions,
      script: change.script, scriptProblem: change.scriptProblem, input: change.input };
    case "refused": return { ...initial, phase: change.phase, title: view.title };
    case "html": return view.phase === "ready" ? { ...view, html: change.html } : view;
    case "event": {
      const { event } = change;
      if (event.kind === "closed") return view;
      if (event.kind === "state") return event.state === "ready" ? view : { ...initial, instanceId: view.instanceId, revision: view.revision, title: view.title, phase: refusedPhase(event.state) };
      if (event.revision < view.revision) return view;
      const ready = event.state === "ready" || view.phase === "ready";
      if (!ready) return view;
      return { phase: "ready", instanceId: view.instanceId, revision: event.revision, html: event.html ?? view.html,
        title: event.title ?? view.title, statusText: event.statusText === null ? view.statusText : event.statusText === "" ? null : event.statusText, actions: event.actions ?? view.actions,
        script: event.script === null ? view.script : event.script === "" ? null : event.script,
        scriptProblem: event.scriptProblem === null ? view.scriptProblem : event.scriptProblem === "" ? null : event.scriptProblem, input: view.input };
    }
  }
}

/**
 * The tab of a canvas: a fragment that its plugin writes, drawn with the components of the window, and the actions
 * of the fragment sent back to the plugin. The plugin holds the state: the tab asks for it each time it is shown, so
 * what the plugin pushed while the tab was away is there when it comes back.
 *
 * A tab that is hidden stays mounted and keeps the latest fragment it was sent without drawing it; it draws it once it
 * is shown. While its plugin is not running, or its canvas is gone, the tab says so, with what can be done about it.
 */
export function CanvasPanel({ tab, spaceId, hub, visible, active, onActivate, onLook, onInstance, onClose, onOpenSource, control, loadScript }: {
  tab: FileTab;
  /** The space the tab is in. */
  spaceId: string | null;
  hub: CanvasHub;
  visible: boolean;
  active: boolean;
  onActivate: () => void;
  /** The title, the status, the icon and the plugin folder the host gave the tab: the strip shows them. */
  onLook: (look: Readonly<{ title?: string; status?: string | null; icon?: string; plugin?: string }>) => void;
  /** The instance the tab shows, or null once it does not: the owner of the tab closes it with the tab. */
  onInstance?: (instanceId: string | null) => void;
  onClose: () => void;
  /** Opens the code editor on the folder of the plugin. */
  onOpenSource: (folder: Readonly<{ id: string; path: string; name: string }>) => void;
  /** What can be done about a plugin that does not run; null when its folder is not known. */
  control: CanvasPluginControl | null;
  /** How the module of the tab's script is loaded: the window imports it, a test gives its own. */
  loadScript?: ScriptLoader;
}) {
  const { t } = useShellLanguage();
  const [view, change] = useReducer(canvasView, initial);
  const [retry, setRetry] = useState(0);
  const [shown, setShown] = useState<Readonly<{ html: string; script: string | null; problem: string | null }>>({ html: "", script: null, problem: null });
  const latest = useRef({ visible, onLook, onClose, onInstance });
  latest.current = { visible, onLook, onClose, onInstance };
  const pluginKey = tab.pluginKey ?? "", canvasId = tab.canvasId ?? "";
  const projectId = tab.projectId || null, sessionId = tab.sessionId ?? null, key = tab.key ?? null;

  // The instance of the tab: opened for it, given the events of its plugin, and told when the tab is shown or hidden.
  useEffect(() => {
    let cancelled = false;
    let detach = () => { };
    let opened: string | null = null;
    change({ kind: "opening" });
    void (async () => {
      const reply = await hub.open({ pluginKey, canvasId, spaceId, projectId, sessionId, key, visible: latest.current.visible });
      if (cancelled) return;
      if (reply.status !== "ok" || !reply.instanceId) {
        // The plugin may still be on its way, or its new version about to come: the tab asks again when the host says plugins changed.
        latest.current.onLook({ ...reply.icon ? { icon: reply.icon } : {} });
        change({ kind: "refused", phase: refusedPhase(reply.status) });
        return;
      }

      opened = reply.instanceId;
      latest.current.onInstance?.(reply.instanceId);
      change({ kind: "opened", instanceId: reply.instanceId, revision: reply.revision, html: reply.html ?? "", title: reply.title, statusText: reply.statusText, actions: reply.actions,
        script: reply.script ?? null, scriptProblem: reply.scriptProblem ?? null, input: reply.input ?? null });
      latest.current.onLook({ ...reply.title ? { title: reply.title } : {}, status: reply.statusText, ...reply.icon ? { icon: reply.icon } : {}, ...reply.package ? { plugin: reply.package } : {} });
      // The plugin closed the instance: its tab goes with it.
      detach = hub.attach(reply.instanceId, reply.revision, event => { if (event.kind === "closed") latest.current.onClose(); else change({ kind: "event", event }); });
    })();
    return () => {
      cancelled = true;
      detach();
      if (opened) latest.current.onInstance?.(null);
      // The tab goes away with its space, or with a close that the owner of the tab reports itself: the instance only learns it is hidden.
      if (opened) void hub.setVisible(opened, false);
    };
  }, [hub, pluginKey, canvasId, spaceId, projectId, sessionId, key, retry]);

  // A tab that waits for its plugin asks again when plugins change.
  const waiting = view.phase !== "ready" && view.phase !== "loading";
  useEffect(() => waiting ? hub.subscribeChanges(() => setRetry(value => value + 1)) : undefined, [hub, waiting]);

  // The plugin is told whether the tab is shown. What it sends while the tab is hidden is drawn once it is shown.
  const instanceId = view.instanceId;
  useEffect(() => {
    if (instanceId) void hub.setVisible(instanceId, visible);
  }, [hub, instanceId, visible]);
  useEffect(() => { if (visible) setShown(current => current.html === view.html && current.script === view.script && current.problem === view.scriptProblem ? current : { html: view.html, script: view.script, problem: view.scriptProblem }); },
    [visible, view.html, view.script, view.scriptProblem]);
  // A tab that is shown draws the latest fragment at once, with the script that goes with it; one that is hidden keeps what it last drew, so a reloaded plugin does not start its script on a skeleton it has not drawn.
  const drawn = visible ? { html: view.html, script: view.script, problem: view.scriptProblem } : shown;

  // What the script of the tab sets (`alta.host.setTitle`, `setStatus`, `setBadge`) wins over what the plugin gave until the plugin gives it again or the script goes.
  const [scripted, setScripted] = useState<Readonly<{ title?: string | null; status?: string | null; badge?: string | null }>>({});
  const scriptTab = useMemo<PluginScriptTab>(() => ({
    setTitle: value => setScripted(current => ({ ...current, title: value })),
    setStatus: value => setScripted(current => ({ ...current, status: value })),
    setBadge: value => setScripted(current => ({ ...current, badge: value })),
  }), []);
  useEffect(() => { setScripted({}); }, [view.script, view.instanceId]);

  // The strip follows the title and the status the plugin gives.
  const title = scripted.title ?? view.title, statusText = scripted.badge ?? scripted.status ?? view.statusText;
  useEffect(() => {
    if (view.phase === "ready") latest.current.onLook({ ...title ? { title } : {}, status: statusText });
  }, [view.phase, title, statusText]);

  const onAction = useCallback((action: string, value: string | null, values: Record<string, string>) => {
    if (!instanceId) return;
    void hub.action(instanceId, action, value, values).then(reply => {
      if (reply.closed) { onClose(); return; }
      if (reply.status === "ok") { if (reply.html !== null) change({ kind: "html", html: reply.html }); return; }
      if (reply.status === "failed") showToast({ message: t("The plugin could not complete the action."), intent: "warning", icon: "warning-sign", timeout: 6000 }, `canvas-action-${instanceId}`);
    });
  }, [hub, instanceId, onClose, t]);

  // The calls of the script to its plugin: carried by the host, and paused while the tab is hidden.
  const rpc = useCanvasRpc(hub, view.instanceId, visible);
  const pane = useMemo(() => ({ projectId, sessionId }), [projectId, sessionId]);
  const scriptInput = useMemo(() => { try { return view.input ? JSON.parse(view.input) as unknown : null; } catch { return null; } }, [view.input]);
  return <div className="canvas-panel" data-phase={view.phase} data-visible={visible} data-active={active} tabIndex={-1} onFocusCapture={onActivate} onPointerDownCapture={onActivate}>
    {view.phase === "ready"
      ? <PluginHtml className="canvas-html" html={drawn.html} pluginKey={pluginKey} pane={pane} onAction={view.actions ? onAction : undefined}
        script={{ path: drawn.script, problem: drawn.problem, visible, tab: scriptTab, load: loadScript, rpc, context: { canvasId, instanceId: view.instanceId, spaceId, key, input: scriptInput } }} />
      : view.phase === "loading"
        ? <div className="canvas-loading"><ActivitySpinner size={18} label={t("Loading…")} /></div>
        : <CanvasPlaceholder phase={view.phase} title={view.title ?? tab.name ?? t("Canvas")} icon={tab.icon} pluginKey={tab.pluginKey} control={control} onClose={onClose} onOpenSource={onOpenSource} onRebuilt={() => setRetry(value => value + 1)} />}
  </div>;
}

/** Why a canvas shows nothing, and what to do: build the plugin again, open its source, or close the tab. */
function CanvasPlaceholder({ phase, title, icon, pluginKey, control, onClose, onOpenSource, onRebuilt }: {
  phase: Exclude<Phase, "loading" | "ready">; title: string; icon?: string; pluginKey?: string; control: CanvasPluginControl | null; onClose: () => void;
  onOpenSource: (folder: Readonly<{ id: string; path: string; name: string }>) => void; onRebuilt: () => void;
}) {
  const { t } = useShellLanguage();
  const [probe, setProbe] = useState<CanvasPluginProbe | null>(null);
  const [building, setBuilding] = useState(false);
  const [problem, setProblem] = useState<string | null>(null);
  // What the host says of the package is read when the placeholder appears: the first reason the plugin did not start.
  useEffect(() => {
    let current = true;
    setProbe(null);
    if (control) void control.probe().then(value => { if (current) setProbe(value); });
    return () => { current = false; };
  }, [control, phase]);
  async function rebuild() {
    if (!control || building) return;
    setBuilding(true);
    setProblem(null);
    const outcome = await control.rebuild();
    setBuilding(false);
    if (outcome.ok) onRebuilt();
    else setProblem(outcome.message ?? t("The plugin could not be built."));
  }

  const state = probe?.state;
  const description = building ? t("Building the plugin…")
    : phase === "missing" ? t("The plugin no longer has this canvas.")
    : phase === "failed" ? t("The plugin could not show this canvas.")
    : state === "failed" ? t("The plugin did not start.")
    : state === "disabled" ? t("The plugin is turned off.")
    : t("The plugin is not running.");
  const detail = problem ?? (state === "failed" ? probe?.message : null);
  const actions: ReactNode[] = [];
  // A plugin that the host does not list any more has nothing to build.
  if (control && state !== "unknown") actions.push(<Button key="rebuild" size="small" icon={building ? <ActivitySpinner size={13} /> : "build"} disabled={building} onClick={() => void rebuild()}>{t("Rebuild plugin")}</Button>);
  return <NonIdealState className="canvas-placeholder" icon={<CanvasIcon name={icon} pluginKey={pluginKey} size={32} />} title={title} description={<>
    <span className="canvas-placeholder-reason" role="status">{description}</span>
    {detail && <code className="canvas-placeholder-detail">{detail}</code>}
  </>} action={<div className="canvas-placeholder-actions">
    {actions}
    {probe?.folder && <Button key="source" size="small" icon="code" onClick={() => onOpenSource(probe.folder!)}>{t("Open plugin source")}</Button>}
    <Button key="close" size="small" variant="minimal" icon="cross" onClick={onClose}>{t("Close")}</Button>
  </div>} />;
}
