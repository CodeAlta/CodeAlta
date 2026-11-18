// An isolated secondary window. Storage stands in for the host's windows.json between page loads.
import type { WindowsReportRequest, WindowsWindow } from "#neoastra";
import { windows as demoWindows } from "./demo-api";
import { writeWindowPage } from "./windows/windowsHub";
export * from "./selectedSessionMenu.neoastra.mount";

const pageKey = "dockLayoutFixtureWindowPage";
const tabs = ["one", "two"].map(sessionId => ({ sessionId, projectId: "project", path: "/fixture/project" }));
const initialPage = writeWindowPage({ sessionTabs: JSON.stringify({ version: 1, open: tabs, active: tabs[0] }), fileTabs: null, layout: null });
const secondary: WindowsWindow = {
  id: "review", main: false, name: "Review", open: true, saved: true, spaceId: "default",
  color: null, icon: null, displayId: null, displayName: null, displayPrimary: false,
  pinnedDisplayId: null, pinnedDisplayName: null, pinnedConnected: false, state: "normal", focused: true,
  tabs: ["session:one", "session:two"], activeTab: "session:one",
};

export const windows = {
  ...demoWindows,
  current: async () => ({ status: "ok", window: secondary, windows: [secondary], restoreAtStartup: true, canDrag: false,
    pageState: localStorage.getItem(pageKey) ?? initialPage }),
  list: async () => ({ status: "ok", windows: [secondary], restoreAtStartup: true }),
  report: async (request: WindowsReportRequest) => {
    if (request.pageState !== null) localStorage.setItem(pageKey, request.pageState);
    return { status: "ok" };
  },
  watch: async (_request: unknown, { signal }: { signal: AbortSignal }) => (async function* () {
    if (!signal.aborted) await new Promise<void>(resolve => signal.addEventListener("abort", () => resolve(), { once: true }));
  })(),
};
