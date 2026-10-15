import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { pressElement } from "./browserPointer";
import { browserBaseArgs, browserExecutable, waitForBrowserPort } from "./browserTarget";

/** The window in headless Edge, with what a test of its tabs needs to drive it and to read it. */
export type SessionWindow = Readonly<{
  command: (method: string, params?: object) => Promise<Record<string, any>>;
  evaluate: (expression: string) => Promise<any>;
  /** Waits until an expression of the page is true; `what` names it when it never is. */
  until: (condition: string, what: string) => Promise<void>;
  /** An expression of the page: the tabs of the dock in the order they are shown. A transient one starts with "~", the one in front ends with "*". */
  tabs: string;
  /** Waits until the tabs of the dock are these. */
  shown: (expected: string[], what: string) => Promise<void>;
  /** The pointer on an element, pressed once or twice as a person does, with the main button or the other one. */
  press: (selector: string, times?: 1 | 2, button?: "left" | "right") => Promise<void>;
  /** Expressions of the page: the row of a session, the row of a scope ("" for the chats), the title of a tab. */
  session: (id: string) => string; scope: (id: string) => string; title: (text: string) => string;
  /** What the page threw or logged as an error. */
  exceptions: string[];
}>;

/**
 * Runs a test in the window itself (main.tsx) over the isolated host of `selectedSessionMenu.neoastra.mount.ts`,
 * with `storage` in its local storage before it loads.
 */
export async function withSessionWindow(storage: Readonly<Record<string, string>>, run: (window: SessionWindow) => Promise<void>) {
  const root = await mkdtemp(join(tmpdir(), "codealta-session-window-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-session-window", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./selectedSessionMenu.neoastra.mount.ts", import.meta.url)) }));
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
    await writeFile(join(root, "style.css"), (await Promise.all([
      "../node_modules/@blueprintjs/core/lib/css/blueprint.css", "../node_modules/flexlayout-react/style/light.css", "./style.css", "./explorer/explorer.css",
    ].map(path => readFile(new URL(path, import.meta.url), "utf8")))).join("\n"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, "--allow-file-access-from-files",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type: string; webSocketDebuggerUrl: string }[];
    socket = new WebSocket(pages.find(tab => tab.type === "page")!.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", reject, { once: true }); });
    const exceptions: string[] = [];
    socket.addEventListener("message", event => {
      const value = JSON.parse(String(event.data));
      if (value.method === "Runtime.exceptionThrown") exceptions.push(value.params.exceptionDetails.exception?.description ?? value.params.exceptionDetails.text);
      if (value.method === "Runtime.consoleAPICalled" && value.params.type === "error")
        exceptions.push(value.params.args.map((arg: { description?: string; value?: unknown }) => arg.description ?? arg.value).join(" "));
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
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined, JSON.stringify(value.exceptionDetails)); return value.result?.value;
    };
    const tabs = `[...document.querySelectorAll('.session-dock .flexlayout__tab_button')].map(button => {
      const node = button.querySelector('[data-session-node]');
      return (node?.dataset.transient ? '~' : '') + (node?.textContent ?? '').trim() + (button.classList.contains('flexlayout__tab_button--selected') ? '*' : ''); })`;
    const until = async (condition: string, what: string) => {
      const found = await evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{let ok=false;try{ok=!!(${condition})}catch{}if(ok)resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(tick,20)};tick()})`);
      assert.equal(found, true, `${what}; tabs: ${JSON.stringify(await evaluate(tabs))}; ${exceptions.join("\n")}`);
    };
    const shown = (expected: string[], what: string) => until(`JSON.stringify(${tabs})===${JSON.stringify(JSON.stringify(expected))}`, what);
    // A double click is two clicks, then the double click; the other button asks for the menu of what is under it.
    // A row gives part of its width to its buttons once the pointer is on it: it is pressed where it then is.
    const press = (selector: string, times: 1 | 2 = 1, button: "left" | "right" = "left") =>
      pressElement(command, selector, selector, { times, button, block: "nearest" });
    const session = (id: string) => `(document.querySelector('.session-row[data-session-id="${id}"] > button:first-child') ?? [...document.querySelectorAll('.session-row > button:first-child')].find(row => row.querySelector('.session-title')?.textContent === ${JSON.stringify(id)}))`;
    const scope = (id: string) => `document.querySelector('.project-action-row > button[data-scope="${id}"]')`;
    const title = (text: string) => `[...document.querySelectorAll('.session-dock .flexlayout__tab_button [data-session-node]')].find(node => node.textContent.trim() === ${JSON.stringify(text)})`;

    await command("Page.enable"); await command("Runtime.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1500, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.addScriptToEvaluateOnNewDocument", { source: Object.entries(storage).map(([key, value]) => `localStorage.setItem(${JSON.stringify(key)}, ${JSON.stringify(value)})`).join("; ") });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await run({ command, evaluate, until, tabs, shown, press, session, scope, title, exceptions });
  } finally {
    socket?.close(); browser?.kill();
    await new Promise(resolve => setTimeout(resolve, 300));
    await rm(root, { recursive: true, force: true, maxRetries: 5 }).catch(() => { });
  }
}
