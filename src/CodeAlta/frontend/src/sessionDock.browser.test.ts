import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe", "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);
test("temporary project tab, composer resizing, pointer splits and session-local notes overlays", { skip: !edge, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-session-dock-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-dock", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./settingsShell.neoastra.mount.ts", import.meta.url)) }));
        // esbuild has no Vite ?worker transform. Bundle the real editor worker into
        // an isolated blob so this file-based fixture does not depend on a host/server.
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
    await writeFile(join(root, "style.css"), readFileSync(new URL("../node_modules/flexlayout-react/style/light.css", import.meta.url), "utf8") + readFileSync(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions",
      `--user-data-dir=${join(root, "profile")}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let i = 0; i < 100 && !port; i++) {
      try { port = (await readFile(join(root, "profile", "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json() as { type?: string; webSocketDebuggerUrl?: string }[];
    const tab = pages.find(tab => tab.type === "page");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
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
      const timer = setTimeout(() => { socket!.removeEventListener("message", reply); reject(new Error(`${method} timed out`)); }, 12_000);
      socket!.addEventListener("message", reply); socket!.send(JSON.stringify({ id, method, params }));
    });
    const evaluate = async (expression: string) => {
      const value = await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true });
      assert.equal(value.exceptionDetails, undefined); return value.result?.value;
    };
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{if(${condition})resolve(true);else if(Date.now()>end)resolve(false);else setTimeout(tick,20)};tick()})`);
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("!!document.querySelector('#catalog-prompt')"), true);
    await evaluate("localStorage.setItem('settingsFixtureOwned','true');localStorage.setItem('settingsFixtureSecondProject','true')");
    await command("Page.reload");
    assert.equal(await wait("!!document.querySelector('#project-list')"), true);
    const project = (name: string) => evaluate(`[...document.querySelectorAll('#project-list button')].find(b=>b.querySelector('strong')?.textContent===${JSON.stringify(name)}).click()`);
    const session = (name: string) => evaluate(`[...document.querySelectorAll('.session-row button')].find(b=>b.querySelector('.session-title')?.textContent===${JSON.stringify(name)}).click()`);
    await project("Project");
    assert.equal(await wait("document.querySelector('.welcome-subtitle')?.textContent.includes('/fixture/project')"), true);
    assert.equal(await evaluate("!!document.querySelector('.blank-project-logo pre') && !!document.querySelector('.blank-project .owned-session')"), true);
    await session("one");
    assert.equal(await wait("!!document.querySelector('#session-prompt') && !document.querySelector('.blank-project')"), true);
    await session("two");
    assert.equal(await wait("document.querySelectorAll('.session-dock > .flexlayout__layout [data-session-node]').length===2"), true);
    await evaluate("window.dockKept={one:document.querySelector('#session-prompt-one'),two:document.querySelector('#session-prompt')}");
    const resize = async () => {
      assert.equal(await wait("Number(document.querySelector('.session-workspace[data-active=true] .composer-splitter')?.getAttribute('aria-valuenow'))>0"), true);
      const before = await evaluate("Number(document.querySelector('.session-workspace[data-active=true] .composer-splitter').getAttribute('aria-valuenow'))") as number;
      const point = await evaluate("(()=>{const r=document.querySelector('.session-workspace[data-active=true] .composer-splitter').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()") as { x: number; y: number };
      await command("Input.dispatchMouseEvent", { type: "mousePressed", ...point, button: "left", buttons: 1, clickCount: 1 });
      await command("Input.dispatchMouseEvent", { type: "mouseMoved", x: point.x, y: point.y - 40, buttons: 1 });
      await command("Input.dispatchMouseEvent", { type: "mouseReleased", x: point.x, y: point.y - 40, button: "left", clickCount: 1 });
      assert.equal(await wait(`Number(document.querySelector('.session-workspace[data-active=true] .composer-splitter').getAttribute('aria-valuenow'))>=${before + 35}`), true);
    };
    await resize();
    await project("Other project");
    assert.equal(await wait("document.querySelector('.welcome-subtitle')?.textContent.includes('/fixture/other')"), true);
    await evaluate("window.dockKept.draft=document.querySelector('[role=tab][aria-controls=flexlayout-tab-session-draft]')");
    await project("Project");
    assert.equal(await wait("document.querySelector('.welcome-subtitle')?.textContent.includes('/fixture/project')"), true);
    assert.equal(await evaluate("dockKept.draft===document.querySelector('[role=tab][aria-controls=flexlayout-tab-session-draft]') && document.querySelectorAll('#flexlayout-tabbutton-session-draft').length===1 && dockKept.one===document.querySelector('#session-prompt-one') && dockKept.two===document.querySelector('#session-prompt-two')"), true);
    await resize();
    await evaluate("[...document.querySelectorAll('.session-dock [data-session-node]')].find(n=>n.textContent.startsWith('one -')).closest('[role=tab]').click()");
    assert.equal(await wait("!document.querySelector('#flexlayout-tabbutton-session-draft') && !!document.querySelector('#session-prompt')"), true);
    // Trusted mouse input exercises pointer capture and movement; synthetic HTML5
    // DragEvents cannot establish whether tab dragging works in the native host.
    await evaluate("window.nativeTabDrags=0;document.addEventListener('dragstart',e=>{if(e.target.closest('[role=tab]'))window.nativeTabDrags++})");
    const drop = async (edge: "right" | "bottom", cancel = false) => {
      const points = await evaluate(`(()=>{
      const source=[...document.querySelectorAll('.session-dock [data-session-node]')].find(n=>n.textContent.startsWith('two -')).closest('[role=tab]');
      const pane=document.querySelector('.session-workspace[data-active=true]'),r=pane.getBoundingClientRect(),s=source.getBoundingClientRect();
      return {start:{x:s.x+20,y:s.y+s.height/2},end:{x:${edge === "right" ? "r.right-6" : "r.x+r.width/2"},y:${edge === "bottom" ? "r.bottom-6" : "r.y+r.height/2"}}};
      })()`) as { start: { x: number; y: number }; end: { x: number; y: number } };
      await command("Input.dispatchMouseEvent", { type: "mousePressed", ...points.start, button: "left", buttons: 1, clickCount: 1 });
      await command("Input.dispatchMouseEvent", { type: "mouseMoved", ...points.end, buttons: 1 });
      assert.equal(await wait("!!document.querySelector('.session-drop-preview')"), true);
      if (cancel) {
        await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
        await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Escape", code: "Escape", windowsVirtualKeyCode: 27 });
      }
      await command("Input.dispatchMouseEvent", { type: "mouseReleased", ...points.end, button: "left", clickCount: 1 });
      assert.equal(await wait("!document.querySelector('.session-drop-preview')"), true);
    };
    await drop("right", true);
    assert.equal(await evaluate("[...document.querySelectorAll('.session-workspace')].filter(n=>n.getBoundingClientRect().height>0).length"), 1);
    await drop("right");
    assert.equal(await wait("[...document.querySelectorAll('.session-workspace')].filter(n=>n.getBoundingClientRect().height>0).length===2"), true);
    assert.equal(await evaluate("window.nativeTabDrags"), 0);
    // Notes overlay the timeline, never create a nested dock or steal editor space.
    const composerBefore = await evaluate("document.querySelector('.session-workspace[data-active=true] .composer-region').getBoundingClientRect().height");
    await evaluate("document.querySelector('.session-workspace[data-active=true] .session-notes-toggle').click()");
    assert.equal(await wait("document.querySelector('.session-workspace[data-active=true] .session-notes-overlay')?.dataset.collapsed==='false'"), true);
    assert.equal(await evaluate(`(()=>{const pane=document.querySelector('.session-workspace[data-active=true]'),overlay=pane.querySelector('.session-notes-overlay'),r=overlay.getBoundingClientRect(),t=pane.querySelector('.session-timeline-area').getBoundingClientRect();
      return r.x>=t.x && r.right<=t.right && r.y>=t.y && r.bottom<=t.bottom && !pane.querySelector('.flexlayout__layout') && [...document.querySelectorAll('.session-notes-overlay')].filter(n=>n.dataset.collapsed==='false').length===1})()`), true);
    assert.equal(await evaluate("document.querySelector('.session-workspace[data-active=true] .composer-region').getBoundingClientRect().height"), composerBefore);
    await evaluate("document.querySelector('.session-workspace[data-active=true] [aria-label=\"Collapse Alta notes\"]').click()");
    assert.equal(await wait("document.querySelector('.session-workspace[data-active=true] .session-notes-toggle')?.getAttribute('aria-expanded')==='false'"), true);
    await project("Project");
    assert.equal(await wait("!!document.querySelector('#flexlayout-tabbutton-session-draft')"), true);
    await session("one");
    assert.equal(await wait("!document.querySelector('#flexlayout-tabbutton-session-draft')"), true);
    await drop("bottom");
    const panes = await evaluate("[...document.querySelectorAll('.session-workspace')].filter(n=>n.getBoundingClientRect().height>0).map(n=>n.getBoundingClientRect().toJSON())") as { x: number; y: number }[];
    assert.equal(panes.length, 2); assert.ok(Math.abs(panes[0].y - panes[1].y) > 100);
    assert.equal(await evaluate("dockKept.one===document.querySelector('#session-prompt-one') && dockKept.two===document.querySelector('#session-prompt')"), true);
    assert.equal(await evaluate("settingsShellFixture.sends.length"), 0);
  } finally {
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
