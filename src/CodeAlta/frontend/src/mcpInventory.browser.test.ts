import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

test("mounted MCP inventory searches, details, read errors and fences session switches", { skip: !edge, timeout: 60_000 }, async () => {
  const app = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(app, /view === "mcp" \? <McpServersPanel/);
  assert.match(app, /read=\{mcpInventory\.list\}/);
  const dir = await mkdtemp(join(tmpdir(), "codealta-mcp-mounted-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./mcpInventory.mount.tsx", import.meta.url))],
      outfile: join(dir, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    const page = join(dir, "fixture.html");
    await writeFile(page, '<!doctype html><html><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    const profile = join(dir, "profile");
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions",
      `--user-data-dir=${profile}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let i = 0; i < 100 && !port; i++) {
      try { port = (await readFile(join(profile, "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const tabs: { type?: string; url?: string; webSocketDebuggerUrl?: string }[] =
      await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    const tab = tabs.find(value => value.type === "page" && value.url === "about:blank");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => {
      socket!.addEventListener("open", () => resolve(), { once: true });
      socket!.addEventListener("error", () => reject(new Error("browser unavailable")), { once: true });
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown } }>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(new Error(`browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)) as { id?: number; result?: { result?: { value?: unknown } }; error?: object };
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(new Error(`browser ${method} failed`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply);
      socket!.send(JSON.stringify({ id, method, params }));
    });
    await command("Page.enable");
    await command("Page.navigate", { url: `file:///${page.replaceAll("\\", "/")}` });
    const evalJs = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true })).result?.value;
    const wait = async (expression: string) => {
      for (let i = 0; i < 100; i++) {
        if (await evalJs(expression)) return;
        await new Promise(resolve => setTimeout(resolve, 50));
      }
      assert.fail(`UI did not satisfy: ${expression}`);
    };
    await wait("window.mcpFixture?.pending.length === 1");
    const reply = (sessionId: string, projectId: string | null) => ({ status: "ok", epoch: "e1", sessionId, projectId, omitted: 1,
      sources: ["Global: read", "Project: read_error"], policyReadError: false,
      servers: [{ name: "Alpha", scope: "Global", transport: "Stdio", enabled: true, overridesGlobal: false },
        { name: "Beta", scope: "Project", transport: "Http", enabled: false, overridesGlobal: true }] });
    await evalJs(`window.mcpFixture.reply(0, ${JSON.stringify(reply("one", "project-one"))})`);
    await wait("document.body.textContent.includes('Alpha')");
    assert.equal(await evalJs("document.body.textContent.includes('read; Project: read_error')"), true);
    assert.equal(await evalJs("document.body.textContent.includes('inventory may be incomplete')"), true);
    // React's controlled input is exercised with the native value setter.
    await evalJs("Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(document.querySelector('input[aria-label=\"Search MCP servers\"]'), 'bet'); document.querySelector('input[aria-label=\"Search MCP servers\"]').dispatchEvent(new Event('input', { bubbles: true }))");
    await wait("document.querySelectorAll('section[aria-label=\"Configured MCP servers\"] button').length === 1");
    await evalJs("document.querySelector('section[aria-label=\"Configured MCP servers\"] button').click()");
    await wait("document.body.textContent.includes('Unknown — Desktop plugins are off')");
    assert.equal(await evalJs("document.body.textContent.includes('overrides global definition')"), true);
    await evalJs("window.mcpFixture.switchSession('two', null)");
    await wait("window.mcpFixture.pending.length === 2");
    await evalJs("window.mcpFixture.switchSession('three', null)");
    await wait("window.mcpFixture.pending.length === 3");
    await evalJs(`window.mcpFixture.reply(1, ${JSON.stringify(reply("two", null))})`);
    assert.equal(await evalJs("document.body.textContent.includes('Alpha')"), false);
    await evalJs("window.mcpFixture.fail(2)");
    await wait("document.body.textContent.includes('MCP inventory could not be read')");
    assert.equal(await evalJs("document.body.textContent.includes('Beta')"), false);
    assert.equal(await evalJs("document.body.textContent.includes('SECRET_ERROR')"), false);
  } finally {
    socket?.close(); browser?.kill();
    await rm(dir, { recursive: true, force: true, maxRetries: 6, retryDelay: 100 });
  }
});
