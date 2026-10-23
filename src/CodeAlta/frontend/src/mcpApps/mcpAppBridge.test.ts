import assert from "node:assert/strict";
import test from "node:test";
import { McpAppBridge, McpAppError, mcpAppProtocolVersion, type McpAppHost } from "./mcpAppBridge";
import { createLiveViews, createMcpAppsHost, type McpAppsApi } from "./mcpAppHost";

type Posted = { jsonrpc: string; id?: number | string; method?: string; params?: any; result?: any; error?: { code: number; message: string } };

function harness(overrides: Partial<McpAppHost> = {}) {
  const posted: Posted[] = [];
  const calls: string[] = [];
  const host: McpAppHost = {
    callTool: async (name, args) => { calls.push(`tool ${name} ${JSON.stringify(args)}`); return { content: [{ type: "text", text: "done" }] }; },
    readResource: async uri => { calls.push(`read ${uri}`); return { contents: [] }; },
    sendMessage: async text => { calls.push(`message ${text}`); return true; },
    openLink: url => { calls.push(`link ${url}`); return true; },
    requestDisplayMode: mode => { calls.push(`mode ${mode}`); return mode === "fullscreen" ? "fullscreen" : "inline"; },
    sizeChanged: size => calls.push(`size ${size.width}x${size.height}`),
    log: () => calls.push("log"),
    ...overrides,
  };
  const bridge = new McpAppBridge(message => posted.push(message as Posted), host, {
    toolInput: { title: "Hello" }, toolResult: { content: [{ type: "text", text: "Showed." }], structuredContent: { items: ["a"] } },
    context: () => ({ theme: "dark", displayMode: "inline" }), hostVersion: "1.2.3",
  });
  const settle = () => new Promise(resolve => setTimeout(resolve, 0));
  const connect = async () => {
    bridge.receive({ jsonrpc: "2.0", id: 1, method: "ui/initialize", params: { protocolVersion: mcpAppProtocolVersion, appInfo: { name: "v", version: "1" }, appCapabilities: {} } });
    bridge.receive({ jsonrpc: "2.0", method: "ui/notifications/initialized", params: {} });
    await settle();
  };
  return { posted, calls, bridge, settle, connect };
}

test("the handshake answers the context, then the view gets its input and its result", async () => {
  const { posted, connect, bridge } = harness();
  await connect();
  assert.equal(posted[0].id, 1);
  assert.equal(posted[0].result.protocolVersion, mcpAppProtocolVersion);
  assert.deepEqual(posted[0].result.hostInfo, { name: "CodeAlta", version: "1.2.3" });
  assert.deepEqual(Object.keys(posted[0].result.hostCapabilities).sort(), ["logging", "message", "openLinks", "serverResources", "serverTools"]);
  assert.equal(posted[0].result.hostContext.theme, "dark");
  assert.equal(posted[1].method, "ui/notifications/tool-input");
  assert.deepEqual(posted[1].params, { arguments: { title: "Hello" } });
  assert.equal(posted[2].method, "ui/notifications/tool-result");
  assert.deepEqual(posted[2].params.structuredContent, { items: ["a"] });
  assert.equal(bridge.ready, true);
});

test("nothing but the handshake is answered before it, and it is answered once", async () => {
  const { posted, bridge, settle, calls } = harness();
  bridge.receive({ jsonrpc: "2.0", id: 1, method: "tools/call", params: { name: "x" } });
  bridge.receive({ jsonrpc: "2.0", id: 2, method: "ui/initialize", params: {} });
  bridge.receive({ jsonrpc: "2.0", id: 3, method: "ui/initialize", params: {} });
  bridge.contextChanged({ theme: "light" });
  await settle();
  assert.equal(posted[0].error?.code, -32600);
  assert.ok(posted[1].result);
  assert.equal(posted[2].error?.code, -32600);
  assert.equal(posted.length, 3, "no context change before the view is ready");
  assert.deepEqual(calls, []);
});

test("a tool call, a read, a message and a link go to the host and are answered", async () => {
  const { posted, connect, bridge, settle, calls } = harness();
  await connect();
  posted.length = 0;
  bridge.receive({ jsonrpc: "2.0", id: 10, method: "tools/call", params: { name: "refresh", arguments: { n: 1 } } });
  bridge.receive({ jsonrpc: "2.0", id: 11, method: "resources/read", params: { uri: "ui://x/data" } });
  bridge.receive({ jsonrpc: "2.0", id: 12, method: "ui/message", params: { role: "user", content: [{ type: "text", text: "Hi" }] } });
  bridge.receive({ jsonrpc: "2.0", id: 13, method: "ui/open-link", params: { url: "https://example.com" } });
  bridge.receive({ jsonrpc: "2.0", id: "s", method: "ping" });
  await settle();
  assert.deepEqual(calls, ['tool refresh {"n":1}', "read ui://x/data", "message Hi", "link https://example.com"]);
  assert.deepEqual(posted.map(message => message.id).sort(), [10, 11, 12, 13, "s"].sort());
  assert.deepEqual(posted.find(message => message.id === 10)!.result, { content: [{ type: "text", text: "done" }] });
  assert.ok(posted.every(message => message.error === undefined));
});

test("a refusal of the host is a JSON-RPC error, and an unknown method is not found", async () => {
  const { posted, connect, bridge, settle } = harness({
    callTool: async () => { throw new McpAppError(-32000, "tool_not_for_app"); },
    openLink: () => false,
    sendMessage: async () => false,
  });
  await connect();
  posted.length = 0;
  bridge.receive({ jsonrpc: "2.0", id: 1, method: "tools/call", params: { name: "model_only" } });
  bridge.receive({ jsonrpc: "2.0", id: 2, method: "ui/open-link", params: { url: "file:///etc/passwd" } });
  bridge.receive({ jsonrpc: "2.0", id: 3, method: "ui/message", params: { role: "user", content: { type: "text", text: "x" } } });
  bridge.receive({ jsonrpc: "2.0", id: 4, method: "ui/update-model-context", params: {} });
  bridge.receive({ jsonrpc: "2.0", id: 5, method: "tools/call", params: { name: "x", arguments: [1] } });
  bridge.receive({ jsonrpc: "2.0", id: 6, method: "ui/message", params: { role: "assistant", content: [{ type: "text", text: "x" }] } });
  await settle();
  const error = (id: number) => posted.find(message => message.id === id)!.error!;
  assert.deepEqual(error(1), { code: -32000, message: "tool_not_for_app" });
  assert.equal(error(2).code, -32000);
  assert.equal(error(3).code, -32000);
  assert.equal(error(4).code, -32601);
  assert.equal(error(5).code, -32602);
  assert.equal(error(6).code, -32602);
});

test("one message of a view is sent at a time", async () => {
  let release!: () => void;
  const { posted, connect, bridge, settle } = harness({ sendMessage: () => new Promise<boolean>(resolve => { release = () => resolve(true); }) });
  await connect();
  posted.length = 0;
  bridge.receive({ jsonrpc: "2.0", id: 1, method: "ui/message", params: { content: [{ type: "text", text: "one" }] } });
  bridge.receive({ jsonrpc: "2.0", id: 2, method: "ui/message", params: { content: [{ type: "text", text: "two" }] } });
  await settle();
  assert.equal(posted.find(message => message.id === 2)!.error!.code, -32000);
  release();
  await settle();
  assert.deepEqual(posted.find(message => message.id === 1)!.result, {});
});

test("sizes, display modes and context changes", async () => {
  const { posted, connect, bridge, settle, calls } = harness();
  await connect();
  posted.length = 0;
  bridge.receive({ jsonrpc: "2.0", method: "ui/notifications/size-changed", params: { width: 300, height: 420 } });
  bridge.receive({ jsonrpc: "2.0", method: "ui/notifications/size-changed", params: { height: "tall" } });
  bridge.receive({ jsonrpc: "2.0", id: 7, method: "ui/request-display-mode", params: { mode: "fullscreen" } });
  bridge.receive({ jsonrpc: "2.0", id: 8, method: "ui/request-display-mode", params: { mode: "pip" } });
  bridge.contextChanged({ theme: "light" });
  await settle();
  assert.deepEqual(calls, ["size 300x420", "mode fullscreen", "mode pip"]);
  assert.deepEqual(posted.find(message => message.id === 7)!.result, { mode: "fullscreen" });
  assert.deepEqual(posted.find(message => message.id === 8)!.result, { mode: "inline" });
  assert.deepEqual(posted.find(message => message.method === "ui/notifications/host-context-changed")!.params, { theme: "light" });
});

test("a teardown tells the view and waits for its answer, then nothing more is said or heard", async () => {
  const { posted, connect, bridge, settle, calls } = harness();
  await connect();
  posted.length = 0;
  const done = bridge.teardown("closed", 5000);
  const request = posted.find(message => message.method === "ui/resource-teardown")!;
  assert.deepEqual(request.params, { reason: "closed" });
  bridge.receive({ jsonrpc: "2.0", id: request.id, result: {} });
  await done;
  bridge.receive({ jsonrpc: "2.0", id: 20, method: "tools/call", params: { name: "x" } });
  bridge.contextChanged({ theme: "light" });
  await settle();
  assert.equal(posted.length, 1);
  assert.deepEqual(calls, []);
  assert.equal(bridge.ready, false);
});

test("a teardown without an answer ends after its delay", async () => {
  const { connect, bridge } = harness();
  await connect();
  const started = Date.now();
  await bridge.teardown("closed", 20);
  assert.ok(Date.now() - started < 1000);
  assert.equal(bridge.ready, false);
});

test("messages that are not JSON-RPC are ignored", async () => {
  const { posted, bridge, settle } = harness();
  bridge.receive("text");
  bridge.receive(null);
  bridge.receive({ jsonrpc: "1.0", id: 1, method: "ui/initialize" });
  bridge.receive({ jsonrpc: "2.0", id: { x: 1 }, method: "ui/initialize" });
  await settle();
  assert.equal(posted.length, 0);
});

test("the live views of a pane are capped: the oldest goes back to its button", () => {
  const evicted: string[] = [];
  const live = createLiveViews(2);
  live.claim("a", () => evicted.push("a"));
  live.claim("b", () => evicted.push("b"));
  live.claim("a", () => evicted.push("a again"));
  live.claim("c", () => evicted.push("c"));
  assert.deepEqual(evicted, ["b"]);
  live.release("a");
  assert.equal(live.count(), 1);
});

test("the host of the views names the epoch, the session and the token, and opens only web and mail links", async () => {
  const requests: unknown[] = [];
  const api: McpAppsApi = {
    open: async request => { requests.push(request); return { status: "ok", view: { token: "t", frameUrl: "mcpapp://o/view/t", server: "s", tool: "x", toolInput: "{}", toolResult: "{}" } }; },
    callTool: async request => { requests.push(request); return request.name === "bad" ? { status: "failed", code: "tool_not_for_app", error: "no" } : { status: "ok", json: '{"content":[]}' }; },
    readResource: async request => { requests.push(request); return { status: "closed" }; },
    close: async request => { requests.push(request); return { status: "ok" }; },
  };
  const opened: string[] = [];
  const host = createMcpAppsHost(api, "epoch", "session", async () => true, url => { opened.push(url); return true; }, "1.0");
  assert.equal((await host.open("42")).view?.token, "t");
  assert.deepEqual(await host.callTool("t", "ok", { a: 1 }), { content: [] });
  await assert.rejects(host.callTool("t", "bad", {}), (error: unknown) => error instanceof McpAppError && error.message === "no");
  await assert.rejects(host.readResource("t", "ui://x"), (error: unknown) => error instanceof McpAppError && error.message === "closed");
  host.close("t");
  assert.equal(host.openLink("javascript:alert(1)"), false);
  assert.equal(host.openLink("https://example.com"), true);
  assert.deepEqual(opened, ["https://example.com"]);
  assert.deepEqual(requests[0], { expectedEpoch: "epoch", sessionId: "session", offset: "42" });
  assert.deepEqual(requests[1], { expectedEpoch: "epoch", token: "t", name: "ok", argumentsJson: '{"a":1}' });
});
