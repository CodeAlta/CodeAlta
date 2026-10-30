import assert from "node:assert/strict";
import test from "node:test";
import { activeToolCount, filterMcpServers, mcpSessionSummary, mcpSessionView } from "./mcpSession";

const tool = (name: string, active: boolean, enabled = true, description: string | null = null) =>
  ({ name, alias: `mcp__docs__${name}`, title: null, description, arguments: [{ name: "query", description: "What to look for" }], enabled, active });
const server = (key: string, extra: Record<string, unknown> = {}) => ({ key, transport: "stdio", source: "mcp.json", scope: "global", enabled: true, required: false,
  activated: false, inUse: false, activeToolCount: null, listedAt: null, omittedTools: 0, tools: [], ...extra });

test("the MCP window accepts the servers of the asked project and leaves out what is malformed", () => {
  const view = mcpSessionView({ status: "ok", projectId: "p1", enabled: true, omitted: 2, servers: [
    server("docs", { inUse: true, activated: true, activeToolCount: 2, tools: [tool("search", true), tool("fetch", true), { name: "" }] }),
    server("off", { enabled: false, inUse: true, activated: true, tools: [tool("run", true)] }),
    { key: 42 },
  ] }, "p1");
  assert.equal(view?.enabled, true);
  assert.equal(view?.omitted, 2);
  assert.deepEqual(view?.servers.map(item => [item.key, item.state, item.activated, item.tools.length]), [["docs", "active", true, 2], ["off", "disabled", false, 1]]);
  // A server the policy turns off has no active tool, whatever the session activated.
  assert.equal(view?.servers[1].tools[0].active, false);
  assert.deepEqual(view?.servers[0].tools[0].arguments, [{ name: "query", description: "What to look for" }]);

  assert.equal(mcpSessionView({ status: "stale_epoch", projectId: "p1", servers: [] }, "p1"), null);
  assert.equal(mcpSessionView({ status: "ok", projectId: "p2", servers: [] }, "p1"), null);
  assert.equal(mcpSessionView({ status: "ok", projectId: null }, null), null);
  assert.deepEqual(mcpSessionView({ status: "ok", projectId: null, enabled: false, omitted: 0, servers: [] }, null), { enabled: false, omitted: 0, servers: [] });
});

test("the summary counts the servers in use and the tools the session has", () => {
  const view = mcpSessionView({ status: "ok", projectId: null, enabled: true, omitted: 0, servers: [
    server("docs", { inUse: true, activeToolCount: 5, omittedTools: 1, tools: [tool("search", true), tool("fetch", false)] }),
    server("git", { inUse: true, tools: [tool("log", true), tool("blame", false)] }),
    server("web", { tools: [tool("get", false)] }),
    server("off", { enabled: false }),
  ] }, null)!;
  // The count the last run was given, else the active tools known.
  assert.equal(activeToolCount(view.servers[0]), 5);
  assert.equal(activeToolCount(view.servers[1]), 1);
  assert.equal(activeToolCount(view.servers[2]), 0);
  assert.deepEqual(mcpSessionSummary(view), { servers: 4, activeServers: 2, disabledServers: 1, activeTools: 6, tools: 6 });
});

test("the filter keeps what the session has, or what a search matches, servers in use first", () => {
  const servers = mcpSessionView({ status: "ok", projectId: null, enabled: true, omitted: 0, servers: [
    server("web", { tools: [tool("get", false, true, "Downloads a page")] }),
    server("docs", { inUse: true, tools: [tool("search", true, true, "Searches the documentation"), tool("fetch", false)] }),
    server("off", { enabled: false, tools: [tool("run", false, false)] }),
  ] }, null)!.servers;

  assert.deepEqual(filterMcpServers(servers, "", "active").map(item => [item.key, item.tools.map(entry => entry.name)]), [["docs", ["search"]]]);
  assert.deepEqual(filterMcpServers(servers, "", "all").map(item => item.key), ["docs", "web", "off"]);
  // A word of a description, of any case; every word must match.
  assert.deepEqual(filterMcpServers(servers, "PAGE", "all").map(item => [item.key, item.tools.map(entry => entry.name)]), [["web", ["get"]]]);
  assert.deepEqual(filterMcpServers(servers, "searches page", "all"), []);
  // The key of a server keeps all its tools in scope.
  assert.deepEqual(filterMcpServers(servers, "docs", "all").map(item => [item.key, item.tools.length]), [["docs", 2]]);
  assert.deepEqual(filterMcpServers(servers, "page", "active"), []);
});
