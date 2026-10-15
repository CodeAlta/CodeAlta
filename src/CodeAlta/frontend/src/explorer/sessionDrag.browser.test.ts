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

test("a session row dragged onto a project drops it there, and the clicks of a drag never open the session", { skip: !edge, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-session-drag-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./sessionDrag.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
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
    const label = () => evaluate("!!document.getElementById('label')");
    const over = () => evaluate("document.querySelector('[data-over]')?.dataset.scope ?? null");
    // The session row is at y 100..140; the row of stride at 0..40 and the row of stride2 at 40..80.
    const session = { x: 100, y: 120 }, stride2 = { x: 100, y: 60 };

    await command("Page.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 800, height: 600, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await evaluate(`new Promise(resolve => { const end = Date.now() + 7000; (function check() {
      if (document.getElementById("session")) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`), true);

    // A press that does not move is a click: the session opens.
    await mouse("mousePressed", session.x, session.y);
    await mouse("mouseReleased", session.x, session.y);
    assert.deepEqual(await recorded(), { clicks: ["s1"], drops: [] });
    await reset();

    // A drag onto another project drops the session there, and the click of the release does not open it.
    await mouse("mousePressed", session.x, session.y);
    await mouse("mouseMoved", session.x, session.y - 20);
    assert.equal(await label(), true, "The label of the session follows the pointer.");
    await mouse("mouseMoved", stride2.x, stride2.y);
    assert.equal(await over(), "stride2");
    await mouse("mouseReleased", stride2.x, stride2.y);
    assert.equal(await label(), false);
    assert.deepEqual(await recorded(), { clicks: [], drops: ["s1->stride2"] });
    await reset();

    // Escape cancels a drag with the button still held: nothing drops, and releasing it over the session row does
    // not open the session.
    await mouse("mousePressed", session.x, session.y);
    await mouse("mouseMoved", stride2.x, stride2.y);
    await escape();
    assert.equal(await label(), false, "Escape ends the drag at once.");
    assert.equal(await over(), null);
    await mouse("mouseMoved", session.x, session.y);
    await mouse("mouseReleased", session.x, session.y);
    assert.deepEqual(await recorded(), { clicks: [], drops: [] });

    // The click after that is a click again.
    await mouse("mousePressed", session.x, session.y);
    await mouse("mouseReleased", session.x, session.y);
    assert.deepEqual(await recorded(), { clicks: ["s1"], drops: [] });
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); await endBrowser(browser); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
