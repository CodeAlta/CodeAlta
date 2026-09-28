// Test-only bridge: bundled in place of #neoastra, never imported by production.
// Exercise the actual App without native transport or provider activation.
import * as demo from "./demo-api";
import type { HistoryRequest } from "#neoastra";
export * from "./demo-api";

const calls: string[] = [];
const unexpected: string[] = [];
function rejectService(service: string) {
  return new Proxy({}, { get: (_, method: string) => async () => {
    unexpected.push(`${service}.${method}`);
    throw new Error(`Unexpected fixture call: ${service}.${method}`);
  } });
}
export const applicationLogs = rejectService("applicationLogs");
export const promptCreation = rejectService("promptCreation");
export const mcpInventory = rejectService("mcpInventory");
export const skillsInspection = rejectService("skillsInspection");
export const reminder = rejectService("reminder");
export const workspace = {
  ...demo.workspace,
  snapshot: async () => { calls.push("workspace.snapshot"); return demo.workspace.snapshot(); },
  historyTimeline: async (request: HistoryRequest) => {
    calls.push(`workspace.historyTimeline:${request.sessionId}`);
    return { page: await demo.workspace.history(request), revision: null, sources: [] };
  },
};
Object.assign(window, { parityFixture: { calls, unexpected } });
