import assert from "node:assert/strict";
import test from "node:test";
import { createElement, type ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { ShellLanguageContext } from "../shellLanguage";
import { McpHostSettings, McpHostView } from "./McpHostSettings";
import { mcpClientConfiguration, mcpServerName, type McpHostState } from "./mcpHost";

const never = () => assert.fail("rendering must not act");
const render = (element: ReactElement) =>
  renderToStaticMarkup(createElement(ShellLanguageContext.Provider, { value: { locale: "en", choice: "en", setLanguage: never } }, element));
// The host is asked nothing while a view is drawn.
const silent = () => new Promise<never>(() => { });
const state = (change: Partial<McpHostState> = {}): McpHostState => ({ status: "ok", state: "running", enabled: true, url: "http://127.0.0.1:2582/mcp",
  token: null, error: null, tools: ["take_snapshot", "click", "alta"], ...change });

test("a client is given the transport and the address of the server, and its token when it asks for one", () => {
  assert.deepEqual(JSON.parse(mcpClientConfiguration("codealta", "http://127.0.0.1:2582/mcp", null)),
    { mcpServers: { codealta: { type: "http", url: "http://127.0.0.1:2582/mcp" } } });
  assert.deepEqual(JSON.parse(mcpClientConfiguration("codealta-dev", "http://build-box:2583/mcp", "secret-token")),
    { mcpServers: { "codealta-dev": { type: "http", url: "http://build-box:2583/mcp", headers: { Authorization: "Bearer secret-token" } } } });
  // The developer instance runs beside the normal one: a client keeps both apart by their names.
  assert.equal(mcpServerName(false), "codealta");
  assert.equal(mcpServerName(true), "codealta-dev");
});

test("the page is drawn before the host answered, and says what it is for", () => {
  const html = render(createElement(McpHostSettings, { epoch: "e", developer: false, api: { status: silent, setEnabled: never } }));
  assert.match(html, /<h1>CodeAlta MCP server<\/h1>/);
  assert.match(html, /Other applications see and drive this window and run CodeAlta commands through this server\./);
  assert.match(html, /Loading…/);
});

test("a running server shows its address, the configuration of a client and its tools", () => {
  const html = render(createElement(McpHostView, { state: state(), developer: false, busy: false, onEnable: never, copy: never }));
  assert.match(html, /<input[^>]*type="checkbox"[^>]*checked=""/);
  assert.match(html, /Run the MCP server/);
  assert.match(html, /value="http:\/\/127\.0\.0\.1:2582\/mcp"/);
  // The configuration is the text a client's file takes, under the name of this instance.
  assert.match(html, /<pre class="mcp-host-configuration">\{\n  &quot;mcpServers&quot;: \{\n    &quot;codealta&quot;: \{\n      &quot;type&quot;: &quot;http&quot;,\n      &quot;url&quot;: &quot;http:\/\/127\.0\.0\.1:2582\/mcp&quot;/);
  assert.equal(html.includes("Access token"), false);
  assert.match(html, /<span class="mcp-host-count">3<\/span>/);
  for (const tool of ["take_snapshot", "click", "alta"]) assert.ok(html.includes(`>${tool}<`), tool);

  const developer = render(createElement(McpHostView, { state: state({ url: "http://127.0.0.1:2583/mcp" }), developer: true, busy: false, onEnable: never, copy: never }));
  assert.match(developer, /&quot;codealta-dev&quot;: \{/);
});

test("a server other computers reach shows the token its clients send", () => {
  const html = render(createElement(McpHostView, { state: state({ url: "http://build-box:2582/mcp", token: "secret-token" }), developer: false, busy: false, onEnable: never, copy: never }));
  assert.match(html, /Access token/);
  assert.match(html, /value="secret-token"/);
  assert.match(html, /&quot;Authorization&quot;: &quot;Bearer secret-token&quot;/);
});

test("a server that is off, or that could not listen, shows nothing to connect to", () => {
  const off = render(createElement(McpHostView, { state: state({ state: "stopped", enabled: false, url: null, tools: [] }), developer: false, busy: false, onEnable: never, copy: never }));
  assert.equal(/<input[^>]*type="checkbox"[^>]*checked=""/.test(off), false);
  assert.equal(off.includes("mcp-host-configuration"), false);
  assert.equal(off.includes("The MCP server is not listening."), false);

  const failed = render(createElement(McpHostView, { state: state({ state: "failed", url: null, tools: [], error: "Only one usage of each socket address is normally permitted." }),
    developer: false, busy: false, onEnable: never, copy: never }));
  assert.match(failed, /The MCP server is not listening\./);
  assert.match(failed, /Only one usage of each socket address is normally permitted\./);
  assert.equal(failed.includes("mcp-host-configuration"), false);
  // The switch stays on: the server is turned on, and says why it does not run.
  assert.match(failed, /<input[^>]*type="checkbox"[^>]*checked=""/);
});
