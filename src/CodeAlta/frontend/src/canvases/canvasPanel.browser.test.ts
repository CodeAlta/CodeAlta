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

test("a canvas tab draws what its plugin writes, sends the actions back, follows its pushes, and pauses while hidden", { skip: !edge, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-canvas-panel-"));
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
    const content = "document.querySelector('.canvas-html > p')?.textContent";
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.canvasFixture"), true);

    // The tab opens its instance, shown, and draws the fragment once, whatever the number of effects that StrictMode runs.
    await evaluate("canvasFixture.render()");
    assert.equal(await wait(`${content}==='first'`), true);
    assert.equal(await evaluate("document.querySelectorAll('.canvas-panel .plugin-html').length"), 1);
    assert.equal(await evaluate("document.querySelector('.canvas-panel').dataset.phase"), "ready");
    assert.equal(await wait("canvasFixture.state.calls.includes('visible:true')"), true);
    assert.ok(await evaluate<boolean>("canvasFixture.state.calls.every(call => call !== 'visible:false') || canvasFixture.state.calls.at(-1) === 'visible:true'"));
    // The strip is given the title, the icon and the plugin folder of the canvas, and the instance to close with the tab.
    assert.deepEqual(await evaluate("canvasFixture.state.looks.find(look => look.icon)"), { title: "Board", status: null, icon: "list-checks", plugin: "plugin:global:board" });
    assert.equal(await evaluate("canvasFixture.state.instances.at(-1)"), "instance-1");

    // A click on an element with an action reaches the plugin with the values of the fragment; its answer replaces the content.
    await evaluate("document.querySelector('[data-alta-action=tick]').click()");
    assert.equal(await wait(`${content}==='after the action'`), true);
    assert.deepEqual(await evaluate("canvasFixture.state.actions"), [{ action: "tick", value: "one", values: { note: "typed" } }]);

    // What the plugin pushes is drawn, with the title and the status it gives; an event older than what the tab has is not.
    await evaluate("canvasFixture.push({ kind: 'update', revision: 2, html: '<p>pushed</p>', title: 'Renamed', statusText: '3 of 8 done' })");
    assert.equal(await wait(`${content}==='pushed'`), true);
    assert.equal(await wait("canvasFixture.state.looks.some(look => look.title === 'Renamed' && look.status === '3 of 8 done')"), true);
    await evaluate("canvasFixture.push({ kind: 'update', revision: 1, html: '<p>stale</p>' })");
    await evaluate("canvasFixture.push({ kind: 'update', revision: 3, statusText: '' })");
    assert.equal(await wait("canvasFixture.state.looks.at(-1).status === null"), true);
    assert.equal(await evaluate(content), "pushed");

    // A hidden tab keeps the latest fragment it was sent and draws it once it is shown; the plugin is told both.
    await evaluate("canvasFixture.render({ visible: false })");
    assert.equal(await wait("canvasFixture.state.calls.at(-1) === 'visible:false'"), true);
    await evaluate("canvasFixture.push({ kind: 'update', revision: 4, html: '<p>while hidden</p>' })");
    await new Promise(resolve => setTimeout(resolve, 300));
    assert.equal(await evaluate(content), "pushed", "nothing is drawn while the tab is hidden");
    await evaluate("canvasFixture.render({ visible: true })");
    assert.equal(await wait(`${content}==='while hidden'`), true);
    assert.equal(await wait("canvasFixture.state.calls.at(-1) === 'visible:true'"), true);

    // The host closed the instance of the hidden tab to make room for others: shown again, the tab asks for its instance again.
    await evaluate("canvasFixture.render({ visible: false })");
    assert.equal(await wait("canvasFixture.state.calls.at(-1) === 'visible:false'"), true);
    const opens = await evaluate<number>("canvasFixture.state.opened");
    await evaluate("window.firstHtml = canvasFixture.scenario.html; Object.assign(canvasFixture.scenario, { html: '<p>opened again</p>', revision: 4 }); canvasFixture.state.evicted = true; canvasFixture.render({ visible: true })");
    assert.equal(await wait(`${content}==='opened again'`), true, "a tab whose instance the host no longer knows opens it again");
    assert.equal(await evaluate("canvasFixture.state.opened"), opens + 1);
    await evaluate("Object.assign(canvasFixture.scenario, { html: window.firstHtml, revision: 1 })");

    // The plugin stops: the tab says so and goes on listening; its new version brings the content back.
    await evaluate("canvasFixture.push({ kind: 'state', state: 'plugin_stopped' })");
    assert.equal(await wait("document.querySelector('.canvas-placeholder')?.textContent.includes('The plugin is not running.')"), true);
    assert.equal(await evaluate("document.querySelector('.canvas-panel').dataset.phase"), "stopped");
    await evaluate("canvasFixture.push({ kind: 'update', revision: 5, html: '<p>back</p>', state: 'ready', actions: true, title: 'Board' })");
    assert.equal(await wait(`${content}==='back'`), true);
    assert.equal(await evaluate("!document.querySelector('.canvas-placeholder')"), true);

    // The plugin closes the instance: the tab is closed with it.
    const closed = await evaluate<number>("canvasFixture.state.closed");
    await evaluate("canvasFixture.push({ kind: 'closed' })");
    assert.equal(await wait(`canvasFixture.state.closed===${closed + 1}`), true);

    // A plugin that is not running at the start: the tab says why, and offers to build it, to open its source and to close.
    await evaluate("canvasFixture.clear(); canvasFixture.scenario.status = 'plugin_stopped'");
    await evaluate("canvasFixture.render({ control: true })");
    const placeholder = "document.querySelector('.canvas-placeholder')";
    assert.equal(await wait(`${placeholder}?.textContent.includes('The plugin did not start.') && ${placeholder}.querySelector('code')`), true);
    assert.equal(await evaluate(`${placeholder}.querySelector('code').textContent`), "plugin.cs(3,1): error CS1002");
    assert.equal(await evaluate(`${placeholder}.querySelector('.bp6-heading').textContent`), "Board");
    assert.deepEqual(await evaluate(`[...${placeholder}.querySelectorAll('button')].map(button => button.textContent.trim())`), ["Rebuild plugin", "Open plugin source", "Close"]);
    await evaluate(`[...${placeholder}.querySelectorAll('button')].find(button => button.textContent.includes('Open plugin source')).click()`);
    assert.deepEqual(await evaluate("canvasFixture.state.sources"), ["plugin:global:board"]);
    const before = await evaluate<number>("canvasFixture.state.closed");
    await evaluate(`[...${placeholder}.querySelectorAll('button')].find(button => button.textContent.trim() === 'Close').click()`);
    assert.equal(await evaluate("canvasFixture.state.closed"), before + 1);
    // A build that fails and says nothing still says so.
    await evaluate("canvasFixture.state.rebuildFails = true");
    await evaluate(`[...${placeholder}.querySelectorAll('button')].find(button => button.textContent.includes('Rebuild plugin')).click()`);
    assert.equal(await wait(`${placeholder}.querySelector('code')?.textContent === 'The plugin could not be built.'`), true);
    await evaluate("canvasFixture.state.rebuildFails = false");
    // Building the plugin brings the tab back.
    await evaluate(`[...${placeholder}.querySelectorAll('button')].find(button => button.textContent.includes('Rebuild plugin')).click()`);
    assert.equal(await wait("canvasFixture.state.rebuilt===1"), true);
    assert.equal(await wait(`${content}==='first'`), true, "the tab opens its canvas again once the plugin runs");

    // A plugin that is gone has nothing to build either.
    await evaluate("canvasFixture.clear(); canvasFixture.state.probeUnknown = true; canvasFixture.scenario.status = 'plugin_stopped'");
    await evaluate("canvasFixture.render({ control: true })");
    assert.equal(await wait(`${placeholder}?.textContent.includes('The plugin is not running.')`), true);
    assert.deepEqual(await evaluate(`[...${placeholder}.querySelectorAll('button')].map(button => button.textContent.trim())`), ["Close"]);
    await evaluate("canvasFixture.state.probeUnknown = false");

    // A canvas the plugin no longer declares has its own words and nothing to build.
    await evaluate("canvasFixture.clear(); canvasFixture.scenario.status = 'unknown_canvas'; canvasFixture.render()");
    assert.equal(await wait("document.querySelector('.canvas-placeholder')?.textContent.includes('The plugin no longer has this canvas.')"), true);
    assert.deepEqual(await evaluate("[...document.querySelectorAll('.canvas-placeholder button')].map(button => button.textContent.trim())"), ["Close"]);

    // A tab that goes away while the host opens its instance: the instance was opened as shown, and nothing else would tell the host it is not.
    await evaluate("canvasFixture.clear(); canvasFixture.scenario.status = 'ok'; canvasFixture.state.holdOpens = true; canvasFixture.state.calls.length = 0; canvasFixture.render(); canvasFixture.clear(); canvasFixture.release()");
    assert.equal(await wait("canvasFixture.state.calls.includes('visible:false')"), true, "the host is told that no tab shows the instance");
    await new Promise(resolve => setTimeout(resolve, 200));
    assert.deepEqual(await evaluate("canvasFixture.state.calls.filter(call => call.startsWith('visible'))"), ["visible:false"], "once, and only for the tab that went away");
    // A tab that stays is not said to be hidden by the first of the two runs React makes of its effects.
    await evaluate("canvasFixture.state.calls.length = 0; canvasFixture.state.holdOpens = true; canvasFixture.render(); canvasFixture.release()");
    assert.equal(await wait(`${content}==='first'`), true);
    assert.equal(await wait("canvasFixture.state.calls.includes('visible:true')"), true);
    assert.deepEqual(await evaluate("canvasFixture.state.calls.filter(call => call === 'visible:false')"), []);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    if (browser && browser.exitCode === null) { const closed = new Promise<void>(resolve => browser!.once("exit", () => resolve())); browser.kill(); await Promise.race([closed, new Promise(resolve => setTimeout(resolve, 5000))]); }
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
