import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "./browserTarget";

// The prompt editor and a `#` picker, in headless Edge. Chromium gives copy, cut and paste to the element that holds the
// selection of the page, not to the focused one: once the picker had closed, the selection stayed where its window was,
// and Ctrl+C, Ctrl+X and Ctrl+V did nothing in the prompt (#306).
test("Ctrl+X still cuts in the prompt after a picker opened over it closes", { skip: !browserExecutable, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-prompt-clipboard-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./promptClipboard.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "prompt-clipboard", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./composer.neoastra.mount.ts", import.meta.url)) }));
        bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
        bundle.onLoad({ filter: /.*/, namespace: "fixture-worker" }, async () => {
          const worker = await build({ entryPoints: [fileURLToPath(new URL("../node_modules/monaco-editor/esm/vs/editor/editor.worker.js", import.meta.url))],
            bundle: true, platform: "browser", format: "iife", write: false });
          return { loader: "js", contents: `export default class extends Worker { constructor() {
            const url=URL.createObjectURL(new Blob([${JSON.stringify(worker.outputFiles[0].text)}],{type:"text/javascript"}));
            super(url); URL.revokeObjectURL(url);
          } }` };
        });
      } }] });
    await writeFile(join(root, "style.css"), readFileSync(new URL("../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + readFileSync(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="app" style="margin-top:120px"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, "--allow-file-access-from-files",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json() as { type?: string; webSocketDebuggerUrl?: string }[];
    const tab = pages.find(value => value.type === "page");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<Record<string, any>>((resolve, reject) => {
      const id = ++sequence;
      const reply = (event: MessageEvent) => {
        const value = JSON.parse(String(event.data));
        if (value.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        value.error ? reject(new Error(`${method}: ${JSON.stringify(value.error)}`)) : resolve(value.result);
      };
      const timer = setTimeout(() => { socket!.removeEventListener("message", reply); reject(new Error(`${method} timed out`)); }, 12_000);
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async <T = unknown>(expression: string): Promise<T> => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value as T;
    };
    // Resolves to true, or to the text of the prompt when the condition never held.
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+10000;const tick=()=>{if(${condition})resolve(true);
      else if(Date.now()>end)resolve({value:clipboardFixture.state.value});else setTimeout(tick,25)};tick()})`);
    // Ctrl+X as a keyboard gives it: the key, and the editing command the browser runs for it, which sends the `cut` event.
    const cut = async () => {
      await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "x", code: "KeyX", windowsVirtualKeyCode: 88, modifiers: 2, commands: ["cut"] });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key: "x", code: "KeyX", windowsVirtualKeyCode: 88, modifiers: 2 });
    };
    await command("Page.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.clipboardFixture?.ready()"), true);

    // Before any picker, Ctrl+X cuts the selected text.
    await evaluate("clipboardFixture.focus()");
    await command("Input.insertText", { text: "hello world" });
    assert.equal(await wait("clipboardFixture.state.value === 'hello world'"), true);
    await evaluate("clipboardFixture.selectAll()");
    await cut();
    assert.equal(await wait("clipboardFixture.state.value === ''"), true, "Ctrl+X cuts before the picker opens");

    // `#` opens the picker, Escape closes it, and the caret is back in the prompt.
    await command("Input.insertText", { text: "fix #" });
    // The picker looks at the token when a key is released, as after typing.
    await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "End", code: "End", windowsVirtualKeyCode: 35 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "End", code: "End", windowsVirtualKeyCode: 35 });
    assert.equal(await wait("clipboardFixture.pickerOpen()"), true, "# opens the picker");
    await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
    assert.equal(await wait("!clipboardFixture.pickerOpen() && clipboardFixture.editorFocused()"), true, "Escape closes the picker and gives the prompt the focus");
    assert.equal(await evaluate("clipboardFixture.state.value"), "fix #");

    // Ctrl+X cuts again.
    await evaluate("clipboardFixture.selectAll()");
    await cut();
    assert.equal(await wait("clipboardFixture.state.value === ''"), true, "Ctrl+X cuts after the picker closed");
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
