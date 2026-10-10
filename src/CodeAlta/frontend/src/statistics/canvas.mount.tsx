// Disposable page: the module the Statistics plugin names (canvas.tsx), drawn the way the window draws it, under an `alta` object whose `rpc` is a plugin played by
// the fixture API. Every call goes through the JSON of the wire. Driven by canvas.browser.test.ts through `window.canvasFixture`.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot, type Root } from "react-dom/client";
import { readChartTokens } from "../charts/tokens";
import { createAlta, type AltaHandle, type AltaRpc } from "../pluginScript/alta";
import { AltaReactContext } from "../pluginScript/PluginScript";
import { ShellLanguageContext } from "../shellLanguage";
import Canvas from "./canvas";
import { eventsName } from "./rpcApi";
import { createFixtureApi, type FixtureApi, type FixtureScenario } from "./fixtureApi";

type Call = { name: string; input: any };
const calls: Call[] = [];
const opened: string[] = [];
const subscriptions = { open: 0, closed: 0 };
let handlers = new Set<(value: unknown) => void>();
const generation = { value: 0, listeners: new Set<(value: number) => void>() };
const nothing = () => { };

/** Plays the plugin: each call is answered by the fixture API, and its answer crosses the wire as JSON. */
function plugin(api: FixtureApi): AltaRpc {
  const wire = <T,>(value: T): T => JSON.parse(JSON.stringify(value)) as T;
  const answer = async (name: string, input: any, signal?: AbortSignal): Promise<unknown> => {
    const request = input?.request;
    switch (name) {
      case "statistics.summary": return api.summary(request, signal);
      case "statistics.series": return api.series(request, input.metric, input.group ?? null, signal);
      case "statistics.top": return api.top(request, input.kind, input.by, signal);
      case "statistics.tools": return api.tools(request, signal);
      case "statistics.models": return api.models(request, signal);
      case "statistics.projects": return api.projects(request, signal);
      case "statistics.sessions": return api.sessions(request, input.sort, signal);
      case "statistics.session": return api.session(input.id, !!input.withChildren, signal);
      case "statistics.distribution": return api.distribution(request, input.measure, input.subject ?? null, signal);
      case "statistics.calendar": return api.calendar(request, signal);
      case "statistics.week-hour": return api.weekHour(request, signal);
      case "statistics.records": return api.records(request, signal);
      case "statistics.health": return api.health(request, signal);
      case "statistics.details": return api.details(request, input.list, signal);
      case "statistics.runs": return api.runs(request, input.sort, signal);
      case "statistics.status": return api.status(signal);
      case "statistics.choose-history": return api.chooseHistory(input.kind === "days" ? { kind: "days", days: input.days } : { kind: input.kind });
      case "statistics.pause": return api.pause();
      case "statistics.resume": return api.resume();
      case "statistics.stop-here": return api.stopHere();
      case "statistics.forget-deleted": return { count: await api.forgetDeleted() };
      case "statistics.reset": return api.resetStatistics!();
      case "statistics.context": return {
        // A day no language of the window starts its week on: the weeks of the canvas are the ones the plugin names.
        weekStart: "Saturday",
        spaces: [{ id: "default", name: "Default", isDefault: true, projectIds: ["proj-codealta", "proj-neoastra", "proj-xenoatom"] }, { id: "work", name: "Work", isDefault: false, projectIds: ["proj-codealta", "proj-neoastra"] }],
        projects: [{ id: "proj-codealta", name: "CodeAlta" }, { id: "proj-neoastra", name: "NeoAstra" }, { id: "proj-xenoatom", name: "XenoAtom" }],
      };
      default: throw Object.assign(new Error("No such call."), { code: "command_not_found" });
    }
  };
  // The fixture tells its changes to one listener; the plugin of the page forwards them as events, the way the event pump does.
  api.subscribe(event => { for (const handler of [...handlers]) handler(wire(event)); });
  return {
    invoke: async (name, input, options) => { calls.push({ name, input: wire(input ?? null) }); return wire(await answer(name, wire(input ?? {}), options?.signal)); },
    stream: async () => { throw new Error("not used"); },
    subscribe: async (name, handler) => {
      if (name !== eventsName) throw new Error("unknown event " + name);
      subscriptions.open++;
      handlers.add(handler);
      return () => { subscriptions.closed++; handlers.delete(handler); };
    },
    generation: { get value() { return generation.value; }, subscribe: listener => { generation.listeners.add(listener); return () => { generation.listeners.delete(listener); }; } },
  };
}

type Options = { scenario?: FixtureScenario; visible?: boolean; spaceId?: string | null; key?: string | null; input?: unknown; instanceId?: string };
let root: Root | null = null;
let api: FixtureApi | null = null;
let handle: AltaHandle | null = null;

function draw(options: Options) {
  root ??= createRoot(document.getElementById("root")!);
  handle?.dispose();
  handle = createAlta({
    context: { pluginKey: "builtin:statistics", canvasId: "statistics", instanceId: options.instanceId ?? "instance-1", spaceId: options.spaceId === undefined ? "work" : options.spaceId, projectId: null, sessionId: null,
      key: options.key ?? null, input: options.input ?? null },
    visible: options.visible ?? true,
    host: { openSession: id => { opened.push(id); } },
    sanitize: text => text, readTheme: () => readChartTokens(document.body), subscribeTheme: () => () => { },
    rpc: plugin(api!),
  });
  flushSync(() => root!.render(createElement(StrictMode, null, createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: nothing } },
    createElement(AltaReactContext.Provider, { value: handle!.alta }, createElement("div", { id: "canvas-host", style: { width: "100%", height: "100%" } }, createElement(Canvas)))))));
}

const fixture = {
  calls: () => calls.map(call => ({ ...call })),
  clearCalls: () => { calls.length = 0; },
  opened,
  subscriptions,
  get control() { return api!.control; },
  render(options: Options = {}) {
    // A new tab: what the old one drew goes first.
    flushSync(() => root?.render(null));
    handlers = new Set();
    api = createFixtureApi({ today: "2026-10-09", scenario: options.scenario });
    draw(options);
  },
  setVisible(visible: boolean) { handle?.setVisible(visible); },
  /** The connection to the plugin was made again. */
  reconnect() { generation.value++; for (const listener of [...generation.listeners]) listener(generation.value); },
  unmount() { flushSync(() => root?.render(null)); handle?.dispose(); },
  setTheme(dark: boolean) { document.documentElement.classList.toggle("bp6-dark", dark); document.documentElement.dataset.theme = dark ? "dark" : "light"; },
};
Object.assign(window, { canvasFixture: fixture });
document.documentElement.classList.add("bp6-dark");
