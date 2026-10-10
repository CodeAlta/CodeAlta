import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable } from "./browserTarget";

// Mount main.tsx itself, not a test-provided PluginButtonsContext around an extracted row: a hook in App cannot
// read the provider App returns. Both a project and Chats must use the row's identity without opening that session.
test("the selected-scope Explorer menu reads plugins below App's provider and acts for its row without selecting it", { skip: !browserExecutable, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-selected-session-menu-"));
  let browser: ReturnType<typeof spawn> | undefined, socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-selected-session-menu", setup(bundle) {
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
      "../node_modules/@blueprintjs/core/lib/css/blueprint.css", "../node_modules/flexlayout-react/style/light.css", "./style.css",
    ].map(path => readFile(new URL(path, import.meta.url), "utf8")))).join("\n"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(browserExecutable!, [...browserBaseArgs, "--allow-file-access-from-files",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(root, "profile/DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
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
    const until = async (condition: string, what: string) => {
      const found = await evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{if(${condition})resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(tick,20)};tick()})`);
      assert.equal(found, true, `${what}; ${exceptions.join("\n")}; menu: ${await evaluate("document.querySelector('.session-tab-popup')?.textContent")}`);
    };
    const frames = () => evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true))))");
    const key = async (key: string, virtual: number, text?: string) => {
      for (const type of ["keyDown", "keyUp"]) await command("Input.dispatchKeyEvent", { type, key, code: key, windowsVirtualKeyCode: virtual, ...(text ? { text } : {}) });
    };
    const row = (id: string) => `document.querySelector('.session-row[data-session-id="${id}"]')`;
    const trigger = (id: string) => `${row(id)}.querySelector('.session-actions-trigger')`;
    const entries = "[...document.querySelectorAll('.session-tab-popup [role=menuitem]')]";
    const selection = `({ scope: document.querySelector('button[data-scope][aria-pressed=true]')?.dataset.scope,
      session: document.querySelector('.session-row > button:first-child[aria-pressed=true]')?.parentElement.dataset.sessionId,
      tabs: [...document.querySelectorAll('.session-dock [data-session-node]')].map(node=>node.dataset.sessionNode) })`;
    const reads = "selectedMenuFixture.state.reads.filter(read=>read.place==='SessionMenu')";
    const openMenu = async (id: string) => {
      await evaluate(`${trigger(id)}.focus(); ${trigger(id)}.click()`);
      await until("!!document.querySelector('.session-tab-popup')", `the selected-scope menu of ${id}`);
    };
    const pluginLine = async () => {
      await until(`${entries}.some(item=>item.textContent.trim()==='Statistics of this session')`, "the plugin line under the production provider");
      assert.deepEqual(await evaluate(`${entries}.map(item=>item.textContent.trim())`), ["Open session", "Rename…", "Delete…", "Open Board", "Statistics of this session"]);
      assert.equal(await evaluate("document.querySelectorAll('.session-tab-popup .bp6-menu-divider').length"), 2, "canvas and plugin groups keep their dividers");
    };
    const assertReads = async (projectId: string | null, sessionId: string) => {
      const actual = await evaluate(`${reads}.map(({ expectedEpoch, projectId, sessionId })=>({ expectedEpoch, projectId, sessionId }))`);
      assert.ok(actual.length > 0, "the row was read, not the default context");
      for (const read of actual) assert.deepEqual(read, { expectedEpoch: "12345678-1234-1234-1234-123456789abc", projectId, sessionId });
    };
    await command("Page.enable"); await command("Runtime.enable");
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.addScriptToEvaluateOnNewDocument", { source: "localStorage.setItem('settingsFixtureOwned','true')" });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    await until(`!!${row("two")} && ${row("one")}.firstElementChild.getAttribute('aria-pressed')==='true'`, "the selected project with session one open");
    await until("selectedMenuFixture.state.reads.some(read=>read.place==='TitleBar')", "the production plugin provider is ready");
    assert.deepEqual(await evaluate(reads), [], "no session menu reads while closed");
    const projectSelection = await evaluate(selection);

    // The nonselected row in the selected project: the original regression, with the application's canvas lines intact.
    await openMenu("two"); await pluginLine(); await assertReads("project", "two");
    assert.deepEqual(await evaluate(selection), projectSelection, "opening the menu neither selects nor opens its session");
    await until("document.activeElement?.textContent.trim()==='Open session'", "the first menu entry has focus");
    await key("End", 35); await key("Enter", 13, "\r");
    await until("selectedMenuFixture.state.invoked.length===1 && !document.querySelector('.session-tab-popup')", "the plugin command ran and the menu closed");
    assert.deepEqual(await evaluate("selectedMenuFixture.state.invoked.map(({commandId,projectId,sessionId})=>({commandId,projectId,sessionId}))"),
      [{ commandId: "builtin:statistics/command/statistics-session", projectId: "project", sessionId: "two" }]);
    assert.deepEqual(await evaluate(selection), projectSelection, "running the plugin does not select or open its session either");
    assert.equal(await evaluate(`document.activeElement===${trigger("two")}`), true, "activation restores focus to the menu's row");

    // Late plugin lines grow the same menu without losing focus or running past the bottom of a short window.
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 320, deviceScaleFactor: 1, mobile: false });
    await evaluate("selectedMenuFixture.state.hold=true"); await openMenu("two");
    await until("selectedMenuFixture.state.pending.length>0", "the menu is waiting for plugins");
    await key("End", 35);
    await evaluate("selectedMenuFixture.release('Statistics of this session'); selectedMenuFixture.state.hold=false");
    await pluginLine(); await frames();
    assert.equal(await evaluate("document.activeElement?.textContent.trim()"), "Open Board", "loading plugins keeps the currently focused entry");
    assert.ok(await evaluate("document.querySelector('.session-tab-popup').getBoundingClientRect().bottom <= innerHeight"), "the enlarged menu stays inside the window");
    await key("Escape", 27);
    await until("!document.querySelector('.session-tab-popup')", "the resized menu closed");
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });

    // The selected row also works; Escape preserves the existing origin and lifetime behavior.
    await evaluate("selectedMenuFixture.state.reads.length=0");
    await openMenu("one"); await pluginLine(); await assertReads("project", "one");
    await key("Escape", 27);
    await until(`!document.querySelector('.session-tab-popup') && document.activeElement===${trigger("one")}`, "Escape returns to the same row");
    // A read retained by a closed menu cannot publish into the next selection or steal its keyboard focus.
    await evaluate("selectedMenuFixture.state.hold=true"); await openMenu("two");
    await until("selectedMenuFixture.state.pending.length>0", "the delayed row read");
    await evaluate(`${row("one")}.firstElementChild.focus(); ${row("one")}.firstElementChild.click()`);
    await until("!document.querySelector('.session-tab-popup') && selectedMenuFixture.state.pending.every(read=>read.signal.aborted)", "closing retires every row read");
    await evaluate("selectedMenuFixture.release(); selectedMenuFixture.state.hold=false"); await frames();
    assert.equal(await evaluate("document.querySelector('.session-tab-popup')"), null);
    assert.equal(await evaluate(`document.activeElement===${row("one")}.firstElementChild`), true);

    // Chats is now the selected scope, not ExplorerSessions' off-scope list. Its nonselected row has no project context.
    await evaluate("document.querySelector('.project-root-list button[data-scope=\"\"]').click()");
    await until(`!!${row("chat-1")}`, "Chats is the selected scope");
    await evaluate(`${row("chat-1")}.firstElementChild.click()`);
    await until(`${row("chat-1")}.firstElementChild.getAttribute('aria-pressed')==='true'`, "the first chat is selected");
    const chatSelection = await evaluate(selection);
    await evaluate(`selectedMenuFixture.state.reads.length=0; ${row("chat-2")}.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true}))`);
    await pluginLine(); await assertReads(null, "chat-2");
    assert.deepEqual(await evaluate(selection), chatSelection);
    await evaluate(`${entries}.at(-1).click()`);
    await until("selectedMenuFixture.state.invoked.length===2 && !document.querySelector('.session-tab-popup')", "the chat's plugin command ran");
    assert.deepEqual(await evaluate("selectedMenuFixture.state.invoked[1].projectId"), null);
    assert.equal(await evaluate("selectedMenuFixture.state.invoked[1].sessionId"), "chat-2");
    assert.deepEqual(await evaluate(selection), chatSelection);
    assert.equal(await evaluate("settingsShellFixture.renameRequests.length+settingsShellFixture.deleteRequests.length+settingsShellFixture.creates.length"), 0);

    // The built-in action still runs before App clears its captured menu target.
    await openMenu("chat-2");
    await evaluate(`${entries}.find(item=>item.textContent.trim()==='Open session').click()`);
    await until(`${row("chat-2")}.firstElementChild.getAttribute('aria-pressed')==='true' && !document.querySelector('.session-tab-popup')`, "Open session keeps its original action");
    assert.equal(await evaluate("selectedMenuFixture.state.invoked.length"), 2);
    assert.deepEqual(exceptions, []);
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
