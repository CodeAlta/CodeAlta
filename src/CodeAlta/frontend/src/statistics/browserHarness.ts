import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable } from "../browserTarget";

// Runs the Statistics canvas in headless Edge, under the page's production content security policy, over the fixture API, and
// hands the test a page to drive: evaluate, wait until, click and type with real input events, take screenshots.

/** The browser, or undefined when there is none and the browser tests skip. */
export const edge = browserExecutable;

const origin = "https://statistics.invalid", pageUrl = `${origin}/index.html`;
/** Where the screenshots go to be looked at: the git-ignored tmp folder of the repository. */
export const pictures = fileURLToPath(new URL("../../../../../tmp/statistics/", import.meta.url));

const hostCss = `html, body { margin: 0; height: 100%; } body { overflow: hidden; background: var(--bg); color: var(--text); font-size: 14px; } #root { height: 100%; }`;

/** What a test drives. */
export type Page = Readonly<{
  evaluate: <T = unknown>(expression: string) => Promise<T>;
  command: (method: string, params?: object) => Promise<Record<string, any>>;
  until: (expression: string, what: string) => Promise<void>;
  shot: (name: string) => Promise<void>;
  /** Moves the pointer onto the element that matches the selector and presses and releases the left button there. */
  click: (selector: string, index?: number) => Promise<void>;
  /** Clicks, with the pointer, the first element that matches the selector and whose text is exactly `text`. */
  clickText: (selector: string, text: string) => Promise<void>;
  key: (name: string, code: number, text?: string) => Promise<void>;
  type: (text: string) => Promise<void>;
  resize: (width: number, height: number, ratio?: number) => Promise<void>;
  problems: string[];
}>;

/** What the browser is started with. */
export type HarnessOptions = Readonly<{ reducedMotion?: boolean; width?: number; height?: number; ratio?: number; colorScheme?: "light" | "dark"; /** Gives the page `gc()`, to measure the heap after a collection. */ exposeGc?: boolean; /** The page the test drives, relative to this folder (default `./statistics.mount.tsx`) and the global it defines (default `statsFixture`). */ entry?: string; fixture?: string }>;

/** Starts the browser on the canvas page, runs `body`, and checks that the page raised no error and asked for nothing outside itself. */
export async function withCanvas(body: (page: Page) => Promise<void>, options: HarnessOptions = {}): Promise<void> {
  const directory = await mkdtemp(join(tmpdir(), "codealta-statistics-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    const config = JSON.parse(await readFile(new URL("../../../neoastra.json", import.meta.url), "utf8")) as { assets: { csp: string } };
    const bundle = await build({ entryPoints: [fileURLToPath(new URL(options.entry ?? "./statistics.mount.tsx", import.meta.url))], bundle: true, platform: "browser", format: "iife", write: false,
      loader: { ".css": "empty" }, define: { "process.env.NODE_ENV": '"development"' } });
    const read = (path: string) => readFile(new URL(path, import.meta.url), "utf8");
    const style = (await read("../../node_modules/@blueprintjs/core/lib/css/blueprint.css")) + await read("../style.css") + await read("../charts/charts.css") + await read("./statistics.css") + hostCss;
    const assets = new Map<string, { type: string; body: string }>([
      [pageUrl, { type: "text/html", body: '<!doctype html><html><head><meta charset="utf-8"><link rel="icon" href="data:,"><link rel="stylesheet" href="/style.css"></head><body><div id="root"></div><script src="/fixture.js"></script></body></html>' }],
      [`${origin}/style.css`, { type: "text/css", body: style }], [`${origin}/fixture.js`, { type: "text/javascript", body: bundle.outputFiles[0].text }]]);
    browser = spawn(edge!, [...browserBaseArgs, ...(options.exposeGc ? ["--js-flags=--expose-gc"] : []), "--host-resolver-rules=MAP * ~NOTFOUND", `--user-data-dir=${join(directory, "profile")}`, "--remote-debugging-port=0", "about:blank"],
      { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(directory, "profile/DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; } catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const tabs = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type: string; url: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(tabs.find(tab => tab.type === "page" && tab.url === "about:blank")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    let sequence = 0;
    const waiting = new Map<number, (value: any) => void>();
    const problems: string[] = [];
    const command = (method: string, params: object = {}) => new Promise<Record<string, any>>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => { waiting.delete(id); reject(new Error(`${method} timed out`)); }, 30_000);
      waiting.set(id, reply => { clearTimeout(timer); reply.error ? reject(new Error(JSON.stringify(reply.error))) : resolve(reply.result ?? {}); });
      socket!.send(JSON.stringify({ id, method, params }));
    });
    socket.addEventListener("message", event => {
      const message = JSON.parse(String(event.data));
      if (waiting.has(message.id)) { waiting.get(message.id)!(message); waiting.delete(message.id); return; }
      if (message.method === "Fetch.requestPaused") {
        const { requestId, request } = message.params, asset = assets.get(request.url);
        void (asset ? command("Fetch.fulfillRequest", { requestId, responseCode: 200, responseHeaders: [{ name: "Content-Type", value: asset.type }, { name: "Content-Security-Policy", value: config.assets.csp }],
          body: Buffer.from(asset.body).toString("base64") }) : command("Fetch.failRequest", { requestId, errorReason: "BlockedByClient" })).catch(() => { });
        if (!asset && !request.url.startsWith("data:")) problems.push(`request: ${request.url}`);
      }
      if (message.method === "Runtime.exceptionThrown") problems.push(`exception: ${JSON.stringify(message.params.exceptionDetails).slice(0, 600)}`);
      if (message.method === "Runtime.consoleAPICalled" && ["error", "warning"].includes(message.params.type)) problems.push(`console ${message.params.type}: ${message.params.args.map((a: any) => a.value ?? a.description).join(" ").slice(0, 400)}`);
      if (message.method === "Log.entryAdded" && message.params.entry.source === "security") problems.push(`security: ${message.params.entry.text}`);
    });
    const evaluate = async <T>(expression: string): Promise<T> => {
      const reply = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(reply.exceptionDetails, undefined, JSON.stringify(reply.exceptionDetails)); return reply.result?.value as T;
    };
    const until = async (expression: string, what: string) => {
      const found = await evaluate<boolean>(`new Promise(resolve => { const end = Date.now() + 15000; (function check() { let ok = false; try { ok = !!(${expression}); } catch {} if (ok) resolve(true); else if (Date.now() > end) resolve(false); else setTimeout(check, 30); })(); })`);
      if (!found) {
        const text = await evaluate<string>(`(document.querySelector('.statistics-canvas')?.innerText ?? 'no canvas').slice(0, 600)`).catch(() => "");
        assert.fail(`waited for ${what}; ${problems.join(" | ")}\n${text}`);
      }
    };
    const shot = async (name: string) => {
      await mkdir(pictures, { recursive: true });
      const picture = await command("Page.captureScreenshot", { format: "png", captureBeyondViewport: false });
      await writeFile(join(pictures, `${name}.png`), Buffer.from(picture.data, "base64"));
    };
    const click = async (selector: string, index = 0) => {
      const point = await evaluate<{ x: number; y: number } | null>(`(() => { const e = document.querySelectorAll(${JSON.stringify(selector)})[${index}]; if (!e) return null; e.scrollIntoView({ block: 'center' }); const r = e.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }; })()`);
      assert.ok(point, `nothing matches ${selector}`);
      await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: point.x, y: point.y });
      await command("Input.dispatchMouseEvent", { type: "mousePressed", x: point.x, y: point.y, button: "left", clickCount: 1 });
      await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: point.x, y: point.y, button: "left", clickCount: 1 });
    };
    const clickText = async (selector: string, text: string) => {
      const find = `[...document.querySelectorAll(${JSON.stringify(selector)})].findIndex(e => e.textContent.trim() === ${JSON.stringify(text)})`;
      await until(`${find} >= 0`, `${selector} with the text "${text}"`);
      // A popover grows into place for a moment: the pointer goes where the element ends up.
      await new Promise(resolve => setTimeout(resolve, 260));
      await click(selector, await evaluate<number>(find));
    };
    const key = async (name: string, code: number, text?: string) => {
      await command("Input.dispatchKeyEvent", { type: "keyDown", key: name, code: name, windowsVirtualKeyCode: code, ...(text ? { text } : {}) });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key: name, code: name, windowsVirtualKeyCode: code });
    };
    const type = async (text: string) => { for (const char of text) await command("Input.dispatchKeyEvent", { type: "char", text: char }); };
    const resize = (width: number, height: number, ratio = 1) => command("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: ratio, mobile: false }).then(async () => { await evaluate(`window.dispatchEvent(new Event('resize'))`); });
    await command("Page.enable"); await command("Runtime.enable"); await command("Log.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: options.width ?? 1280, height: options.height ?? 800, deviceScaleFactor: options.ratio ?? 1, mobile: false });
    if (options.reducedMotion) await command("Emulation.setEmulatedMedia", { features: [{ name: "prefers-reduced-motion", value: "reduce" }] });
    await command("Fetch.enable", { patterns: [{ urlPattern: "*", requestStage: "Request" }] });
    await command("Page.navigate", { url: pageUrl });
    await until(`window.${options.fixture ?? "statsFixture"}`, "the fixture");
    await body({ evaluate, command, until, shot, click, clickText, key, type, resize, problems });
    assert.deepEqual(problems, [], "no exception, console error or policy violation");
  } finally {
    socket?.close(); browser?.kill();
    await new Promise(resolve => setTimeout(resolve, 300));
    await rm(directory, { recursive: true, force: true, maxRetries: 5 }).catch(() => { });
  }
}
