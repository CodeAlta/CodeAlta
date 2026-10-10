// The real shell over an inert host; /landing sends the same canvas request as the Desktop plugin.
import type { CanvasEvent, CanvasItem, CanvasOpenRequest, PluginUiInvokeRequest } from "#neoastra";
import { canvases as demoCanvases, pluginUi as demoPluginUi } from "./demo-api";
import { desktopShell as fixtureShell } from "./projectFocus.neoastra.mount";
export * from "./projectFocus.neoastra.mount";

export const desktopShell = { ...fixtureShell, preferences: async () => ({ ...await fixtureShell.preferences(), zoom: 100 }) };

const landing: CanvasItem = { pluginKey: "builtin:landing", plugin: "Landing page", package: null, id: "landing", title: "Welcome",
  description: null, icon: "house", iconData: null, scope: "Application", input: false, actions: 0, describes: true };
const fixture = { enabled: true, commands: [] as PluginUiInvokeRequest[], opens: [] as CanvasOpenRequest[] };
Object.assign(window, { windowFrameFixture: fixture });
const queued: CanvasEvent[] = [];
let wake = () => {};

export const pluginUi = {
  ...demoPluginUi,
  contributions: async (request: { projectId: string | null }) => ({ status: "ok", projectId: request.projectId, pickers: [], regions: false,
    commands: [
      // An unrelated plugin using the same name must never become Home when the built-in is disabled.
      { id: "other-landing", pluginKey: "builtin:other", plugin: "Other", name: "landing", label: "Other landing", description: "Not Welcome" },
      ...(fixture.enabled ? [{ id: "welcome-command", pluginKey: landing.pluginKey, plugin: landing.plugin, name: "landing", label: "Welcome", description: "Open Welcome" }] : []),
    ] }),
  invokeCommand: async (request: PluginUiInvokeRequest) => {
    fixture.commands.push(request);
    if (request.commandId !== "welcome-command") throw new Error("Home invoked an unrelated plugin");
    queued.push({ kind: "open", pluginKey: landing.pluginKey, canvasId: landing.id, spaceId: request.spaceId, projectId: null, sessionId: null, key: null,
      focus: true, title: landing.title, icon: landing.icon, package: null, actions: null, html: null, instanceId: null, revision: null,
      connection: null, frames: null, reason: null, script: null, scriptProblem: null, state: null, statusText: null });
    wake();
    return { status: "started" };
  },
};

export const canvases = {
  ...demoCanvases,
  list: async () => ({ status: "ok", canvases: fixture.enabled ? [landing] : [] }),
  open: async (request: CanvasOpenRequest) => {
    fixture.opens.push(request);
    return { status: "ok", instanceId: "welcome-instance", title: "Welcome", statusText: null, html: "<p>Welcome fixture</p>", actions: false,
      revision: 1, package: null, icon: "house", iconData: null, script: null, scriptProblem: null, input: null };
  },
  visible: async () => ({ status: "ok" }),
  close: async () => ({ status: "ok" }),
  watch: async (_request: object, options?: { signal?: AbortSignal }) => {
    options?.signal?.addEventListener("abort", () => wake(), { once: true });
    return (async function* () {
      while (!options?.signal?.aborted) {
        if (queued.length === 0) await new Promise<void>(resolve => { wake = resolve; });
        while (queued.length > 0) yield queued.shift()!;
      }
    })();
  },
};
