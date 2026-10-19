import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "./browserTarget";

test("issues checked in the picker opened by the attach menu are attached, and # still inserts a link", { skip: !browserExecutable, timeout: 60_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-issue-picker-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./issuePicker.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      loader: { ".css": "empty", ".svg": "dataurl" }, plugins: [{ name: "isolated-issue-picker", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./issuePicker.neoastra.mount.ts", import.meta.url)) }));
      } }] });
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
    const key = async (key: string, code: string, virtual: number, modifiers = 0) => {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key, code, windowsVirtualKeyCode: virtual, modifiers });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: virtual, modifiers });
    };
    const shiftEnter = () => key("Enter", "Enter", 13, 8);
    const dialog = "document.querySelector('dialog[open].reference-palette')";
    const attached = "document.querySelector('#attached').textContent";
    const prompt = "document.querySelector('#prompt').value";
    await command("Page.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 900, height: 650, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await until("document.querySelector('#attach')"), true);

    // Add issues: the picker opens at once, two issues are checked, Enter attaches them and leaves the text as it is.
    await evaluate("document.querySelector('#prompt').value = ''; document.querySelector('#attach').click()");
    assert.equal(await until(`${dialog} && document.querySelectorAll('.issue-row').length === 3`), true, "the picker lists the issues");
    const action = "document.querySelector('.reference-actions .bp6-intent-primary')?.textContent";
    assert.equal(await until(`${action} === 'Attach issueEnter'`), true, "the button says what Enter does");
    // Shift+Enter checks the issue and moves to the next one.
    await shiftEnter();
    await shiftEnter();
    assert.equal(await until("document.querySelectorAll('.issue-row[aria-checked=\"true\"]').length === 2"), true, "two issues are checked");
    // A click on a box, with the mouse, checks its issue once and leaves the picker open; another click unchecks it.
    const box = await evaluate("(() => { const r = document.querySelectorAll('.issue-row .issue-check .bp6-control-indicator')[2].getBoundingClientRect(); return [r.x + r.width / 2, r.y + r.height / 2]; })()") as [number, number];
    const click = async () => {
      for (const type of ["mousePressed", "mouseReleased"]) await command("Input.dispatchMouseEvent", { type, x: box[0], y: box[1], button: "left", clickCount: 1 });
    };
    await click();
    assert.equal(await until("document.querySelectorAll('.issue-row[aria-checked=\"true\"]').length === 3"), true, "the mouse checks the third issue");
    assert.equal(await evaluate(`!!${dialog}`), true, "checking does not choose the row");
    await click();
    assert.equal(await until("document.querySelectorAll('.issue-row[aria-checked=\"true\"]').length === 2"), true, "a second click unchecks it");
    assert.equal(await until(`${action} === 'Attach 2 issuesEnter'`), true, "the button counts the checked issues");
    // A click on a row, beside its box, checks it too; another click unchecks it.
    await evaluate("document.querySelectorAll('.issue-row .issue-title')[2].click()");
    assert.equal(await until("document.querySelectorAll('.issue-row[aria-checked=\"true\"]').length === 3"), true, "a click on the row checks it");
    await evaluate("document.querySelectorAll('.issue-row .issue-title')[2].click()");
    assert.equal(await until("document.querySelectorAll('.issue-row[aria-checked=\"true\"]').length === 2"), true, "a second click on the row unchecks it");
    // A press outside the window keeps it, with the issues checked.
    for (const type of ["mousePressed", "mouseReleased"]) await command("Input.dispatchMouseEvent", { type, x: 4, y: 640, button: "left", clickCount: 1 });
    await new Promise(resolve => setTimeout(resolve, 200));
    assert.equal(await evaluate(`!!${dialog} && document.querySelectorAll('.issue-row[aria-checked=\"true\"]').length === 2`), true, "checked issues survive a press outside");
    // The button attaches them, as Enter does.
    await evaluate("document.querySelector('.reference-actions .bp6-intent-primary').click()");
    assert.equal(await until(`!${dialog}`), true);
    // The window is closed when the issues are handed over: the prompt of a new session refuses them under a modal window.
    assert.equal(await evaluate("document.querySelector('#refused').textContent"), "0", "no modal window is open when the issues are attached");
    assert.equal(await evaluate(attached), "1,2");
    assert.equal(await evaluate(prompt), "", "attaching does not write in the prompt");

    // A picker shown again after a Send does not open by itself for the request it already handled.
    await evaluate("document.querySelector('#remount').click()");
    await new Promise(resolve => setTimeout(resolve, 400));
    assert.equal(await evaluate(`!!${dialog}`), false, "the picker stays closed after it is shown again");

    // # in the prompt opens it to insert a link, as before.
    await evaluate("document.querySelector('#prompt').focus()");
    await command("Input.insertText", { text: "fix #" });
    assert.equal(await until(`${dialog} && document.querySelectorAll('.issue-row').length === 3`), true, "# opens the picker");
    // Inserting picks one issue: no box, no button, Enter or a click inserts it at once.
    assert.equal(await evaluate("document.querySelectorAll('.issue-check, .reference-actions').length"), 0, "no box and no button to insert");
    await key("Enter", "Enter", 13);
    assert.equal(await until(`!${dialog}`), true);
    assert.equal(await evaluate(prompt), "fix [#1](https://github.com/owner/repo/issues/1)");
    await evaluate("{ const area = document.querySelector('#prompt'); area.focus(); area.setSelectionRange(area.value.length, area.value.length); }");
    await command("Input.insertText", { text: " and #" });
    assert.equal(await until(`${dialog} && document.querySelectorAll('.issue-row').length === 3`), true, "# opens the picker again");
    await evaluate("document.querySelectorAll('.issue-row .issue-title')[1].click()");
    assert.equal(await until(`!${dialog}`), true, "a click on an issue inserts it");
    assert.equal(await evaluate(prompt), "fix [#1](https://github.com/owner/repo/issues/1) and [#2](https://github.com/owner/repo/issues/2)");
    assert.equal(await evaluate(attached), "1,2", "nothing more is attached");
    await command("Browser.close").catch(() => { });
  } finally {
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
