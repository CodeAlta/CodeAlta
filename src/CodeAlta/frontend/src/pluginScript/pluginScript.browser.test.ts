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

test("the script of plugin HTML: a component drawn in the tree, a mount on the fragment, charts, pauses, reloads and failures, under StrictMode", { skip: !edge, timeout: 180_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-plugin-script-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./pluginScript.mount.tsx", import.meta.url))], outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), await readFile(new URL("../style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
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
    const settle = (milliseconds = 250) => new Promise(resolve => setTimeout(resolve, milliseconds));
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("window.scriptFixture"), true);

    // Without script, the fragment is drawn as it always was.
    await evaluate("scriptFixture.render()");
    assert.equal(await evaluate("document.querySelector('.alta-skeleton')?.textContent"), "skeleton");
    assert.equal(await evaluate("document.querySelector('.plugin-html').dataset.script ?? null"), null);

    // A component takes the place of the fragment, once, in the tree of the window (hooks run, the alta object is the one of its content).
    await evaluate("scriptFixture.clear(); scriptFixture.drain(); scriptFixture.render({ path: '/plugin/k/one/board.js' })");
    assert.equal(await wait("document.querySelector('.board-title')?.textContent === 'Board board'"), true);
    assert.equal(await evaluate("document.querySelectorAll('.board').length"), 1, "one component, whatever StrictMode runs twice");
    assert.equal(await evaluate("document.querySelector('.alta-skeleton')"), null, "the fragment gives way to the component");
    assert.equal(await evaluate("document.querySelector('.plugin-html').dataset.script"), "component");
    assert.equal(await wait("document.querySelector('.board').dataset.count === 'true'"), true, "its timer runs while the content is shown");
    assert.equal((await evaluate<string[]>("scriptFixture.drain()")).filter(line => line.startsWith("load:")).length >= 1, true);

    // It is told when the content is hidden, and pauses; shown again, it goes on.
    await evaluate("scriptFixture.render({ path: '/plugin/k/one/board.js', visible: false })");
    assert.equal(await wait("document.querySelector('.board').dataset.visible === 'false'"), true);
    await settle(100);
    const paused = await evaluate<string>("document.querySelector('.board').dataset.count");
    await evaluate("scriptFixture.state.renders = 0");
    await settle(300);
    assert.equal(await evaluate("scriptFixture.state.renders"), 0, "a hidden component draws nothing");
    assert.equal(await evaluate("document.querySelector('.board').dataset.count"), paused);
    await evaluate("scriptFixture.render({ path: '/plugin/k/one/board.js', visible: true })");
    assert.equal(await wait("document.querySelector('.board').dataset.visible === 'true'"), true);

    // alta.host reaches the window through what the content already has, and alta.html cleans what a script inserts.
    await evaluate("scriptFixture.drain(); document.querySelector('.board-open').click()");
    assert.deepEqual(await evaluate<string[]>("scriptFixture.drain()"), [
      "link:{\"address\":\"src/a.cs:12\",\"scope\":{\"projectId\":\"p1\",\"sessionId\":\"s1\"}}", "open-session:\"s-1\"", "run-named:{\"name\":\"refresh\",\"pluginKey\":\"plugin:k\"}",
      "bridge-canvas:{\"pluginKey\":\"plugin:k\",\"canvasId\":\"other\",\"projectId\":\"p1\",\"sessionId\":\"s1\",\"key\":null}", "bridge-changes:\"p1\"",
      "link:{\"address\":\"https://example.com/x\",\"scope\":null}", "tab-badge:\"3\"", "tab-title:\"Mine\""]);
    await evaluate("document.querySelector('.board-html').click()");
    assert.equal(await evaluate("!!document.querySelector('.alta-injected')"), true);
    assert.equal(await evaluate("document.querySelector('.board img, .board script')"), null, "an image with a handler and a script are not inserted");
    await settle(100);
    assert.equal(await evaluate("window.__pwned ?? null"), null);

    // A reloaded plugin is a new path: the component is replaced, and the old one is let go.
    await evaluate("scriptFixture.drain(); scriptFixture.render({ path: '/plugin/k/two/board.js' })");
    assert.equal(await wait("document.querySelector('.board-two')?.textContent === 'second version'"), true);
    assert.equal(await evaluate("document.querySelector('.board')"), null);
    assert.ok((await evaluate<string[]>("scriptFixture.drain()")).includes("component-cleanup"));

    // A mount fills the element that holds the fragment, is ended when the content goes away, and the fragment is put back for the next one.
    await evaluate("scriptFixture.clear(); scriptFixture.drain(); scriptFixture.render({ html: '<div class=\"alta-target\">fragment</div>', path: '/plugin/k/one/mount.js' })");
    assert.equal(await wait("document.querySelector('.alta-mounted')?.textContent === 'mounted'"), true);
    assert.equal(await evaluate("document.querySelector('.plugin-html').dataset.script"), "ready");
    assert.equal(await evaluate("document.querySelector('.alta-target').textContent"), "fragmentmounted", "the script worked on the skeleton of the fragment");
    let log = await evaluate<string[]>("scriptFixture.drain()");
    assert.deepEqual(log.filter(line => line.startsWith("mount")), ["mount:\"fragment\""], "mounted once, on a clean fragment");
    assert.ok(!log.includes("mount-cleanup"));
    // The plugin writes another fragment: what the script made went with the first one, so the script is ended and started on the new one,
    // with an alta object that is its own and not a closed one. What the first run set of its tab ends with it, before the next one starts.
    await evaluate("scriptFixture.render({ html: '<div class=\"alta-target\">second</div>', path: '/plugin/k/one/mount.js' })");
    assert.equal(await wait("document.querySelector('.alta-target')?.textContent === 'secondmounted'"), true, "the script fills the new fragment");
    log = await evaluate<string[]>("scriptFixture.drain()");
    assert.deepEqual(log.filter(line => !line.startsWith("load")), ["closed-signal", "tab-title:null", "tab-status:null", "tab-badge:null", "mount-cleanup", "mount:\"second\"", "alta-open:true"]);
    assert.equal(await evaluate("document.querySelectorAll('.alta-mounted').length"), 1);
    // The same fragment again changes nothing.
    await evaluate("scriptFixture.render({ html: '<div class=\"alta-target\">second</div>', path: '/plugin/k/one/mount.js' })");
    await settle(150);
    assert.deepEqual(await evaluate<string[]>("scriptFixture.drain()"), []);
    await evaluate("scriptFixture.render({ html: '<div class=\"alta-target\">fragment</div>', path: '/plugin/k/one/mount.js' })");
    assert.equal(await wait("document.querySelector('.alta-target')?.textContent === 'fragmentmounted'"), true);
    await evaluate("scriptFixture.drain()");
    await evaluate("scriptFixture.render({ html: '<div class=\"alta-target\">fragment</div>', path: '/plugin/k/two/mount.js' })");
    assert.equal(await wait("scriptFixture.state.log.some(entry => entry.name === 'mount-two')"), true);
    log = await evaluate<string[]>("scriptFixture.drain()");
    assert.ok(log.includes("closed-signal") && log.includes("mount-cleanup"), "the old script was ended: its signal aborted and its function called");
    assert.ok(log.includes("mount-two:\"fragment\""), "the next script finds the fragment as it was, not what the first one made");
    assert.equal(await evaluate("document.querySelector('.alta-mounted')"), null);
    await evaluate("scriptFixture.clear()");
    await settle();
    // Nothing is left running after the content went away.
    await evaluate("scriptFixture.drain()");

    // alta.rpc is the one the window carries, and says it is not there until it does.
    await evaluate("scriptFixture.clear(); scriptFixture.drain(); scriptFixture.render({ path: '/plugin/k/one/rpc.js' })");
    assert.equal(await wait("document.querySelector('.rpc-result')?.textContent.startsWith('error:')"), true);
    assert.equal(await evaluate("document.querySelector('.rpc-result').textContent"), "error: alta.rpc is not available here: only the script of a canvas can call its plugin.");
    await evaluate("scriptFixture.clear(); scriptFixture.drain(); scriptFixture.render({ path: '/plugin/k/one/rpc.js', rpc: true })");
    assert.equal(await wait("document.querySelector('.rpc-result')?.textContent === 'rows 3 latest 3'"), true, "the hooks give what the carried calls and streams give");
    assert.ok((await evaluate<string[]>("scriptFixture.drain()")).some(line => line.startsWith("rpc:") && line.includes("board.get")));

    // A script that fails shows its error where it is, copyable; the rest of the window goes on.
    for (const [path, message] of [["/plugin/k/one/throws.js", "the render exploded"], ["/plugin/k/one/mount-throws.js", "the mount exploded"], ["/plugin/k/one/mount-rejects.js", "the async mount exploded"],
      ["/plugin/k/one/fails.js", "Failed to fetch dynamically imported module"], ["/plugin/k/one/shape.js", "exports no default component and no mount function"], ["/plugin/k/one/missing.js", "no module"]]) {
      await evaluate(`scriptFixture.clear(); scriptFixture.render({ path: ${JSON.stringify(path)}, sibling: true })`);
      assert.equal(await wait(`document.querySelector('.plugin-script-failure')?.textContent.includes(${JSON.stringify(message)})`), true, path);
      assert.equal(await evaluate("document.querySelector('.plugin-script-failure').textContent.includes('The script of this plugin failed.')"), true);
      assert.equal(await evaluate("document.querySelector('#sibling')?.textContent"), "the rest of the window", `${path}: the window around it is untouched`);
      assert.equal(await evaluate("!!document.querySelector('.plugin-script-failure button')"), true, "the error can be copied");
    }

    await evaluate("scriptFixture.clear(); scriptFixture.drain(); scriptFixture.render({ path: '/plugin/k/one/board.js', problem: 'The script of the canvas could not be found.' })");
    assert.equal(await wait("document.querySelector('.plugin-script-failure')?.textContent.includes('could not be found')"), true, "a script the host could not serve is told, and not loaded");
    await settle();
    assert.equal((await evaluate<string[]>("scriptFixture.drain()")).some(line => line.startsWith("load:")), false);

    // A card starts its script when it is first on the screen.
    await evaluate("scriptFixture.clear(); scriptFixture.drain(); scriptFixture.render({ path: '/plugin/k/one/board.js', whenShown: true, spacer: 4000 })");
    await settle(400);
    assert.equal(await evaluate("document.querySelector('.board')"), null, "below the screen, nothing started");
    assert.equal((await evaluate<string[]>("scriptFixture.drain()")).some(line => line.startsWith("load:")), false);
    await evaluate("document.querySelector('.plugin-html').scrollIntoView()");
    assert.equal(await wait("document.querySelector('.board')"), true, "scrolled into view, it starts");

    // Charts: a block with a JSON option is drawn; an option that is not data is a short message; nothing throws.
    const option = JSON.stringify({ xAxis: { type: "category", data: ["a", "b"] }, yAxis: {}, series: [{ type: "bar", data: [1, 2] }] }).replaceAll("\"", "&quot;");
    await evaluate(`scriptFixture.clear(); scriptFixture.render({ html: '<div class="alta-chart" data-option="${option}" data-label="Bars"></div><div class="alta-chart" data-option="{&quot;series&quot;:[{&quot;formatter&quot;:&quot;function(){}&quot;}],&quot;graphic&quot;:[]}"></div><div class="alta-chart" data-option="not json"></div><div class="alta-chart"></div>' })`);
    assert.equal(await wait("document.querySelector('.alta-chart svg, .alta-chart canvas')", 15_000), true, "a valid option is drawn");
    assert.equal(await evaluate("document.querySelectorAll('.plugin-chart-invalid').length"), 3, "an option that is not data, not JSON or missing is a quiet message");
    assert.equal(await evaluate("document.querySelector('.plugin-chart-invalid').textContent"), "This chart could not be drawn.");
    assert.equal(await evaluate("document.querySelector('.alta-chart .chart-label').textContent"), "Bars", "the label is the accessible name");
    // The fragment keeps no handler or script the author wrote in it.
    await evaluate("scriptFixture.clear(); scriptFixture.render({ html: '<p onclick=\"window.__pwned=3\" class=\"alta-x\">a</p><script>window.__pwned=4</script><iframe src=\"https://example.com\"></iframe><img src=x onerror=\"window.__pwned=5\"><div data-option=\"{}\" class=\"alta-not-a-chart\">b</div>' })");
    assert.equal(await evaluate("document.querySelector('.plugin-html').innerHTML"), "<p class=\"alta-x\">a</p><div class=\"alta-not-a-chart\">b</div>");
    assert.equal(await evaluate("window.__pwned ?? null"), null);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close();
    if (browser && browser.exitCode === null) { const closed = new Promise<void>(resolve => browser!.once("exit", () => resolve())); browser.kill(); await Promise.race([closed, new Promise(resolve => setTimeout(resolve, 5000))]); }
    await rm(root, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  }
});
