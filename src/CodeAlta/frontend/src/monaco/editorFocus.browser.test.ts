import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable } from "../browserTarget";

const edge = browserExecutable;

// Two real editors in a real page: the text and the keys are the browser's own input, as for a user.
test("what is typed goes to the editor that has the keyboard, also after a focus that no event announced", { skip: !edge, timeout: 120_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-editor-focus-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./editorFocus.mount.ts", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".ttf": "empty" }, plugins: [{ name: "inline-worker", setup(bundle) {
        bundle.onResolve({ filter: /^monaco-editor\/.*\?worker$/ }, args => ({ path: args.path, namespace: "fixture-worker" }));
        bundle.onLoad({ filter: /.*/, namespace: "fixture-worker" }, async () => {
          const worker = await build({ entryPoints: [fileURLToPath(new URL("../../node_modules/monaco-editor/esm/vs/editor/editor.worker.js", import.meta.url))],
            bundle: true, platform: "browser", format: "iife", write: false });
          return { loader: "js", contents: `export default class extends Worker { constructor() {
            const url=URL.createObjectURL(new Blob([${JSON.stringify(worker.outputFiles[0].text)}],{type:"text/javascript"}));
            super(url); URL.revokeObjectURL(url);
          } }` };
        });
      } }] });
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, "--allow-file-access-from-files",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type?: string; webSocketDebuggerUrl?: string }[];
    socket = new WebSocket(pages.find(tab => tab.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
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
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    type State = { focus: boolean; value: string }[];
    const state = async () => JSON.parse(await evaluate("JSON.stringify(editorFocusFixture.state())")) as State;
    // What the page shows once it has settled on the expected state, or what it shows instead.
    const settled = async (expected: State) => {
      const text = JSON.stringify(expected);
      await evaluate(`new Promise(resolve=>{const end=Date.now()+5000;const tick=()=>{
        if(JSON.stringify(editorFocusFixture.state())===${JSON.stringify(text)}||Date.now()>end)resolve(true);else setTimeout(tick,20)};tick()})`);
      return state();
    };
    const shows = async (input: string, expected: State) => {
      const actual = await settled(expected);
      assert.deepEqual(actual, expected, `${input}: ${JSON.stringify(actual)}`);
    };
    const type = (text: string) => command("Input.insertText", { text });
    const backspace = async () => {
      for (const kind of ["keyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type: kind, key: "Backspace", code: "Backspace", windowsVirtualKeyCode: 8 });
    };

    await command("Page.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    // The input of Windows (EditContext), then the one of an engine without it (a text area), as on macOS.
    for (const [variant, input] of [["", "div"], ["#textarea", "textarea"]]) {
      await command("Page.navigate", { url: "about:blank" });
      await command("Page.navigate", { url: pathToFileURL(page).href + variant });
      assert.equal(await evaluate(`new Promise(resolve=>{const end=Date.now()+15000;const tick=()=>{
        if(window.editorFocusFixture)resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(tick,20)};tick()})`), true, variant);

      // A focus the browser announces: one editor has it, and what is typed is written there.
      await evaluate("editorFocusFixture.focus('first')");
      assert.equal(await evaluate("editorFocusFixture.input()"), input, variant);
      await type("one");
      await shows(input, [{ focus: true, value: "one" }, { focus: false, value: "" }]);

      // The second editor takes the focus as in a window that is in the background: the first one hears nothing.
      // It is told all the same, and the text and the keys go to the second one only.
      await evaluate("editorFocusFixture.focusInBackground('second')");
      await type("two!");
      await backspace();
      await shows(input, [{ focus: false, value: "one" }, { focus: true, value: "two" }]);

      // And back to the editor that comes first in the page.
      await evaluate("editorFocusFixture.focusInBackground('first')");
      await type(" more!");
      await backspace();
      await shows(input, [{ focus: true, value: "one more" }, { focus: false, value: "two" }]);

      // An announced focus after these goes as before.
      await evaluate("editorFocusFixture.focus('second')");
      await type("!");
      await shows(input, [{ focus: false, value: "one more" }, { focus: true, value: "two!" }]);
    }
  } finally {
    socket?.close();
    browser?.kill();
    await rm(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => { /* The profile of a browser that is still closing. */ });
  }
});
