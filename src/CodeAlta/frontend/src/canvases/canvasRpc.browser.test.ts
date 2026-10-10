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

test("a canvas script calls its plugin under StrictMode: one session, a call, an error, a stream, a reload, a hidden tab", { skip: !edge, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-canvas-rpc-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./canvasRpc.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
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
    assert.equal(await wait("window.rpcFixture"), true);

    await evaluate("window.rpcFixture.render()");
    // One session for the tab under StrictMode, not two: the call, the failure and the stream all arrive.
    assert.equal(await wait("document.querySelector('.rpc-board .call')?.textContent === 'echo 1'"), true);
    assert.equal(await evaluate("document.querySelector('.rpc-board .failed').textContent"), "not_found: No such board.");
    assert.equal(await wait("document.querySelector('.rpc-board .stream')?.textContent.startsWith('latest 50')"), true);
    assert.equal(await evaluate("rpcFixture.state.connections"), 1, "the double run of effects opened one session");

    // The acknowledgements went to the host merged, not one call for each item.
    assert.ok(await evaluate<number>("rpcFixture.state.batches.length") < 40, "the frames travel together");

    // The script is replaced by a reload of the plugin: the host ends the connection, and the new script connects again.
    await evaluate("rpcFixture.push({ kind: 'rpcClosed', connection: 'c1', reason: 'closed' })");
    await evaluate("rpcFixture.push({ kind: 'update', revision: 2, html: '<p>x</p>', script: '/plugin/k/two/board.js', scriptProblem: '', title: 'Board', statusText: '', actions: false, state: 'ready' })");
    assert.equal(await wait("rpcFixture.state.connections === 2"), true);
    assert.equal(await wait("document.querySelector('.rpc-board .call')?.textContent === 'echo 1'"), true);

    // A hidden tab asks for nothing more, and a shown one takes its stream again.
    await evaluate("rpcFixture.render({ visible: false })");
    const before = await evaluate<number>("rpcFixture.state.acks.length");
    await new Promise(resolve => setTimeout(resolve, 300));
    assert.equal(await evaluate("rpcFixture.state.acks.length"), before);
    await evaluate("rpcFixture.render({ visible: true })");
    assert.equal(await wait("document.querySelector('.rpc-board .stream')?.textContent.startsWith('latest 50')", 15_000), true);
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    if (browser && browser.exitCode === null) { const closed = new Promise<void>(resolve => browser!.once("exit", () => resolve())); browser.kill(); await Promise.race([closed, new Promise(resolve => setTimeout(resolve, 5000))]); }
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
