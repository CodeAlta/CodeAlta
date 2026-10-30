import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "./browserTarget";

const edge = browserExecutable;

// The prompt editor alone, in headless Edge: a formula is tinted and previewed while the caret is in it, the equation
// editor (MathLive) edits it and writes it back, and Alt+= writes a new one.
test("the prompt previews the formula at the caret, and the equation editor writes it", { skip: !edge, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-prompt-math-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./promptMath.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "prompt-math", setup(bundle) {
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
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="app" style="margin-top:200px"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, "--allow-file-access-from-files",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type?: string; webSocketDebuggerUrl?: string }[];
    const tab = pages.find(tab => tab.type === "page");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    const exceptions: string[] = [];
    socket.addEventListener("message", event => {
      const value = JSON.parse(String(event.data));
      if (value.method === "Runtime.exceptionThrown") exceptions.push(value.params.exceptionDetails.exception?.description ?? value.params.exceptionDetails.text);
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<Record<string, any>>((resolve, reject) => {
      const id = ++sequence;
      const reply = (event: MessageEvent) => {
        const value = JSON.parse(String(event.data));
        if (value.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        value.error ? reject(new Error(JSON.stringify(value.error))) : resolve(value.result);
      };
      const timer = setTimeout(() => { socket!.removeEventListener("message", reply); reject(new Error(`${method} timed out`)); }, 12_000);
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async <T = unknown>(expression: string): Promise<T> => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value as T;
    };
    // Resolves to true, or to what the page shows when the condition never held.
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+10000;const tick=()=>{if(${condition})resolve(true);
      else if(Date.now()>end)resolve({value:mathFixture.state.value,preview:mathFixture.preview(),editor:mathFixture.editor()});else setTimeout(tick,25)};tick()})`);
    await command("Page.enable");
    await command("Runtime.enable");
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.mathFixture?.ready()"), true);

    // The caret in the formula draws it above it; out of it, the preview goes. The formula is tinted.
    await evaluate("mathFixture.caret(8)");
    assert.equal(await wait("mathFixture.preview()?.drawn"), true);
    assert.equal(await evaluate("mathFixture.preview().edit"), "Edit equation (Alt+=)");
    assert.equal(await wait("mathFixture.tinted() > 0"), true);
    await evaluate("mathFixture.caret(1)");
    assert.equal(await wait("mathFixture.preview() === null"), true);

    // Its button opens the equation editor on it, with MathLive, its keyboard in the window; Replace writes it back.
    await evaluate("mathFixture.caret(8)");
    assert.equal(await wait("mathFixture.preview()?.drawn"), true);
    await evaluate("mathFixture.clickEdit()");
    assert.equal(await wait("mathFixture.editor()?.field === '\\\\pi r^2'"), true);
    assert.deepEqual(await evaluate("mathFixture.editor()"), { open: true, title: "Edit equation", field: "\\pi r^2", source: "\\pi r^2", keyboard: true, action: "Replace" });
    await evaluate("mathFixture.write('\\\\frac{a}{b}')");
    assert.equal(await wait("mathFixture.editor()?.source === '\\\\frac{a}{b}'"), true);
    await evaluate("mathFixture.insert()");
    assert.equal(await wait("mathFixture.editor() === null"), true);
    assert.equal(await evaluate("mathFixture.state.value"), "Area $\\frac{a}{b}$ here");

    // Alt+= at the end of the prompt writes a new equation, displayed on lines of its own, with Ctrl+Enter.
    await evaluate("mathFixture.caret(mathFixture.state.value.length)");
    for (const type of ["keyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key: "=", code: "Equal", windowsVirtualKeyCode: 187, modifiers: 1 });
    assert.equal(await wait("mathFixture.editor()?.field === ''"), true);
    assert.equal(await evaluate("mathFixture.editor().title"), "Insert equation");
    await evaluate("mathFixture.write('x^2')");
    await evaluate("mathFixture.display()");
    for (const type of ["keyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key: "Enter", code: "Enter", windowsVirtualKeyCode: 13, modifiers: 2 });
    assert.equal(await wait("mathFixture.editor() === null"), true);
    // The model writes its own line breaks (CRLF on Windows), as for any line typed in the prompt.
    assert.equal((await evaluate<string>("mathFixture.state.value")).replaceAll("\r\n", "\n"), "Area $\\frac{a}{b}$ here\n$$\nx^2\n$$");
    assert.deepEqual(exceptions, []);
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
