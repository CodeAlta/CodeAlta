import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "./browserTarget";

test("project deletion always reviews the archived identity, defaults to Cancel, and requires explicit confirmation", { skip: !browserExecutable, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-project-delete-dialog-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./projectDeleteDialog.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown }; exceptionDetails?: unknown }>((resolve, reject) => {
      const id = ++sequence, timer = setTimeout(() => reject(Error(`${method} timed out`)), 12_000);
      const receive = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)); if (message.id !== id) return;
        socket!.removeEventListener("message", receive); clearTimeout(timer);
        if (message.error) reject(Error(JSON.stringify(message.error))); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", receive); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    const frames = () => evaluate("new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))");
    const key = async (key: string, code: string, virtual: number) => {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key, code, windowsVirtualKeyCode: virtual });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: virtual });
      await frames();
    };
    await command("Page.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + 7000; (function check() {
      if (document.querySelector('dialog[open]')) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`), true);
    await frames();
    assert.equal(await evaluate("document.activeElement.textContent"), "Cancel");
    assert.equal(await evaluate("document.querySelector('dialog').textContent.includes('Archived project') && document.querySelector('dialog').textContent.includes('/synthetic/source')"), true);
    assert.equal(await evaluate("document.querySelector('dialog').textContent.includes('source files and git worktrees are kept')"), true);
    assert.equal(await evaluate("document.querySelectorAll('dialog input[type=checkbox]').length"), 0, "there is no confirmation opt-out");
    await key("Enter", "Enter", 13);
    assert.equal(await evaluate("document.querySelector('dialog') === null && document.querySelector('#confirmations').textContent === '0'"), true);
    await evaluate("document.querySelector('#reopen').click()"); await frames();
    await key("Escape", "Escape", 27);
    assert.equal(await evaluate("document.querySelector('dialog') === null && document.querySelector('#confirmations').textContent === '0'"), true);
    await evaluate("document.querySelector('#reopen').click()"); await frames();
    await command("Emulation.setDeviceMetricsOverride", { width: 900, height: 650, deviceScaleFactor: 1, mobile: false }); await frames();
    assert.equal(await evaluate("(() => { const body = document.querySelector('.app-window-body'); return body.scrollWidth <= body.clientWidth; })()"), true);
    await evaluate("[...document.querySelectorAll('dialog button')].find(button => button.textContent === 'Delete project and sessions').click()"); await frames();
    assert.equal(await evaluate("document.querySelector('dialog') === null && document.querySelector('#confirmations').textContent === '1'"), true);
    await command("Browser.close");
  } finally {
    socket?.close();
    if (browser && browser.exitCode === null) browser.kill();
    await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
