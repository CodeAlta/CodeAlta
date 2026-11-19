import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "./browserTarget";
import { devToolsAnswerLimit, pageWaitLimit, testLimit, testLimitRanOut } from "./testTime";

test("typing / opens the skill picker, a choice writes /name, and a deletion, a click or a path never loses text", { skip: !browserExecutable, timeout: testLimit(60_000) }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-skill-picker-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./skillPicker.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      loader: { ".css": "empty", ".svg": "dataurl" }, plugins: [{ name: "isolated-skill-picker", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./demo-api.ts", import.meta.url)) }));
      } }] });
    await writeFile(join(root, "style.css"), await readFile(new URL("../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(testLimit(10_000)) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown }; exceptionDetails?: unknown }>((resolve, reject) => {
      const id = ++sequence, timer = setTimeout(() => reject(Error(testLimitRanOut(devToolsAnswerLimit, `the answer to ${method}`))), devToolsAnswerLimit);
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
    const until = (condition: string) => evaluate(`new Promise(resolve => { const end = Date.now() + ${pageWaitLimit}; (function check() {
      if (${condition}) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 20); })(); })`);
    const settle = () => new Promise(resolve => setTimeout(resolve, 200));
    // A key as the keyboard gives it: a key-down that types its character, then its key-up.
    const down = (key: string, code: string, virtual: number, text?: string) =>
      command("Input.dispatchKeyEvent", { type: "keyDown", key, code, windowsVirtualKeyCode: virtual, ...text ? { text } : {} });
    const up = (key: string, code: string, virtual: number) => command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: virtual });
    const key = async (key: string, code: string, virtual: number, text?: string) => { await down(key, code, virtual, text); await up(key, code, virtual); };
    const slash = () => key("/", "Slash", 191, "/");
    const dialog = "document.querySelector('dialog[open].reference-palette')";
    const query = `${dialog}?.querySelector('input')?.value`;
    const prompt = "document.querySelector('#prompt').value";
    await command("Page.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 900, height: 650, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await until("document.querySelector('#prompt')"), true);
    await evaluate("document.querySelector('#prompt').focus()");

    // Typing `/` at a word start opens the picker at once, which asks for the skills again; Enter writes `/name `.
    await command("Input.insertText", { text: "ask " });
    await slash();
    assert.equal(await until(`!!${dialog} && ${query} === ''`), true, "the picker opens on the / typed");
    assert.equal(await evaluate("document.querySelector('#refreshes').textContent"), "1", "the picker asks for the skills again");
    await command("Input.insertText", { text: "tdd" });
    await key("Enter", "Enter", 13);
    assert.equal(await until(`!${dialog} && ${prompt} === 'ask /tdd '`), true, "the chosen skill is written with a space after it");
    assert.equal(await until("document.activeElement === document.querySelector('#prompt') && document.querySelector('#prompt').selectionStart === 9"), true,
      "the caret is back after the skill");

    // Deleting into the token, or clicking in it, leaves it as text: the picker stays closed.
    await key("Backspace", "Backspace", 8);
    await key("Backspace", "Backspace", 8);
    await settle();
    assert.equal(await evaluate(`!${dialog} && ${prompt} === 'ask /td'`), true, "a deletion does not open the picker");
    const point = await evaluate("(() => { const r = document.querySelector('#prompt').getBoundingClientRect(); return [r.x + 30, r.y + 8]; })()") as [number, number];
    for (const type of ["mousePressed", "mouseReleased"]) await command("Input.dispatchMouseEvent", { type, x: point[0], y: point[1], button: "left", clickCount: 1 });
    await settle();
    assert.equal(await evaluate(`!${dialog}`), true, "a click in the token does not open the picker");
    await evaluate("{ const p = document.querySelector('#prompt'); p.focus(); p.setSelectionRange(p.value.length, p.value.length) }");

    // A path typed after a `/` goes back in the prompt as soon as a `/` or a space shows it is no skill, and typing goes on there.
    await command("Input.insertText", { text: " " });
    await slash();
    assert.equal(await until(`!!${dialog}`), true);
    await command("Input.insertText", { text: "usr" });
    await slash();
    assert.equal(await until(`!${dialog} && ${prompt} === 'ask /td /usr/'`), true, "the path is written back");
    await command("Input.insertText", { text: "bin" });
    await settle();
    assert.equal(await evaluate(`!${dialog} && ${prompt} === 'ask /td /usr/bin'`), true, "typing goes on in the prompt");

    // Escape, and Enter when nothing matches, write back what was typed.
    await command("Input.insertText", { text: " " });
    await slash();
    assert.equal(await until(`!!${dialog}`), true);
    await command("Input.insertText", { text: "tmp" });
    await key("Escape", "Escape", 27);
    assert.equal(await until(`!${dialog} && ${prompt} === 'ask /td /usr/bin /tmp'`), true, "Escape writes back the text typed");
    await command("Input.insertText", { text: " " });
    await slash();
    assert.equal(await until(`!!${dialog}`), true);
    await command("Input.insertText", { text: "nothing" });
    await key("Enter", "Enter", 13);
    assert.equal(await until(`!${dialog} && ${prompt} === 'ask /td /usr/bin /tmp /nothing'`), true, "Enter without a match writes back the text typed");

    // A `/` typed before a word starts a token of its own: the word stays after the chosen skill.
    await evaluate("{ const p = document.querySelector('#prompt'); p.value = 'please review this'; p.setSelectionRange(14, 14) }");
    await slash();
    assert.equal(await until(`!!${dialog} && ${query} === ''`), true);
    await command("Input.insertText", { text: "grilling" });
    await key("Enter", "Enter", 13);
    assert.equal(await until(`!${dialog} && ${prompt} === 'please review /grilling this'`), true, "the word after the caret stays");

    // A letter typed right after the `/`, before its key-up, becomes the query of the picker.
    await evaluate("{ const p = document.querySelector('#prompt'); p.setSelectionRange(p.value.length, p.value.length) }");
    await command("Input.insertText", { text: " " });
    await down("/", "Slash", 191, "/");
    await down("g", "KeyG", 71, "g");
    await up("/", "Slash", 191);
    await up("g", "KeyG", 71);
    assert.equal(await until(`!!${dialog} && ${query} === 'g'`), true, "fast typing opens the picker with what was typed");
    await key("Escape", "Escape", 27);
    assert.equal(await until(`!${dialog} && ${prompt}.endsWith(' /g')`), true, "and closing it keeps that text");

    // Ctrl+Space opens the picker again in a token that a period follows: the chosen skill keeps the period.
    await evaluate("{ const p = document.querySelector('#prompt'); p.value = 'ask /td. Then'; p.focus(); p.setSelectionRange(7, 7) }");
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: " ", code: "Space", windowsVirtualKeyCode: 32, modifiers: 2 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: " ", code: "Space", windowsVirtualKeyCode: 32, modifiers: 2 });
    assert.equal(await until(`!!${dialog} && ${query} === 'td'`), true, "Ctrl+Space opens the picker on the token");
    await key("Enter", "Enter", 13);
    assert.equal(await until(`!${dialog} && ${prompt} === 'ask /tdd. Then'`), true, "the period after the token stays");

    // Enter on the Close button of the picker closes it: it does not choose the selected skill.
    await evaluate("{ const p = document.querySelector('#prompt'); p.value = 'ask '; p.focus(); p.setSelectionRange(4, 4) }");
    await slash();
    assert.equal(await until(`!!${dialog}`), true);
    await command("Input.insertText", { text: "td" });
    assert.equal(await until(`${dialog}?.querySelector('[aria-selected="true"]')?.textContent.includes('/tdd')`), true);
    await evaluate(`${dialog}.querySelector('button[aria-label="Close"]').focus()`);
    await key("Enter", "Enter", 13);
    assert.equal(await until(`!${dialog} && ${prompt} === 'ask /td'`), true, "Enter on Close writes back the text typed, not the skill");
    await command("Browser.close").catch(() => { });
  } finally {
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
