import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { browserBaseArgs, browserExecutable } from "./browserTarget";

const edge = browserExecutable;

type Box = [left: number, top: number, width: number, height: number];
type Frame = { mark: Box; explorer: Box; content: Box; tabs: number };
// Where the parts of the frame are: the mark of the title bar, the Explorer, the content, and the room the first
// tab strip leaves at its start.
const frame = `(() => {
  const box = selector => { const rect = document.querySelector(selector).getBoundingClientRect(); return [rect.left, rect.top, rect.width, rect.height].map(Math.round); };
  return { mark: box(".window-brand"), explorer: box(".session-content-rail-slot"), content: box(".session-content-main-panel"),
    tabs: Math.round(parseFloat(getComputedStyle(document.querySelector(".flexlayout__tabset_tabbar_outer[data-titlebar-start]")).paddingLeft)) };
})()`;
// The height of the title bar, the width the mark takes when it is alone, and the width the Explorer starts with.
const bar = 38, mark = 248, explorer = 272;

test("the title bar keeps Explorer in place and Home opens or focuses one Welcome tab", { skip: !edge, timeout: 90_000 }, async () => {
  const root = await mkdtemp(join(tmpdir(), "codealta-window-frame-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-window-frame", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./windowFrame.neoastra.mount.ts", import.meta.url)) }));
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
    await writeFile(join(root, "style.css"), readFileSync(new URL("../node_modules/@blueprintjs/core/lib/css/blueprint.css", import.meta.url), "utf8")
      + readFileSync(new URL("../node_modules/flexlayout-react/style/light.css", import.meta.url), "utf8") + readFileSync(new URL("./style.css", import.meta.url), "utf8"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, [...browserBaseArgs, "--allow-file-access-from-files",
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
    const exceptions: string[] = [];
    socket.addEventListener("message", event => {
      const value = JSON.parse(String(event.data));
      if (value.method === "Runtime.exceptionThrown") exceptions.push(value.params.exceptionDetails.exception?.description ?? value.params.exceptionDetails.text);
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
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{if(${condition})resolve(true);else if(Date.now()>end)resolve(document.body.innerText.slice(0,600));else setTimeout(tick,25);};tick();})`);
    const frames = () => evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true)))))");
    const toggle = `document.querySelector('.activity-rail button[aria-controls="project-rail"]')`;
    const size = async (width: number, height: number) => {
      await command("Emulation.setDeviceMetricsOverride", { width, height, deviceScaleFactor: 1, mobile: false });
      assert.equal(await wait(`innerWidth===${width} && innerHeight===${height}`), true);
    };
    // The frame once the Explorer is as asked: shown, by a click on its button when it is not, or hidden.
    const shown = async (open: boolean) => {
      if (await evaluate(`${toggle}.getAttribute('aria-expanded')`) !== String(open)) await evaluate(`${toggle}.click()`);
      assert.equal(await wait(`${toggle}.getAttribute('aria-expanded')===${JSON.stringify(String(open))}`), true);
      await frames();
      return await evaluate(frame) as Frame;
    };

    await command("Page.enable");
    await command("Runtime.enable");
    await command("Page.addScriptToEvaluateOnNewDocument", { source: `Object.defineProperty(navigator,'languages',{get:()=>['en-US']});
      localStorage.setItem('settingsFixtureOwned','true');
      localStorage.setItem('codealta.desktop.landing.startup.v1','off');` });
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await size(800, 900);
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait(`!!document.querySelector('#catalog-prompt, #session-prompt') && !!document.querySelector('.flexlayout__tabset_tabbar_outer[data-titlebar-start]')`), true, exceptions.join("\n"));

    // A narrow window starts without the Explorer: the mark is at the start of the title bar, the first tab strip
    // starts after it, and the content has the whole window.
    const alone: Frame = { mark: [0, 0, mark, bar], explorer: [0, 0, 0, 0], content: [0, 0, 800, 900], tabs: mark };
    assert.equal(await evaluate(`${toggle}.getAttribute('aria-expanded')`), "false");
    assert.deepEqual(await shown(false), alone);

    // Its button shows the Explorer over the tabs, under the title bar: nothing else moves.
    assert.deepEqual(await shown(true), { ...alone, explorer: [0, bar, 320, 900 - bar] });
    await size(800, 560);
    assert.deepEqual(await shown(true), { ...alone, explorer: [0, bar, 320, 560 - bar], content: [0, 0, 800, 560] });
    assert.deepEqual(await shown(false), { ...alone, content: [0, 0, 800, 560] });

    // A wide window gives the Explorer a column, with the mark above it and its divider, which is before the content.
    await size(1280, 900);
    assert.deepEqual(await shown(true), { mark: [0, 0, explorer + 2, bar], explorer: [0, bar, explorer, 900 - bar], content: [explorer + 2, 0, 1280 - explorer - 2, 900], tabs: 0 });
    assert.deepEqual(await shown(false), { ...alone, content: [0, 0, 1280, 900] });

    const home = `document.querySelector('.window-actions button[aria-label="Home"]')`;
    const welcomeTabs = `Array.from(document.querySelectorAll('.flexlayout__tab_button')).filter(tab=>tab.textContent.trim()==='Welcome')`;
    assert.equal(await wait(`${home} && !${home}.disabled`), true, "The top bar offers an accessible Home button");
    assert.equal(await evaluate(`${home}.title`), "Home");
    assert.equal(await evaluate(`${welcomeTabs}.length`), 0, "Startup preference stays off");
    await evaluate(`${home}.focus()`);
    await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Enter", code: "Enter", windowsVirtualKeyCode: 13 });
    assert.equal(await wait(`${welcomeTabs}.length === 1 && ${home}.classList.contains('bp6-active')`), true);
    await evaluate(`${home}.click(); ${home}.click()`);
    assert.equal(await wait("windowFrameFixture.commands.length === 3"), true);
    await frames();
    assert.equal(await evaluate(`${welcomeTabs}.length`), 1, "Repeated clicks reuse the tab");
    assert.deepEqual(await evaluate("windowFrameFixture.commands.map(command=>command.commandId)"), ["welcome-command", "welcome-command", "welcome-command"]);
    assert.equal(await evaluate("windowFrameFixture.opens.every(open=>open.projectId===null && open.sessionId===null && open.key===null)"), true);
    await evaluate("document.querySelector('.activity-canvases').click()");
    assert.equal(await wait(`!${home}.classList.contains('bp6-active')`), true);
    await evaluate(`${home}.click()`);
    assert.equal(await wait(`${welcomeTabs}.length === 1 && ${home}.classList.contains('bp6-active')`), true, "Home focuses Welcome from another tab");
    // The control remains visible and clickable in both themes, even in a narrow window.
    for (const width of [1280, 390]) {
      await size(width, 650);
      for (const dark of [false, true]) {
        const theme = dark ? "dark" : "light";
        assert.equal(await evaluate(`(async()=>{for(let i=0;i<4 && document.documentElement.dataset.theme!==${JSON.stringify(theme)};i++) {
          document.querySelector('.theme-switch').click(); await new Promise(resolve=>requestAnimationFrame(resolve));
        } return document.documentElement.dataset.theme})()`), theme);
        assert.equal(await evaluate(`(()=>{const e=${home}, r=e.getBoundingClientRect(), icon=e.querySelector('svg');
          return r.width>=28 && r.height>=28 && r.left>=0 && r.right<=innerWidth && r.top>=0 && r.bottom<=38
            && e.contains(document.elementFromPoint(r.left+r.width/2,r.top+r.height/2))
            && !!icon && getComputedStyle(icon).stroke!=='none' && getComputedStyle(e).visibility==='visible'})()`), true, `Home at ${width}px, dark=${dark}`);
      }
    }
    assert.equal(await evaluate("localStorage.getItem('codealta.desktop.landing.startup.v1')"), "off", "Opening Welcome never changes startup");
    await evaluate("windowFrameFixture.enabled=false; projectFocusFixture.notify({kind:'plugins-changed',runningSessions:0,busyTerminals:0})");
    assert.equal(await wait(`${home}.disabled`), true, "Home is unavailable when the built-in Landing page is disabled, not routed to another plugin");
    assert.deepEqual(exceptions, []);
  } finally {
    // Edge's launcher can exit while the browser it started goes on: the browser itself is asked to close.
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
