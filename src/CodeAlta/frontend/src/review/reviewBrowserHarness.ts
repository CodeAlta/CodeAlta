// The page of a review browser test: a fixture bundled with esbuild (Monaco's worker inlined, `#neoastra` replaced
// when the fixture plays the host), the styles the application ships, and Edge driven over CDP.
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";

export type Point = Readonly<{ x: number; y: number }>;
/** CDP modifier bits: Alt 1, Ctrl 2, Meta 4, Shift 8. */
export const shift = 8, ctrl = 2;

export type ReviewPage = Awaited<ReturnType<typeof openReviewPage>>;

/**
 * Builds the fixture `entry` (a URL of a `.mount.tsx`), opens it in Edge at 1200×1100 CSS pixels, and waits for
 * `ready`, an expression of the page. `neoastra` is the module that stands for `#neoastra`, when the fixture needs one.
 */
export async function openReviewPage(entry: URL, ready: string, neoastra?: URL) {
  const root = await mkdtemp(join(tmpdir(), "codealta-review-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  const close = async () => {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 99_999, method: "Browser.close" }));
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }).catch(() => { /* The profile of a browser that is still closing. */ });
  };
  try {
    await build({ entryPoints: [fileURLToPath(entry)], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife",
      loader: { ".ttf": "dataurl", ".svg": "dataurl", ".flf": "text" }, define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, logLevel: "error",
      plugins: [{ name: "review-fixture", setup(bundle) {
        if (neoastra) bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(neoastra) }));
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
    // The styles of the bundle (Monaco's) are beside it; those of the application are the ones it ships, reviews included.
    await writeFile(join(root, "style.css"), await readFile(new URL("../../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("./review.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="fixture.css"><link rel="stylesheet" href="style.css"></head>'
      + '<body style="margin:0"><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, "--allow-file-access-from-files",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type?: string; webSocketDebuggerUrl?: string }[];
    const opened = new WebSocket(pages.find(tab => tab.type === "page")!.webSocketDebuggerUrl!);
    socket = opened;
    await new Promise<void>((resolve, reject) => { opened.addEventListener("open", () => resolve(), { once: true }); opened.addEventListener("error", reject, { once: true }); });
    // What the page threw or wrote as an error: a test ends with none.
    const errors: string[] = [];
    opened.addEventListener("message", event => {
      const value = JSON.parse(String(event.data));
      if (value.method === "Runtime.exceptionThrown") errors.push(value.params.exceptionDetails.exception?.description ?? value.params.exceptionDetails.text);
      if (value.method === "Runtime.consoleAPICalled" && value.params.type === "error")
        errors.push(value.params.args.map((arg: { description?: string; value?: unknown }) => arg.description ?? String(arg.value)).join(" "));
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<Record<string, any>>((resolve, reject) => {
      const id = ++sequence;
      const reply = (event: MessageEvent) => {
        const value = JSON.parse(String(event.data));
        if (value.id !== id) return;
        opened.removeEventListener("message", reply); clearTimeout(timer);
        value.error ? reject(new Error(JSON.stringify(value.error))) : resolve(value.result);
      };
      const timer = setTimeout(() => { opened.removeEventListener("message", reply); reject(new Error(`${method} timed out`)); }, 12_000);
      opened.addEventListener("message", reply); opened.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    const value = async <T>(expression: string) => JSON.parse(await evaluate(`JSON.stringify(${expression}) ?? "null"`)) as T;
    // An expression of the page once it is true, or a failure that says what it was and what `show` shows then.
    const until = async (expression: string, show = "null", timeout = 8000) => {
      const done = await evaluate(`new Promise(resolve => { const end = Date.now() + ${timeout}; (function check() {
        if (${expression}) resolve(true); else if (Date.now() > end) resolve(JSON.stringify(${show})); else setTimeout(check, 20); })(); })`);
      assert.equal(done, true, `waited for ${expression}; ${show}: ${done}`);
    };
    // A few frames, for Monaco and React to draw what they were told.
    const frames = () => evaluate("new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(resolve, 50))))");
    const mouse = (type: "mouseMoved" | "mousePressed" | "mouseReleased", point: Point, modifiers = 0, pressed = false) =>
      command("Input.dispatchMouseEvent", { type, x: point.x, y: point.y, modifiers, ...(type === "mouseMoved" ? pressed ? { button: "left", buttons: 1 } : {} : { button: "left", buttons: type === "mousePressed" ? 1 : 0, clickCount: 1 }) });
    const click = async (point: Point, modifiers = 0) => {
      await mouse("mouseMoved", point, modifiers);
      await mouse("mousePressed", point, modifiers);
      await mouse("mouseReleased", point, modifiers);
    };
    /** Clicks the middle of the element an expression of the page gives (not scrolled to: Monaco owns the scrolling of its zones). */
    const clickOn = async (element: string, modifiers = 0) => {
      const point = await value<Point | null>(`(element => { if (!element) return null; const box = element.getBoundingClientRect();
        return { x: Math.round(box.left + box.width / 2), y: Math.round(box.top + box.height / 2) }; })(${element})`);
      assert.ok(point, `nothing to click: ${element}`);
      await click(point, modifiers);
    };
    const key = async (name: "Enter" | "Escape", modifiers = 0) => {
      const code = name === "Enter" ? 13 : 27;
      await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key: name, code: name, windowsVirtualKeyCode: code, nativeVirtualKeyCode: code, modifiers });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key: name, code: name, windowsVirtualKeyCode: code, nativeVirtualKeyCode: code, modifiers });
    };
    const type = (text: string) => command("Input.insertText", { text });

    await command("Runtime.enable");
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1200, height: 1100, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await until(ready, "null", 20_000);
    await frames();
    return { command, evaluate, value, until, frames, mouse, click, clickOn, key, type, errors, close };
  } catch (error) {
    await close();
    throw error;
  }
}
