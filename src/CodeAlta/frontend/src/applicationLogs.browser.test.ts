import assert from "node:assert/strict";
import { workflowLanguages } from "./workflowLocalizationChecks";
import { inventoryNarrow } from "./inventoryLocalizationChecks";
import { spawn } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath, pathToFileURL } from "node:url";
import { build } from "esbuild";
import { createApplicationLogClearActions } from "./applicationLogClear";
import type { ApplicationLogsClearResponse } from "#neoastra";

const edge = ["C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
  "C:/Program Files/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

test("clear owner retains uncertain original request and never retries or unlocks from refresh", async () => {
  const target = { captureId: "11111111-1111-4111-8111-111111111111", boundary: "42", grant: "22222222-2222-4222-8222-222222222222", rows: 1, captureOmitted: "0", readOmitted: 0 };
  const next = { ...target, boundary: "43", grant: "33333333-3333-4333-8333-333333333333" };
  let reject!: (error: Error) => void;
  let calls = 0;
  const actions = createApplicationLogClearActions(() => { calls++; return new Promise<ApplicationLogsClearResponse>((_, fail) => { reject = fail; }); });
  actions.subscribe(() => { throw new Error("broken observer"); });
  assert.equal(actions.submit(target, "CLEAR CAPTURED LOGS"), true);
  assert.equal(actions.submit(target, "CLEAR CAPTURED LOGS"), false);
  reject(new Error("network error containing sensitive details"));
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.deepEqual(actions.snapshot().target, target);
  assert.equal(actions.snapshot().kind, "uncertain");
  assert.equal(actions.submit(next, "CLEAR CAPTURED LOGS"), false);
  assert.equal(calls, 1);
  assert.equal(JSON.stringify(actions.snapshot()).includes("sensitive details"), false);
  const mismatch = createApplicationLogClearActions(async () => ({ status: "cleared", captureId: target.captureId, boundary: "43", clearedRows: 0, coveredOmitted: "0" }));
  assert.equal(mismatch.submit(target, "CLEAR CAPTURED LOGS"), true);
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(mismatch.snapshot().kind, "uncertain");
  assert.deepEqual(mismatch.snapshot().target, target);
  assert.equal(mismatch.submit(next, "CLEAR CAPTURED LOGS"), false);
  const accepted = createApplicationLogClearActions(async () => ({ status: "cleared", captureId: target.captureId, boundary: target.boundary, clearedRows: 0, coveredOmitted: "0" }));
  assert.equal(accepted.submit(target, "CLEAR CAPTURED LOGS"), true);
  await new Promise(resolve => setTimeout(resolve, 0));
  assert.equal(accepted.snapshot().kind, "confirmed");
  assert.equal(accepted.submit(next, "CLEAR CAPTURED LOGS"), true);
});

test("mounted production logs screen is explicit, bounded, plain text and fences stale reads", { skip: !edge, timeout: 60_000 }, async () => {
  const app = readFileSync(fileURLToPath(new URL("./main.tsx", import.meta.url)), "utf8");
  assert.match(app, /useState\(\(\) => createApplicationLogClearActions\(applicationLogs\.clear\)\)/);
  assert.match(app, /settingsSection === "logs" \? <>\{settingsCard\("logs"\)\}\s*<ApplicationLogsPanel clearActions=\{logClearActions\} read=\{demoMode[\s\S]*?: applicationLogs\.read\}/);
  assert.match(app, /\["Diagnostics", \[\["logs", "Application Logs"\], \["about", "About"\]\]\]/);
  assert.match(app, /<SettingsOverlay section=\{settingsSection\} onSection=\{navigate\}/);
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
    const languages = () => workflowLanguages(evaluate, "[logsFixture.reads,logsFixture.clearRequests,[...document.querySelectorAll('.logs-rows pre')].map(n=>n.firstChild?.textContent)]", ".application-logs", ".application-logs h1", "Application Logs", ".logs-metadata,code", "logsFixture.clearRequests[0]");
    assert.equal(await wait("!!document.querySelector('#navigate-logs')"), true);
    await click("#navigate-logs");
    assert.equal(await wait("!!document.querySelector('.logs-toolbar button')"), true);
    assert.equal(await evaluate("window.logsFixture.reads"), 0);
    assert.equal(await evaluate("document.activeElement.textContent.trim()"), "Refresh logs");
    await click(".logs-toolbar button");
    assert.equal(await wait("window.logsFixture.reads===1"), true);
    await languages();
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
    await languages();
    await inventoryNarrow(evaluate, command);
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
    assert.equal(await evaluate("document.querySelectorAll('.logs-toolbar button').length"), 1);
    await click(".logs-toolbar button");
    await evaluate("window.logsFixture.resolve({status:'unavailable',rows:[],captureOmitted:'0',readOmitted:0})");
    assert.equal(await wait("document.body.innerText.includes('In-memory capture unavailable')"), true);
    assert.equal(await evaluate("document.querySelectorAll('.logs-toolbar button').length"), 1);
    await click(".logs-toolbar button");
    await evaluate("window.logsFixture.resolve({status:'read_failed',rows:[],captureOmitted:'0',readOmitted:0})");
    assert.equal(await wait("document.body.innerText.includes('Application logs could not be read')"), true);
    await click(".logs-toolbar button");
    await evaluate("window.logsFixture.reject('private-path-and-exception-text')");
    assert.equal(await wait("!!document.querySelector('[role=alert]')"), true);
    assert.equal(await evaluate("document.body.innerText.includes('private-path-and-exception-text')"), false);
    await languages();
    await click(".logs-toolbar button");
    await evaluate("window.logsFixture.resolve({status:'ok',rows:[{timestamp:'t',level:'Error',logger:'Settings',text:'observed Copy /Settings/Refresh.log',textTruncated:false}],captureOmitted:'2',readOmitted:1})");
    assert.equal(await wait("!!document.querySelector('.logs-toolbar button:nth-child(3)') && document.body.innerText.includes('observed')"), true);
    await click(".logs-toolbar button:nth-child(3)");
    assert.equal(await wait("!!document.querySelector('.logs-confirm input')"), true);
    assert.equal(await evaluate("document.activeElement.matches('.logs-confirm input')"), true);
    assert.match(String(await evaluate("document.querySelector('.logs-confirm').textContent")), /1 displayed row.*1 additional response-omitted.*2 older capacity-omitted/);
    for (const width of [390, 1120]) {
      await command("Emulation.setDeviceMetricsOverride", { width, height: 800, deviceScaleFactor: 1, mobile: false });
      for (const theme of ["dark", "light"]) {
        await evaluate(`document.documentElement.dataset.theme=${JSON.stringify(theme)}`);
        const layout = await evaluate(`({right:document.querySelector('.logs-confirm').getBoundingClientRect().right,
          overflow:document.documentElement.scrollWidth,focus:document.activeElement.matches('.logs-confirm input')})`) as {right:number;overflow:number;focus:boolean};
        assert.ok(layout.right <= width + 2 && layout.overflow <= width + 2 && layout.focus, `${width} ${theme}: ${JSON.stringify(layout)}`);
      }
    }
    await evaluate("document.querySelector('.logs-confirm input').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,cancelable:true}))");
    assert.equal(await wait("!document.querySelector('.logs-confirm')"), true);
    assert.equal(await evaluate("document.activeElement.textContent"), "Clear captured messages…");
    assert.equal(await evaluate("window.logsFixture.clearRequests.length"), 0);
    await click(".logs-toolbar button:nth-child(3)");
    await evaluate(`(() => {const input=document.querySelector('.logs-confirm input');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'CLEAR CAPTURED LOGS');
      input.dispatchEvent(new Event('input',{bubbles:true}));})()`);
    assert.equal(await wait("document.querySelector('.logs-confirm button[type=submit]').disabled===false"), true);
    await languages();
    await inventoryNarrow(evaluate, command);
    await evaluate("document.querySelector('.logs-confirm input').dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',isComposing:true,bubbles:true,cancelable:true}))");
    assert.equal(await evaluate("document.querySelector('.logs-confirm input').value"), "CLEAR CAPTURED LOGS", "composing Escape retains exact confirmation");
    await click(".logs-confirm button[type=submit]");
    assert.equal(await wait("window.logsFixture.clearRequests.length===1"), true);
    await languages();
    const first = await evaluate("window.logsFixture.clearRequests[0]") as {captureId:string;boundary:string;grant:string;confirmation:string};
    assert.equal(first.confirmation, "CLEAR CAPTURED LOGS");
    assert.match(first.captureId, /^[0-9a-f-]{36}$/);
    assert.match(first.boundary, /^[1-9]\d*$/);
    await click("#navigate-logs"); await click("#navigate-logs");
    assert.equal(await wait("document.querySelector('.logs-clear-status')?.textContent.includes('pending')"), true);
    assert.equal(await evaluate("document.querySelector('.logs-clear-status').textContent.includes(" + JSON.stringify(first.boundary) + ")"), true);
    await click(".logs-toolbar button:first-child");
    await evaluate("window.logsFixture.resolve({status:'ok',rows:[{timestamp:'t',level:'Info',logger:'test',text:'newer',textTruncated:false}],captureOmitted:'0',readOmitted:0})");
    assert.equal(await wait("document.body.innerText.includes('newer')"), true);
    assert.equal(await evaluate("!!document.querySelector('.logs-toolbar button:nth-child(3)')"), false);
    assert.equal(await evaluate("window.logsFixture.clearRequests.length"), 1);
    await click("#navigate-logs");
    await evaluate(`window.logsFixture.resolveClear({status:'cleared',captureId:${JSON.stringify(first.captureId)},boundary:${JSON.stringify(first.boundary)},clearedRows:1,coveredOmitted:'2'})`);
    await click("#navigate-logs");
    assert.equal(await wait("document.querySelector('.logs-clear-status')?.textContent.includes('confirmed')"), true);
    await click(".logs-toolbar button:first-child");
    await evaluate("window.logsFixture.resolve({status:'ok',rows:[{timestamp:'t',level:'Info',logger:'test',text:'latest',textTruncated:false}],captureOmitted:'0',readOmitted:0})");
    assert.equal(await wait("!!document.querySelector('.logs-toolbar button:nth-child(3)') && document.body.innerText.includes('latest')"), true);
    await click(".logs-toolbar button:nth-child(3)");
    await evaluate(`(() => {const input=document.querySelector('.logs-confirm input');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'CLEAR CAPTURED LOGS');
      input.dispatchEvent(new Event('input',{bubbles:true}));})()`);
    assert.equal(await wait("document.querySelector('.logs-confirm button[type=submit]').disabled===false"), true);
    await click(".logs-confirm button[type=submit]");
    assert.equal(await wait("window.logsFixture.clearRequests.length===2"), true);
    await evaluate("window.logsFixture.resolveClear({status:'cleared',captureId:'not-original',boundary:'999',clearedRows:1,coveredOmitted:'0'})");
    assert.equal(await wait("document.querySelector('.logs-clear-status')?.textContent.includes('unconfirmed')"), true);
    await languages();
    await click("#navigate-logs"); await click("#navigate-logs");
    await click(".logs-toolbar button:first-child");
    await evaluate("window.logsFixture.resolve({status:'ok',rows:[{timestamp:'t',level:'Info',logger:'test',text:'fresh',textTruncated:false}],captureOmitted:'0',readOmitted:0})");
    assert.equal(await wait("document.body.innerText.includes('fresh')"), true);
    assert.equal(await evaluate("document.querySelectorAll('.logs-toolbar button').length"), 1);
    assert.equal(await evaluate("window.logsFixture.clearRequests.length"), 2);
    // A new renderer/owner is required to exercise a second permanently uncertain outcome.
    await command("Page.navigate", { url: pathToFileURL(page).href });
    assert.equal(await wait("!!document.querySelector('#navigate-logs') && window.logsFixture?.reads===0"), true);
    await click("#navigate-logs");
    await click(".logs-toolbar button:first-child");
    await evaluate("window.logsFixture.resolve({status:'ok',rows:[{timestamp:'t',level:'Info',logger:'test',text:'new-window',textTruncated:false}],captureOmitted:'0',readOmitted:0})");
    assert.equal(await wait("!!document.querySelector('.logs-toolbar button:nth-child(3)')"), true);
    await click(".logs-toolbar button:nth-child(3)");
    await evaluate(`(() => {const input=document.querySelector('.logs-confirm input');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input,'CLEAR CAPTURED LOGS');
      input.dispatchEvent(new Event('input',{bubbles:true}));})()`);
    assert.equal(await wait("document.querySelector('.logs-confirm button[type=submit]').disabled===false"), true);
    await click(".logs-confirm button[type=submit]");
    assert.equal(await wait("window.logsFixture.clearRequests.length===1"), true);
    const lostBoundary = await evaluate("window.logsFixture.clearRequests[0].boundary");
    await evaluate("window.logsFixture.rejectClear('sensitive transport failure')");
    assert.equal(await wait("document.querySelector('.logs-clear-status')?.textContent.includes('unconfirmed')"), true);
    await click("#navigate-logs"); await click("#navigate-logs");
    assert.equal(await evaluate("document.querySelector('.logs-clear-status').textContent.includes(" + JSON.stringify(lostBoundary) + ")"), true);
    await click(".logs-toolbar button:first-child");
    await evaluate("window.logsFixture.resolve({status:'ok',rows:[{timestamp:'t',level:'Info',logger:'test',text:'after-loss',textTruncated:false}],captureOmitted:'0',readOmitted:0})");
    assert.equal(await wait("document.body.innerText.includes('after-loss')"), true);
    assert.equal(await evaluate("document.querySelectorAll('.logs-toolbar button').length"), 1);
    assert.equal(await evaluate("window.logsFixture.clearRequests.length"), 1);
    assert.equal(await evaluate("document.body.innerText.includes('sensitive transport failure')"), false);
  } finally {
    socket?.close(); browser?.kill();
    await rm(root, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
});
