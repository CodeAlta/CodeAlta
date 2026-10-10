// Disposable page; the production tab of a canvas over a hub whose host the test plays.
import { StrictMode, createElement, useEffect } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import type { CanvasEvent } from "#neoastra";
import { canvasTab, type FileTab } from "../fileTabs";
import { CanvasPanel } from "./CanvasPanel";
import { createCanvasHub, type CanvasApi } from "./canvasHub";
import type { CanvasPluginControl } from "./canvasPlugin";
import { useAlta } from "../pluginScript/PluginScript";
import type { ScriptLoader } from "../pluginScript/scriptModule";

type Scenario = { status: string; html: string; title: string; statusText: string | null; revision: number; script: string | null; scriptProblem: string | null; input: string | null };
const root = createRoot(document.getElementById("root")!);
const state = {
  calls: [] as string[], actions: [] as { action: string; value: string | null; values: Record<string, string> }[], looks: [] as unknown[], instances: [] as (string | null)[],
  closed: 0, activated: 0, rebuilt: 0, rebuildFails: false, probeUnknown: false, sources: [] as string[], opened: 0, scripts: [] as string[],
  /** The host closed the instance to make room for others: it does not know it until the tab asks for it again. */
  evicted: false,
  /** The host takes its time to open an instance: each open waits to be released. */
  holdOpens: false, held: [] as (() => void)[],
};
const scenario: Scenario = { status: "ok", html: "<p>first</p><input name=\"note\" value=\"typed\"><button data-alta-action=\"tick\" data-alta-value=\"one\">Tick</button>", title: "Board", statusText: null, revision: 1, script: null, scriptProblem: null, input: null };
let actionAnswer: { status: string; html: string | null; closed: boolean; revision: number } = { status: "ok", html: "<p>after the action</p>", closed: false, revision: 2 };
// The events the host sends on its one channel: the test pushes them.
const waiting: ((result: IteratorResult<CanvasEvent>) => void)[] = [];
const queued: CanvasEvent[] = [];
const api: CanvasApi = {
  list: async () => ({ status: "ok", canvases: [] }),
  open: async request => {
    state.calls.push(`open:${request.visible}`);
    state.opened++;
    state.evicted = false;
    if (state.holdOpens) await new Promise<void>(resolve => state.held.push(resolve));
    return scenario.status === "ok"
      ? { status: "ok", instanceId: "instance-1", title: scenario.title, statusText: scenario.statusText, html: scenario.html, actions: true, revision: scenario.revision, package: "plugin:global:board", icon: "list-checks", iconData: null, script: scenario.script, scriptProblem: scenario.scriptProblem, input: scenario.input }
      : { status: scenario.status, instanceId: null, title: scenario.title, statusText: null, html: null, actions: false, revision: 0, package: "plugin:global:board", icon: "list-checks", iconData: null, script: null, scriptProblem: null, input: null };
  },
  visible: async request => { state.calls.push(`visible:${request.visible}`); return { status: state.evicted ? "unknown" : "ok" }; },
  close: async () => ({ status: "ok" }),
  closeSpace: async () => ({ status: "ok" }),
  action: async request => { state.actions.push({ action: request.action ?? "", value: request.value, values: request.values ?? {} }); return actionAnswer; },
  describe: async () => ({ status: "ok", markdown: null }),
  rpcOpen: async () => ({ status: "unavailable", connection: null, maximumFrameBytes: 0 }),
  rpcSend: async () => ({ status: "closed" }),
  rpcClose: async () => ({ status: "ok" }),
  watch: async () => ({ [Symbol.asyncIterator]: () => ({
    next: () => new Promise<IteratorResult<CanvasEvent>>(resolve => { const ready = queued.shift(); if (ready) resolve({ done: false, value: ready }); else waiting.push(resolve); }),
    return: async () => ({ done: true as const, value: undefined }),
  }) }),
};
// The modules of the scripts the test serves: a component that names its version, reads its context and sets what it can of the tab.
const board = (version: string) => ({ default: function Board() {
  const alta = useAlta();
  useEffect(() => {
    state.scripts.push(`mount:${version}`);
    alta.host.setTitle(`Script ${version}`); alta.host.setBadge(7);
    return () => { state.scripts.push(`cleanup:${version}`); };
  }, [alta]);
  return createElement("p", { className: "scripted", "data-instance": alta.context.instanceId ?? "", "data-input": JSON.stringify(alta.context.input) }, `board ${version}`);
} });
const loadScript: ScriptLoader = async path => {
  if (path.endsWith("/one/board.js")) return board("one");
  if (path.endsWith("/two/board.js")) return board("two");
  throw new Error("no such script");
};
const hub = createCanvasHub(api);
const disconnect = hub.connect("epoch");
void disconnect;

const tab: FileTab = canvasTab({ pluginKey: "builtin:board", canvasId: "board", key: "k" }, { title: "Board", icon: "list-checks", plugin: "plugin:global:board" });
const control: CanvasPluginControl = {
  probe: async () => state.probeUnknown ? { state: "unknown", message: null, folder: null } : ({ state: "failed", message: "plugin.cs(3,1): error CS1002", folder: { id: "plugin:global:board", path: "/plugins/board", name: "board" } }),
  rebuild: async () => { if (state.rebuildFails) return { ok: false, message: null }; state.rebuilt++; scenario.status = "ok"; return { ok: true, message: null }; },
};

const fixture = {
  state, scenario,
  /** What the next action of the page is answered with. */
  answerAction(answer: typeof actionAnswer) { actionAnswer = answer; },
  push(event: Partial<CanvasEvent> & { kind: string }) {
    const full = { actions: null, canvasId: null, focus: false, html: null, icon: null, instanceId: "instance-1", key: null, package: null, pluginKey: null, projectId: null, revision: null,
      script: null, scriptProblem: null, sessionId: null, spaceId: null, state: null, statusText: null, title: null, ...event } as CanvasEvent;
    const next = waiting.shift();
    if (next) next({ done: false, value: full }); else queued.push(full);
  },
  /** Under StrictMode, as in the application: React then runs each effect of a new component twice. */
  render(options: { visible?: boolean; control?: boolean; space?: string | null } = {}) {
    flushSync(() => root.render(createElement(StrictMode, null, createElement(CanvasPanel, {
      tab, spaceId: options.space === undefined ? "work" : options.space, hub, visible: options.visible ?? true, active: true,
      onActivate: () => { state.activated++; }, onLook: look => { state.looks.push(look); }, onInstance: instance => { state.instances.push(instance); },
      onClose: () => { state.closed++; }, onOpenSource: folder => { state.sources.push(folder.id); }, control: options.control ? control : null, loadScript,
    }))));
  },
  clear() { flushSync(() => root.render(null)); },
  /** Lets the host answer the opens that waited. */
  release() { state.holdOpens = false; for (const answer of state.held.splice(0)) answer(); },
};
Object.assign(window, { canvasFixture: fixture });
