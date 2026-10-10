// Disposable page; the production PluginHtml with the scripts of plugins, over modules the test defines. Driven by pluginScript.browser.test.ts.
import { Fragment, StrictMode, createElement, useEffect, useState, type ReactNode } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { MarkdownLinksContext } from "../MarkdownContent";
import { PluginHtml } from "../PluginHtml";
import { PluginUiContext, noPluginContributions } from "../pluginUi";
import { SessionLinksContext } from "../SessionReference";
import { useAlta } from "./PluginScript";
import { useRpc, useStream } from "./codealta";
import type { AltaRpc } from "./alta";
import { PluginHostBridgeContext } from "./hostBridge";
import { createHtml } from "./html";
import type { ScriptLoader } from "./scriptModule";

const root = createRoot(document.getElementById("root")!);
const html = createHtml(createElement as never, Fragment) as (strings: TemplateStringsArray, ...values: unknown[]) => ReactNode;

type Log = { name: string; detail?: unknown };
const state = { log: [] as Log[], renders: 0 };
const note = (name: string, detail?: unknown) => state.log.push({ name, detail });

/** The modules the test serves, by path: each is what a script of a plugin would export. */
const modules: Record<string, () => unknown> = {
  "/plugin/k/one/board.js": () => ({ default: function Board() {
    const alta = useAlta();
    const [count, setCount] = useState(0);
    const [visible, setVisible] = useState(alta.visible.value);
    state.renders++;
    useEffect(() => { note("component-effect"); return () => { note("component-cleanup"); }; }, []);
    useEffect(() => alta.visible.subscribe(value => { setVisible(value); note("visible", value); }), [alta]);
    useEffect(() => {
      if (!visible) return undefined;
      const timer = setInterval(() => setCount(value => value + 1), 20);
      return () => clearInterval(timer);
    }, [visible]);
    return html`<div class="board" data-visible=${String(visible)} data-count=${String(count > 3)}>
      <b class="board-title">Board ${alta.context.canvasId}</b>
      <button class="board-open" onClick=${() => { alta.host.openFile("src/a.cs", { line: 12 }); alta.host.openSession("s-1"); alta.host.runCommand("refresh"); alta.host.openCanvas("other"); alta.host.openDiff(); alta.host.openLink("https://example.com/x"); alta.host.setBadge(3); alta.host.setTitle("Mine"); }}>Go</button>
      <button class="board-html" onClick=${(event: { currentTarget: HTMLElement }) => { event.currentTarget.parentElement!.insertAdjacentHTML("beforeend", alta.html("<i class=\"alta-injected\">x</i><img src=x onerror=\"window.__pwned = 1\"><script>window.__pwned = 2</script>")); }}>Html</button>
    </div>`;
  } }),
  "/plugin/k/two/board.js": () => ({ default: function Board() { return createElement("p", { className: "board-two" }, "second version"); } }),
  "/plugin/k/one/mount.js": () => ({ mount(rootElement: HTMLElement, alta: { closed: AbortSignal; visible: { value: boolean }; html(text: string): string }) {
    const target = rootElement.querySelector(".alta-target")!;
    note("mount", target.textContent);
    target.insertAdjacentHTML("beforeend", alta.html("<span class=\"alta-mounted\">mounted</span>"));
    note("alta-open", !alta.closed.aborted);
    alta.closed.addEventListener("abort", () => note("closed-signal"));
    return () => note("mount-cleanup");
  } }),
  "/plugin/k/two/mount.js": () => ({ async mount(rootElement: HTMLElement) { await Promise.resolve(); note("mount-two", rootElement.querySelector(".alta-target")?.textContent); } }),
  "/plugin/k/one/throws.js": () => ({ default: function Throws() { throw new Error("the render exploded"); } }),
  "/plugin/k/one/mount-throws.js": () => ({ mount() { throw new Error("the mount exploded"); } }),
  "/plugin/k/one/mount-rejects.js": () => ({ async mount() { throw new Error("the async mount exploded"); } }),
  "/plugin/k/one/rpc.js": () => ({ default: function Rpc() {
    const call = useRpc<{ rows: number }>("board.get", { project: "p1" });
    const stream = useStream<number>("board.watch");
    return createElement("p", { className: "rpc-result" }, call.loading ? "loading" : call.error ? `error: ${call.error.message}` : `rows ${call.data?.rows} latest ${stream.latest ?? "none"}`);
  } }),
  "/plugin/k/one/shape.js": () => ({ nothing: true }),
};
const load: ScriptLoader = async path => {
  note("load", path);
  if (path === "/plugin/k/one/fails.js") throw new Error("Failed to fetch dynamically imported module");
  const make = modules[path];
  if (!make) throw new Error(`no module ${path}`);
  return make();
};

const bridge = { openCanvas: (request: unknown) => note("bridge-canvas", request), showChanges: (projectId: string | null) => note("bridge-changes", projectId) };
const ui = { epoch: null, projectId: null, contributions: noPluginContributions, run: () => { }, runNamed: (name: string, pluginKey: string | null) => note("run-named", { name, pluginKey }) };
const sessions = { title: (id: string) => `Session ${id}`, open: (id: string) => note("open-session", id) };

/** The calls of a plugin that the window carries: a call answers with the rows, a stream gives 1, 2, 3 and ends. */
const carried: AltaRpc = {
  invoke: async (name, input) => { note("rpc", { name, input }); return { rows: 3 }; },
  stream: async () => (async function* () { yield 1; yield 2; yield 3; })(),
  subscribe: async () => () => { },
  generation: { value: 0, subscribe: () => () => { } },
};

type Options = { rpc?: boolean; html?: string; path?: string | null; problem?: string | null; visible?: boolean; whenShown?: boolean; spacer?: number; sibling?: boolean };
const fixture = {
  state,
  render(options: Options = {}) {
    const content = createElement(PluginHtml, {
      html: options.html ?? "<p class=\"alta-skeleton\">skeleton</p>", pluginKey: "plugin:k", pane: { projectId: "p1", sessionId: "s1" },
      script: { path: options.path ?? null, problem: options.problem ?? null, visible: options.visible ?? true, whenShown: options.whenShown, load, rpc: options.rpc ? carried : undefined,
        context: { canvasId: "board", instanceId: "i1", spaceId: "work", key: null, input: { a: 1 } },
        tab: { setTitle: value => note("tab-title", value), setStatus: value => note("tab-status", value), setBadge: value => note("tab-badge", value) } },
    });
    flushSync(() => root.render(createElement(StrictMode, null,
      createElement(PluginHostBridgeContext.Provider, { value: bridge },
        createElement(PluginUiContext.Provider, { value: ui },
          createElement(SessionLinksContext.Provider, { value: sessions },
            createElement(MarkdownLinksContext.Provider, { value: (address: string, scope: unknown) => note("link", { address, scope }) },
              createElement("div", { id: "frame" }, options.spacer ? createElement("div", { style: { height: options.spacer } }) : null, content,
                options.sibling ? createElement("p", { id: "sibling" }, "the rest of the window") : null))))))));
  },
  clear() { flushSync(() => root.render(null)); },
  /** The log since the last call, as `name` or `name:detail` text. */
  drain() { const lines = state.log.map(entry => entry.detail === undefined ? entry.name : `${entry.name}:${JSON.stringify(entry.detail)}`); state.log.length = 0; return lines; },
};
Object.assign(window, { scriptFixture: fixture });
