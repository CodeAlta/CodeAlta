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

// Real main, dependency observers and production styles, but an empty inert owned host. A fresh
// browser profile captures the first placement, not a reload after window geometry was remembered.
test("empty owned startup opens usable provider settings and a guide without ResizeObserver errors", { skip: !edge, timeout: 90_000 }, async t => {
  const root = await mkdtemp(join(tmpdir(), "codealta-startup-layout-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./main.tsx", import.meta.url))], outfile: join(root, "fixture.js"),
      bundle: true, platform: "browser", format: "iife", loader: { ".css": "empty", ".flf": "text", ".svg": "dataurl" },
      define: { "import.meta.env.VITE_DEMO_MODE": '"false"' }, plugins: [{ name: "isolated-startup-layout", setup(bundle) {
        bundle.onResolve({ filter: /^#neoastra$/ }, () => ({ path: fileURLToPath(new URL("./startupLayout.neoastra.mount.ts", import.meta.url)) }));
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
    const styles = ["../node_modules/normalize.css/normalize.css", "../node_modules/@blueprintjs/core/lib/css/blueprint.css",
      "../node_modules/flexlayout-react/style/light.css", "../node_modules/@xterm/xterm/css/xterm.css", "./style.css",
      "./editor/editor.css", "./explorer/explorer.css", "./terminal/terminal.css", "./automations/automations.css",
      "./workItems/workItems.css", "./issues/issues.css", "./worktrees/worktrees.css", "./mcpHost/mcpHost.css", "./spaces/spaces.css"];
    await writeFile(join(root, "style.css"), styles.map(path => readFileSync(new URL(path, import.meta.url), "utf8")).join("\n"));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><meta charset="utf-8"><link rel="stylesheet" href="style.css"></head><body><div id="root"></div><script src="fixture.js"></script></body></html>');
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions", "--edge-skip-compat-layer-relaunch", "--allow-file-access-from-files",
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
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{const end=Date.now()+7000;const tick=()=>{if(${condition})resolve(true);else if(Date.now()>end)resolve({settings:document.querySelector('.settings-dialog')?.open,focus:document.activeElement?.outerHTML.slice(0,200),prompt:document.querySelector('#catalog-prompt .view-lines')?.textContent,errors:window.startupErrors});else setTimeout(tick,25)};tick()})`);
    const frames = () => evaluate("new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(()=>requestAnimationFrame(()=>resolve(true)))))");
    const stable = async (stage: string) => {
      // Blueprint's opening/closing transition deliberately moves the guide card for 300ms.
      // Do not confuse that finite animation with unsettled dock geometry.
      await new Promise(resolve => setTimeout(resolve, 400));
      await frames();
      const samples = await evaluate(`new Promise(resolve=>{const samples=[];const sample=()=>{
        const boxes=[...document.querySelectorAll('.flexlayout__tabset_tabbar_outer,.flexlayout__tab,.session-workspace,.composer-region,.settings-dialog .app-window,.guided-tour-card')]
          .map(element=>{const box=element.getBoundingClientRect();return [box.x,box.y,box.width,box.height].map(value=>Math.round(value*100)/100)});
        samples.push({boxes,errors:startupErrors.length});if(samples.length===12)resolve(samples);else requestAnimationFrame(sample)};requestAnimationFrame(sample)})`) as { boxes: number[][]; errors: number }[];
      for (const sample of samples) assert.deepEqual(sample, samples[0], `${stage}: layout must settle without recurrent error notifications`);
      assert.equal(await evaluate("(()=>{const bars=[...document.querySelectorAll('.flexlayout__tabset_tabbar_outer[data-titlebar-start]')];return bars.length>0&&bars.every(element=>element.getBoundingClientRect().height===38)})()"), true, stage);
      t.diagnostic(`${stage}: stable across ${samples.length} frames; captured errors=${samples[0].errors}`);
    };
    const guide = async () => {
      if (!await evaluate("!!document.querySelector('.guided-tour-card')")) {
        assert.equal(await wait("!!document.querySelector('.provider-settings button[aria-label=\"Setup guide\"]:not(:disabled)')"), true);
        await evaluate("document.querySelector('.provider-settings button[aria-label=\"Setup guide\"]').click()");
      }
      assert.equal(await wait("!!document.querySelector('.guided-tour-card')"), true);
      assert.equal(await evaluate("document.querySelector('.guided-tour-card').getBoundingClientRect().width>0 && [...document.querySelectorAll('.guided-tour-card button')].some(button=>button.textContent==='Done'&&!button.disabled)"), true);
    };
    const done = async () => {
      await evaluate("[...document.querySelectorAll('.guided-tour-card button')].find(button=>button.textContent==='Done').click()");
      assert.equal(await wait("!document.querySelector('.guided-tour-card')"), true);
    };

    await command("Page.enable"); await command("Runtime.enable");
    // Record, do not preventDefault or stop propagation: browser errors remain fully observable.
    await command("Page.addScriptToEvaluateOnNewDocument", { source: `Object.defineProperty(navigator,'languages',{get:()=>['en-US']});
      window.startupStorageWasEmpty=localStorage.length===0;window.startupErrors=[];window.startupRejections=[];
      window.addEventListener('error',event=>startupErrors.push(event.message));
      window.addEventListener('unhandledrejection',event=>startupRejections.push(String(event.reason)))` });
    await command("Emulation.setFocusEmulationEnabled", { enabled: true });
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("document.querySelector('.settings-dialog')?.open && !!document.querySelector('.provider-settings-list') && !!document.querySelector('#catalog-prompt')"), true, exceptions.join("\n"));
    assert.equal(await evaluate("startupStorageWasEmpty"), true, "First mount starts with genuinely empty storage");
    assert.equal(await evaluate("document.querySelectorAll('[data-provider-key]').length"), 0);
    assert.equal(await evaluate("document.querySelector('.provider-settings').textContent.includes('No providers are configured yet.')"), true);
    assert.equal(await evaluate("startupLayoutFixture.calls.includes('providers')"), true, "Providers settings read the inert configuration listing");
    await guide(); await stable("cold wide startup with guide"); await done();

    await command("Emulation.setDeviceMetricsOverride", { width: 390, height: 650, deviceScaleFactor: 1, mobile: false });
    assert.equal(await wait("innerWidth===390 && innerHeight===650"), true);
    await guide(); await stable("narrow settings with guide"); await done();
    await evaluate("document.querySelector('[data-settings-section=\"about\"]').click()");
    assert.equal(await wait("document.querySelector('[data-settings-section=\"about\"]')?.getAttribute('aria-current')==='page'"), true);
    await stable("narrow settings navigation");
    await evaluate("document.querySelector('[data-settings-section=\"providers\"]').click()");
    assert.equal(await wait("!!document.querySelector('.provider-settings-list')"), true);
    await guide(); await done();
    await evaluate("document.querySelector('button[aria-label=\"Close settings\"]').click()");
    assert.equal(await wait("!document.querySelector('.settings-dialog') && !!document.querySelector('#catalog-prompt')"), true);
    await stable("narrow workspace after settings close");
    await command("Emulation.setDeviceMetricsOverride", { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
    await stable("wide workspace after resize");
    await evaluate("document.querySelector('#catalog-prompt').focus()");
    await command("Input.insertText", { text: "local startup draft" });
    assert.equal(await wait("document.querySelector('#catalog-prompt .view-lines')?.textContent.replaceAll('\\u00a0',' ').includes('local startup draft')"), true, "Prompt remains usable without starting a session");
    await stable("wide workspace after typing");
    assert.deepEqual(await evaluate("startupRejections"), []);
    assert.deepEqual(await evaluate("startupErrors"), [], "Cold startup and subsequent navigation must not emit even a settling ResizeObserver error");
    assert.deepEqual(exceptions, []);
  } finally {
    if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ id: 9999, method: "Browser.close" }));
    socket?.close(); browser?.kill(); await rm(root, { recursive: true, force: true, maxRetries: 8, retryDelay: 100 });
  }
});
