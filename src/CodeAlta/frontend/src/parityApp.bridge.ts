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
export const reminder = rejectService("reminder");
export const workspace = {
  ...demo.workspace,
  snapshot: async () => { calls.push("workspace.snapshot"); return demo.workspace.snapshot(); },
  historyTimeline: async (request: HistoryRequest) => {
    calls.push(`workspace.historyTimeline:${request.sessionId}`);
    const page = await demo.workspace.history(request);
    const entries = [...page.entries, ...["Read source", "Search references", "Inspect changes"].flatMap((name, index) => [{
      ...page.entries[0], offset: String(10 + index * 2), eventType: "activity", kind: "ToolCall", phase: "Completed",
      contentId: null, activityId: `tool-${index}`, name, text: `Result for ${name}`, details: JSON.stringify({ arguments: { command: "git diff --stat " + "long-argument".repeat(50), enabled: true, count: 2 } }),
      tool: { primary: index === 0 ? "git diff --stat " + "long-argument".repeat(12) : null, isCommand: index === 0, output: "Recorded output", outputLines: 27, outputBytes: 1434, fields: [] },
    }, { ...page.entries[0], offset: String(11 + index * 2), eventType: "raw", kind: null }])];
    entries.push({ ...page.entries[0], offset: "5", eventType: "notes", kind: "Set", text: "Notes stay outside the conversation" });
    entries.push({ ...page.entries[0], offset: "6", eventType: "sessionUpdate", kind: "DiffUpdated", text: "Turn diff updated", details: "{", detailsTruncated: true,
      files: { partial: true, rows: [{ path: "src/first.ts", kind: "update", added: 2, removed: 0, diff: null },
        { path: "src/last.ts", kind: "create", added: 1, removed: 0, diff: "@@ -0,0 +1 @@\n+new file\n" }] } });
    return { page: { ...page, entries }, revision: null, sources: [] };
  },
};
Object.assign(window, { parityFixture: { calls, unexpected } });
