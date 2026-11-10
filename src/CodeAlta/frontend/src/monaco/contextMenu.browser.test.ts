import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { readFileSync } from "node:fs";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "../browserTarget";
import { testLimit, testLimitRanOut } from "../testTime";

const edge = browserExecutable;

// The menu of a right-click in an editor whose widgets are fixed to the window (the prompt, a file) is drawn beside the
// editor, outside of it: it had no surface and black entries, unreadable over the dark shell.
test("the menu of a right-click in the prompt and in a file has a surface and readable entries in both themes", { skip: !edge, timeout: testLimit(120_000) }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-context-menu-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./contextMenu.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "css", ".ttf": "empty", ".woff": "empty", ".woff2": "empty", ".flf": "text", ".svg": "dataurl" },
      plugins: [{ name: "inline-worker", setup(bundle) {
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
    // The shell's style gives the prompt its size and its colors; the editor's own is bundled with it (fixture.css).
    await writeFile(join(root, "style.css"), readFileSync(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + readFileSync(new URL("../style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"><link rel="stylesheet" href="fixture.css"></head><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, "--allow-file-access-from-files", "--window-size=900,700",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
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
      const timer = setTimeout(() => { socket!.removeEventListener("message", reply); reject(new Error(testLimitRanOut(testLimit(12_000), `the answer to ${method}`))); }, testLimit(12_000));
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    const until = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+${testLimit(8000)};const tick=()=>{
      const value=${condition};if(value||Date.now()>end)resolve(value);else setTimeout(tick,20)};tick()})`);
    const key = async (name: string, code: number) => {
      for (const type of ["keyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key: name, code: name, windowsVirtualKeyCode: code });
    };

    await command("Page.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await until("window.contextMenuFixture?.ready()"), true);
    for (const theme of ["dark", "light"] as const) {
      await evaluate(`contextMenuFixture.theme(${JSON.stringify(theme)})`);
      for (const editor of ["prompt", "file"]) {
        // A right-click of the mouse, the browser's own input.
        const { x, y } = await evaluate(`contextMenuFixture.point(${JSON.stringify(editor)})`) as { x: number; y: number };
        for (const type of ["mousePressed", "mouseReleased"]) await command("Input.dispatchMouseEvent", { type, x, y, button: "right", buttons: 2, clickCount: 1 });
        const menu = await until("contextMenuFixture.menu()") as { entries: string[]; opacity: number; contrast: number; background: number } | null;
        const where = `${editor}, ${theme}: ${JSON.stringify(menu)}`;
        assert.ok(menu, where);
        assert.ok(menu.entries.includes("Copy"), where);
        // An opaque surface of the theme, under entries that read on it (WCAG AA: 4.5).
        assert.equal(menu.opacity, 1, where);
        assert.ok(theme === "dark" ? menu.background < 0.2 : menu.background > 0.6, where);
        assert.ok(menu.contrast >= 4.5, where);
        await key("Escape", 27);
        assert.equal(await until("contextMenuFixture.menu() === null"), true, where);
      }
    }
  } finally {
    socket?.close();
    browser?.kill();
    await rm(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => { /* The profile of a browser that is still closing. */ });
  }
});
