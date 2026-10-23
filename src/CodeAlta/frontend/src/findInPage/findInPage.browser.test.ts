import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable, endBrowser, waitForBrowserPort } from "../browserTarget";

const edge = browserExecutable;

const first = `# Plan

The **review** of a plan, then the review of its changes.

| Step | What |
| --- | --- |
| 1 | Review the plan |
`;
const second = "# Notes\n\nA review here, and another review there.\n";

test("a rendered Markdown page finds in itself with Ctrl+F, beside another page that finds in itself too", { skip: !edge, timeout: 120_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-find-in-page-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./findInPage.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("../style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html data-theme="dark"><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: ["ignore", "ignore", "pipe"], windowsHide: true });
    const port = await waitForBrowserPort(join(root, "profile"), browser);
    const pages: { type?: string; webSocketDebuggerUrl?: string }[] = await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    socket = new WebSocket(pages.find(value => value.type === "page")!.webSocketDebuggerUrl!);
    await new Promise<void>((resolve, reject) => { socket!.addEventListener("open", () => resolve(), { once: true }); socket!.addEventListener("error", () => reject(Error("Browser unavailable")), { once: true }); });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<any>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(Error(`${method} timed out`)), 30_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data));
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(Error(JSON.stringify(message.error))); else resolve(message.result);
      };
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async <T,>(expression: string): Promise<T> => {
      const result = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(result.exceptionDetails, undefined, JSON.stringify(result.exceptionDetails));
      return result.result?.value as T;
    };
    const expect = async (expression: string, message?: string) => {
      const ok = await evaluate<boolean>(`new Promise(resolve=>{const end=Date.now()+8000;const tick=()=>{let ok=false;try{ok=!!(${expression})}catch{}ok?resolve(true):Date.now()>end?resolve(false):setTimeout(tick,25)};tick()})`);
      assert.equal(ok, true, message ?? expression);
    };
    const findModifier = process.platform === "darwin" ? 4 : 2;
    const press = async (key: string, code: string, virtualKey: number, modifiers = 0) => {
      await command("Input.dispatchKeyEvent", { type: "rawKeyDown", key, code, windowsVirtualKeyCode: virtualKey, modifiers });
      await command("Input.dispatchKeyEvent", { type: "keyUp", key, code, windowsVirtualKeyCode: virtualKey, modifiers });
    };
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await expect("window.findFixture");
    await evaluate(`findFixture.render(${JSON.stringify(first)}, ${JSON.stringify(second)})`);
    await expect("document.querySelectorAll('.markdown-content').length === 2");
    const input = (name: string) => `document.querySelector('#${name} .find-in-page input')`;
    const count = (name: string) => `document.querySelector('#${name} .find-in-page-count').textContent`;
    const marks = "(CSS.highlights.get('find-in-page')?.size ?? 0)";

    // Ctrl+F in the page opens its search, and the window does not search the sessions.
    await evaluate("document.querySelector('#first .editor-markdown').focus()");
    await press("f", "KeyF", 70, findModifier);
    await expect(`document.activeElement === ${input("first")}`);
    assert.equal(await evaluate("findFixture.state.searchedSessions"), 0);
    // Every place of the text is found, across the parts written differently, in the table too; not in the gutter.
    await evaluate(`findFixture.type(${input("first")}, 'REVIEW')`);
    await expect(`${count("first")} === '1 of 3' && ${marks} === 3`);
    assert.equal(await evaluate("[...CSS.highlights.get('find-in-page-current')][0].toString()"), "review");
    // The options, as in the text editor: Alt+C matches the case, the buttons of the box too.
    const option = (label: string) => `document.querySelector('#first .find-in-page button[aria-label="${label}"]')`;
    await press("c", "KeyC", 67, 1);
    await expect(`${count("first")} === '0 of 0' && ${option("Match case")}.getAttribute('aria-pressed') === 'true' && !CSS.highlights.has('find-in-page')`);
    await evaluate(`findFixture.type(${input("first")}, 'Review')`);
    await expect(`${count("first")} === '1 of 1'`);
    await press("c", "KeyC", 67, 1);
    await evaluate(`${option("Use regular expression")}.click(); findFixture.type(${input("first")}, 'r[a-z]+w')`);
    await expect(`${count("first")} === '1 of 3' && document.activeElement === ${input("first")}`, "A regular expression; the button leaves the keyboard in the box.");
    await evaluate(`findFixture.type(${input("first")}, 'review(')`);
    await expect(`${count("first")} === '0 of 0' && ${input("first")}.getAttribute('aria-invalid') === 'true'`, "A regular expression that is not valid finds nothing, and says so.");
    await evaluate(`${option("Use regular expression")}.click(); ${option("Match whole word")}.click(); findFixture.type(${input("first")}, 'REVIEW')`);
    await expect(`${count("first")} === '1 of 3'`);
    await evaluate(`findFixture.type(${input("first")}, 'revie')`);
    await expect(`${count("first")} === '0 of 0'`, "Only a whole word.");
    await press("w", "KeyW", 87, 1);
    await expect(`${count("first")} === '1 of 3'`);
    await evaluate(`findFixture.type(${input("first")}, 'REVIEW')`);
    await expect(`${count("first")} === '1 of 3'`);
    await press("Enter", "Enter", 13);
    await expect(`${count("first")} === '2 of 3'`);

    // The other page searches at the same time: each marks its own places, and takes only its own back.
    await evaluate("document.querySelector('#second .editor-markdown').focus()");
    await press("f", "KeyF", 70, findModifier);
    await expect(`document.activeElement === ${input("second")}`);
    await evaluate(`findFixture.type(${input("second")}, 'review')`);
    await expect(`${count("second")} === '1 of 2' && ${marks} === 5`);
    assert.equal(await evaluate("CSS.highlights.get('find-in-page-current').size"), 2);
    await press("Escape", "Escape", 27);
    await expect(`!document.querySelector('#second .find-in-page') && ${marks} === 3`, "Escape closes the search and takes its own marks away.");
    assert.equal(await evaluate("document.activeElement === document.querySelector('#second .editor-markdown')"), true, "The keyboard goes back to the page.");
    assert.equal(await evaluate(`${count("first")}`), "2 of 3");

    // Escape in the page, outside the box, closes its search too.
    await evaluate("document.querySelector('#first .editor-markdown').focus()");
    await press("Escape", "Escape", 27);
    await expect(`!document.querySelector('#first .find-in-page') && !CSS.highlights.has('find-in-page') && !CSS.highlights.has('find-in-page-current')`);

    // Outside the pages Ctrl+F is the search of the sessions.
    await evaluate("(() => { const other = document.createElement('button'); other.id = 'outside'; document.body.appendChild(other); other.focus(); })()");
    await press("f", "KeyF", 70, 2);
    await expect("findFixture.state.searchedSessions === 1");
    assert.equal(await evaluate("document.querySelector('.find-in-page')"), null);
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    await endBrowser(browser);
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
