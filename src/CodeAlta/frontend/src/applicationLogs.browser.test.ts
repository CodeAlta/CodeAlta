import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

test("mounted production logs screen is explicit, bounded, plain text and fences stale reads", { skip: !edge, timeout: 60_000 }, async () => {
  const app = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(app, /view === "logs" \? <ApplicationLogsPanel read=\{demoMode[\s\S]*?: applicationLogs\.read\}/);
  assert.match(app, /onOpenLogs=\{\(\) => navigate\("logs"\)\}/);
  const root = await mkdtemp(join(tmpdir(), "codealta-app-logs-"));
  let browser: ReturnType<typeof spawn> | undefined;
  let socket: WebSocket | undefined;
  try {
    await build({ entryPoints: [fileURLToPath(new URL("./applicationLogs.mount.tsx", import.meta.url))],
      outfile: join(root, "fixture.js"), bundle: true, platform: "browser", format: "iife" });
    await writeFile(join(root, "style.css"), readFileSync(fileURLToPath(new URL("./style.css", import.meta.url))));
    const page = join(root, "fixture.html");
    await writeFile(page, '<!doctype html><html><head><link rel="stylesheet" href="style.css"></head><body><div id="app"></div><script src="fixture.js"></script></body></html>');
    const profile = join(root, "profile");
    browser = spawn(edge!, ["--headless=new", "--disable-gpu", "--no-first-run", "--disable-background-networking", "--disable-extensions",
      `--user-data-dir=${profile}`, "--remote-debugging-port=0", "about:blank"], { stdio: "ignore", windowsHide: true });
    let port = "";
    for (let attempt = 0; attempt < 100 && !port; attempt++) {
      try { port = (await readFile(join(profile, "DevToolsActivePort"), "utf8")).split(/\r?\n/)[0]; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.match(port, /^\d+$/);
    const pages: { type?: string; url?: string; webSocketDebuggerUrl?: string }[] =
      await (await fetch(`http://127.0.0.1:${port}/json/list`, { signal: AbortSignal.timeout(10_000) })).json();
    const tab = pages.find(value => value.type === "page" && value.url === "about:blank");
    assert.ok(tab?.webSocketDebuggerUrl);
    socket = new WebSocket(tab.webSocketDebuggerUrl);
    await new Promise<void>((resolve, reject) => {
      socket!.addEventListener("open", () => resolve(), { once: true });
      socket!.addEventListener("error", () => reject(new Error("test browser unavailable")), { once: true });
    });
    let sequence = 0;
    const command = (method: string, params: object = {}) => new Promise<{ result?: { value?: unknown } }>((resolve, reject) => {
      const id = ++sequence;
      const timer = setTimeout(() => reject(new Error(`browser ${method} timed out`)), 12_000);
      const reply = (event: MessageEvent) => {
        const message = JSON.parse(String(event.data)) as { id?: number; result?: { result?: { value?: unknown } }; error?: object };
        if (message.id !== id) return;
        socket!.removeEventListener("message", reply); clearTimeout(timer);
        if (message.error) reject(new Error(`browser ${method} failed`)); else resolve(message.result ?? {});
      };
      socket!.addEventListener("message", reply);
      socket!.send(JSON.stringify({ id, method, params }));
    });
    await command("Page.enable");
    await command("Emulation.setDeviceMetricsOverride", { width: 1120, height: 800, deviceScaleFactor: 1, mobile: false });
    await command("Page.navigate", { url: pathToFileURL(page).href });
    const evaluate = async (expression: string) => (await command("Runtime.evaluate", { expression, returnByValue: true, awaitPromise: true })).result?.value;
    const wait = (condition: string) => evaluate(`new Promise(resolve=>{let end=Date.now()+7000;function tick(){if(${condition})resolve(true);
      else if(Date.now()>end)resolve(document.body.innerText.slice(0,400));else setTimeout(tick,20)}tick()})`);
    const click = (selector: string) => evaluate(`document.querySelector(${JSON.stringify(selector)}).click()`);
    assert.equal(await wait("!!document.querySelector('#navigate-logs')"), true);
    await click("#navigate-logs");
    assert.equal(await wait("!!document.querySelector('.logs-toolbar button')"), true);
    assert.equal(await evaluate("window.logsFixture.reads"), 0);
    assert.equal(await evaluate("document.activeElement.textContent.trim()"), "Refresh logs");
    await click(".logs-toolbar button");
    assert.equal(await wait("window.logsFixture.reads===1"), true);
    await click("#navigate-logs");
    await evaluate(`window.logsFixture.resolve({status:'ok',rows:[{timestamp:'2026',level:'Warn',logger:'test',text:'STALE',textTruncated:false}],captureOmitted:'0',readOmitted:0})`);
    await click("#navigate-logs");
    assert.equal(await wait("!!document.querySelector('.logs-toolbar button')"), true);
    assert.equal(await evaluate("document.body.innerText.includes('STALE')"), false);
    await click(".logs-toolbar button");
    assert.equal(await wait("window.logsFixture.reads===2"), true);
    await evaluate(`window.logsFixture.resolve({status:'ok',rows:[{timestamp:'2026-01-01',level:'Info',logger:'test',text:'<script>no execution</script> <a href="https://example.test">private</a> \\u0000',textTruncated:true}],captureOmitted:'7',readOmitted:3})`);
    assert.equal(await wait("!!document.querySelector('.logs-rows pre')"), true);
    assert.equal(await evaluate("document.querySelectorAll('.logs-rows a,.logs-rows script').length"), 0);
    assert.equal(await evaluate("document.querySelector('.logs-rows pre').textContent.includes('<script>')"), true);
    assert.match(String(await evaluate("document.querySelector('[role=status]').textContent")), /7 from in-memory capacity, 3 from the bounded response/);
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`);
        const layout = await evaluate(`({right:document.querySelector('.logs-toolbar').getBoundingClientRect().right,
          overflow:document.documentElement.scrollWidth,wrap:document.querySelector('.logs-rows').classList.contains('logs-wrap')})`) as {right:number;overflow:number;wrap:boolean};
        assert.ok(layout.right <= width + 2 && layout.overflow <= width + 2 && layout.wrap, `${width} ${theme}: ${JSON.stringify(layout)}`);
        await evaluate("document.querySelector('.logs-toolbar button').focus()");
        assert.equal(await evaluate("document.activeElement.textContent.trim()"), "Refresh logs");
        await command("Input.dispatchKeyEvent", { type: "keyDown", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
        await command("Input.dispatchKeyEvent", { type: "keyUp", key: "Tab", code: "Tab", windowsVirtualKeyCode: 9 });
        assert.equal(await evaluate("document.activeElement.matches('.logs-toolbar input')"), true);
        const before = await evaluate("document.activeElement.checked");
        await command("Input.dispatchKeyEvent", { type: "keyDown", key: " ", code: "Space", windowsVirtualKeyCode: 32 });
        await command("Input.dispatchKeyEvent", { type: "keyUp", key: " ", code: "Space", windowsVirtualKeyCode: 32 });
        assert.equal(await wait(`document.querySelector('.logs-toolbar input').checked===${!before}`), true);
        await click(".logs-toolbar input");
      }
    }
    await click(".logs-toolbar button");
    await evaluate("window.logsFixture.resolve({status:'ok',rows:[],captureOmitted:'0',readOmitted:0})");
    assert.equal(await wait("document.body.innerText.includes('No captured messages')"), true);
    await click(".logs-toolbar button");
    await evaluate("window.logsFixture.resolve({status:'unavailable',rows:[],captureOmitted:'0',readOmitted:0})");
    assert.equal(await wait("document.body.innerText.includes('In-memory capture unavailable')"), true);
    await click(".logs-toolbar button");
    await evaluate("window.logsFixture.resolve({status:'read_failed',rows:[],captureOmitted:'0',readOmitted:0})");
    assert.equal(await wait("document.body.innerText.includes('Application logs could not be read')"), true);
    await click(".logs-toolbar button");
    await evaluate("window.logsFixture.reject('private-path-and-exception-text')");
    assert.equal(await wait("!!document.querySelector('[role=alert]')"), true);
    assert.equal(await evaluate("document.body.innerText.includes('private-path-and-exception-text')"), false);
  } finally {
    socket?.close(); browser?.kill();
    await rm(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
});
