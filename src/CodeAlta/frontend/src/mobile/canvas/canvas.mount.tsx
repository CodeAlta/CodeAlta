// Disposable page: the module the Mobile plugin names (canvas.tsx), drawn the way the window draws it, under an `alta` object whose `rpc` is a
// plugin the fixture plays. Every call and event goes through the JSON of the wire. Driven by canvas.browser.test.ts through `window.mobileFixture`.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { readChartTokens } from "../../charts/tokens";
import { AltaError, createAlta, type AltaHandle, type AltaRpc } from "../../pluginScript/alta";
import { AltaReactContext } from "../../pluginScript/PluginScript";
import { ShellLanguageContext } from "../../shellLanguage";
import Canvas from "./canvas";
import { changedEvent } from "./api";
import type { MobileStatus } from "./status";

const wire = <T,>(value: T): T => JSON.parse(JSON.stringify(value)) as T;
const state = {
  calls: [] as string[], opened: [] as string[], copies: [] as string[], notices: [] as string[],
  status: null as MobileStatus | null,
  /** What `mobile.retry` makes the status. */
  afterRetry: null as MobileStatus | null,
  /** The calls fail while this is set, as a plugin that does not answer. */
  down: false,
};
const handlers = new Set<(value: unknown) => void>();

const rpc: AltaRpc = {
  invoke: async name => {
    state.calls.push(name);
    if (state.down) throw new AltaError("connection_closed", "The connection to the plugin is closed.");
    if (name === "mobile.retry" && state.afterRetry) { state.status = state.afterRetry; state.afterRetry = null; }
    if (name === "mobile.status" || name === "mobile.retry") return wire(state.status);
    throw new AltaError("command_not_found", `No handler ${name}.`);
  },
  stream: async () => { throw new Error("not used"); },
  subscribe: async (name, handler) => {
    if (name !== changedEvent) throw new Error(`unknown event ${name}`);
    handlers.add(handler);
    return () => { handlers.delete(handler); };
  },
  generation: { value: 0, subscribe: () => () => { } },
};

// The clipboard of a page opened from a file refuses writes: the fixture keeps what is copied.
Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: async (text: string) => { state.copies.push(text); } } });

let handle: AltaHandle | null = null;
const root = createRoot(document.getElementById("root")!);
const nothing = () => { };

const fixture = {
  state,
  render(status: MobileStatus | null) {
    state.status = status;
    flushSync(() => root.render(null));
    handle?.dispose();
    handle = createAlta({
      context: { pluginKey: "builtin:mobile", canvasId: "mobile", instanceId: "instance-1", spaceId: null, projectId: null, sessionId: null, key: null, input: null },
      visible: true,
      host: { openLink: url => { state.opened.push(url); }, notify: message => { state.notices.push(message); } },
      sanitize: text => text, readTheme: () => readChartTokens(document.body), subscribeTheme: () => () => { },
      rpc,
    });
    flushSync(() => root.render(createElement(StrictMode, null, createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: nothing } },
      createElement(AltaReactContext.Provider, { value: handle!.alta }, createElement("div", { id: "canvas-host", style: { width: "100%", height: "100%" } }, createElement(Canvas)))))));
  },
  /** The plugin tells of a new status. */
  publish(status: MobileStatus) { state.status = status; for (const handler of [...handlers]) handler(wire(status)); },
  listeners: () => handlers.size,
  setTheme(dark: boolean) { document.documentElement.classList.toggle("bp6-dark", dark); document.body.classList.toggle("bp6-dark", dark); document.documentElement.dataset.theme = dark ? "dark" : "light"; },
};
Object.assign(window, { mobileFixture: fixture });
fixture.setTheme(true);
