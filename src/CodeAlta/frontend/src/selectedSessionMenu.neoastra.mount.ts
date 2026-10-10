// Isolated bridge for the real App, including its plugin provider and selected-scope Explorer rows.
// No native bridge, plugin runtime or user catalog is touched.
import type { CanvasItem, PluginUiWindowButton, PluginUiButtonsRequest, PluginUiButtonsResponse, PluginUiInvokeRequest, WorkspaceSnapshot } from "#neoastra";
import { workspace as baseWorkspace } from "./projectFocus.neoastra.mount";
import { pluginUi as basePluginUi, canvases as baseCanvases } from "./demo-api";
export * from "./projectFocus.neoastra.mount";

const statistics: PluginUiWindowButton = {
  id: "builtin:statistics/statistics-session", pluginKey: "builtin:statistics", pluginId: "statistics", plugin: "Statistics",
  buttonId: "statistics-session", place: "SessionMenu", icon: "chart-column", iconData: null, label: "Statistics of this session",
  commandId: "builtin:statistics/command/statistics-session", canvas: null, canvasScope: null,
  badge: "none", count: 0, tone: "Info", hidden: false, disabled: false, tooltip: null,
};
const board: CanvasItem = { pluginKey: "builtin:fixture", plugin: "Fixture", package: null, id: "board", title: "Board",
  description: null, icon: null, iconData: null, scope: "Session", input: false, actions: 0, describes: false };
const state = {
  reads: [] as PluginUiButtonsRequest[], invoked: [] as PluginUiInvokeRequest[], hold: false,
  pending: [] as { request: PluginUiButtonsRequest; signal?: AbortSignal; resolve: (reply: PluginUiButtonsResponse) => void }[],
};
Object.assign(window, { selectedMenuFixture: {
  state,
  release(label = "Late Statistics") {
    for (const read of state.pending.splice(0)) read.resolve({ status: "ok", place: read.request.place,
      buttons: [{ ...statistics, label }] });
  },
} });

export const pluginUi = {
  ...basePluginUi,
  buttons: async (request: PluginUiButtonsRequest, options?: { signal?: AbortSignal }): Promise<PluginUiButtonsResponse> => {
    state.reads.push(request);
    if (state.hold && request.place === "SessionMenu") return new Promise(resolve => state.pending.push({ request, signal: options?.signal, resolve }));
    return { status: "ok", place: request.place, buttons: request.place === "SessionMenu" ? [statistics] : [] };
  },
  invokeCommand: async (request: PluginUiInvokeRequest) => { state.invoked.push(request); return { status: "started" }; },
};
export const canvases = {
  ...baseCanvases,
  list: async () => ({ status: "ok", canvases: [board] }),
  watch: async (_request: unknown, options: { signal: AbortSignal }) => (async function* () {
    if (!options.signal.aborted) await new Promise<void>(resolve => options.signal.addEventListener("abort", () => resolve(), { once: true }));
  })(),
};
export const workspace = {
  ...baseWorkspace,
  snapshot: async (): Promise<WorkspaceSnapshot> => {
    const snapshot = await baseWorkspace.snapshot();
    return { ...snapshot, sessions: [...snapshot.sessions, ...["chat-1", "chat-2"].map(id => ({ ...snapshot.sessions[0],
      id, title: id, fullTitle: id, scopeKind: "global", projectId: null, workspacePath: "/fixture/global" }))] };
  },
};
