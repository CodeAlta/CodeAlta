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
type Recorded = { clicks: string[]; drops: string[] };

test("a label is dragged before, after or under another one, a project onto a label, and the clicks of a drag never reach a row", { skip: !edge, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-label-drag-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./labelDrag.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
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
    // The moves of this test are all made with the button held.
    const mouse = async (type: "mousePressed" | "mouseMoved" | "mouseReleased", x: number, y: number) => {
      await command("Input.dispatchMouseEvent", { type, x, y, button: "left", buttons: type === "mouseReleased" ? 0 : 1, clickCount: 1 });
      await frames();
    };
    const escape = async () => {
      for (const type of ["rawKeyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
      await frames();
    };
    const recorded = async () => await evaluate("({ clicks: [...window.recorded.clicks], drops: [...window.recorded.drops] })") as Recorded;
    const reset = () => evaluate("window.recorded.clicks.length = 0, window.recorded.drops.length = 0");
    const ghost = () => evaluate("document.getElementById('ghost')?.textContent ?? null");
    const over = () => evaluate("(e => e ? e.id + ':' + e.dataset.drop : null)(document.querySelector('[data-drop]'))");
    // The titles of infra, helm and tools are at y 0..40, 40..80 and 80..120; the project at 120..160; the projects of no label at 160..200.
    const tools = { x: 100, y: 100 }, project = { x: 100, y: 140 };

    await command("Page.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 800, height: 600, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + 7000; (function check() {
      if (document.getElementById("project")) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`), true);

    // A press that does not move is a click.
    await mouse("mousePressed", tools.x, tools.y);
    await mouse("mouseReleased", tools.x, tools.y);
    assert.deepEqual(await recorded(), { clicks: ["tools"], drops: [] });
    await reset();

    // The upper edge of a title puts the label before it, the middle under it, the lower edge after it.
    await mouse("mousePressed", tools.x, tools.y);
    await mouse("mouseMoved", tools.x, tools.y - 20);
    assert.equal(await ghost(), "tools", "The name of the label follows the pointer.");
    await mouse("mouseMoved", 100, 4);
    assert.equal(await over(), "infra:before");
    await mouse("mouseMoved", 100, 60);
    assert.equal(await over(), "helm:into");
    await mouse("mouseMoved", 100, 37);
    assert.equal(await over(), "infra:after");
    await mouse("mouseReleased", 100, 37);
    assert.equal(await ghost(), null);
    assert.deepEqual(await recorded(), { clicks: [], drops: ["tools:after:infra"] });
    await reset();

    // A project dropped on a label goes there; dropped on the projects of no label, it leaves its label.
    await mouse("mousePressed", project.x, project.y);
    await mouse("mouseMoved", 100, 70);
    assert.equal(await over(), "helm:into", "A project goes into a label, never beside it.");
    await mouse("mouseReleased", 100, 70);
    await mouse("mousePressed", project.x, project.y);
    await mouse("mouseMoved", 100, 180);
    await mouse("mouseReleased", 100, 180);
    assert.deepEqual(await recorded(), { clicks: [], drops: ["alpha:infra->helm", "alpha:infra->none"] });
    await reset();

    // Escape cancels a drag with the button still held: nothing drops, and the release does not click.
    await mouse("mousePressed", tools.x, tools.y);
    await mouse("mouseMoved", 100, 60);
    await escape();
    assert.equal(await ghost(), null, "Escape ends the drag at once.");
    assert.equal(await over(), null);
    await mouse("mouseMoved", tools.x, tools.y);
    await mouse("mouseReleased", tools.x, tools.y);
    assert.deepEqual(await recorded(), { clicks: [], drops: [] });
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); await endBrowser(browser); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
