// Disposable page; the production view of an MCP tool call (McpAppSlot) with a host of the test, whose view is served
// by the test on an origin of its own.
import { StrictMode, createElement } from "react";
import { flushSync } from "react-dom";
import { createRoot } from "react-dom/client";
import { McpAppError } from "./mcpAppBridge";
import { createLiveViews, McpAppsContext, type McpAppsHost } from "./mcpAppHost";
import { McpAppSlot } from "./McpAppView";

const root = createRoot(document.getElementById("root")!);
const state = { failOpen: false, opened: 0, closed: [] as string[], calls: [] as { name: string; args: Record<string, unknown> }[], messages: [] as string[], links: [] as string[] };
const host: McpAppsHost = {
  startedAt: Date.now(), hostVersion: "test", live: createLiveViews(),
  async open() {
    if (state.failOpen) throw new Error("The host did not answer.");
    const token = `t${++state.opened}`;
    return { view: { token, frameUrl: `https://view.invalid/view/${token}`, server: "tiny", tool: "show_view", prefersBorder: true,
      toolInput: JSON.stringify({ title: "Hi" }), toolResult: JSON.stringify({ content: [{ type: "text", text: "Showed." }], structuredContent: { items: ["alpha"] } }),
      toolDefinition: JSON.stringify({ name: "show_view", inputSchema: { type: "object" } }) } };
  },
  async callTool(_token, name, args) {
    state.calls.push({ name, args });
    if (name === "model_only") throw new McpAppError(-32000, "tool_not_for_app");
    return { content: [{ type: "text", text: "Refreshed." }], structuredContent: { count: state.calls.length } };
  },
  async readResource() { return { contents: [] }; },
  close(token) { state.closed.push(token); },
  async sendMessage(text) { state.messages.push(text); return true; },
  openLink(url) { state.links.push(url); return true; },
};
const fixture = {
  state,
  // Under StrictMode, as in the application: each effect of a new component runs twice.
  render(live: boolean) {
    const timestamp = new Date(live ? Date.now() + 1000 : 0).toISOString();
    flushSync(() => root.render(createElement(StrictMode, null, createElement(McpAppsContext.Provider, { value: host },
      createElement(McpAppSlot, { offset: "1", server: "tiny", timestamp })))));
  },
  clear() { flushSync(() => root.render(null)); },
};
Object.assign(window, { mcpAppFixture: fixture });
