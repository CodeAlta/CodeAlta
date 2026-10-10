import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable } from "../browserTarget";

const edge = browserExecutable;

test("a canvas tab with a script: the module draws it, a reload replaces it, a hidden tab keeps the one it drew, and the tab takes what the script sets", { skip: !edge, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-canvas-script-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./canvasPanel.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("../style.css", import.meta.url), "utf8") + await readFile(new URL("./canvases.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root" style="height:400px"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
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
    const wait = (expression: string, milliseconds = 8_000) => evaluate<boolean>(
      `new Promise(resolve=>{const end=Date.now()+${milliseconds};const tick=()=>{let ok=false;try{ok=!!(${expression})}catch{}ok?resolve(true):Date.now()>end?resolve(false):setTimeout(tick,25)};tick()})`);
    const settle = (milliseconds = 300) => new Promise(resolve => setTimeout(resolve, milliseconds));
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.canvasFixture"), true);

    // The host says the canvas has a script and the input the tab was opened with: the component is drawn in the tab, and knows both.
    await evaluate(`canvasFixture.scenario.script = '/plugin/k/one/board.js'; canvasFixture.scenario.input = JSON.stringify({ a: 1 }); canvasFixture.render()`);
    assert.equal(await wait("document.querySelector('.canvas-panel .scripted')?.textContent === 'board one'"), true);
    assert.equal(await evaluate("document.querySelector('.scripted').dataset.instance"), "instance-1");
    assert.equal(await evaluate("document.querySelector('.scripted').dataset.input"), JSON.stringify({ a: 1 }));
    assert.equal(await evaluate("document.querySelectorAll('.canvas-panel .scripted').length"), 1);
    // What the script sets on its tab goes to the strip; the badge takes the place of the status while it is set.
    assert.equal(await wait("canvasFixture.state.looks.some(look => look.title === 'Script one' && look.status === '7')"), true);

    // The plugin is reloaded while the tab is hidden: the tab goes on with the script it has, and draws the new one once it is shown.
    await evaluate("canvasFixture.render({ visible: false })");
    await evaluate("canvasFixture.push({ kind: 'update', revision: 2, html: '<p>x</p>', script: '/plugin/k/two/board.js', scriptProblem: '', title: 'Board', statusText: '', actions: true, state: 'ready' })");
    await settle();
    assert.equal(await evaluate("document.querySelector('.canvas-panel .scripted').textContent"), "board one", "the hidden tab keeps what it drew");
    await evaluate("canvasFixture.render({ visible: true })");
    assert.equal(await wait("document.querySelector('.canvas-panel .scripted')?.textContent === 'board two'"), true);
    assert.equal(await evaluate("canvasFixture.state.scripts.includes('cleanup:one') && canvasFixture.state.scripts.at(-1)"), "mount:two", "the old module was let go before the new one started");

    // A reload whose script is gone shows why, in the tab; the window around it goes on.
    await evaluate("canvasFixture.push({ kind: 'update', revision: 3, script: '', scriptProblem: 'The script of the canvas could not be found.' })");
    assert.equal(await wait("document.querySelector('.canvas-panel .plugin-script-failure')?.textContent.includes('could not be found')"), true);
    assert.equal(await evaluate("document.querySelector('.canvas-panel').dataset.phase"), "ready");
    // And a script that comes back replaces the failure.
    await evaluate("canvasFixture.push({ kind: 'update', revision: 4, script: '/plugin/k/one/board.js', scriptProblem: '' })");
    assert.equal(await wait("document.querySelector('.canvas-panel .scripted')?.textContent === 'board one'"), true);

    // A script that fills the fragment starts again on each fragment the plugin writes: what it set of its tab for one is not said of the next.
    await evaluate("canvasFixture.push({ kind: 'update', revision: 5, html: '<p class=\"alta-titled\">From the script</p>', script: '/plugin/k/one/fill.js', scriptProblem: '' })");
    assert.equal(await wait("canvasFixture.state.looks.at(-1).title === 'From the script' && canvasFixture.state.looks.at(-1).status === '3'"), true);
    await evaluate("canvasFixture.push({ kind: 'update', revision: 6, html: '<p>plain</p>' })");
    assert.equal(await wait("canvasFixture.state.scripts.at(-1) === 'fill:plain'"), true, "the script fills the new fragment");
    assert.equal(await wait("canvasFixture.state.looks.at(-1).title === 'Board' && canvasFixture.state.looks.at(-1).status === null"), true, "the strip shows what the plugin gives again");
    // And says it again on a fragment that asks for it.
    await evaluate("canvasFixture.push({ kind: 'update', revision: 7, html: '<p class=\"alta-titled\">Again</p>' })");
    assert.equal(await wait("canvasFixture.state.looks.at(-1).title === 'Again' && canvasFixture.state.looks.at(-1).status === '3'"), true);
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    if (browser && browser.exitCode === null) { const closed = new Promise<void>(resolve => browser!.once("exit", () => resolve())); browser.kill(); await Promise.race([closed, new Promise(resolve => setTimeout(resolve, 5000))]); }
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
