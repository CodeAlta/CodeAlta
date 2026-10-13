import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "./browserTarget";

test("a question beside a row has the focus on the answer Enter gives, and one that is always asked on Cancel", { skip: !browserExecutable, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-confirm-popover-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./confirmPopover.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
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
        if (message.error) reject(Error(`${method}: ${JSON.stringify(message.error)}`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", receive); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    // The page waits until what is asked holds, or gives up: the focus is taken a moment after the question is shown.
    const until = (condition: string) => evaluate(`new Promise(resolve => { const end = Date.now() + 5000; (function check() {
      if (${condition}) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`);
    const key = async (key: string, code: string, virtual: number) => {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key, code, windowsVirtualKeyCode: virtual });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: virtual });
    };
    const focused = "document.activeElement?.textContent";
    const answers = "document.querySelector('#answers').textContent";
    const closed = "document.querySelector('.confirm-form') === null";
    await command("Page.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 900, height: 650, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await until("document.querySelector('#archive')"), true);

    // A question that can be turned off: its check box, the focus on the button that does it, Enter answers yes.
    await evaluate("document.querySelector('#archive').click()");
    assert.equal(await until(`${focused} === 'Archive'`), true, "the button that archives has the focus");
    assert.equal(await evaluate("document.querySelectorAll('.confirm-form input[type=checkbox]').length"), 1);
    await key("Enter", "Enter", 13);
    assert.equal(await until(closed), true);
    assert.equal(await evaluate(answers), "archive:false");

    // A question that is always asked: no check box, the focus on Cancel, Enter leaves everything as it is.
    await evaluate("document.querySelector('#delete').click()");
    assert.equal(await until(`${focused} === 'Cancel'`), true, "Cancel has the focus");
    assert.equal(await evaluate("document.querySelectorAll('.confirm-form input[type=checkbox]').length"), 0);
    await key("Enter", "Enter", 13);
    assert.equal(await until(closed), true);
    assert.equal(await evaluate(answers), "archive:false,cancel");
    assert.equal(await evaluate("document.activeElement?.id"), "row", "the row has the focus again");

    // Asked from a menu, whose button takes the focus back when it closes: the question takes it again.
    await evaluate("document.querySelector('#menu').click()");
    assert.equal(await until("document.activeElement?.id === 'menu'"), true, "the menu took the focus back");
    assert.equal(await until(`${focused} === 'Cancel'`), true, "the question has the focus again");
    await key("Escape", "Escape", 27);
    assert.equal(await until(closed), true);
    assert.equal(await evaluate(answers), "archive:false,cancel,cancel");

    // Deleting is one deliberate click.
    await evaluate("document.querySelector('#delete').click()");
    assert.equal(await until(`${focused} === 'Cancel'`), true);
    await evaluate("[...document.querySelectorAll('.confirm-form button')].find(button => button.textContent === 'Delete').click()");
    assert.equal(await until(closed), true);
    assert.equal(await evaluate(answers), "archive:false,cancel,cancel,delete");
    await command("Browser.close").catch(() => { });
  } finally {
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
