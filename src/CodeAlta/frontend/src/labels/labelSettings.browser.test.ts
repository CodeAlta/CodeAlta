import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";

const edge = browserExecutable;

test("the page of the labels lists their tree, moves a label one step, and changes the label selected", { skip: !edge, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-label-settings-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./labelSettings.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty" } });
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"></head><body style="margin:0"><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown }; exceptionDetails?: unknown }>((resolve, reject) => {
      const id = ++sequence, timer = setTimeout(() => reject(Error(`${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)); if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(`${method}: ${JSON.stringify(message.error)}`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value;
    };
    const frames = () => evaluate("new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))");
    const calls = async () => await evaluate("[...window.calls]") as string[];
    const click = (selector: string) => evaluate(`(() => { const element = document.querySelector(${JSON.stringify(selector)}); element?.click(); return !!element; })()`);
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1100, height: 800, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + 7000; (function check() {
      if (document.querySelectorAll(".label-tree-row").length === 3) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`), true);

    // The tree in its order, a sub-label stepped in under its parent.
    assert.deepEqual(await evaluate(`[...document.querySelectorAll(".label-tree-row")].map(row => row.querySelector(".label-badge-name").textContent + ":" + row.style.paddingInlineStart)`),
      ["Infra:6px", "Helm:24px", "Tools:6px"]);
    // The buttons of a row move it one step; a step that cannot be made is disabled.
    assert.equal(await evaluate(`document.querySelector('[aria-label="Move Infra up"]').disabled`), true);
    assert.equal(await click('[aria-label="Move Infra down"]'), true);
    assert.equal(await click('[aria-label="Move Tools under the label above"]'), true);
    assert.equal(await click('[aria-label="Move Helm out of its parent"]'), true);
    await frames();
    assert.deepEqual(await calls(), ["move infra null 1", "move tools infra -1", "move helm null 1"]);

    // The label selected shows its form: its projects are checked, and a check gives one more.
    assert.equal(await click(".label-tree-row:nth-child(2) .label-tree-open"), true);
    await frames();
    assert.equal(await evaluate(`document.querySelector(".label-form-card input").value`), "Helm");
    assert.equal(await click(".label-tree-row:first-child .label-tree-open"), true);
    await frames();
    assert.deepEqual(await evaluate(`[...document.querySelectorAll(".space-project-checks input")].map(input => input.checked)`), [true, false]);
    assert.equal(await click(".space-project-checks label:nth-child(2) input"), true);
    await frames();
    assert.equal((await calls()).at(-1), "assign beta +infra -");
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); await endBrowser(browser); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
