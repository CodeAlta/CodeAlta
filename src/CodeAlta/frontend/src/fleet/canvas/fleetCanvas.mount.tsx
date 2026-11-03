// Disposable page: the Fleet canvas of a computer linked to no server, with two paired machines. Driven by
// fleetCanvas.browser.test.ts through `fleetCanvasFixture`: what the canvas asks of the window is recorded.
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import type { FleetDirectoryResult } from "#neoastra";
import { ShellLanguageContext } from "../../shellLanguage";
import { FleetCanvas, type FleetCanvasActions, type FleetCanvasApi } from "./FleetCanvas";

document.documentElement.classList.add("bp6-dark");

const calls: string[] = [];
const directory: FleetDirectoryResult = {
  status: "paired", linkId: null, url: null, organization: null, connection: null,
  machines: [
    { id: "mini-id", name: "Mac mini", kind: "node", os: "macos", version: "4.0.1", online: true, presence: "online", activeSessions: 1, isThisComputer: false,
      isPaired: true, providers: ["Codex"], sessions: 2, isReachable: true },
    { id: "box-id", name: "build box", kind: "node", os: null, version: null, online: false, presence: "offline", activeSessions: 0, isThisComputer: false,
      isPaired: true, providers: [], sessions: 0, isReachable: true },
  ],
  sessions: [
    { id: "s-works", machineId: "mini-id", state: "running", providerType: "codex", model: "gpt-6", createdAt: "2026-11-03T08:00:00Z", updatedAt: "2026-11-03T09:30:00Z",
      turns: 0, tokens: 0, canOpen: true, title: "Fix the build" },
    { id: "s-idle", machineId: "mini-id", state: "idle", providerType: "codex", model: "gpt-6", createdAt: "2026-11-03T07:00:00Z", updatedAt: "2026-11-03T08:00:00Z",
      turns: 0, tokens: 0, canOpen: true, title: "Release notes" },
  ],
};
const api: FleetCanvasApi = {
  directory: async () => directory,
  watch: async signal => ({ async *[Symbol.asyncIterator]() { await new Promise(resolve => signal.addEventListener("abort", resolve)); } }),
};
const actions: FleetCanvasActions = {
  openSession: id => calls.push(JSON.stringify({ session: id })),
  openMachine: id => calls.push(JSON.stringify({ machine: id })),
  runCommand: name => { calls.push(JSON.stringify({ command: name })); return true; },
};
Object.assign(window, { fleetCanvasFixture: { calls } });

createRoot(document.getElementById("root")!).render(<StrictMode>
  <ShellLanguageContext.Provider value={{ locale: "en", choice: "en", setLanguage: () => {} }}>
    <FleetCanvas api={api} visible actions={actions} />
  </ShellLanguageContext.Provider>
</StrictMode>);
