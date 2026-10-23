import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";

const edge = browserExecutable;
const origin = "https://app.invalid";
const pageUrl = `${origin}/index.html`;
// The policy of a view without declarations, as the host serves it (DesktopMcpAppPolicy).
const viewPolicy = "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; "
  + "media-src 'self' data:; connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";

// A view that speaks the protocol without a library: it reports what it got, as messages to the session.
const view = `<!DOCTYPE html><html><head><meta charset="utf-8"></head><body><div style="height:300px">view</div><script>
let id = 1; const pending = new Map();
const request = (method, params) => new Promise((resolve, reject) => { const n = id++; pending.set(n, { resolve, reject }); parent.postMessage({ jsonrpc: "2.0", id: n, method, params }, "*"); });
const notify = (method, params) => parent.postMessage({ jsonrpc: "2.0", method, params }, "*");
const report = text => request("tools/call", { name: "report", arguments: { text } });
const say = text => request("ui/message", { role: "user", content: [{ type: "text", text }] });
let input = null, context = {};
window.addEventListener("message", async event => {
  const message = event.data;
  if (message.method === undefined) { const entry = pending.get(message.id); pending.delete(message.id); message.error ? entry.reject(message.error) : entry.resolve(message.result); return; }
  if (message.method === "ui/notifications/tool-input") input = message.params.arguments;
  if (message.method === "ui/notifications/tool-result") {
    const refreshed = await request("tools/call", { name: "view_refresh", arguments: { n: 1 } });
    let refused = "called"; try { await request("tools/call", { name: "model_only" }); } catch (error) { refused = error.message; }
    let top = "reached"; try { void window.top.location.href; } catch { top = "blocked"; }
    let fetched = "reached"; try { await fetch("https://elsewhere.invalid/"); } catch { fetched = "blocked"; }
    let alone = "sent"; try { await say("on its own"); } catch (error) { alone = "refused"; }
    await report("alone " + alone + " input " + JSON.stringify(input) + " result " + message.params.content[0].text + " items " + message.params.structuredContent.items.join(",")
      + " refreshed " + refreshed.structuredContent.count + " refused " + refused + " top " + top + " fetch " + fetched
      + " theme " + context.theme + " variables " + (context.styles?.variables?.["--color-text-primary"] ? "yes" : "no")
      + " tool " + context.toolInfo?.tool?.name + " mode " + context.displayMode);
  }
  if (message.method === "ui/notifications/host-context-changed" && message.params.theme) {
    // The test gave the frame the keyboard, as a press in the view does: the message goes to the session.
    await say("theme " + message.params.theme);
    location.href = "https://other.invalid/away";
  }
});
request("ui/initialize", { protocolVersion: "2026-01-26", appInfo: { name: "test", version: "1" }, appCapabilities: { availableDisplayModes: ["inline"] } })
  .then(result => { context = result.hostContext; notify("ui/notifications/initialized", {}); notify("ui/notifications/size-changed", { width: 400, height: document.documentElement.scrollHeight }); });
</script></body></html>`;
// The page the view tries to go to: it would speak as the view, were the frame let go there.
const away = `<!DOCTYPE html><script>parent.postMessage({ jsonrpc: "2.0", id: 99, method: "ui/message", params: { role: "user", content: [{ type: "text", text: "from away" }] } }, "*");</script>`;

test("a view gets its input, its result and its context, calls the tools of its server, and is stopped once it leaves its page",
  { skip: !edge, timeout: 180_000 }, async () => {
    const directory = await mkdtemp(join(tmpdir(), "codealta-mcp-app-"));
    let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
    try {
      const config = JSON.parse(await readFile(new URL("../../../neoastra.json", import.meta.url), "utf8")) as { assets: { csp: string } };
      // The page's own policy, with the origin of the views of this test in the place of the scheme of the host.
      const pagePolicy = config.assets.csp.replace("frame-src mcpapp:", "frame-src https://view.invalid");
      assert.notEqual(pagePolicy, config.assets.csp, "the page's policy names the scheme of the views");
      const bundle = await build({ entryPoints: [fileURLToPath(new URL("./mcpAppView.mount.tsx", import.meta.url))], bundle: true, platform: "browser", format: "iife", write: false,
        define: { "process.env.NODE_ENV": '"development"' } });
      const style = (await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")) + await readFile(new URL("../style.css", import.meta.url), "utf8");
      const assets = new Map<string, { type: string; body: string; policy: string }>([
        [pageUrl, { type: "text/html", policy: pagePolicy, body: '<!doctype html><html data-theme="dark"><head><meta charset="utf-8"><link rel="icon" href="data:,"><link rel="stylesheet" href="/style.css"></head><body class="ide-shell"><div id="root"></div><script src="/fixture.js"></script></body></html>' }],
        [`${origin}/style.css`, { type: "text/css", body: style, policy: pagePolicy }],
        [`${origin}/fixture.js`, { type: "text/javascript", body: bundle.outputFiles[0].text, policy: pagePolicy }],
        ["https://view.invalid/view/t1", { type: "text/html", body: view, policy: viewPolicy }],
        ["https://view.invalid/view/t2", { type: "text/html", body: view, policy: viewPolicy }],
        ["https://other.invalid/away", { type: "text/html", body: away, policy: viewPolicy }],
      ]);
      browser = spawn(edge!, [...browserBaseArgs, "--host-resolver-rules=MAP * ~NOTFOUND", `--user-data-dir=${join(directory, "profile")}`, "--remote-debugging-port=0", "about:blank"],
        { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
      const port = await waitForBrowserPort(join(directory, "profile"), browser);
      const tabs = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type: string; url: string; webSocketDebuggerUrl: string }[];
      socket = new WebSocket(tabs.find(tab => tab.type === "page" && tab.url === "about:blank")!.webSocketDebuggerUrl);
      await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
      let sequence = 0;
      const waiting = new Map<number, (value: any) => void>();
      const requested: string[] = [];
      const problems: string[] = [];
      const command = (method: string, params: object = {}) => new Promise<Record<string, any>>((resolve, reject) => {
        const id = ++sequence;
        const timer = setTimeout(() => { waiting.delete(id); reject(new Error(`${method} timed out`)); }, 20_000);
        waiting.set(id, reply => { clearTimeout(timer); reply.error ? reject(new Error(JSON.stringify(reply.error))) : resolve(reply.result ?? {}); });
        socket!.send(JSON.stringify({ id, method, params }));
      });
      socket.addEventListener("message", event => {
        const message = JSON.parse(String(event.data));
        if (waiting.has(message.id)) { waiting.get(message.id)!(message); waiting.delete(message.id); return; }
        if (message.method === "Fetch.requestPaused") {
          const { requestId, request } = message.params, asset = assets.get(request.url);
          requested.push(request.url);
          void (asset ? command("Fetch.fulfillRequest", { requestId, responseCode: 200,
            responseHeaders: [{ name: "Content-Type", value: asset.type }, { name: "Content-Security-Policy", value: asset.policy }],
            body: Buffer.from(asset.body).toString("base64") }) : command("Fetch.failRequest", { requestId, errorReason: "BlockedByClient" })).catch(() => { });
        }
        if (message.method === "Runtime.exceptionThrown") problems.push(`exception: ${JSON.stringify(message.params.exceptionDetails).slice(0, 400)}`);
        if (message.method === "Runtime.consoleAPICalled") problems.push(`console ${message.params.type}: ${message.params.args.map((a: any) => a.value ?? a.description).join(" ").slice(0, 300)}`);
        if (message.method === "Log.entryAdded") problems.push(`log: ${message.params.entry.text}`);
      });
      const evaluate = async <T>(expression: string): Promise<T> => {
        const reply = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
        assert.equal(reply.exceptionDetails, undefined, JSON.stringify(reply.exceptionDetails)); return reply.result?.value as T;
      };
      const until = async (expression: string, what: string) => {
        const found = await evaluate<boolean>(`new Promise(resolve => { const end = Date.now() + 15000; (function check() { let ok = false; try { ok = !!(${expression}); } catch {} if (ok) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 30); })(); })`);
        if (!found) assert.fail(`waited for ${what}: ${JSON.stringify(await evaluate("mcpAppFixture.state"))}; frames ${JSON.stringify(await evaluate("[...document.querySelectorAll('iframe')].map(frame => frame.src)"))}; requests ${requested.join(" ")}; ${problems.join(" | ")}`);
      };
      await command("Page.enable"); await command("Runtime.enable"); await command("Log.enable");
      await command("Fetch.enable", { patterns: [{ urlPattern: "*", requestStage: "Request" }] });
      await command("Page.navigate", { url: pageUrl });
      await until("window.mcpAppFixture", "the fixture");

      // A call of this run of the window shows its view at once, opened once whatever StrictMode runs twice.
      await evaluate("mcpAppFixture.render(true)");
      await until("mcpAppFixture.state.calls.some(call => call.name === 'report')", "the report of the view");
      const calls = await evaluate<{ name: string; args: { text?: string } }[]>("mcpAppFixture.state.calls");
      assert.equal(calls.find(call => call.name === "report")!.args.text,
        'alone refused input {"title":"Hi"} result Showed. items alpha refreshed 1 refused tool_not_for_app top blocked fetch blocked theme dark variables yes tool show_view mode inline');
      assert.deepEqual(calls.slice(0, 2), [{ name: "view_refresh", args: { n: 1 } }, { name: "model_only", args: {} }]);
      assert.deepEqual(await evaluate("mcpAppFixture.state.messages"), [], "a view does not send a message on its own");
      assert.equal(await evaluate("mcpAppFixture.state.opened"), 1);
      assert.deepEqual(await evaluate("[...document.querySelectorAll('iframe')].map(frame => [frame.getAttribute('sandbox'), frame.src])"),
        [["allow-scripts allow-same-origin allow-forms", "https://view.invalid/view/t1"]]);
      await until("document.querySelector('iframe').getBoundingClientRect().height >= 300", "the height the view asked for");
      assert.ok(!requested.includes("https://elsewhere.invalid/"), "the policy of the view blocks what it does not declare");

      // A change of theme is told to the view. The view then tries to leave for another origin: the page's own policy
      // keeps the frame from going there, and the window stops talking to whatever the frame now holds.
      const problemCount = problems.length;
      await evaluate("document.querySelector('iframe').focus()");
      await evaluate("document.documentElement.dataset.theme = 'light'");
      await until("mcpAppFixture.state.messages.includes('theme light')", "the change of theme");
      await until("document.querySelector('.mcp-app-notice')?.textContent === 'The view left its page.'", "the view to leave");
      await new Promise(resolve => setTimeout(resolve, 300));
      assert.ok(!requested.includes("https://other.invalid/away"), "the frame cannot go to another origin");
      assert.ok(problems.slice(problemCount).some(problem => problem.includes("frame-src")), `the page's policy refused the frame: ${problems.join(" | ")}`);
      assert.ok(!(await evaluate<string[]>("mcpAppFixture.state.messages")).includes("from away"));

      // Gone from the timeline, its token is closed; an older call waits for a press.
      await evaluate("mcpAppFixture.clear()");
      await until("mcpAppFixture.state.closed.includes('t1')", "the token to close");
      await evaluate("mcpAppFixture.render(false)");
      await new Promise(resolve => setTimeout(resolve, 300));
      assert.equal(await evaluate("mcpAppFixture.state.opened"), 1);
      await evaluate("[...document.querySelectorAll('.mcp-app-view button')].find(button => button.textContent === 'Show view').click()");
      await until("mcpAppFixture.state.opened === 2 && document.querySelector('iframe')?.src.endsWith('/t2')", "the view on demand");
      await until("mcpAppFixture.state.calls.filter(call => call.name === 'report').length === 2", "the second report");

      // A host that does not answer leaves the view to be tried again, not waiting for ever.
      await evaluate("mcpAppFixture.clear(); mcpAppFixture.state.failOpen = true; mcpAppFixture.render(false)");
      await evaluate("[...document.querySelectorAll('.mcp-app-view button')].find(button => button.textContent === 'Show view').click()");
      await until("document.querySelector('.mcp-app-notice')?.textContent.startsWith('The view could not be opened: The host did not answer.')", "the failure");
      await evaluate("mcpAppFixture.state.failOpen = false; [...document.querySelectorAll('.mcp-app-view button')].find(button => button.textContent === 'Show view').click()");
      await until("document.querySelector('iframe')?.src.endsWith('/t3')", "the view tried again");
    } finally {
      if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
      socket?.close();
      await endBrowser(browser);
      await rm(directory, { recursive: true, force: true, maxRetries: 5 }).catch(() => { });
    }
  });
